using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Sheron.Core.Capture;
using Sheron.Core.Video;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Sheron.Win.View;

/// <summary>
/// Shows decoded NV12 pictures in one window (TRD › Viewer): flip-model swap chain, the Direct3D 11 video processor
/// for colour and scaling, the pointer drawn on top. Also reads back when Windows put each present on screen, for the
/// capture-to-display timer. Used by the viewer thread only.
/// </summary>
internal sealed class VideoRenderer : IDisposable
{
    /// <summary>Control Room background (TRD › Windows UI) around the picture.</summary>
    private static readonly VideoColor Background = new()
    {
        Rgba = new VideoColorRgba { R = 0x0E / 255f, G = 0x14 / 255f, B = 0x1B / 255f, A = 1f },
    };

    private const int BufferCount = 2;
    private const SwapChainFlags Flags = SwapChainFlags.FrameLatencyWaitableObject;

    /// <summary>Place of IDXGISwapChainMedia::GetFrameStatisticsMedia in the COM vtable (dxgi1_3.h): right after IUnknown.</summary>
    private const int MediaStatisticsSlot = 3;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IntPtr _ready;
    private readonly PointerOverlay _pointer;
    private IDXGISwapChainMedia? _media;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private (int VideoWidth, int VideoHeight, int Width, int Height) _processorSize;
    private IntPtr _inputTexture;
    private ID3D11VideoProcessorInputView? _inputView;

    public VideoRenderer(ID3D11Device device, ID3D11DeviceContext context, IntPtr window, int width, int height)
    {
        _device = device;
        _context = context;
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext>();
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _swapChain = factory.CreateSwapChainForHwnd(device, window, new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = SampleDescription.Default,
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = Flags,
        });
        // Sheron handles its own window keys; DXGI must not switch to full screen on Alt+Enter.
        factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAll);
        using (var swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>())
        {
            // At most one frame waits to be shown: the newest picture is always the next one on screen.
            swapChain2.MaximumFrameLatency = 1;
            _ready = swapChain2.FrameLatencyWaitableObject;
        }
        _media = _swapChain.QueryInterfaceOrNull<IDXGISwapChainMedia>();
        _pointer = new PointerOverlay(device);
    }

    /// <summary>Back buffer size: the window's client area in pixels.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Where the picture went in the window last time (inside the background bars).</summary>
    public ScreenRect Picture { get; private set; }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        // Nothing may hold the back buffer while it is resized.
        _context.ClearState();
        _context.Flush();
        _swapChain.ResizeBuffers(BufferCount, (uint)width, (uint)height, Format.Unknown, Flags).CheckError();
        Width = width;
        Height = height;
    }

    /// <summary>Waits until the swap chain can take a frame without queueing it behind another.</summary>
    public bool WaitUntilReady(int timeoutMs) => WaitForSingleObjectEx(_ready, (uint)timeoutMs, true) == 0;

    /// <summary>
    /// Draws the picture, fitted to the window, then the pointer if it is visible. <paramref name="position"/> is the
    /// top-left corner of the pointer image on the captured screen, as Desktop Duplication reports it.
    /// </summary>
    public void Draw(ID3D11Texture2D nv12, int videoWidth, int videoHeight, PointerImage? pointer, PointerPosition position)
    {
        SetUpProcessor(videoWidth, videoHeight);
        if (_inputView is null || _inputTexture != nv12.NativePointer)
        {
            _inputView?.Dispose();
            _inputView = _videoDevice.CreateVideoProcessorInputView(nv12, _enumerator, new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            });
            _inputTexture = nv12.NativePointer;
        }

        // Buffer 0 is always the one to draw into; views on it are cheap, so they are made fresh each frame.
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        using (var output = _videoDevice.CreateVideoProcessorOutputView(backBuffer, _enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D }))
        {
            var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView };
            _videoContext.VideoProcessorBlt(_processor, output, 0, 1, [stream]).CheckError();
        }

        if (pointer is null || !position.Visible) return;
        double scale = (double)Picture.Width / videoWidth;
        using var target = _device.CreateRenderTargetView(backBuffer);
        _pointer.Draw(_context, target, pointer, Picture.Left + position.X * scale, Picture.Top + position.Y * scale, scale, Picture);
    }

    /// <summary>Hands the frame to Windows, to show at the next screen refresh. Returns its present count.</summary>
    public uint Present()
    {
        _swapChain.Present(0, PresentFlags.None).CheckError();
        return _swapChain.LastPresentCount;
    }

    /// <summary>
    /// Reads DXGI frame statistics: which present is on screen and since which refresh. <paramref name="mode"/> says
    /// how Windows showed it, where Windows reports that (IDXGISwapChainMedia).
    /// </summary>
    public unsafe bool TryReadScreen(out ScreenReport report, out FramePresentationMode? mode)
    {
        report = default;
        mode = null;
        if (_media is not null)
        {
            // Called through the vtable: this is asked hundreds of times a second and must not throw.
            FrameStatisticsMedia media;
            var getStatistics = (delegate* unmanaged[Stdcall]<IntPtr, FrameStatisticsMedia*, int>)
                (*(IntPtr**)_media.NativePointer)[MediaStatisticsSlot];
            var result = new Result(getStatistics(_media.NativePointer, &media));
            if (result.Success)
            {
                report = new ScreenReport(media.PresentCount, media.PresentRefreshCount, media.SyncRefreshCount, media.SyncQPCTime);
                mode = media.CompositionMode;
                return true;
            }
            if (result != Vortice.DXGI.ResultCode.FrameStatisticsDisjoint)
            {
                // This swap chain does not give media statistics: use the plain ones from now on.
                _media.Dispose();
                _media = null;
            }
        }

        if (_swapChain.GetFrameStatistics(out var statistics).Failure) return false;
        report = new ScreenReport(statistics.PresentCount, statistics.PresentRefreshCount, statistics.SyncRefreshCount, statistics.SyncQPCTime);
        return true;
    }

    /// <summary>
    /// One video processor per picture and window size: BT.709 limited-range NV12 in, full-range RGB out, the picture
    /// fitted to the window, keeping its shape, on the background colour.
    /// </summary>
    [MemberNotNull(nameof(_enumerator), nameof(_processor))]
    private void SetUpProcessor(int videoWidth, int videoHeight)
    {
        if (_processor is not null && _enumerator is not null && _processorSize == (videoWidth, videoHeight, Width, Height)) return;
        _inputView?.Dispose();
        _inputView = null;
        _processor?.Dispose();
        _enumerator?.Dispose();

        var rate = new Rational(60, 1);
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = rate,
            InputWidth = (uint)videoWidth,
            InputHeight = (uint)videoHeight,
            OutputFrameRate = rate,
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            Usage = VideoUsage.OptimalSpeed,
        });
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        _processorSize = (videoWidth, videoHeight, Width, Height);

        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { RGB_Range = 0 });
        _videoContext.VideoProcessorSetOutputBackgroundColor(_processor, false, Background);

        Picture = Letterbox.Fit(videoWidth, videoHeight, Width, Height);
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, new RawRect(0, 0, videoWidth, videoHeight));
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true,
            new RawRect(Picture.Left, Picture.Top, Picture.Right, Picture.Bottom));
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, Width, Height));
    }

    public void Dispose()
    {
        _inputView?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _pointer.Dispose();
        _media?.Dispose();
        CloseHandle(_ready);
        _swapChain.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, [MarshalAs(UnmanagedType.Bool)] bool alertable);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
