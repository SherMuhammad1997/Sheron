using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Sheron.Core.Capture;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Sheron.Win.Capture;

internal enum CaptureStatus
{
    /// <summary>A new desktop image was copied to <see cref="DesktopDuplicator.Frame"/>.</summary>
    NewFrame,
    /// <summary>Only the pointer moved or changed shape.</summary>
    PointerOnly,
    /// <summary>Nothing changed before the wait ran out: a still screen.</summary>
    NoChange,
    /// <summary>The duplication stopped working (desktop switch, resolution change, lock screen, UAC); open a new one.</summary>
    Lost,
}

/// <param name="CapturedAt">When Windows handed the frame over (<c>Stopwatch</c> timestamp): where the
/// capture-to-display timer starts (TRD › Viewer).</param>
internal sealed record CaptureResult(
    CaptureStatus Status,
    double FrameMs,
    IReadOnlyList<ScreenRect> ChangedAreas,
    PointerPosition? Pointer,
    PointerShape? Shape,
    string? LostReason,
    long CapturedAt = 0)
{
    public static readonly CaptureResult Still = new(CaptureStatus.NoChange, 0, [], null, null, null);

    public static CaptureResult LostBecause(Result error) =>
        new(CaptureStatus.Lost, 0, [], null, null, DesktopDuplicator.Explain(error));
}

