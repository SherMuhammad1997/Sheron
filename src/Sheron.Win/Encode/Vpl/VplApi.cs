using System.Runtime.InteropServices;

namespace Sheron.Win.Encode.Vpl;

/// <summary>
/// The parts of Intel's oneVPL C API (v2.16 headers) that the Intel encoder route uses, called through the libvpl
/// dispatcher shipped in third_party/libvpl. Struct sizes and field offsets come from vpl-layout.txt, checked under
/// the Windows x64 ABI; do not change a number here without re-running third_party/libvpl/build-libvpl.sh.
/// </summary>
internal static unsafe class VplApi
{
    private const string Dll = "libvpl";

    // Status codes (mfxdefs.h).
    public const int ErrNone = 0;
    public const int ErrMoreData = -10;
    public const int WarnDeviceBusy = 2;
    public const int WarnPartialAcceleration = 4;
    public const int WarnIncompatibleVideoParam = 5;

    // Values (mfxcommon.h, mfxstructures.h, mfxmemory.h).
    public const ushort VariantVersion = 257;               // MFX_VARIANT_VERSION
    public const int VariantTypeU32 = 5;                    // MFX_VARIANT_TYPE_U32
    public const uint ImplTypeHardware = 2;                 // MFX_IMPL_TYPE_HARDWARE
    public const uint AccelModeViaD3D11 = 0x0300;           // MFX_ACCEL_MODE_VIA_D3D11
    public const int HandleD3D11Device = 3;                 // MFX_HANDLE_D3D11_DEVICE
    public const int HandleMemoryInterface = 1001;          // MFX_HANDLE_MEMORY_INTERFACE
    public const int ResourceDx11Texture = 5;               // MFX_RESOURCE_DX11_TEXTURE
    public const uint CodecAvc = 0x20435641;                // MFX_CODEC_AVC, "AVC "
    public const uint FourCcNv12 = 0x3231564E;              // MFX_FOURCC_NV12, "NV12"
    public const ushort ChromaFormatYuv420 = 1;
    public const ushort PicStructProgressive = 1;
    public const ushort TargetUsageBestSpeed = 7;
    public const ushort RateControlCbr = 1;
    public const ushort CodingOptionOn = 0x10;
    public const ushort IoPatternInVideoMemory = 0x01;
    public const ushort ProfileAvcMain = 77;
    public const ushort FrameTypeI = 0x0001;
    public const ushort FrameTypeIdr = 0x0080;
    public const uint ExtBuffVideoSignalInfo = 0x4E495356;  // MFX_EXTBUFF_VIDEO_SIGNAL_INFO, "VSIN"
    public const int SurfaceTypeD3D11Tex2D = 2;
    public const uint SurfaceFlagImportShared = 0x0010;
    public const uint SurfaceFlagImportCopy = 0x0020;
    public const int SurfaceComponentEncode = 1;
    public const ushort SurfaceInterfaceVersion = 256;      // MFX_SURFACEINTERFACE_VERSION

    // Sizes (vpl-layout.txt).
    public const int VideoParamSize = 208;
    public const int BitstreamSize = 72;
    public const int VideoSignalInfoSize = 20;
    public const int SurfaceD3D11Tex2DSize = 248;

    // Offsets into mfxFrameSurfaceInterface / mfxMemoryInterface (function pointers).
    private const int SurfaceReleaseOffset = 24;
    private const int SurfaceGetNativeHandleOffset = 56;
    private const int MemoryImportFrameSurfaceOffset = 16;

    /// <summary>mfxVariant holding a U32 (16 bytes: Version at 0, Type at 4, Data at 8).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct Variant
    {
        [FieldOffset(0)] public ushort Version;
        [FieldOffset(4)] public int Type;
        [FieldOffset(8)] public uint U32;

        public static Variant OfU32(uint value) => new() { Version = VariantVersion, Type = VariantTypeU32, U32 = value };
    }

