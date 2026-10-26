using System.Diagnostics;
using System.Runtime.InteropServices;
using Sheron.Core.Video;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using static Sheron.Win.Encode.Vpl.VplApi;

namespace Sheron.Win.Encode.Vpl;

/// <summary>How the NV12 frame reaches the oneVPL encoder.</summary>
internal enum VplInput
{
    /// <summary>Copy it on the GPU into a surface the encoder hands out (MFXMemory_GetSurfaceForEncode).</summary>
    CopyToEncoderSurface,
    /// <summary>Hand our texture to the encoder (ImportFrameSurface, API 2.10, experimental in v2.16).</summary>
    Import,
}

/// <summary>
/// The Intel encoder route (Plan step 4b): H.264 through Intel oneVPL on the capture device, frames staying on the GPU.
/// Low-power encoding, fastest target usage, async depth 1, no B-frames, constant bitrate (TRD › Video path).
/// All calls happen on the caller's thread; not thread-safe.
/// </summary>
internal sealed unsafe class VplEncoder : IVideoEncoder
{
    private const uint SyncWaitMs = 1000;

    private readonly ID3D11DeviceContext _context;
    private readonly VideoSettings _settings;
    private readonly VplInput _input;
    private readonly IntPtr _loader;
    private readonly IntPtr _session;
    private readonly IntPtr _extParams;       // mfxExtBuffer*[1] → the video signal info below
    private readonly IntPtr _signalInfo;
    private readonly Bitstream* _bitstream;   // native, the runtime writes to it after EncodeFrameAsync returns
    private readonly IntPtr _bitstreamData;
    private readonly Dictionary<IntPtr, ID3D11Texture2D> _encoderTextures = [];
    private IntPtr _memoryInterface;
    private bool _encoderStarted;
    private string _importMode = "";

    private VplEncoder(ID3D11Device device, ID3D11DeviceContext context, uint adapterIndex, VideoSettings settings, VplInput input)
    {
        _context = context;
        _settings = settings;
        _input = input;
        var applied = new List<string>();
        try
        {
            _loader = MFXLoad();
            if (_loader == IntPtr.Zero) throw new InvalidOperationException("MFXLoad failed");
            Filter("mfxImplDescription.Impl", ImplTypeHardware);
            Filter("mfxImplDescription.AccelerationMode", AccelModeViaD3D11);
            Filter("DXGIAdapterIndex", adapterIndex);
            Check(MFXCreateSession(_loader, 0, out _session), "MFXCreateSession");
            Check(MFXVideoCORE_SetHandle(_session, HandleD3D11Device, device.NativePointer), "SetHandle(D3D11 device)");
            Check(MFXQueryVersion(_session, out uint version), "MFXQueryVersion");
            applied.Add($"oneVPL {version >> 16}.{version & 0xFFFF}");

            _signalInfo = Marshal.AllocHGlobal(VideoSignalInfoSize);
            *(VideoSignalInfo*)_signalInfo = new VideoSignalInfo
            {
                BufferId = ExtBuffVideoSignalInfo,
                BufferSize = VideoSignalInfoSize,
                VideoFormat = 5,               // unspecified
                VideoFullRange = 0,            // 16–235
                ColourDescriptionPresent = 1,
                ColourPrimaries = 1,           // BT.709
                TransferCharacteristics = 1,
                MatrixCoefficients = 1,
            };
            _extParams = Marshal.AllocHGlobal(IntPtr.Size);
            *(IntPtr*)_extParams = _signalInfo;

            var wanted = Parameters();
            VideoParam corrected = wanted;
            int query = MFXVideoENCODE_Query(_session, &wanted, &corrected);
            if (query < 0) Check(query, "MFXVideoENCODE_Query");
            int init = MFXVideoENCODE_Init(_session, &wanted);
            Check(init, "MFXVideoENCODE_Init");
            _encoderStarted = true;
            if (init == WarnPartialAcceleration) applied.Add("partly in software ✗");

            VideoParam actual = default;
            actual.ExtParam = IntPtr.Zero;
            Check(MFXVideoENCODE_GetVideoParam(_session, &actual), "MFXVideoENCODE_GetVideoParam");
            applied.Add(actual.LowPower == CodingOptionOn ? "low power ✓" : "low power ✗");
            applied.Add(actual.TargetUsage == TargetUsageBestSpeed ? "fastest ✓" : $"target usage {actual.TargetUsage}");
            applied.Add(actual.RateControlMethod == RateControlCbr ? "constant bitrate ✓" : $"rate control {actual.RateControlMethod}");
            applied.Add($"{actual.TargetKbps * Math.Max(1, (int)actual.BrcParamMultiplier) / 1000.0:0.#} Mbps");
            applied.Add(actual.GopRefDist <= 1 ? "no B-frames ✓" : $"B-frames ✗ ({actual.GopRefDist - 1})");
            applied.Add($"keyframe every {actual.GopPicSize}");
            applied.Add(input == VplInput.Import ? "input: import" : "input: GPU copy");
            if (query == WarnIncompatibleVideoParam) applied.Add("some settings corrected by Intel");

            uint maxLength = (uint)(_settings.Width * _settings.Height * 3 / 2);
            _bitstreamData = Marshal.AllocHGlobal((int)maxLength);
            _bitstream = (Bitstream*)Marshal.AllocHGlobal(BitstreamSize);
            new Span<byte>(_bitstream, BitstreamSize).Clear();
            _bitstream->Data = _bitstreamData;
            _bitstream->MaxLength = maxLength;

            if (input == VplInput.Import)
                Check(MFXVideoCORE_GetHandle(_session, HandleMemoryInterface, out _memoryInterface), "GetHandle(memory interface)");
        }
        catch
        {
            Dispose();
            throw;
        }
        Settings = applied;
    }

