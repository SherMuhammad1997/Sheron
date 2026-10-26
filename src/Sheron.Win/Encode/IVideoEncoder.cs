using Vortice.Direct3D11;

namespace Sheron.Win.Encode;

/// <summary>An H.264 encoder route (TRD › Video path). <see cref="Encode"/> is called from one thread only.</summary>
internal interface IVideoEncoder : IDisposable
{
    string Name { get; }
    EncoderKind Kind { get; }
    bool IsHardware { get; }

    /// <summary>Extra bind flags the encoder needs on its NV12 input textures.</summary>
    BindFlags InputBindFlags { get; }

    /// <summary>One line per setting with ✓ or ✗, for the capture test window.</summary>
    IReadOnlyList<string> Settings { get; }

    /// <summary>Facts that help when this encoder fails, for test messages and the test window.</summary>
    string Details { get; }

    /// <summary>Frames the encoder kept instead of answering at once; above 0 means low latency is not working.</summary>
    int HeldBack { get; }

    /// <summary>
    /// Encodes one NV12 frame and returns what the encoder gave back: normally exactly this frame. An empty list
    /// means the encoder kept the frame (counted in <see cref="HeldBack"/>).
    /// </summary>
    IReadOnlyList<EncodedFrame> Encode(ID3D11Texture2D nv12, TimeSpan at);
}