    /// <summary>mfxVideoParam with mfxInfoMFX and mfxFrameInfo laid out inside (208 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = VideoParamSize)]
    public struct VideoParam
    {
        [FieldOffset(14)] public ushort AsyncDepth;
        // mfx (mfxInfoMFX) starts at 16.
        [FieldOffset(16 + 28)] public ushort LowPower;
        [FieldOffset(16 + 30)] public ushort BrcParamMultiplier;
        // mfx.FrameInfo (mfxFrameInfo) starts at 16 + 32 = 48.
        [FieldOffset(48 + 32)] public uint FourCc;
        [FieldOffset(48 + 36)] public ushort Width;
        [FieldOffset(48 + 38)] public ushort Height;
        [FieldOffset(48 + 40)] public ushort CropX;
        [FieldOffset(48 + 42)] public ushort CropY;
        [FieldOffset(48 + 44)] public ushort CropW;
        [FieldOffset(48 + 46)] public ushort CropH;
        [FieldOffset(48 + 48)] public uint FrameRateExtN;
        [FieldOffset(48 + 52)] public uint FrameRateExtD;
        [FieldOffset(48 + 58)] public ushort AspectRatioW;
        [FieldOffset(48 + 60)] public ushort AspectRatioH;
        [FieldOffset(48 + 62)] public ushort PicStruct;
        [FieldOffset(48 + 64)] public ushort ChromaFormat;
        // Back in mfxInfoMFX.
        [FieldOffset(16 + 100)] public uint CodecId;
        [FieldOffset(16 + 104)] public ushort CodecProfile;
        [FieldOffset(16 + 106)] public ushort CodecLevel;
        [FieldOffset(16 + 110)] public ushort TargetUsage;
        [FieldOffset(16 + 112)] public ushort GopPicSize;
        [FieldOffset(16 + 114)] public ushort GopRefDist;
        [FieldOffset(16 + 116)] public ushort GopOptFlag;
        [FieldOffset(16 + 118)] public ushort IdrInterval;
        [FieldOffset(16 + 120)] public ushort RateControlMethod;
        [FieldOffset(16 + 122)] public ushort InitialDelayInKB;
        [FieldOffset(16 + 124)] public ushort BufferSizeInKB;
        [FieldOffset(16 + 126)] public ushort TargetKbps;
        [FieldOffset(16 + 128)] public ushort MaxKbps;
        [FieldOffset(16 + 130)] public ushort NumSlice;
        [FieldOffset(16 + 132)] public ushort NumRefFrame;
        // Back in mfxVideoParam.
        [FieldOffset(186)] public ushort IoPattern;
        [FieldOffset(192)] public IntPtr ExtParam;
        [FieldOffset(200)] public ushort NumExtParam;
    }

    /// <summary>mfxBitstream (72 bytes). Kept in native memory, because the runtime writes to it after the call.</summary>
    [StructLayout(LayoutKind.Explicit, Size = BitstreamSize)]
    public struct Bitstream
    {
        [FieldOffset(32)] public ulong TimeStamp;
        [FieldOffset(40)] public IntPtr Data;
        [FieldOffset(48)] public uint DataOffset;
        [FieldOffset(52)] public uint DataLength;
        [FieldOffset(56)] public uint MaxLength;
        [FieldOffset(62)] public ushort FrameType;
    }

    /// <summary>mfxExtVideoSignalInfo (20 bytes): the colour description written into the stream.</summary>
    [StructLayout(LayoutKind.Explicit, Size = VideoSignalInfoSize)]
    public struct VideoSignalInfo
    {
        [FieldOffset(0)] public uint BufferId;
        [FieldOffset(4)] public uint BufferSize;
        [FieldOffset(8)] public ushort VideoFormat;
        [FieldOffset(10)] public ushort VideoFullRange;
        [FieldOffset(12)] public ushort ColourDescriptionPresent;
        [FieldOffset(14)] public ushort ColourPrimaries;
        [FieldOffset(16)] public ushort TransferCharacteristics;
        [FieldOffset(18)] public ushort MatrixCoefficients;
    }

    /// <summary>mfxSurfaceD3D11Tex2D (248 bytes) for ImportFrameSurface: header at 0, interface version at 48, texture at 184.</summary>
    [StructLayout(LayoutKind.Explicit, Size = SurfaceD3D11Tex2DSize)]
    public struct SurfaceD3D11Tex2D
    {
        [FieldOffset(0)] public int SurfaceType;
        [FieldOffset(4)] public uint SurfaceFlags;
        [FieldOffset(8)] public uint StructSize;
        [FieldOffset(48)] public ushort Version;
        [FieldOffset(184)] public IntPtr Texture2D;
    }

