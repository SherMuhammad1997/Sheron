using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace Sheron.Win.View;

/// <summary>Decoder routes, best first (TRD › Viewer). The viewer falls back down this list.</summary>
internal enum DecoderKind
{
    /// <summary>The decoder works on the GPU (DXVA) and gives back Direct3D 11 textures.</summary>
    Hardware,
    /// <summary>The decoder works on the CPU; each frame is uploaded to the GPU.</summary>
    Software,
}

/// <summary>
/// The Microsoft H.264 decoder (Media Foundation) in low-latency mode, so each frame comes out as soon as it goes in
/// (TRD › Viewer). Every decoded picture is copied on the GPU into <see cref="Frame"/>, the viewer's own NV12 texture,
/// so the decoder gets its surface back at once. A synchronous transform: every call happens on the caller's thread,
/// which needs COM (an MTA thread). Not thread-safe.
/// </summary>
internal sealed class H264Decoder : IDisposable
{
    /// <summary>MF_LOW_LATENCY (mfapi.h); the same ID as CODECAPI_AVLowLatencyMode.</summary>
    private static readonly Guid LowLatencyAttribute = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    // Result codes from the Windows SDK (winerror.h, mferror.h).
    private const int NotImplemented = unchecked((int)0x80004001);   // E_NOTIMPL
    private const int NoMoreTypes = unchecked((int)0xC00D36B9);      // MF_E_NO_MORE_TYPES
    private const int NotAccepting = unchecked((int)0xC00D36B5);     // MF_E_NOTACCEPTING

    /// <summary>MFVideoArea (mfobjects.h): two 4-byte offsets, then the width and height as 4-byte numbers.</summary>
    private const int VideoAreaSize = 16;

    /// <summary>Place of IMFTransform::ProcessOutput in the COM vtable (mftransform.h): after IUnknown's 3 and 22 others.</summary>
    private const int ProcessOutputSlot = 25;

    private static readonly Lazy<Result> MediaFoundationStarted = new(() => MediaFactory.MFStartup());

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IMFActivate _activate;
    private readonly IMFTransform _transform;
    private IMFDXGIDeviceManager? _deviceManager;
    private int _inputStreamId;
    private int _outputStreamId;
    private bool _providesSamples;
    private IMFSample? _outputSample;
    // Software route: how the decoder lays out NV12 in memory, and the texture that carries it to the GPU.
    private int _outputStride;
    private int _outputRows;
    private ID3D11Texture2D? _staging;
    private ID3D11Texture2D? _frame;

    private H264Decoder(IMFActivate activate, string name, DecoderKind kind, ID3D11Device device, ID3D11DeviceContext context)
    {
        _activate = activate;
        Name = name;
        Kind = kind;
        _device = device;
        _context = context;
        _transform = activate.ActivateObject<IMFTransform>();
    }

    public string Name { get; }
    public DecoderKind Kind { get; }

    /// <summary>The last decoded picture, NV12, <see cref="Width"/> × <see cref="Height"/>. Null until the first one.</summary>
    public ID3D11Texture2D? Frame => _frame;

    /// <summary>The picture size the stream says, without the decoder's padding.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Frames that went in and did not come straight back out; above 0 means low latency is not working.</summary>
    public int HeldBack { get; private set; }

