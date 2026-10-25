using System.Diagnostics;
using Sheron.Core.Capture;

namespace Sheron.Win.Capture;

internal sealed record CaptureLoopSnapshot(
    bool Capturing,
    string Status,
    string Adapter,
    string Monitor,
    int Width,
    int Height,
    CaptureSnapshot Stats,
    PointerPosition Pointer,
    PointerShape? Shape,
    int ShapeChanges,
    int Reopens);

/// <summary>
/// Runs <see cref="DesktopDuplicator"/> on its own thread, reopens it when Windows takes it away, and keeps the
/// figures the capture test window shows.
/// </summary>
internal sealed class CaptureLoop : IDisposable
{
    /// <summary>How long one wait for a change lasts; the thread sleeps inside Windows meanwhile, so a still screen costs no CPU.</summary>
    private const uint WaitMs = 100;
    private const int RetryMs = 250;

    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CaptureStats _stats = new();
    private readonly object _lock = new();
    private volatile bool _stopping;

    private bool _capturing;
    private string _status = "Starting";
    private string _adapter = "–";
    private string _monitor = "–";
    private int _width;
    private int _height;
    private PointerPosition _pointer;
    private PointerShape? _shape;
    private int _shapeChanges;
    private int _reopens;

    public CaptureLoop()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Sheron capture" };
        _thread.Start();
    }

    public CaptureLoopSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new CaptureLoopSnapshot(_capturing, _status, _adapter, _monitor, _width, _height,
                _stats.Snapshot(_clock.Elapsed), _pointer, _shape, _shapeChanges, _reopens);
        }
    }

    private void Run()
    {
        DesktopDuplicator? duplicator = null;
        try
        {
            while (!_stopping)
            {
                duplicator ??= TryOpen();
                if (duplicator is null)
                {
                    Thread.Sleep(RetryMs);
                    continue;
                }

                CaptureResult result;
                try
                {
                    result = duplicator.Capture(WaitMs);
                }
                catch (SharpGen.Runtime.SharpGenException error)
                {
                    result = CaptureResult.LostBecause(error.ResultCode);
                }

                if (result.Status == CaptureStatus.Lost)
                {
                    duplicator.Dispose();
                    duplicator = null;
                    lock (_lock)
                    {
                        _capturing = false;
                        _status = result.LostReason ?? "Lost";
                        _reopens++;
                    }
                    continue;
                }
                Record(result, duplicator);
            }
        }
        finally
        {
            duplicator?.Dispose();
        }
    }

    private DesktopDuplicator? TryOpen()
    {
        try
        {
            var duplicator = DesktopDuplicator.OpenMainMonitor();
            lock (_lock)
            {
                _capturing = true;
                _status = "Capturing";
                _adapter = duplicator.AdapterName;
                _monitor = duplicator.MonitorName;
                _width = duplicator.Width;
                _height = duplicator.Height;
            }
            return duplicator;
        }
        catch (Exception error)
        {
            lock (_lock)
            {
                _capturing = false;
                _status = DesktopDuplicator.Explain(error);
            }
            return null;
        }
    }

    private void Record(CaptureResult result, DesktopDuplicator duplicator)
    {
        lock (_lock)
        {
            if (result.Status == CaptureStatus.NewFrame)
            {
                double changed = ChangedArea.Percent(result.ChangedAreas, duplicator.Width, duplicator.Height);
                _stats.AddFrame(_clock.Elapsed, result.FrameMs, changed);
            }
            if (result.Pointer is { } pointer) _pointer = pointer;
            if (result.Shape is { } shape)
            {
                _shape = shape;
                _shapeChanges++;
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