    [DllImport(Dll)] public static extern IntPtr MFXLoad();
    [DllImport(Dll)] public static extern void MFXUnload(IntPtr loader);
    [DllImport(Dll)] public static extern IntPtr MFXCreateConfig(IntPtr loader);
    [DllImport(Dll)] public static extern int MFXSetConfigFilterProperty(IntPtr config, [MarshalAs(UnmanagedType.LPStr)] string name, Variant value);
    [DllImport(Dll)] public static extern int MFXCreateSession(IntPtr loader, uint index, out IntPtr session);
    [DllImport(Dll)] public static extern int MFXClose(IntPtr session);
    [DllImport(Dll)] public static extern int MFXQueryVersion(IntPtr session, out uint version);
    [DllImport(Dll)] public static extern int MFXVideoCORE_SetHandle(IntPtr session, int type, IntPtr handle);
    [DllImport(Dll)] public static extern int MFXVideoCORE_GetHandle(IntPtr session, int type, out IntPtr handle);
    [DllImport(Dll)] public static extern int MFXVideoCORE_SyncOperation(IntPtr session, IntPtr syncPoint, uint waitMs);
    [DllImport(Dll)] public static extern int MFXVideoENCODE_Query(IntPtr session, VideoParam* input, VideoParam* output);
    [DllImport(Dll)] public static extern int MFXVideoENCODE_Init(IntPtr session, VideoParam* parameters);
    [DllImport(Dll)] public static extern int MFXVideoENCODE_GetVideoParam(IntPtr session, VideoParam* parameters);
    [DllImport(Dll)] public static extern int MFXVideoENCODE_EncodeFrameAsync(IntPtr session, IntPtr control, IntPtr surface, Bitstream* bitstream, out IntPtr syncPoint);
    [DllImport(Dll)] public static extern int MFXVideoENCODE_Close(IntPtr session);
    [DllImport(Dll)] public static extern int MFXMemory_GetSurfaceForEncode(IntPtr session, out IntPtr surface);

    /// <summary>mfxFrameSurface1::FrameInterface->Release.</summary>
    public static int ReleaseSurface(IntPtr surface)
    {
        var release = (delegate* unmanaged[Cdecl]<IntPtr, int>)FrameInterfaceFunction(surface, SurfaceReleaseOffset);
        return release(surface);
    }

    /// <summary>mfxFrameSurface1::FrameInterface->GetNativeHandle: the surface's own texture, not AddRef'd.</summary>
    public static int GetNativeHandle(IntPtr surface, out IntPtr resource, out int resourceType)
    {
        var getNativeHandle = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int*, int>)FrameInterfaceFunction(surface, SurfaceGetNativeHandleOffset);
        IntPtr handle;
        int type;
        int status = getNativeHandle(surface, &handle, &type);
        resource = handle;
        resourceType = type;
        return status;
    }

    /// <summary>mfxMemoryInterface->ImportFrameSurface (API 2.10, experimental in the v2.16 headers).</summary>
    public static int ImportFrameSurface(IntPtr memoryInterface, int component, SurfaceD3D11Tex2D* external, out IntPtr surface)
    {
        var import = (delegate* unmanaged[Cdecl]<IntPtr, int, SurfaceD3D11Tex2D*, IntPtr*, int>)
            *(IntPtr*)(memoryInterface + MemoryImportFrameSurfaceOffset);
        IntPtr imported;
        int status = import(memoryInterface, component, external, &imported);
        surface = imported;
        return status;
    }

    /// <summary>A function pointer from the surface's mfxFrameSurfaceInterface (FrameInterface is at offset 0).</summary>
    private static IntPtr FrameInterfaceFunction(IntPtr surface, int offset)
    {
        IntPtr frameInterface = *(IntPtr*)surface;
        return *(IntPtr*)(frameInterface + offset);
    }

    /// <summary>Status names for messages (mfxdefs.h).</summary>
    public static string Describe(int status) => status switch
    {
        0 => "MFX_ERR_NONE",
        -1 => "MFX_ERR_UNKNOWN",
        -2 => "MFX_ERR_NULL_PTR",
        -3 => "MFX_ERR_UNSUPPORTED",
        -4 => "MFX_ERR_MEMORY_ALLOC",
        -5 => "MFX_ERR_NOT_ENOUGH_BUFFER",
        -6 => "MFX_ERR_INVALID_HANDLE",
        -7 => "MFX_ERR_LOCK_MEMORY",
        -8 => "MFX_ERR_NOT_INITIALIZED",
        -9 => "MFX_ERR_NOT_FOUND",
        -10 => "MFX_ERR_MORE_DATA",
        -12 => "MFX_ERR_ABORTED",
        -15 => "MFX_ERR_INVALID_VIDEO_PARAM",
        -16 => "MFX_ERR_UNDEFINED_BEHAVIOR",
        -17 => "MFX_ERR_DEVICE_FAILED",
        -21 => "MFX_ERR_GPU_HANG",
        1 => "MFX_WRN_IN_EXECUTION",
        2 => "MFX_WRN_DEVICE_BUSY",
        4 => "MFX_WRN_PARTIAL_ACCELERATION",
        5 => "MFX_WRN_INCOMPATIBLE_VIDEO_PARAM",
        _ => $"mfxStatus {status}",
    };
}