    /// <summary>
    /// Finds the H.264 decoder and sets it up for a <paramref name="width"/> × <paramref name="height"/> stream.
    /// Throws if this route does not work on this PC.
    /// </summary>
    public static H264Decoder Create(ID3D11Device device, ID3D11DeviceContext context, DecoderKind kind, int width, int height)
    {
        MediaFoundationStarted.Value.CheckError();
        var (activate, name) = Find();
        var decoder = new H264Decoder(activate, name, kind, device, context);
        try
        {
            decoder.Start(width, height);
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    public static string Describe(DecoderKind kind) => kind == DecoderKind.Hardware ? "hardware (DXVA)" : "software";

    /// <summary>The first decoder Windows lists for H.264 → NV12: the Microsoft H.264 decoder.</summary>
    private static (IMFActivate Activate, string Name) Find()
    {
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var flags = EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter;
        using var all = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, (uint)flags, input, output);
        foreach (var activate in all)
        {
            string name;
            try
            {
                name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
            }
            catch (SharpGenException)
            {
                name = "H.264 decoder";
            }
            // Our own reference, so the decoder outlives the list.
            return (activate.QueryInterface<IMFActivate>(), name);
        }
        throw new InvalidOperationException("No H.264 decoder found (Windows N editions need the Media Feature Pack).");
    }

    private void Start(int width, int height)
    {
        ReadStreamIds();
        using (var attributes = _transform.Attributes)
        {
            if (Kind == DecoderKind.Hardware
                && (attributes.GetUInt32(TransformAttributeKeys.D3D11Aware, out uint aware).Failure || aware == 0))
                throw new NotSupportedException($"{Name} cannot use Direct3D 11.");
            attributes.Set(LowLatencyAttribute, 1u).CheckError();
        }
        if (Kind == DecoderKind.Hardware)
        {
            _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            _deviceManager.ResetDevice(_device).CheckError();
            _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)_deviceManager.NativePointer);
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            input.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | (uint)height).CheckError();
            _transform.SetInputType(_inputStreamId, input, 0);
        }
        SetOutputType();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    /// <summary>
    /// Decodes one H.264 frame (Annex B, as the encoder makes it). True when a picture came out; it is in
    /// <see cref="Frame"/> now (the GPU copy is queued).
    /// </summary>
    public bool Decode(byte[] data, bool keyFrame, TimeSpan at)
    {
        using var sample = InputSample(data, keyFrame, at);
        bool decoded = false;
        try
        {
            _transform.ProcessInput(_inputStreamId, sample, 0);
        }
        catch (SharpGenException error) when (error.ResultCode.Code == NotAccepting)
        {
            // A picture was still waiting: collect it, then the decoder takes the frame.
            decoded = ReadOutput();
            _transform.ProcessInput(_inputStreamId, sample, 0);
        }
        decoded |= ReadOutput();
        if (!decoded) HeldBack++;
        return decoded;
    }

    private static IMFSample InputSample(byte[] data, bool keyFrame, TimeSpan at)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out IntPtr target, out _, out _);
        try
        {
            Marshal.Copy(data, 0, target, data.Length);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = data.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = at.Ticks;
        if (keyFrame) sample.Set(SampleAttributeKeys.CleanPoint, 1u).CheckError();
        return sample;
    }

    /// <summary>Stream IDs are 0 unless the decoder says otherwise (it may not implement GetStreamIDs).</summary>
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

    /// <summary>Picks the decoder's NV12 output type; again whenever the stream changes size.</summary>
    private void SetOutputType()
    {
        for (int index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(_outputStreamId, index);
            }
            catch (SharpGenException error) when (error.ResultCode.Code == NoMoreTypes)
            {
                throw new InvalidOperationException("The decoder offers no NV12 output type.");
            }
            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12) continue;
                _transform.SetOutputType(_outputStreamId, type, 0);
                ReadLayout(type);
            }
            break;
        }

        var info = _transform.GetOutputStreamInfo(_outputStreamId);
        const int provides = (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples);
        _providesSamples = (info.Flags & provides) != 0;
        _outputSample?.Dispose();
        _outputSample = null;
        if (!_providesSamples)
        {
            _outputSample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, _outputStride * _outputRows * 3 / 2));
            _outputSample.AddBuffer(buffer);
        }
    }

    /// <summary>
    /// The decoder's frame size includes its padding (1080 rows become 1088); the picture itself is the minimum display
    /// aperture when the decoder gives one.
    /// </summary>
    private void ReadLayout(IMFMediaType type)
    {
        ulong size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        int frameWidth = (int)(size >> 32);
        _outputRows = (int)(size & 0xFFFFFFFF);
        _outputStride = type.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint stride).Success
            ? Math.Abs((int)stride)
            : frameWidth;

        int width = frameWidth, height = _outputRows;
        if (type.GetBlobSize(MediaTypeAttributeKeys.MinimumDisplayAperture, out uint areaSize).Success && areaSize >= VideoAreaSize)
        {
            var area = type.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture);
            width = BitConverter.ToInt32(area, 8);
            height = BitConverter.ToInt32(area, 12);
        }
        // NV12 textures need even sizes.
        width &= ~1;
        height &= ~1;
        if (width != Width || height != Height)
        {
            _frame?.Dispose();
            _frame = null;
            _staging?.Dispose();
            _staging = null;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Collects every picture the decoder has ready, keeping the newest. Called through the vtable so we own exactly
    /// the sample reference the decoder hands back.
    /// </summary>
    private unsafe bool ReadOutput()
    {
        var processOutput = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, NativeOutputDataBuffer*, uint*, int>)
            (*(IntPtr**)_transform.NativePointer)[ProcessOutputSlot];
        bool decoded = false;
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
                if (result == Vortice.MediaFoundation.ResultCode.TransformNeedMoreInput) return decoded;
                if (result != Vortice.MediaFoundation.ResultCode.TransformStreamChange) result.CheckError();
                // The stream changed (first frame, or a new picture size): take the new NV12 type and ask again.
                SetOutputType();
                continue;
            }

            if (_providesSamples)
            {
                using var sample = new IMFSample(output.Sample);
                Keep(sample);
            }
            else
            {
                Keep(_outputSample!);
            }
            decoded = true;
        }
    }

    private void ResetOutputSample()
    {
        _outputSample!.DeleteAllItems();
        using var buffer = _outputSample.GetBufferByIndex(0);
        buffer.CurrentLength = 0;
    }

    /// <summary>Copies the picture into <see cref="Frame"/>: on the GPU when the decoder gave a texture, else from memory.</summary>
    private void Keep(IMFSample sample)
    {
        using var buffer = sample.GetBufferByIndex(0);
        EnsureFrame();
        using var surface = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
        if (surface is not null)
            KeepGpu(surface);
        else
            KeepCpu(buffer);
    }

    /// <summary>Hardware route: the picture is one slice of the decoder's texture array; copy it on the GPU.</summary>
    private void KeepGpu(IMFDXGIBuffer surface)
    {
        using var texture = new ID3D11Texture2D(surface.GetResource(typeof(ID3D11Texture2D).GUID));
        _context.CopySubresourceRegion(_frame!, 0, 0, 0, 0, texture, surface.SubresourceIndex, new Box(0, 0, 0, Width, Height, 1));
    }

    /// <summary>
    /// Software route: NV12 in memory, Y rows then UV rows, each <see cref="_outputStride"/> long; the UV rows start
    /// after all the decoder's rows (padding included). Copied through a staging texture, without the padding.
    /// </summary>
    private unsafe void KeepCpu(IMFMediaBuffer buffer)
    {
        _staging ??= _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = SampleDescription.Default,
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        buffer.Lock(out IntPtr data, out _, out int length);
        try
        {
            if (length < _outputStride * _outputRows * 3 / 2)
                throw new InvalidOperationException($"The decoder gave {length} bytes for a {_outputStride} × {_outputRows} NV12 frame.");
            var mapped = _context.Map(_staging, 0, MapMode.Write);
            try
            {
                byte* from = (byte*)data;
                byte* to = (byte*)mapped.DataPointer;
                int pitch = (int)mapped.RowPitch;
                for (int row = 0; row < Height; row++)
                    Buffer.MemoryCopy(from + (long)row * _outputStride, to + (long)row * pitch, Width, Width);
                // In a mapped NV12 texture the UV rows follow the Y plane at RowPitch × Height.
                byte* fromUv = from + (long)_outputStride * _outputRows;
                byte* toUv = to + (long)pitch * Height;
                for (int row = 0; row < Height / 2; row++)
                    Buffer.MemoryCopy(fromUv + (long)row * _outputStride, toUv + (long)row * pitch, Width, Width);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }
        finally
        {
            buffer.Unlock();
        }
        _context.CopyResource(_frame!, _staging);
    }

    /// <summary>
    /// The viewer's NV12 texture: a render target as the video processor reads it most widely, and a shader resource
    /// where the GPU allows it (the same choice as the capture side's converter).
    /// </summary>
    private void EnsureFrame()
    {
        if (_frame is not null) return;
        var description = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = SampleDescription.Default,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        try
        {
            _frame = _device.CreateTexture2D(description);
        }
        catch (SharpGenException)
        {
            description.BindFlags = BindFlags.RenderTarget;
            _frame = _device.CreateTexture2D(description);
        }
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
        _activate.ShutdownObject();
        _outputSample?.Dispose();
        _transform.Dispose();
        _activate.Dispose();
        _deviceManager?.Dispose();
        _staging?.Dispose();
        _frame?.Dispose();
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