    /// <summary>Starts the Intel encoder on the capture device's adapter. Throws if oneVPL is missing or refuses.</summary>
    public static VplEncoder Start(ID3D11Device device, ID3D11DeviceContext context, uint adapterIndex,
        VideoSettings settings, VplInput input) => new(device, context, adapterIndex, settings, input);

    public string Name => "Intel Quick Sync H.264";
    public EncoderKind Kind => EncoderKind.IntelVpl;
    public bool IsHardware => true;
    public BindFlags InputBindFlags => BindFlags.None;
    public IReadOnlyList<string> Settings { get; }
    public int HeldBack { get; private set; }
    public string Details => $"{Name}, {_input}{_importMode}";

    public IReadOnlyList<EncodedFrame> Encode(ID3D11Texture2D nv12, TimeSpan at)
    {
        IntPtr surface = _input == VplInput.Import ? ImportSurface(nv12) : CopyToEncoderSurface(nv12);
        try
        {
            long started = Stopwatch.GetTimestamp();
            _bitstream->DataOffset = 0;
            _bitstream->DataLength = 0;

            int status;
            IntPtr syncPoint;
            while ((status = MFXVideoENCODE_EncodeFrameAsync(_session, IntPtr.Zero, surface, _bitstream, out syncPoint)) == WarnDeviceBusy)
                Thread.Sleep(1);
            if (status == ErrMoreData)
            {
                HeldBack++;
                return [];
            }
            Check(status, "EncodeFrameAsync");
            if (syncPoint == IntPtr.Zero)
            {
                HeldBack++;
                return [];
            }
            Check(MFXVideoCORE_SyncOperation(_session, syncPoint, SyncWaitMs), "SyncOperation");
            double ownMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var data = new byte[_bitstream->DataLength];
            Marshal.Copy(_bitstreamData + (int)_bitstream->DataOffset, data, 0, data.Length);
            bool keyFrame = (_bitstream->FrameType & (FrameTypeIdr | FrameTypeI)) != 0;
            return [new EncodedFrame(data, keyFrame, ownMs)];
        }
        finally
        {
            // The encoder keeps its own reference for as long as it needs the surface.
            ReleaseSurface(surface);
        }
    }

    private VideoParam Parameters()
    {
        int kbps = _settings.BitsPerSecond / 1000;
        ushort multiplier = (ushort)(kbps / ushort.MaxValue + 1);
        return new VideoParam
        {
            AsyncDepth = 1,
            IoPattern = IoPatternInVideoMemory,
            LowPower = CodingOptionOn,
            BrcParamMultiplier = multiplier,
            FourCc = FourCcNv12,
            ChromaFormat = ChromaFormatYuv420,
            PicStruct = PicStructProgressive,
            // Surfaces are 16-aligned; the picture itself is the crop.
            Width = (ushort)Align16(_settings.Width),
            Height = (ushort)Align16(_settings.Height),
            CropW = (ushort)_settings.Width,
            CropH = (ushort)_settings.Height,
            FrameRateExtN = (uint)_settings.FramesPerSecond,
            FrameRateExtD = 1,
            AspectRatioW = 1,
            AspectRatioH = 1,
            CodecId = CodecAvc,
            CodecProfile = ProfileAvcMain,
            TargetUsage = TargetUsageBestSpeed,
            GopPicSize = (ushort)_settings.KeyFrameInterval,
            GopRefDist = 1,
            IdrInterval = 0,
            RateControlMethod = RateControlCbr,
            TargetKbps = (ushort)(kbps / multiplier),
            MaxKbps = (ushort)(kbps / multiplier),
            NumRefFrame = 1,
            NumSlice = 1,
            ExtParam = _extParams,
            NumExtParam = 1,
        };
    }