/// <summary>
/// Captures the main monitor with DXGI Desktop Duplication (TRD › Video path). Each new image is copied into a GPU
/// texture the encoder reads (Plan step 4); the changed areas and the pointer come with it.
/// Not thread-safe: one capture thread owns it.
/// </summary>
internal sealed class DesktopDuplicator : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    private static readonly uint MoveRectSize = (uint)Unsafe.SizeOf<OutduplMoveRect>();
    private static readonly uint DirtyRectSize = (uint)Unsafe.SizeOf<RawRect>();

    private readonly IDXGIAdapter1 _adapter;
    private readonly IDXGIOutput _output;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private ID3D11Texture2D? _frame;
    private Texture2DDescription _frameDescription;
    private OutduplMoveRect[] _moveRects = [];
    private RawRect[] _dirtyRects = [];

    private DesktopDuplicator(IDXGIAdapter1 adapter, uint adapterIndex, IDXGIOutput output, ID3D11Device device,
        ID3D11DeviceContext context, IDXGIOutputDuplication duplication)
    {
        _adapter = adapter;
        _output = output;
        _device = device;
        _context = context;
        _duplication = duplication;

        var adapterDescription = adapter.Description1;
        AdapterName = adapterDescription.Description;
        AdapterIndex = adapterIndex;
        AdapterVendorId = adapterDescription.VendorId;
        MonitorName = output.Description.DeviceName;
        var mode = duplication.Description.ModeDescription;
        Width = (int)mode.Width;
        Height = (int)mode.Height;
    }

    public string AdapterName { get; }

    /// <summary>The adapter's index in DXGI's list; oneVPL picks its GPU by this number.</summary>
    public uint AdapterIndex { get; }

    /// <summary>PCI vendor of the graphics adapter (0x8086 Intel, 0x10DE NVIDIA, 0x1002 AMD).</summary>
    public uint AdapterVendorId { get; }

    public string MonitorName { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>The device the frames live on; the converter and encoder use it too.</summary>
    public ID3D11Device Device => _device;
    public ID3D11DeviceContext Context => _context;

    /// <summary>The latest desktop image, kept on the GPU. Null until the first new frame.</summary>
    public ID3D11Texture2D? Frame => _frame;

    /// <summary>
    /// Opens the main monitor: the one at desktop position (0, 0). The D3D device is made on that monitor's own
    /// adapter, because duplication fails on any other one (laptops with two GPUs).
    /// </summary>
    public static DesktopDuplicator OpenMainMonitor()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                var place = output.Description;
                if (place.AttachedToDesktop && place.DesktopCoordinates.Left == 0 && place.DesktopCoordinates.Top == 0)
                    return Open(adapter, a, output);
                output.Dispose();
            }
            adapter.Dispose();
        }
        throw new InvalidOperationException("No monitor was found at desktop position (0, 0).");
    }

    /// <summary>Takes ownership of the adapter and output, and disposes them if opening fails.</summary>
    private static DesktopDuplicator Open(IDXGIAdapter1 adapter, uint adapterIndex, IDXGIOutput output)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        try
        {
            // Video support lets the GPU convert colours for the encoder; without it only capture works.
            var created = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, FeatureLevels, out device, out context);
            if (created.Failure)
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, FeatureLevels,
                    out device, out context).CheckError();
            // A hardware encoder uses the device from its own threads (TRD › Video path).
            using (var multithread = context!.QueryInterfaceOrNull<ID3D11Multithread>())
                multithread?.SetMultithreadProtected(true);
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            var duplication = output1.DuplicateOutput(device);
            return new DesktopDuplicator(adapter, adapterIndex, output, device!, context!, duplication);
        }
        catch
        {
            context?.Dispose();
            device?.Dispose();
            output.Dispose();
            adapter.Dispose();
            throw;
        }
    }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for the screen or pointer to change.</summary>
    public CaptureResult Capture(uint timeoutMs)
    {
        var acquired = _duplication.AcquireNextFrame(timeoutMs, out var info, out var resource);
        if (acquired == Vortice.DXGI.ResultCode.WaitTimeout) return CaptureResult.Still;
        if (acquired.Failure) return CaptureResult.LostBecause(acquired);

        long started = Stopwatch.GetTimestamp();
        var status = info.LastPresentTime != 0 ? CaptureStatus.NewFrame : CaptureStatus.PointerOnly;
        IReadOnlyList<ScreenRect> changed = [];
        PointerPosition? pointer = null;
        PointerShape? shape = null;
        try
        {
            if (status == CaptureStatus.NewFrame)
            {
                changed = ReadChangedAreas(info.TotalMetadataBufferSize);
                using var desktop = resource.QueryInterface<ID3D11Texture2D>();
                CopyToFrame(desktop);
            }
            if (info.LastMouseUpdateTime != 0)
            {
                var position = info.PointerPosition;
                pointer = new PointerPosition(position.Visible, position.Position.X, position.Position.Y);
            }
            if (info.PointerShapeBufferSize > 0)
                shape = ReadPointerShape(info.PointerShapeBufferSize);
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }
        return new CaptureResult(status, Stopwatch.GetElapsedTime(started).TotalMilliseconds, changed, pointer, shape, null,
            started);
    }

    /// <summary>A short reason in plain words for the capture test window.</summary>
    public static string Explain(Exception error) =>
        error is SharpGenException com ? Explain(com.ResultCode) : error.Message;

    public static string Explain(Result error)
    {
        if (error == Vortice.DXGI.ResultCode.AccessLost)
            return "Desktop changed (switch, resolution or full-screen app); reopening";
        if (error == Result.AccessDenied)
            return "Paused: lock screen or UAC prompt. Needs the Sheron service (Plan step 17)";
        if (error == Vortice.DXGI.ResultCode.Unsupported)
            return "This graphics setup cannot be captured (DXGI unsupported)";
        if (error == Vortice.DXGI.ResultCode.NotCurrentlyAvailable)
            return "Too many apps are capturing this screen";
        if (error == Vortice.DXGI.ResultCode.SessionDisconnected)
            return "This Windows session is not on screen (Remote Desktop?)";
        return $"{error.ApiCode} (0x{error.Code:X8})";
    }

    /// <summary>Moved and redrawn areas of the new image; a moved area counts at its new place.</summary>
    private List<ScreenRect> ReadChangedAreas(uint metadataBytes)
    {
        var changed = new List<ScreenRect>();
        if (metadataBytes == 0) return changed;

        // Never hand DXGI an empty buffer: it would get a null pointer.
        EnsureCapacity(ref _moveRects, Math.Max(1, (int)(metadataBytes / MoveRectSize)));
        _duplication.GetFrameMoveRects((uint)_moveRects.Length * MoveRectSize, _moveRects, out uint moveBytes).CheckError();
        for (int i = 0; i < moveBytes / MoveRectSize; i++)
        {
            var to = _moveRects[i].DestinationRect;
            changed.Add(new ScreenRect(to.Left, to.Top, to.Right, to.Bottom));
        }

        EnsureCapacity(ref _dirtyRects, Math.Max(1, (int)(metadataBytes / DirtyRectSize)));
        _duplication.GetFrameDirtyRects((uint)_dirtyRects.Length * DirtyRectSize, _dirtyRects, out uint dirtyBytes).CheckError();
        for (int i = 0; i < dirtyBytes / DirtyRectSize; i++)
        {
            var area = _dirtyRects[i];
            changed.Add(new ScreenRect(area.Left, area.Top, area.Right, area.Bottom));
        }
        return changed;
    }

    private PointerShape ReadPointerShape(uint bytes)
    {
        var pixels = new byte[bytes];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            _duplication.GetFramePointerShape(bytes, pin.AddrOfPinnedObject(), out _, out var info).CheckError();
            return new PointerShape((PointerShapeKind)info.Type, (int)info.Width, (int)info.Height, (int)info.Pitch,
                info.HotSpot.X, info.HotSpot.Y, pixels);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>Copies the image into our own texture, so the frame can go back to Windows at once.</summary>
    private void CopyToFrame(ID3D11Texture2D desktop)
    {
        var source = desktop.Description;
        if (_frame is null || _frameDescription.Width != source.Width || _frameDescription.Height != source.Height
            || _frameDescription.Format != source.Format)
        {
            _frame?.Dispose();
            _frameDescription = new Texture2DDescription
            {
                Width = source.Width,
                Height = source.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = source.Format,
                SampleDescription = SampleDescription.Default,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            };
            _frame = _device.CreateTexture2D(_frameDescription);
        }
        _context.CopyResource(_frame, desktop);
    }

    private static void EnsureCapacity<T>(ref T[] buffer, int count)
    {
        if (buffer.Length < count) buffer = new T[count];
    }

    public void Dispose()
    {
        _frame?.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
        _output.Dispose();
        _adapter.Dispose();
    }
}
