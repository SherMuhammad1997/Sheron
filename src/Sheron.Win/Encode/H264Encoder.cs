using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Sheron.Core.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Sheron.Win.Encode;

/// <summary>Encoder routes, best first (TRD › Video path). The capture loop falls back down this list.</summary>
internal enum EncoderKind
{
    /// <summary>Hardware encoder reading the NV12 GPU texture directly.</summary>
    HardwareGpu,
    /// <summary>Hardware encoder fed a CPU copy of the frame.</summary>
    HardwareMemory,
    /// <summary>Microsoft software encoder fed a CPU copy of the frame.</summary>
    Software,
}

/// <summary>
/// One encoded H.264 frame in Annex B form (with start codes). <paramref name="EncoderOwnMs"/> is the encoder's own
/// part: from the frame going in to the output being ready.
/// </summary>
internal sealed record EncodedFrame(byte[] Data, bool KeyFrame, double EncoderOwnMs = 0);

/// <summary>
/// One H.264 encoder: a Media Foundation transform (TRD › Video path). The software encoder answers at once on the
/// caller's thread. A hardware encoder is asynchronous: its events say when it wants input and has output, and one
/// worker thread owns it, so each call follows the event that asked for it before the next event is read (Intel's
/// encoder fails with E_UNEXPECTED otherwise). <see cref="Encode"/> is called from one thread only.
/// </summary>
internal sealed class H264Encoder : IDisposable
{
    /// <summary>MF_LOW_LATENCY (mfapi.h); the same ID as CODECAPI_AVLowLatencyMode.</summary>
    private static readonly Guid LowLatencyAttribute = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    /// <summary>MFSampleExtension_Discontinuity (mfapi.h): marks the first sample of a stream.</summary>
    private static readonly Guid DiscontinuityAttribute = new("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a");

    // Result codes from the Windows SDK (winerror.h, mferror.h).
    private const int NotImplemented = unchecked((int)0x80004001);   // E_NOTIMPL
    private const int NoMoreTypes = unchecked((int)0xC00D36B9);      // MF_E_NO_MORE_TYPES

    // Media type values from the Windows SDK (mfobjects.h, codecapi.h).
    private const uint InterlaceProgressive = 2;   // MFVideoInterlace_Progressive
    private const uint ProfileMain = 77;           // eAVEncH264VProfile_Main
    private const uint TransferMatrixBt709 = 1;    // MFVideoTransferMatrix_BT709
    private const uint NominalRange16To235 = 2;    // MFNominalRange_16_235

    /// <summary>Place of IMFTransform::ProcessOutput in the COM vtable (mftransform.h): after IUnknown's 3 and 22 others.</summary>
    private const int ProcessOutputSlot = 25;

    private const int WaitForInputMs = 500;
    private const int WaitForOutputMs = 200;
    private const int EventLogSize = 8;

    private readonly IMFActivate _activate;
    private readonly IMFTransform _transform;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly VideoSettings _settings;
    private readonly EncoderOptions _options;
    private int _inputStreamId;
    private int _outputStreamId;
    private int _inputAlignment;
    private string _streamInfo = "";
    private bool _providesSamples;
    private IMFSample? _outputSample;
    private ID3D11Texture2D? _staging;
    private IMFDXGIDeviceManager? _deviceManager;
    private bool _sentFirstSample;

    // Hardware encoders only: the worker thread that owns the transform, and what it shares with Encode.
    private IMFMediaEventGenerator? _events;
    private Thread? _worker;
    private readonly BlockingCollection<IMFSample> _inbox = new(boundedCapacity: 1);
    private readonly BlockingCollection<EncodedFrame> _outbox = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Queue<string> _eventLog = new();
    private volatile string? _workerError;

    /// <summary>Takes ownership of <paramref name="activate"/>.</summary>
    public H264Encoder(IMFActivate activate, string name, EncoderKind kind, ID3D11Device device,
        ID3D11DeviceContext context, VideoSettings settings, EncoderOptions? options = null)
    {
        _activate = activate;
        Name = name;
        Kind = kind;
        _device = device;
        _context = context;
        _settings = settings;
        _options = options ?? EncoderOptions.Default;
        _transform = activate.ActivateObject<IMFTransform>();
    }

    public string Name { get; }
    public EncoderKind Kind { get; }
    public bool IsHardware => Kind != EncoderKind.Software;
    private bool UsesGpuInput => Kind == EncoderKind.HardwareGpu;

    /// <summary>Extra bind flags the encoder asks for on its input textures (MF_SA_D3D11_BINDFLAGS); GPU input only.</summary>
    public BindFlags InputBindFlags { get; private set; }

    /// <summary>One line per encoder setting with ✓ or ✗, for the capture test window.</summary>
    public IReadOnlyList<string> Settings { get; private set; } = [];