    private static int Align16(int value) => (value + 15) & ~15;

    /// <summary>Takes a surface from the encoder's pool and copies the frame into its texture on the GPU.</summary>
    private IntPtr CopyToEncoderSurface(ID3D11Texture2D nv12)
    {
        Check(MFXMemory_GetSurfaceForEncode(_session, out IntPtr surface), "MFXMemory_GetSurfaceForEncode");
        try
        {
            Check(GetNativeHandle(surface, out IntPtr resource, out int resourceType), "GetNativeHandle");
            if (resourceType != ResourceDx11Texture)
                throw new InvalidOperationException($"encoder surface is resource type {resourceType}, not a D3D11 texture");
            var target = EncoderTexture(resource);
            var description = target.Description;
            if (description.ArraySize != 1)
                throw new InvalidOperationException($"encoder surface is a texture array of {description.ArraySize}");
            _context.CopySubresourceRegion(target, 0, 0, 0, 0, nv12, 0, new Box(0, 0, 0, _settings.Width, _settings.Height, 1));
            return surface;
        }
        catch
        {
            ReleaseSurface(surface);
            throw;
        }
    }

    /// <summary>Wraps an encoder-owned texture once; our own reference keeps the wrapper valid.</summary>
    private ID3D11Texture2D EncoderTexture(IntPtr resource)
    {
        if (!_encoderTextures.TryGetValue(resource, out var texture))
        {
            Marshal.AddRef(resource);
            texture = new ID3D11Texture2D(resource);
            _encoderTextures[resource] = texture;
        }
        return texture;
    }

    /// <summary>Hands our NV12 texture to the encoder (shared if it can, else copied by the runtime).</summary>
    private IntPtr ImportSurface(ID3D11Texture2D nv12)
    {
        var external = new SurfaceD3D11Tex2D
        {
            SurfaceType = SurfaceTypeD3D11Tex2D,
            SurfaceFlags = SurfaceFlagImportShared | SurfaceFlagImportCopy,
            StructSize = SurfaceD3D11Tex2DSize,
            Version = SurfaceInterfaceVersion,
            Texture2D = nv12.NativePointer,
        };
        Check(ImportFrameSurface(_memoryInterface, SurfaceComponentEncode, &external, out IntPtr surface), "ImportFrameSurface");
        if (_importMode.Length == 0)
            _importMode = external.SurfaceFlags == SurfaceFlagImportShared ? ", imported shared" : $", imported with flags 0x{external.SurfaceFlags:X}";
        return surface;
    }

    private void Filter(string property, uint value)
    {
        IntPtr config = MFXCreateConfig(_loader);
        if (config == IntPtr.Zero) throw new InvalidOperationException("MFXCreateConfig failed");
        Check(MFXSetConfigFilterProperty(config, property, Variant.OfU32(value)), $"filter {property}");
    }

    /// <summary>Warnings (positive) are fine here; errors (negative) throw with the call's name.</summary>
    private static void Check(int status, string call)
    {
        if (status < 0) throw new InvalidOperationException($"{call}: {Describe(status)}");
    }

    public void Dispose()
    {
        foreach (var texture in _encoderTextures.Values) texture.Dispose();
        _encoderTextures.Clear();
        if (_encoderStarted) MFXVideoENCODE_Close(_session);
        if (_session != IntPtr.Zero) MFXClose(_session);
        if (_loader != IntPtr.Zero) MFXUnload(_loader);
        if (_bitstream != null) Marshal.FreeHGlobal((IntPtr)_bitstream);
        if (_bitstreamData != IntPtr.Zero) Marshal.FreeHGlobal(_bitstreamData);
        if (_extParams != IntPtr.Zero) Marshal.FreeHGlobal(_extParams);
        if (_signalInfo != IntPtr.Zero) Marshal.FreeHGlobal(_signalInfo);
    }
}
