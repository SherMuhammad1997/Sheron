namespace Sheron.Core.Video;

/// <summary>Encoder figures for the last second.</summary>
public readonly record struct EncodeSnapshot(int FramesPerSecond, double AverageEncodeMs, double MaxEncodeMs, int KilobitsPerSecond, int KeyFrames);

/// <summary>
/// Rolling one-second encoder figures: frames, time per frame, bitrate and keyframes.
/// The capture thread adds frames; the test window reads snapshots, so every call is locked.
/// </summary>
public sealed class EncodeStats
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly Queue<(TimeSpan At, double EncodeMs, int Bytes, bool KeyFrame)> _frames = new();
    private readonly object _lock = new();

    public void AddFrame(TimeSpan at, double encodeMs, int bytes, bool keyFrame)
    {
        lock (_lock)
        {
            _frames.Enqueue((at, encodeMs, bytes, keyFrame));
            DropOlderThanWindow(at);
        }
    }

    public EncodeSnapshot Snapshot(TimeSpan now)
    {
        lock (_lock)
        {
            DropOlderThanWindow(now);
            if (_frames.Count == 0) return default;

            double total = 0, max = 0;
            long bytes = 0;
            int keyFrames = 0;
            foreach (var frame in _frames)
            {
                total += frame.EncodeMs;
                max = Math.Max(max, frame.EncodeMs);
                bytes += frame.Bytes;
                if (frame.KeyFrame) keyFrames++;
            }
            return new EncodeSnapshot(_frames.Count, total / _frames.Count, max, (int)(bytes * 8 / 1000), keyFrames);
        }
    }

    private void DropOlderThanWindow(TimeSpan now)
    {
        while (_frames.Count > 0 && now - _frames.Peek().At > Window)
            _frames.Dequeue();
    }
}
