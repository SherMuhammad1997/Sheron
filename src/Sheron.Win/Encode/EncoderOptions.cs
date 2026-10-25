namespace Sheron.Win.Encode;

/// <summary>
/// Which settings an encoder gets and how it is driven. <see cref="Default"/> is the TRD setup; the other
/// combinations exist to find what a fussy hardware encoder accepts (Windows test
/// <c>Hardware_encoder_settings_check</c>, Plan step 4).
/// </summary>
/// <param name="OfferedTypes">Start from the media types the encoder offers, instead of types we build ourselves.</param>
/// <param name="ColourAttributes">Mark the types BT.709, 16–235.</param>
/// <param name="NoBFrames">ICodecAPI: no B-frames.</param>
/// <param name="LowLatency">ICodecAPI: low-latency mode.</param>
/// <param name="ConstantBitrate">ICodecAPI: constant bitrate at the target rate.</param>
/// <param name="KeyFrameInterval">ICodecAPI and the output type: keyframe every N frames.</param>
/// <param name="Level">H.264 level as eAVEncH264VLevel (51 = 5.1); 0 lets the encoder choose.</param>
/// <param name="LowLatencyAttribute">MF_LOW_LATENCY on the encoder's attributes.</param>
/// <param name="Scenario">ICodecAPI AVScenarioInfo; 1 = display remoting (the default: Intel Quick Sync fails on its
/// first output without it), 0 leaves it unset.</param>
/// <param name="CallerAllocatesOutput">Hand the encoder our own output sample even if it offers to provide one.</param>
/// <param name="VorticeProcessOutput">Collect output through Vortice's ProcessOutput instead of the direct COM call.</param>
/// <param name="AlignedInput">Memory input: allocate the frame buffer with the alignment the encoder asks for.</param>
/// <param name="QualityVsSpeed">ICodecAPI AVEncCommonQualityVsSpeed, 0 (fastest) to 100 (best quality); -1 leaves it unset.</param>
/// <param name="SampleDuration">Give each input sample its duration (one frame at the set frame rate).</param>
internal sealed record EncoderOptions(
    bool OfferedTypes = false,
    bool ColourAttributes = true,
    bool NoBFrames = true,
    bool LowLatency = true,
    bool ConstantBitrate = true,
    bool KeyFrameInterval = true,
    uint Level = 0,
    bool LowLatencyAttribute = true,
    uint Scenario = EncoderOptions.DisplayRemoting,
    bool CallerAllocatesOutput = false,
    bool VorticeProcessOutput = false,
    bool AlignedInput = false,
    int QualityVsSpeed = 0,
    bool SampleDuration = true)
{
    /// <summary>eAVScenarioInfo_DisplayRemoting (codecapi.h).</summary>
    public const uint DisplayRemoting = 1;

    public static readonly EncoderOptions Default = new();

    /// <summary>Nothing beyond size, frame rate, profile and bitrate.</summary>
    public static readonly EncoderOptions Bare = new(OfferedTypes: false, ColourAttributes: false, NoBFrames: false,
        LowLatency: false, ConstantBitrate: false, KeyFrameInterval: false, LowLatencyAttribute: false, Scenario: 0,
        QualityVsSpeed: -1);
}