    /// <summary>Facts that help when this encoder fails, for test messages and the test window.</summary>
    public string Details
    {
        get
        {
            string events;
            lock (_eventLog) events = string.Join(", ", _eventLog);
            return $"{Name}, {Kind}, provides samples: {_providesSamples}, input bind flags: 0x{(uint)InputBindFlags:X}, {_streamInfo}"
                + (IsHardware ? $", last events: {(events.Length > 0 ? events : "none")}" : "");
        }
    }

    /// <summary>Frames the encoder kept instead of answering at once; above 0 means low latency is not working.</summary>
    public int HeldBack { get; private set; }

    /// <summary>Configures the encoder and starts streaming. Throws if this encoder cannot do it.</summary>
    public void Start()
    {
        if (IsHardware)
        {
            // A hardware encoder stays locked until the caller says it handles asynchronous transforms.
            using var attributes = _transform.Attributes;
            attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
        }
        ReadStreamIds();
        if (UsesGpuInput)
        {
            _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            _deviceManager.ResetDevice(_device).CheckError();
            _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)_deviceManager.NativePointer);
        }
        if (_options.LowLatencyAttribute) TrySetLowLatencyAttribute();
        Settings = CodecApi.Apply(_transform, _settings, _options);

        // Encoders take the output type first, then the input type.
        using (var output = MediaType(output: true, VideoFormatGuids.H264))
        {
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_settings.BitsPerSecond).CheckError();
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, ProfileMain).CheckError();
            if (_options.KeyFrameInterval)
                output.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)_settings.KeyFrameInterval).CheckError();
            if (_options.Level != 0)
                output.Set(MediaTypeAttributeKeys.Mpeg2Level, _options.Level).CheckError();
            _transform.SetOutputType(_outputStreamId, output, 0);
        }
        using (var input = MediaType(output: false, VideoFormatGuids.NV12))
            _transform.SetInputType(_inputStreamId, input, 0);
        if (UsesGpuInput) InputBindFlags = ReadInputBindFlags();

        var inputInfo = _transform.GetInputStreamInfo(_inputStreamId);
        _inputAlignment = inputInfo.Alignment;
        var info = _transform.GetOutputStreamInfo(_outputStreamId);
        _streamInfo = $"input stream flags 0x{inputInfo.Flags:X} size {inputInfo.Size} align {inputInfo.Alignment}, "
            + $"output stream flags 0x{info.Flags:X} size {info.Size} align {info.Alignment}";
        const int provides = (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples);
        _providesSamples = !_options.CallerAllocatesOutput && (info.Flags & provides) != 0;
        if (!_providesSamples)
        {
            _outputSample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, _settings.Width * _settings.Height * 3 / 2));
            _outputSample.AddBuffer(buffer);
        }

        if (IsHardware) _events = _transform.QueryInterface<IMFMediaEventGenerator>();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        if (IsHardware)
        {
            // From here on only the worker calls the transform, until Dispose stops it.
            _worker = new Thread(RunWorker) { IsBackground = true, Name = "Sheron encoder" };
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }
    }

    /// <summary>
    /// Encodes one NV12 frame and returns what the encoder gave back: normally exactly this frame. An empty list means
    /// the encoder kept the frame (counted in <see cref="HeldBack"/>); it comes out with a later call.
    /// </summary>
    public IReadOnlyList<EncodedFrame> Encode(ID3D11Texture2D nv12, TimeSpan at)
    {
        var sample = UsesGpuInput ? GpuSample(nv12) : CpuSample(nv12);
        sample.SampleTime = at.Ticks;
        sample.SampleDuration = TimeSpan.TicksPerSecond / _settings.FramesPerSecond;
        if (!_sentFirstSample)
        {
            sample.Set(DiscontinuityAttribute, 1u).CheckError();
            _sentFirstSample = true;
        }

        var frames = new List<EncodedFrame>(1);
        if (IsHardware)
        {
            // Output that came late belongs to earlier frames: collect it first.
            while (_outbox.TryTake(out var late)) frames.Add(late);
            ThrowIfWorkerFailed();
            if (!_inbox.TryAdd(sample, WaitForInputMs))
            {
                sample.Dispose();
                ThrowIfWorkerFailed();
                throw new TimeoutException("The encoder stopped asking for frames.");
            }
            // The worker owns the sample now. Wait for this frame's output.
            if (_outbox.TryTake(out var frame, WaitForOutputMs))
            {
                frames.Add(frame);
                while (_outbox.TryTake(out var more)) frames.Add(more);
            }
            ThrowIfWorkerFailed();
        }
        else
        {
            long inputAt = Stopwatch.GetTimestamp();
            using (sample) _transform.ProcessInput(_inputStreamId, sample, 0);
            while (ReadOutput(frames, Stopwatch.GetElapsedTime(inputAt).TotalMilliseconds)) { }
        }

        if (frames.Count == 0) HeldBack++;
        return frames;
    }

    /// <summary>
    /// Hardware encoders: read one event, do what it asks, then read the next, all on this thread.
    /// Stops when Dispose shuts the encoder down (GetEvent then fails) or on the first error.
    /// </summary>
    private void RunWorker()
    {
        long lastInputAt = Stopwatch.GetTimestamp();
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                MediaEventTypes type;
                Result status;
                using (var e = _events!.GetEvent(0))
                {
                    type = e.EventType;
                    status = e.Status;
                }
                LogEvent(type, status);
                if (status.Failure || type == MediaEventTypes.Error)
                {
                    Fail($"{type}: {EncoderFactory.Describe(status)}");
                    return;
                }

                switch (type)
                {
                    case MediaEventTypes.TransformNeedInput:
                        IMFSample sample;
                        try
                        {
                            sample = _inbox.Take(_stopping.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        long inputAt = Stopwatch.GetTimestamp();
                        using (sample) _transform.ProcessInput(_inputStreamId, sample, 0);
                        lastInputAt = inputAt;
                        break;

                    case MediaEventTypes.TransformHaveOutput:
                        var frames = new List<EncodedFrame>(1);
                        ReadOutput(frames, Stopwatch.GetElapsedTime(lastInputAt).TotalMilliseconds);
                        foreach (var frame in frames) _outbox.Add(frame);
                        break;
                }
            }
        }
        catch (Exception error) when (!_stopping.IsCancellationRequested)
        {
            Fail(EncoderFactory.Describe(error));
        }
        catch (Exception)
        {
            // Shut down while waiting: the normal way out.
        }
    }

    private void Fail(string reason)
    {
        _workerError = reason;
        _outbox.CompleteAdding();   // wakes Encode if it is waiting for output
    }

    private void ThrowIfWorkerFailed()
    {
        if (_workerError is { } error) throw new InvalidOperationException($"The encoder failed: {error}.");
    }

    private void LogEvent(MediaEventTypes type, Result status)
    {
        string name = type switch
        {
            MediaEventTypes.TransformNeedInput => "need input",
            MediaEventTypes.TransformHaveOutput => "have output",
            _ => type.ToString(),
        };
        lock (_eventLog)
        {
            _eventLog.Enqueue(status.Success ? name : $"{name} {EncoderFactory.Describe(status)}");
            if (_eventLog.Count > EventLogSize) _eventLog.Dequeue();
        }
    }

    /// <summary>Stream IDs are 0 unless the encoder says otherwise (it may not implement GetStreamIDs).</summary>
    private void ReadStreamIds()
    {
        try
        {
            var inputs = new int[1];
            var outputs = new int[1];
            _transform.GetStreamIDs(1, inputs, 1, outputs);
            _inputStreamId = inputs[0];
            _outputStreamId = outputs[0];
        }
        catch (SharpGenException error) when (error.ResultCode.Code == NotImplemented)
        {
        }
    }

    /// <summary>A video type of the given subtype, with our size, frame rate and (if chosen) colour marks.</summary>
    private IMFMediaType MediaType(bool output, Guid subtype)
    {
        IMFMediaType type;
        if (_options.OfferedTypes)
        {
            type = OfferedType(output, subtype);
        }
        else
        {
            type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
        }
        type.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)_settings.Width, (uint)_settings.Height)).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)_settings.FramesPerSecond, 1)).CheckError();
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1)).CheckError();
        type.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive).CheckError();
        if (_options.ColourAttributes)
        {
            type.Set(MediaTypeAttributeKeys.YuvMatrix, TransferMatrixBt709).CheckError();
            type.Set(MediaTypeAttributeKeys.VideoNominalRange, NominalRange16To235).CheckError();
        }
        return type;
    }

    /// <summary>The first type of the given subtype that the encoder itself offers.</summary>
    private IMFMediaType OfferedType(bool output, Guid subtype)
    {
        for (int index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = output
                    ? _transform.GetOutputAvailableType(_outputStreamId, index)
                    : _transform.GetInputAvailableType(_inputStreamId, index);
            }
            catch (SharpGenException error) when (error.ResultCode.Code == NoMoreTypes)
            {
                throw new InvalidOperationException($"The encoder offers no {(output ? "H.264 output" : "NV12 input")} type.");
            }
            if (type.GetGUID(MediaTypeAttributeKeys.Subtype) == subtype) return type;
            type.Dispose();
        }
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

    /// <summary>The bind flags a GPU-input encoder wants on input textures, if it says (MF_SA_D3D11_BINDFLAGS).</summary>
    private BindFlags ReadInputBindFlags()
    {
        try
        {
            using var attributes = _transform.GetInputStreamAttributes(_inputStreamId);
            return attributes.GetUInt32(TransformAttributeKeys.D3D11Bindflags, out uint flags).Success ? (BindFlags)flags : BindFlags.None;
        }
        catch (SharpGenException)
        {
            return BindFlags.None;
        }
    }

    /// <summary>GPU input: the encoder reads the texture itself.</summary>
    private static IMFSample GpuSample(ID3D11Texture2D nv12)
    {
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, nv12, 0, false);
        using (var buffer2D = buffer.QueryInterface<IMF2DBuffer>())
            buffer.CurrentLength = buffer2D.ContiguousLength;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        return sample;
    }

    /// <summary>Memory input: copy the NV12 frame from the GPU, Y rows then UV rows, without padding.</summary>
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
        // MFCreateAlignedMemoryBuffer takes the alignment minus one (MF_16_BYTE_ALIGNMENT is 15).
        using var buffer = _options.AlignedInput && _inputAlignment > 1
            ? MediaFactory.MFCreateAlignedMemoryBuffer(frameBytes, _inputAlignment - 1)
            : MediaFactory.MFCreateMemoryBuffer(frameBytes);
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
    /// <param name="encoderOwnMs">The encoder's own time for this output, recorded with the frame.</param>
    private unsafe bool ReadOutput(List<EncodedFrame> frames, double encoderOwnMs)
    {
        if (_options.VorticeProcessOutput) return ReadOutputThroughVortice(frames, encoderOwnMs);
        var processOutput = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, NativeOutputDataBuffer*, uint*, int>)
            (*(IntPtr**)_transform.NativePointer)[ProcessOutputSlot];
        while (true)
        {
            if (!_providesSamples) ResetOutputSample();
            var output = new NativeOutputDataBuffer
            {
                StreamId = (uint)_outputStreamId,
                Sample = _providesSamples ? IntPtr.Zero : _outputSample!.NativePointer,
            };
            uint status;
            var result = new Result(processOutput(_transform.NativePointer, 0, 1, &output, &status));
            if (output.Events != IntPtr.Zero) Marshal.Release(output.Events);

            if (result.Failure)
            {
                if (_providesSamples && output.Sample != IntPtr.Zero) Marshal.Release(output.Sample);
                if (result == Vortice.MediaFoundation.ResultCode.TransformNeedMoreInput) return false;
                if (result != Vortice.MediaFoundation.ResultCode.TransformStreamChange) result.CheckError();
                // The encoder changed its output format: accept the new one and ask again.
                using var type = _transform.GetOutputAvailableType(_outputStreamId, 0);
                _transform.SetOutputType(_outputStreamId, type, 0);
                continue;
            }

            if (_providesSamples)
            {
                using var sample = new IMFSample(output.Sample);
                frames.Add(ToFrame(sample) with { EncoderOwnMs = encoderOwnMs });
            }
            else
            {
                frames.Add(ToFrame(_outputSample!) with { EncoderOwnMs = encoderOwnMs });
            }
            return true;
        }
    }

    /// <summary>The same as <see cref="ReadOutput"/>, through Vortice's wrapper (settings check only).</summary>
    private bool ReadOutputThroughVortice(List<EncodedFrame> frames, double encoderOwnMs)
    {
        while (true)
        {
            if (!_providesSamples) ResetOutputSample();
            var output = new OutputDataBuffer { StreamID = _outputStreamId, Sample = _providesSamples ? null! : _outputSample! };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            if (result.Failure)
            {
                if (result == Vortice.MediaFoundation.ResultCode.TransformNeedMoreInput) return false;
                if (result != Vortice.MediaFoundation.ResultCode.TransformStreamChange) result.CheckError();
                using var type = _transform.GetOutputAvailableType(_outputStreamId, 0);
                _transform.SetOutputType(_outputStreamId, type, 0);
                continue;
            }
            frames.Add(ToFrame(output.Sample) with { EncoderOwnMs = encoderOwnMs });
            if (_providesSamples) output.Sample.Dispose();
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

    public void Dispose()
    {
        if (_worker is not null)
        {
            // Stop the worker first: cancel its wait for a frame, and shut the encoder down, which makes a pending
            // GetEvent fail. Only then may this thread call the transform.
            _stopping.Cancel();
            try
            {
                using var shutdown = _transform.QueryInterfaceOrNull<IMFShutdown>();
                shutdown?.Shutdown();
            }
            catch (SharpGenException)
            {
            }
            _worker.Join(TimeSpan.FromSeconds(1));
        }
        else
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
        }

        _activate.ShutdownObject();
        while (_inbox.TryTake(out var unused)) unused.Dispose();
        _events?.Dispose();
        _outputSample?.Dispose();
        _staging?.Dispose();
        _transform.Dispose();
        _activate.Dispose();
        _deviceManager?.Dispose();
        _inbox.Dispose();
        _outbox.Dispose();
        _stopping.Dispose();
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
