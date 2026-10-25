using SharpGen.Runtime;
using Sheron.Core.Video;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Sheron.Win.Encode;

/// <summary>Finds and starts an H.264 encoder: hardware first, then the Microsoft software encoder (TRD › Video path).</summary>
internal static class EncoderFactory
{
    private static readonly Lazy<Result> MediaFoundationStarted = new(() => MediaFactory.MFStartup());

    /// <param name="allowHardware">False skips hardware encoders (the <c>--software-encoder</c> test switch).</param>
    /// <param name="failures">Encoders that were tried and failed, with the reason.</param>
    public static H264Encoder? Create(ID3D11Device device, ID3D11DeviceContext context, VideoSettings settings,
        bool allowHardware, out IReadOnlyList<string> failures)
    {
        MediaFoundationStarted.Value.CheckError();
        var tried = new List<string>();
        failures = tried;

        if (allowHardware)
        {
            var hardware = StartFirst(Find(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter), true,
                device, context, settings, tried);
            if (hardware is not null) return hardware;
        }
        return StartFirst(Find(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter), false,
            device, context, settings, tried);
    }

    public static string Describe(Exception error) =>
        error is SharpGenException com ? Describe(com.ResultCode) : error.Message;

    public static string Describe(Result result) =>
        $"{(string.IsNullOrEmpty(result.ApiCode) ? "error" : result.ApiCode)} (0x{result.Code:X8})";

    private static H264Encoder? StartFirst(List<(IMFActivate Activate, string Name)> candidates, bool hardware,
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
                encoder = new H264Encoder(activate, name, hardware, device, context, settings);
                encoder.Start();
                started = encoder;
            }
            catch (Exception error)
            {
                tried.Add($"{name}: {Describe(error)}");
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

    /// <summary>Encoders that take NV12 and make H.264, best first.</summary>
    private static List<(IMFActivate Activate, string Name)> Find(EnumFlag flags)
    {
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        var found = new List<(IMFActivate, string)>();
        using var all = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, (uint)flags, input, output);
        foreach (var activate in all)
        {
            // Our own reference, so the encoder outlives the list.
            found.Add((activate.QueryInterface<IMFActivate>(), NameOf(activate)));
        }
        return found;
    }

    private static string NameOf(IMFActivate activate)
    {
        try
        {
            return activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
        }
        catch (SharpGenException)
        {
            return "Unnamed H.264 encoder";
        }
    }
}
