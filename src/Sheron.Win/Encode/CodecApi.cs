using System.Runtime.InteropServices;
using Sheron.Core.Video;
using Vortice.MediaFoundation;

namespace Sheron.Win.Encode;

/// <summary>
/// The first methods of the documented Windows <c>ICodecAPI</c> interface (strmif.h), in vtable order.
/// Vortice does not wrap it; only <see cref="SetValue"/> is called.
/// </summary>
[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);
    [PreserveSig] int GetParameterRange(ref Guid api, IntPtr valueMin, IntPtr valueMax, IntPtr steppingDelta);
    [PreserveSig] int GetParameterValues(ref Guid api, IntPtr values, IntPtr valuesCount);
    [PreserveSig] int GetDefaultValue(ref Guid api, IntPtr value);
    [PreserveSig] int GetValue(ref Guid api, IntPtr value);
    [PreserveSig] int SetValue(ref Guid api, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
}

/// <summary>Encoder settings from TRD › Video path, through ICodecAPI. IDs are from the Windows SDK's codecapi.h.</summary>
internal static class CodecApi
{
    private static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid DefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid ScenarioInfo = new("b28a6e64-3ff9-446a-8a4b-0d7a53413236");
    private static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    /// <summary>eAVEncCommonRateControlMode_CBR.</summary>
    private const uint ConstantBitrate = 0;

    /// <summary>
    /// Applies each setting on its own, because encoders support different ones. Returns one line per setting with
    /// ✓ or ✗, for the capture test window. Call before the media types are set.
    /// </summary>
    public static IReadOnlyList<string> Apply(IMFTransform transform, VideoSettings settings, EncoderOptions options)
    {
        object unknown = Marshal.GetObjectForIUnknown(transform.NativePointer);
        try
        {
            if (unknown is not ICodecAPI codec) return ["encoder settings not offered ✗"];
            var applied = new List<string>();
            if (options.LowLatency)
                applied.Add(Set(codec, LowLatencyMode, true, "low latency"));
            if (options.ConstantBitrate)
            {
                applied.Add(Set(codec, RateControlMode, ConstantBitrate, "constant bitrate"));
                applied.Add(Set(codec, MeanBitRate, (uint)settings.BitsPerSecond, $"{settings.BitsPerSecond / 1_000_000.0:0.#} Mbps"));
            }
            if (options.NoBFrames)
                applied.Add(Set(codec, DefaultBPictureCount, 0u, "no B-frames"));
            if (options.KeyFrameInterval)
                applied.Add(Set(codec, GopSize, (uint)settings.KeyFrameInterval, $"keyframe every {settings.KeyFrameInterval}"));
            if (options.QualityVsSpeed >= 0)
            {
                string name = options.QualityVsSpeed == 0 ? "fastest preset" : $"quality {options.QualityVsSpeed}";
                applied.Add(Set(codec, QualityVsSpeed, (uint)options.QualityVsSpeed, name));
            }
            if (options.Scenario != 0)
            {
                string name = options.Scenario == EncoderOptions.DisplayRemoting ? "display remoting" : $"scenario {options.Scenario}";
                applied.Add(Set(codec, ScenarioInfo, options.Scenario, name));
            }
            return applied.Count > 0 ? applied : ["encoder defaults"];
        }
        finally
        {
            Marshal.ReleaseComObject(unknown);
        }
    }

    private static string Set(ICodecAPI codec, Guid api, object value, string label) =>
        codec.SetValue(ref api, ref value) >= 0 ? $"{label} ✓" : $"{label} ✗";
}
