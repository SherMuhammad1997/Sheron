using Sheron.Core.Capture;
using Sheron.Win.Encode;

namespace Sheron.Win.Capture;

/// <summary>Takes what the capture loop makes, on the capture thread: the loop test window's viewer (Plan step 5).</summary>
internal interface ICaptureSink
{
    /// <param name="width">Picture size the encoder was set up with.</param>
    /// <param name="capturedAt">When the frame was captured, and <paramref name="encodedAt"/> when the encoder gave it
    /// back (<c>Stopwatch</c> timestamps).</param>
    void OnVideo(EncodedFrame frame, int width, int height, long capturedAt, long encodedAt);

    /// <summary>The pointer moved or changed shape; null means that part did not change.</summary>
    void OnPointer(PointerPosition? position, PointerShape? shape);
}
