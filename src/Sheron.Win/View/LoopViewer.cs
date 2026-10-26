using System.Collections.Concurrent;
using System.Diagnostics;
using Sheron.Core.Capture;
using Sheron.Core.Video;
using Sheron.Win.Capture;
using Sheron.Win.Encode;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Sheron.Win.View;

internal sealed record LoopViewerSnapshot(
    string Status,
    string Adapter,
    string Decoder,
    int VideoWidth,
    int VideoHeight,
    ScreenRect Picture,
    LatencySnapshot Latency,
    string Screen,
    int HeldBack);

/// <summary>
/// The viewer half of the loop test (Plan step 5): takes the capture loop's encoded frames and pointer changes,
/// decodes and draws them in one window on its own thread and device, and times capture to display (TRD › Viewer).
/// </summary>
internal sealed class LoopViewer : ICaptureSink, IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    /// <summary>How long one wait for work lasts; shorter while presents wait to be seen on screen.</summary>
    private const int IdleWaitMs = 50;
    private const int ScreenPollMs = 2;

    private readonly IntPtr _window;
    private readonly DecoderKind[] _kinds;
    private readonly BlockingCollection<object> _inbox = new();
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LatencyStats _latency = new(Stopwatch.Frequency);
    private readonly object _lock = new();
    private volatile bool _stopping;

    // Used by the viewer thread only.
    private readonly PresentTracker _tracker = new();
    private readonly List<FrameTimes> _shown = [];
    private readonly List<string> _decoderFailures = [];
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private VideoRenderer? _renderer;
    private H264Decoder? _decoder;
    /// <summary>Which of <see cref="_kinds"/> to try next; moves on when a decoder fails.</summary>
    private int _kindIndex;
    /// <summary>A decoder can only start on a keyframe.</summary>
    private bool _waitingForKeyFrame = true;
    /// <summary>The video frame on screen now; its present time is that of its first present.</summary>
    private FrameTimes? _frame;
    private PointerImage? _pointerImage;
    private PointerPosition _pointer;

    // Shared with the window: read and written under _lock.
    private int _windowWidth;
    private int _windowHeight;
    private string _status = "Starting";
    private string _adapter = "–";
    private string _decoderName = "–";
    private int _videoWidth;
    private int _videoHeight;
    private ScreenRect _picture;
    private string _screen = "not reported yet";
    private int _heldBack;

    /// <param name="window">The child window the video goes into; <paramref name="width"/> × <paramref name="height"/> its size in pixels.</param>
    /// <param name="allowHardwareDecoder">False uses the software decoder only (<c>--software-decoder</c>).</param>
    public LoopViewer(IntPtr window, int width, int height, bool allowHardwareDecoder = true)
    {
        _window = window;
        _windowWidth = width;
        _windowHeight = height;
        _kinds = allowHardwareDecoder ? [DecoderKind.Hardware, DecoderKind.Software] : [DecoderKind.Software];
        _thread = new Thread(Run) { IsBackground = true, Name = "Sheron viewer" };
        // Media Foundation needs COM; an MTA thread has it set up when it starts.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>The window changed size (pixels); called on the UI thread.</summary>
    public void Resize(int width, int height)
    {
        lock (_lock)
        {
            _windowWidth = width;
            _windowHeight = height;
        }
        Post(Wake.Instance);
    }

    public LoopViewerSnapshot Snapshot()
    {
        var now = _clock.Elapsed;
        lock (_lock)
        {
            return new LoopViewerSnapshot(_status, _adapter, _decoderName, _videoWidth, _videoHeight, _picture,
                _latency.Snapshot(now), _screen, _heldBack);
        }
    }

    void ICaptureSink.OnVideo(EncodedFrame frame, int width, int height, long capturedAt, long encodedAt) =>
        Post(new VideoItem(frame, width, height, capturedAt, encodedAt));

    void ICaptureSink.OnPointer(PointerPosition? position, PointerShape? shape) => Post(new PointerItem(position, shape));

    private void Post(object item)
    {
        if (_stopping) return;
        try
        {
            _inbox.Add(item);
        }
        catch (ObjectDisposedException)
        {
            // Closing: nobody is drawing any more.
        }
    }

    private void Run()
    {
        try
        {
            Open();
            while (!_stopping)
            {
                bool newFrame = false, redraw = false;
                if (_inbox.TryTake(out var item, _tracker.Waiting > 0 ? ScreenPollMs : IdleWaitMs))
                {
                    // Take everything that is waiting; every frame is decoded, only the newest is drawn.
                    do
                    {
                        switch (item)
                        {
                            case VideoItem video:
                                newFrame |= Decode(video);
                                break;
                            case PointerItem pointer:
                                redraw |= SetPointer(pointer);
                                break;
                        }
                    }
                    while (_inbox.TryTake(out item));
                }
                redraw |= ApplyWindowSize();
                if (newFrame || redraw) Show(newFrame);
                ReadScreen();
            }
        }
        catch (Exception error)
        {
            SetStatus($"Stopped: {EncoderFactory.Describe(error)}");
        }
        finally
        {
            _decoder?.Dispose();
            _renderer?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
    }

    /// <summary>
    /// The viewer's own device, as on a second PC (TRD › Viewer): the default graphics adapter, with BGRA and video
    /// support, multithread-protected because the hardware decoder uses it from its own threads.
    /// </summary>
    internal static (ID3D11Device Device, ID3D11DeviceContext Context) CreateDevice()
    {
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            FeatureLevels, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using (var multithread = context.QueryInterfaceOrNull<ID3D11Multithread>())
            multithread?.SetMultithreadProtected(true);
        return (device, context);
    }

    private void Open()
    {
        (_device, _context) = CreateDevice();
        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        {
            lock (_lock) _adapter = adapter.Description.Description;
        }
        int width, height;
        lock (_lock) (width, height) = (_windowWidth, _windowHeight);
        _renderer = new VideoRenderer(_device, _context, _window, width, height);
        SetStatus("Waiting for the first keyframe");
    }

    /// <summary>Decodes one frame. True when a new picture came out.</summary>
    private bool Decode(VideoItem video)
    {
        if (_waitingForKeyFrame && !video.Frame.KeyFrame) return false;
        if (_decoder is null)
        {
            StartDecoder(video.Width, video.Height);
            if (_decoder is null) return false;
        }
        _waitingForKeyFrame = false;
        try
        {
            if (!_decoder.Decode(video.Frame.Data, video.Frame.KeyFrame, _clock.Elapsed))
            {
                lock (_lock) _heldBack = _decoder.HeldBack;
                return false;
            }
            _frame = new FrameTimes(video.CapturedAt, video.EncodedAt, Stopwatch.GetTimestamp(), 0, 0);
            lock (_lock)
            {
                _status = "Showing";
                _videoWidth = _decoder.Width;
                _videoHeight = _decoder.Height;
            }
            return true;
        }
        catch (Exception error)
        {
            // This route does not work on this PC: the next one starts at the next keyframe.
            _decoderFailures.Add($"{H264Decoder.Describe(_decoder.Kind)}: {EncoderFactory.Describe(error)}");
            _decoder.Dispose();
            _decoder = null;
            _kindIndex++;
            _waitingForKeyFrame = true;
            _frame = null;
            SetStatus("Decoder failed; waiting for the next keyframe");
            return false;
        }
    }

    /// <summary>Starts the best decoder route that works, from <see cref="_kindIndex"/> down the list.</summary>
    private void StartDecoder(int width, int height)
    {
        for (; _kindIndex < _kinds.Length; _kindIndex++)
        {
            var kind = _kinds[_kindIndex];
            try
            {
                _decoder = H264Decoder.Create(_device!, _context!, kind, width, height);
                SetDecoder($"{_decoder.Name} · {H264Decoder.Describe(kind)}{TriedFirst()}");
                return;
            }
            catch (Exception error)
            {
                _decoderFailures.Add($"{H264Decoder.Describe(kind)}: {EncoderFactory.Describe(error)}");
            }
        }
        SetDecoder("none" + TriedFirst());
    }

    private bool SetPointer(PointerItem item)
    {
        if (item.Position is { } position) _pointer = position;
        if (item.Shape is { } shape) _pointerImage = PointerImage.From(shape);
        return _frame is not null;
    }

    private bool ApplyWindowSize()
    {
        int width, height;
        lock (_lock) (width, height) = (_windowWidth, _windowHeight);
        if (_renderer is null || width <= 0 || height <= 0 || (width == _renderer.Width && height == _renderer.Height)) return false;
        _renderer.Resize(width, height);
        return _frame is not null;
    }

    /// <summary>Draws the current frame and the pointer, presents it, and starts timing it if it is a new frame.</summary>
    private void Show(bool newFrame)
    {
        if (_renderer is null || _decoder?.Frame is not { } texture || _frame is not { } frame) return;
        _renderer.Draw(texture, _decoder.Width, _decoder.Height, _pointerImage, _pointer);
        long drawn = Stopwatch.GetTimestamp();
        uint count = _renderer.Present();
        if (newFrame)
        {
            frame = frame with { Drawn = drawn, Presented = Stopwatch.GetTimestamp() };
            _frame = frame;
        }
        _tracker.Presented(count, frame);
        lock (_lock) _picture = _renderer.Picture;
    }

    /// <summary>Asks Windows which present is on screen and records the frames that got there.</summary>
    private void ReadScreen()
    {
        if (_renderer is null || _tracker.Waiting == 0) return;
        if (!_renderer.TryReadScreen(out var report, out var mode)) return;
        _shown.Clear();
        int untimed = _tracker.Report(report, _shown);
        var now = _clock.Elapsed;
        foreach (var frame in _shown) _latency.AddShown(now, frame);
        for (int i = 0; i < untimed; i++) _latency.AddUntimed(now);
        lock (_lock) _screen = Describe(mode);
    }

    private static string Describe(FramePresentationMode? mode) => mode switch
    {
        FramePresentationMode.Composed => "composed by Windows (DWM)",
        FramePresentationMode.Overlay => "overlay plane",
        FramePresentationMode.None => "direct (independent flip)",
        FramePresentationMode.CompositionFailure => "composition failed",
        _ => "not reported by Windows",
    };

    private string TriedFirst() =>
        _decoderFailures.Count == 0 ? "" : $" (tried first: {string.Join(" · ", _decoderFailures)})";

    private void SetDecoder(string name)
    {
        lock (_lock) _decoderName = name;
    }

    private void SetStatus(string status)
    {
        lock (_lock) _status = status;
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(2));
        _inbox.Dispose();
    }

    private sealed record VideoItem(EncodedFrame Frame, int Width, int Height, long CapturedAt, long EncodedAt);

    private sealed record PointerItem(PointerPosition? Position, PointerShape? Shape);

    /// <summary>Wakes the viewer thread to look at the window size.</summary>
    private sealed class Wake
    {
        public static readonly Wake Instance = new();
    }
}
