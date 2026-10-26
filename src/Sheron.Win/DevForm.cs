using Sheron.Core;

namespace Sheron.Win;

/// <summary>
/// Base of the developer windows from Plan step 5 on: a heading and two columns of figures drawn on the form, and
/// optionally one child window for video below them (CLAUDE.md › Windows UI: few native controls). The figures are
/// read again every half second.
/// </summary>
internal abstract class DevForm : Form
{
    protected static readonly Color Background = Color.FromArgb(0x0E, 0x14, 0x1B);
    protected static readonly Color Muted = Color.FromArgb(0x9F, 0xB0, 0xC3);
    protected static readonly Color Value = Color.FromArgb(0xE6, 0xED, 0xF3);
    protected static readonly Color Accent = Color.FromArgb(0xF2, 0xA9, 0x3B);
    protected static readonly Color Good = Color.FromArgb(0x3F, 0xB8, 0x71);
    protected static readonly Color Bad = Color.FromArgb(0xE5, 0x53, 0x4B);
    private const TextFormatFlags OneLine = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    private readonly string _heading;
    private readonly int _headerHeight;
    private readonly int _rowsPerColumn;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private readonly CpuMeter _cpu = new();
    private readonly Font _titleFont = new("Segoe UI Semibold", 12f);
    private readonly Font _labelFont = new("Segoe UI", 10f);
    private readonly Font _valueFont = new("Consolas", 10.5f);

    /// <param name="title">Window title after "Sheron — ".</param>
    /// <param name="heading">The line above the figures.</param>
    /// <param name="size">Client size at 96 DPI.</param>
    /// <param name="headerHeight">Height of the figures at 96 DPI; with <paramref name="withVideo"/> the video fills the rest.</param>
    protected DevForm(string title, string heading, Size size, int headerHeight, int rowsPerColumn, bool withVideo)
    {
        _heading = heading;
        _headerHeight = headerHeight;
        _rowsPerColumn = rowsPerColumn;
        if (withVideo) Video = new VideoPanel();

        // Sizes are at 96 DPI; ResumeLayout scales them to the screen's DPI.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = size;
        Text = $"{AppInfo.Name} — {title}";
        BackColor = Background;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        if (Video is not null) Controls.Add(Video);
        ResumeLayout(false);

        _refresh.Tick += (_, _) =>
        {
            CpuPercent = _cpu.Read();
            ReadFigures();
            Invalidate(new Rectangle(0, 0, ClientSize.Width, LogicalToDeviceUnits(_headerHeight)));
        };
    }

    /// <summary>The child window video is drawn into, if this window shows video.</summary>
    protected VideoPanel? Video { get; }

    /// <summary>Sheron's own CPU use over the last 3 s, in percent of this PC.</summary>
    protected double CpuPercent { get; private set; }

    /// <summary>Takes fresh snapshots for <see cref="Rows"/>; called every half second on the UI thread.</summary>
    protected abstract void ReadFigures();

    /// <summary>Label, value and value colour (null: normal); the first column, then the second.</summary>
    protected abstract IEnumerable<(string Label, string Value, Color? Color)> Rows();

    /// <summary>Starts the half-second refresh; call once the work has started.</summary>
    protected void StartRefresh() => _refresh.Start();

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Video is null) return;
        int top = LogicalToDeviceUnits(_headerHeight);
        Video.Bounds = new Rectangle(0, top, ClientSize.Width, Math.Max(1, ClientSize.Height - top));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        int margin = LogicalToDeviceUnits(20);
        int labelWidth = LogicalToDeviceUnits(118);
        int rowHeight = LogicalToDeviceUnits(24);
        int columnWidth = (ClientSize.Width - 2 * margin) / 2;
        int y = LogicalToDeviceUnits(14);

        TextRenderer.DrawText(g, _heading, _titleFont, new Point(margin, y), Accent);
        y += _titleFont.Height + LogicalToDeviceUnits(12);

        var rows = Rows().ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var (label, value, color) = rows[i];
            int left = margin + (i / _rowsPerColumn) * columnWidth;
            int top = y + (i % _rowsPerColumn) * rowHeight;
            int valueWidth = columnWidth - labelWidth - LogicalToDeviceUnits(12);
            TextRenderer.DrawText(g, label, _labelFont, new Rectangle(left, top, labelWidth, rowHeight), Muted, OneLine);
            TextRenderer.DrawText(g, value, _valueFont, new Rectangle(left + labelWidth, top, valueWidth, rowHeight),
                color ?? Value, OneLine);
        }
    }

    /// <summary>Calls <paramref name="action"/> on the UI thread, unless the window is closing.</summary>
    protected void OnUiThread(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window closed meanwhile.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            _cpu.Dispose();
            _titleFont.Dispose();
            _labelFont.Dispose();
            _valueFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The child window video is drawn into; Windows Forms paints nothing on it.</summary>
internal sealed class VideoPanel : Control
{
    public VideoPanel()
    {
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.Selectable, false);
    }
}
