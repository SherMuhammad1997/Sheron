using System.Diagnostics;
using Sheron.Core.Capture;
using Sheron.Core.Video;
using Sheron.Win.Encode;

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
    int Reopens,
    string Encoder,
    string EncoderSettings,
    EncodeSnapshot Encode,
    int HeldBack);

/// <summary>
/// Runs <see cref="DesktopDuplicator"/> and the encoder on their own thread, reopens them when Windows takes the
/// screen away, and keeps the figures the capture test window shows.
/// </summary>
internal sealed class CaptureLoop : IDisposable
{
    /// <summary>How long one wait for a change lasts; the thread sleeps inside Windows meanwhile, so a still screen costs no CPU.</summary>
    private const uint WaitMs = 100;
    private const int RetryMs = 250;

    private readonly bool _allowHardwareEncoder;
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CaptureStats _stats = new();
    private readonly EncodeStats _encodeStats = new();
    private readonly object _lock = new();
    private volatile bool _stopping;

    // Used by the capture thread only.
    private Nv12Converter? _converter;
    private H264Encoder? _encoder;

    // Shared with the window: read and written under _lock.
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
    private string _encoderName = "–";
    private string _encoderSettings = "–";
    private int _heldBack;

    /// <param name="allowHardwareEncoder">False uses the software encoder only (<c>--software-encoder</c>).</param>
    public CaptureLoop(bool allowHardwareEncoder = true)
    {
        _allowHardwareEncoder = allowHardwareEncoder;
        _thread = new Thread(Run) { IsBackground = true, Name = "Sheron capture" };
        // Media Foundation needs COM; an MTA thread has it set up when it starts.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public CaptureLoopSnapshot Snapshot()
    {
        var now = _clock.Elapsed;
        lock (_lock)
        {
            return new CaptureLoopSnapshot(_capturing, _status, _adapter, _monitor, _width, _height,
                _stats.Snapshot(now), _pointer, _shape, _shapeChanges, _reopens,
                _encoderName, _encoderSettings, _encodeStats.Snapshot(now), _heldBack);
        }
    }

    private void Run()
    {
        DesktopDuplicator? duplicator = null;
        try
        {
            while (!_stopping)
            {
                if (duplicator is null)
                {
                    duplicator = TryOpen();
                    if (duplicator is null)
                    {
                        Thread.Sleep(RetryMs);
                        continue;
                    }
                    StartEncoder(duplicator);
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
                    StopEncoder();
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
                if (result.Status == CaptureStatus.NewFrame) EncodeLatest(duplicator);
            }
        }
        finally
        {
            StopEncoder();
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

    private void StartEncoder(DesktopDuplicator duplicator)
    {
        var settings = VideoSettings.ForScreen(duplicator.Width, duplicator.Height);
        try
        {
            _encoder = EncoderFactory.Create(duplicator.Device, duplicator.Context, settings, _allowHardwareEncoder,
                out var failures);
            string tried = failures.Count == 0 ? "" : $" (tried first: {string.Join(" · ", failures)})";
            if (_encoder is null)
            {
                SetEncoder("none" + (failures.Count == 0 ? ": no H.264 encoder found" : tried), "–");
                return;
            }
            _converter = new Nv12Converter(duplicator.Device, duplicator.Context, duplicator.Width, duplicator.Height, settings);
            string kind = _encoder.IsHardware ? "hardware" : "software";
            SetEncoder($"{_encoder.Name} · {kind}{tried}", string.Join(" · ", _encoder.Settings));
        }
        catch (Exception error)
        {
            StopEncoder();
            SetEncoder("failed: " + EncoderFactory.Describe(error), "–");
        }
    }

    private void EncodeLatest(DesktopDuplicator duplicator)
    {
        if (_encoder is null || _converter is null || duplicator.Frame is not { } frame) return;
        try
        {
            long started = Stopwatch.GetTimestamp();
            var at = _clock.Elapsed;
            var frames = _encoder.Encode(_converter.Convert(frame), at);
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            foreach (var encoded in frames)
                _encodeStats.AddFrame(at, ms, encoded.Data.Length, encoded.KeyFrame);
            lock (_lock) _heldBack = _encoder.HeldBack;
        }
        catch (Exception error)
        {
            string name = _encoder.Name;
            StopEncoder();
            SetEncoder($"{name} stopped: {EncoderFactory.Describe(error)}", "–");
        }
    }

    private void SetEncoder(string name, string settings)
    {
        lock (_lock)
        {
            _encoderName = name;
            _encoderSettings = settings;
        }
    }

    private void StopEncoder()
    {
        _encoder?.Dispose();
        _encoder = null;
        _converter?.Dispose();
        _converter = null;
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
