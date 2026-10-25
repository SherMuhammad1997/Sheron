using SharpGen.Runtime;
using Sheron.Core.Video;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Sheron.Win.Encode;

/// <summary>Finds and starts an H.264 encoder of one <see cref="EncoderKind"/> (TRD › Video path).</summary>
internal static class EncoderFactory
{
    private static readonly Lazy<Result> MediaFoundationStarted = new(() => MediaFactory.MFStartup());

    /// <summary>The order the capture loop tries: hardware with GPU input, hardware with memory input, software.</summary>
    public static readonly EncoderKind[] AllKinds = [EncoderKind.HardwareGpu, EncoderKind.HardwareMemory, EncoderKind.Software];

    /// <param name="adapterVendorId">The capture adapter's vendor: a hardware encoder of another GPU cannot use its frames.</param>
    /// <param name="failures">Encoders that were tried and failed, with the reason.</param>
    public static H264Encoder? Create(ID3D11Device device, ID3D11DeviceContext context, VideoSettings settings,
        EncoderKind kind, uint adapterVendorId, out IReadOnlyList<string> failures)
    {
        MediaFoundationStarted.Value.CheckError();
        var tried = new List<string>();
        failures = tried;

        var candidates = kind == EncoderKind.Software
            ? Find(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter, null)
            : Find(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter, $"VEN_{adapterVendorId:X4}");
        if (candidates.Count == 0) tried.Add($"{Describe(kind)}: none found");
        return StartFirst(candidates, kind, device, context, settings, tried);
    }

    public static string Describe(EncoderKind kind) => kind switch
    {
        EncoderKind.HardwareGpu => "hardware, GPU input",
        EncoderKind.HardwareMemory => "hardware, memory input",
        _ => "software",
    };

    public static string Describe(Exception error) =>
        error is SharpGenException com ? Describe(com.ResultCode) : error.Message;

    public static string Describe(Result result) =>
        $"{(string.IsNullOrEmpty(result.ApiCode) ? "error" : result.ApiCode)} (0x{result.Code:X8})";

    private static H264Encoder? StartFirst(List<(IMFActivate Activate, string Name)> candidates, EncoderKind kind,
        ID3D11Device device, ID3D11DeviceContext context, VideoSettings settings, List<string> tried)
    {
        H264Encoder? started = null;
        foreach (var (activate, name) in candidates)
        {
            if (started is not null)
            {
                activate.Dispose();
                continue;
            }

            H264Encoder? encoder = null;
            try
            {
                encoder = new H264Encoder(activate, name, kind, device, context, settings);
                encoder.Start();
                started = encoder;
            }
            catch (Exception error)
            {
                tried.Add($"{name} ({Describe(kind)}): {Describe(error)}");
                if (encoder is not null)
                {
                    encoder.Dispose();
                }
                else
                {
                    activate.ShutdownObject();
                    activate.Dispose();
                }
            }
        }
        return started;
    }

    /// <summary>Encoders that take NV12 and make H.264, best first; hardware ones only for the given vendor.</summary>
    private static List<(IMFActivate Activate, string Name)> Find(EnumFlag flags, string? vendor)
    {
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        var found = new List<(IMFActivate, string)>();
        using var all = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, (uint)flags, input, output);
        foreach (var activate in all)
        {
            if (vendor is not null && ReadString(activate, TransformAttributeKeys.MftEnumHardwareVendorIdAttribute) is { } vendorId
                && !vendorId.Equals(vendor, StringComparison.OrdinalIgnoreCase))
                continue;
            // Our own reference, so the encoder outlives the list.
            found.Add((activate.QueryInterface<IMFActivate>(), ReadString(activate, TransformAttributeKeys.MftFriendlyNameAttribute) ?? "Unnamed H.264 encoder"));
        }
        return found;
    }

    private static string? ReadString(IMFActivate activate, Guid key)
    {
        try
        {
            return activate.GetString(key);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }
}
