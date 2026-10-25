namespace Sheron.Win.Encode;

/// <summary>
/// Which settings an encoder gets. <see cref="Default"/> is the TRD setup; the other combinations exist to find
/// what a fussy hardware encoder accepts (Windows test <c>Hardware_encoder_settings_check</c>, Plan step 4).
/// </summary>
/// <param name="OfferedTypes">Start from the media types the encoder offers, instead of types we build ourselves.</param>
/// <param name="ColourAttributes">Mark the types BT.709, 16–235.</param>
/// <param name="NoBFrames">ICodecAPI: no B-frames.</param>
/// <param name="LowLatency">MF_LOW_LATENCY and ICodecAPI low-latency mode.</param>
/// <param name="ConstantBitrate">ICodecAPI: constant bitrate at the target rate.</param>
/// <param name="KeyFrameInterval">ICodecAPI and the output type: keyframe every N frames.</param>
/// <param name="Level">H.264 level as eAVEncH264VLevel (51 = 5.1); 0 lets the encoder choose.</param>
internal sealed record EncoderOptions(
    bool OfferedTypes = false,
    bool ColourAttributes = true,
    bool NoBFrames = true,
    bool LowLatency = true,
    bool ConstantBitrate = true,
    bool KeyFrameInterval = true,
    uint Level = 0)
{
    public static readonly EncoderOptions Default = new();

    /// <summary>Nothing beyond size, frame rate, profile and bitrate.</summary>
    public static readonly EncoderOptions Bare = new(OfferedTypes: false, ColourAttributes: false, NoBFrames: false,
        LowLatency: false, ConstantBitrate: false, KeyFrameInterval: false);
}
