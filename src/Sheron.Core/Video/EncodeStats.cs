namespace Sheron.Core.Video;

/// <summary>
/// Encoder figures for the last second. Encode time is the whole round trip seen by the capture thread; the
/// encoder's own part is from the frame going in to the encoder saying the output is ready.
/// </summary>
public readonly record struct EncodeSnapshot(int FramesPerSecond, double AverageEncodeMs, double MaxEncodeMs, int KilobitsPerSecond,
    int KeyFrames, double AverageEncoderOwnMs);

/// <summary>
/// Rolling one-second encoder figures: frames, time per frame, bitrate and keyframes.
/// The capture thread adds frames; the test window reads snapshots, so every call is locked.
/// </summary>
public sealed class EncodeStats
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly Queue<(TimeSpan At, double EncodeMs, int Bytes, bool KeyFrame, double EncoderOwnMs)> _frames = new();
    private readonly object _lock = new();

    public void AddFrame(TimeSpan at, double encodeMs, int bytes, bool keyFrame, double encoderOwnMs = 0)
    {
        lock (_lock)
        {
            _frames.Enqueue((at, encodeMs, bytes, keyFrame, encoderOwnMs));
            DropOlderThanWindow(at);
        }
    }

    public EncodeSnapshot Snapshot(TimeSpan now)
    {
        lock (_lock)
        {
            DropOlderThanWindow(now);
            if (_frames.Count == 0) return default;

            double total = 0, max = 0, own = 0;
            long bytes = 0;
            int keyFrames = 0;
            foreach (var frame in _frames)
            {
                total += frame.EncodeMs;
                max = Math.Max(max, frame.EncodeMs);
                own += frame.EncoderOwnMs;
                bytes += frame.Bytes;
                if (frame.KeyFrame) keyFrames++;
            }
            int count = _frames.Count;
            return new EncodeSnapshot(count, total / count, max, (int)(bytes * 8 / 1000), keyFrames, own / count);
        }
    }

    private void DropOlderThanWindow(TimeSpan now)
    {
        while (_frames.Count > 0 && now - _frames.Peek().At > Window)
            _frames.Dequeue();
    }
}
