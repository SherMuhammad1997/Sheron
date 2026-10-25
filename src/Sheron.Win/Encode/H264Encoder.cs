using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Sheron.Core.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Sheron.Win.Encode;

/// <summary>One encoded H.264 frame in Annex B form (with start codes).</summary>
internal sealed record EncodedFrame(byte[] Data, bool KeyFrame);

/// <summary>
/// One H.264 encoder: a Media Foundation transform (TRD › Video path). A hardware encoder is fed the NV12 GPU
/// texture directly and is asynchronous: its events say when it wants input and when output is ready. The software
/// encoder gets a CPU copy and answers at once. Not thread-safe: the capture thread owns it.
/// </summary>
internal sealed class H264Encoder : IDisposable
{
    /// <summary>MF_LOW_LATENCY (mfapi.h); the same ID as CODECAPI_AVLowLatencyMode.</summary>
    private static readonly Guid LowLatencyAttribute = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    // Media type values from the Windows SDK (mfobjects.h, codecapi.h).
    private const uint InterlaceProgressive = 2;   // MFVideoInterlace_Progressive
    private const uint ProfileMain = 77;           // eAVEncH264VProfile_Main
    private const uint TransferMatrixBt709 = 1;    // MFVideoTransferMatrix_BT709
    private const uint NominalRange16To235 = 2;    // MFNominalRange_16_235

    /// <summary>Place of IMFTransform::ProcessOutput in the COM vtable (mftransform.h): after IUnknown's 3 and 22 others.</summary>
    private const int ProcessOutputSlot = 25;

    private const int WaitForInputMs = 500;
    private const int WaitForOutputMs = 200;

    private readonly IMFActivate _activate;
    private readonly IMFTransform _transform;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly VideoSettings _settings;
    private readonly SemaphoreSlim _needInput = new(0);
    private readonly SemaphoreSlim _haveOutput = new(0);
    private IMFDXGIDeviceManager? _deviceManager;
    private IMFMediaEventGenerator? _events;
    private Thread? _eventPump;
    private volatile string? _eventError;
    private bool _providesSamples;
    private IMFSample? _outputSample;
    private ID3D11Texture2D? _staging;

    /// <summary>Takes ownership of <paramref name="activate"/>.</summary>
    public H264Encoder(IMFActivate activate, string name, bool hardware, ID3D11Device device,
        ID3D11DeviceContext context, VideoSettings settings)
    {
        _activate = activate;
        Name = name;
        IsHardware = hardware;
        _device = device;
        _context = context;
        _settings = settings;
        _transform = activate.ActivateObject<IMFTransform>();
    }

    public string Name { get; }
    public bool IsHardware { get; }

    /// <summary>One line per encoder setting with ✓ or ✗, for the capture test window.</summary>
    public IReadOnlyList<string> Settings { get; private set; } = [];

    /// <summary>Frames the encoder kept instead of answering at once; above 0 means low latency is not working.</summary>
    public int HeldBack { get; private set; }

    /// <summary>Configures the encoder and starts streaming. Throws if this encoder cannot do it.</summary>
    public void Start()
    {
        if (IsHardware)
        {
            // A hardware encoder stays locked until the caller says it handles asynchronous transforms.
            using (var attributes = _transform.Attributes)
                attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
            _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            _deviceManager.ResetDevice(_device).CheckError();
            _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)_deviceManager.NativePointer);
        }
        TrySetLowLatencyAttribute();
        Settings = CodecApi.Apply(_transform, _settings);

        // Encoders take the output type first, then the input type.
        using (var output = CreateType(VideoFormatGuids.H264))
        {
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_settings.BitsPerSecond).CheckError();
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, ProfileMain).CheckError();
            output.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)_settings.KeyFrameInterval).CheckError();
            _transform.SetOutputType(0, output, 0);
        }
        using (var input = CreateType(VideoFormatGuids.NV12))
            _transform.SetInputType(0, input, 0);

        var info = _transform.GetOutputStreamInfo(0);
        const int provides = (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples);
        _providesSamples = (info.Flags & provides) != 0;
        if (!_providesSamples)
        {
            _outputSample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, _settings.Width * _settings.Height * 3 / 2));
            _outputSample.AddBuffer(buffer);
        }

        if (IsHardware)
        {
            _events = _transform.QueryInterface<IMFMediaEventGenerator>();
            _eventPump = new Thread(PumpEvents) { IsBackground = true, Name = "Sheron encoder events" };
            _eventPump.Start();
        }
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    /// <summary>
    /// Encodes one NV12 frame and returns what the encoder gave back: normally exactly this frame. An empty list means
    /// the encoder kept the frame (counted in <see cref="HeldBack"/>); it comes out with a later call.
    /// </summary>
    public IReadOnlyList<EncodedFrame> Encode(ID3D11Texture2D nv12, TimeSpan at)
    {
        using var sample = IsHardware ? GpuSample(nv12) : CpuSample(nv12);
        sample.SampleTime = at.Ticks;
        sample.SampleDuration = TimeSpan.TicksPerSecond / _settings.FramesPerSecond;

        var frames = new List<EncodedFrame>(1);
        if (IsHardware)
        {
            // Output that arrived late belongs to earlier frames: collect it first.
            while (_haveOutput.Wait(0)) ReadOutput(frames);
            if (!_needInput.Wait(WaitForInputMs)) throw new TimeoutException("The encoder stopped asking for frames.");
            ThrowIfEncoderFailed();
            _transform.ProcessInput(0, sample, 0);
            if (_haveOutput.Wait(WaitForOutputMs))
            {
                ThrowIfEncoderFailed();
                ReadOutput(frames);
            }
        }
        else
        {
            _transform.ProcessInput(0, sample, 0);
            while (ReadOutput(frames)) { }
        }

        if (frames.Count == 0) HeldBack++;
        return frames;
    }

    private IMFMediaType CreateType(Guid subtype)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        type.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)_settings.Width, (uint)_settings.Height)).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)_settings.FramesPerSecond, 1)).CheckError();
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1)).CheckError();
        type.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive).CheckError();
        type.Set(MediaTypeAttributeKeys.YuvMatrix, TransferMatrixBt709).CheckError();
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, NominalRange16To235).CheckError();
        return type;
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    private void TrySetLowLatencyAttribute()
    {
        try
        {
            using var attributes = _transform.Attributes;
            attributes.Set(LowLatencyAttribute, 1u);
        }
        catch (SharpGenException)
        {
            // Some encoders keep no attribute store; ICodecAPI still asks for low latency.
        }
    }

    /// <summary>Hardware path: the encoder reads the GPU texture itself.</summary>
    private static IMFSample GpuSample(ID3D11Texture2D nv12)
    {
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, nv12, 0, false);
        using (var buffer2D = buffer.QueryInterface<IMF2DBuffer>())
            buffer.CurrentLength = buffer2D.ContiguousLength;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        return sample;
    }

    /// <summary>Software path: copy the NV12 frame from the GPU into memory, Y rows then UV rows, without padding.</summary>
    private unsafe IMFSample CpuSample(ID3D11Texture2D nv12)
    {
        int width = _settings.Width, height = _settings.Height;
        _staging ??= _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = SampleDescription.Default,
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _context.CopyResource(_staging, nv12);

        int frameBytes = width * height * 3 / 2;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(frameBytes);
        var mapped = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            buffer.Lock(out IntPtr target, out _, out _);
            try
            {
                byte* from = (byte*)mapped.DataPointer;
                byte* to = (byte*)target;
                int pitch = (int)mapped.RowPitch;
                // The UV plane follows the Y plane at RowPitch × Height in a mapped NV12 texture.
                for (int row = 0; row < height + height / 2; row++)
                    Buffer.MemoryCopy(from + (long)row * pitch, to + (long)row * width, width, width);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
        buffer.CurrentLength = frameBytes;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        return sample;
    }

    /// <summary>
    /// Calls ProcessOutput once (more after a format change). Returns false when the encoder has nothing yet.
    /// Called through the vtable so we own exactly the sample reference the encoder hands back.
    /// </summary>
    private unsafe bool ReadOutput(List<EncodedFrame> frames)
    {
        var processOutput = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, NativeOutputDataBuffer*, uint*, int>)
            (*(IntPtr**)_transform.NativePointer)[ProcessOutputSlot];
        while (true)
        {
            if (!_providesSamples) ResetOutputSample();
            var output = new NativeOutputDataBuffer { Sample = _providesSamples ? IntPtr.Zero : _outputSample!.NativePointer };
            uint status;
            var result = new Result(processOutput(_transform.NativePointer, 0, 1, &output, &status));
            if (output.Events != IntPtr.Zero) Marshal.Release(output.Events);

            if (result.Failure)
            {
                if (_providesSamples && output.Sample != IntPtr.Zero) Marshal.Release(output.Sample);
                if (result == Vortice.MediaFoundation.ResultCode.TransformNeedMoreInput) return false;
                if (result != Vortice.MediaFoundation.ResultCode.TransformStreamChange) result.CheckError();
                // The encoder changed its output format: accept the new one and ask again.
                using var type = _transform.GetOutputAvailableType(0, 0);
                _transform.SetOutputType(0, type, 0);
                continue;
            }

            if (_providesSamples)
            {
                using var sample = new IMFSample(output.Sample);
                frames.Add(ToFrame(sample));
            }
            else
            {
                frames.Add(ToFrame(_outputSample!));
            }
            return true;
        }
    }

    private void ResetOutputSample()
    {
        // Clear the last frame's attributes (such as the keyframe flag) and data.
        _outputSample!.DeleteAllItems();
        using var buffer = _outputSample.GetBufferByIndex(0);
        buffer.CurrentLength = 0;
    }

    private static EncodedFrame ToFrame(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr data, out _, out int length);
        byte[] bytes;
        try
        {
            bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, length);
        }
        finally
        {
            buffer.Unlock();
        }
        bool keyFrame = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out uint cleanPoint).Success && cleanPoint != 0;
        return new EncodedFrame(bytes, keyFrame);
    }

    private void PumpEvents()
    {
        try
        {
            while (true)
            {
                using var e = _events!.GetEvent(0);
                switch (e.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _needInput.Release();
                        break;
                    case MediaEventTypes.TransformHaveOutput:
                        _haveOutput.Release();
                        break;
                    case MediaEventTypes.Error:
                        _eventError = EncoderFactory.Describe(e.Status);
                        _needInput.Release();
                        _haveOutput.Release();
                        return;
                }
            }
        }
        catch (SharpGenException)
        {
            // GetEvent fails once the encoder is shut down: the normal way out.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ThrowIfEncoderFailed()
    {
        if (_eventError is { } error) throw new InvalidOperationException($"The encoder reported {error}.");
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
        }
        catch (SharpGenException)
        {
            // Not started, or already broken: nothing to end.
        }
        if (_events is not null)
        {
            // Shutting a hardware encoder down makes its pending GetEvent fail, which stops the pump.
            try
            {
                using var shutdown = _transform.QueryInterfaceOrNull<IMFShutdown>();
                shutdown?.Shutdown();
            }
            catch (SharpGenException)
            {
            }
        }
        _activate.ShutdownObject();
        _eventPump?.Join(TimeSpan.FromSeconds(1));
        _events?.Dispose();
        _outputSample?.Dispose();
        _staging?.Dispose();
        _transform.Dispose();
        _activate.Dispose();
        _deviceManager?.Dispose();
        _needInput.Dispose();
        _haveOutput.Dispose();
    }

    /// <summary>MFT_OUTPUT_DATA_BUFFER (mftransform.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOutputDataBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }
}
