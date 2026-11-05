using Sheron.Core;

namespace Sheron.Win.Ui;

internal enum ButtonLook
{
    /// <summary>Amber, dark text: the main action (Connect, Accept).</summary>
    Primary,
    /// <summary>Thin edge, no fill (Deny, Connect on a Recent row).</summary>
    Outline,
    /// <summary>Red edge and text (Disconnect).</summary>
    Danger,
    /// <summary>A raised square with an icon (copy).</summary>
    Icon,
}

/// <summary>A button drawn on its form, not a native control (CLAUDE.md › Windows UI).</summary>
internal sealed class PaintedButton(ButtonLook look, string text, Action click)
{
    public ButtonLook Look { get; } = look;
    public string Text { get; set; } = text;
    public Action Click { get; } = click;
    public Rectangle Bounds { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Visible { get; set; } = true;
    /// <summary>Corner radius at 96 DPI; 999 for a pill.</summary>
    public int Radius { get; set; } = 8;
    public Font? Font { get; set; }
    /// <summary>Fill for <see cref="ButtonLook.Danger"/> when not hovered (the banner's dark red).</summary>
    public Color? Fill { get; set; }
    /// <summary>Draws the icon of an <see cref="ButtonLook.Icon"/> button.</summary>
    public Action<Graphics, Rectangle, Color>? DrawIcon { get; set; }
}

/// <summary>
/// Base of the product's windows (Plan step 15a): sizes at 96 DPI scaled with <see cref="Px"/>, the bundled fonts at
/// this window's DPI, and painted buttons with hover. Native controls only for text boxes (CLAUDE.md › Windows UI).
/// </summary>
internal abstract class PaintedForm : Form
{
    private readonly Dictionary<(bool Mono, int Size, Weight Weight), Font> _fonts = [];
    private PaintedButton? _hover;
    private PaintedButton? _pressed;

    protected PaintedForm()
    {
        Fonts.Load();
        // DPI scaling is not set here: setting it lays the window out at once, before the window's own fields exist
        // (troubleshooting 2026-11-05). Each window calls SizeAt96 at the end of its constructor instead.
        BackColor = Theme.Background;
        ForeColor = Theme.Ink;
        Text = AppInfo.Name;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected List<PaintedButton> Buttons { get; } = [];

    /// <summary>
    /// The client size at 96 DPI, scaled once to the screen's DPI (TRD › Windows UI › DPI). Call it last in the window's
    /// constructor, when everything its layout uses is made.
    /// </summary>
    protected void SizeAt96(int width, int height)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(width, height);
        ResumeLayout(false);
    }

    /// <summary>A length at 96 DPI in this window's pixels.</summary>
    protected int Px(int logical) => LogicalToDeviceUnits(logical);

    protected Rectangle Px(int x, int y, int width, int height) => new(Px(x), Px(y), Px(width), Px(height));

    /// <summary>Plex Sans at <paramref name="size"/> board pixels, scaled to this window; owned by the window.</summary>
    protected Font Sans(int size, Weight weight = Weight.Regular) => Get(false, size, weight);

    protected Font Mono(int size, Weight weight = Weight.Regular) => Get(true, size, weight);

    private Font Get(bool mono, int size, Weight weight)
    {
        if (_fonts.TryGetValue((mono, size, weight), out var font)) return font;
        float pixels = size * DeviceDpi / 96f;
        font = mono ? Fonts.Mono(pixels, weight) : Fonts.Sans(pixels, weight);
        _fonts[(mono, size, weight)] = font;
        return font;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // New fonts first, then the old ones go: text boxes still hold them until OnFontsChanged.
        var old = _fonts.Values.ToList();
        _fonts.Clear();
        OnFontsChanged();
        foreach (var font in old) font.Dispose();
        PerformLayout();
        Invalidate();
    }

    /// <summary>The fonts were made again for a new DPI: give text boxes theirs.</summary>
    protected virtual void OnFontsChanged()
    {
    }

    private void DropFonts()
    {
        foreach (var font in _fonts.Values) font.Dispose();
        _fonts.Clear();
    }

    protected void DrawButtons(Graphics g)
    {
        foreach (var button in Buttons)
            if (button.Visible) DrawButton(g, button);
    }

    protected void DrawButton(Graphics g, PaintedButton b)
    {
        bool hover = b == _hover && b.Enabled;
        float radius = Px(b.Radius);
        var font = b.Font ?? Sans(14);
        switch (b.Look)
        {
            case ButtonLook.Primary:
                Draw.Box(g, b.Bounds, radius, b.Enabled ? hover ? Theme.AccentHover : Theme.Accent : Theme.Line, null);
                Draw.Text(g, b.Text, font, b.Bounds, b.Enabled ? Theme.Background : Theme.Muted, Draw.Centred);
                break;
            case ButtonLook.Outline:
                Draw.Box(g, b.Bounds, radius, hover ? Theme.Raised : null, b.Enabled ? Theme.Edge : Theme.Line, Px(1));
                Draw.Text(g, b.Text, font, b.Bounds, b.Enabled ? Theme.Ink : Theme.Placeholder, Draw.Centred);
                break;
            case ButtonLook.Danger:
                Draw.Box(g, b.Bounds, radius, hover ? Theme.StopShade : b.Fill, Theme.Stop, Px(1));
                Draw.Text(g, b.Text, font, b.Bounds, Theme.StopText, Draw.Centred);
                break;
            case ButtonLook.Icon:
                Draw.Box(g, b.Bounds, radius, hover ? Theme.RaisedEdge : Theme.Raised, Theme.RaisedEdge, Px(1));
                var icon = Rectangle.Inflate(b.Bounds, -Px(12), -Px(12));
                b.DrawIcon?.Invoke(g, icon, Theme.Soft);
                break;
        }
    }

    private PaintedButton? ButtonAt(Point at) => Buttons.LastOrDefault(b => b.Visible && b.Enabled && b.Bounds.Contains(at));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var over = ButtonAt(e.Location);
        if (over == _hover) return;
        if (_hover is not null) Invalidate(_hover.Bounds);
        _hover = over;
        if (over is not null) Invalidate(over.Bounds);
        Cursor = over is null ? CursorAt(e.Location) : Cursors.Hand;
    }

    /// <summary>The pointer over a spot with no button: a hand over other clickable things.</summary>
    protected virtual Cursor CursorAt(Point at) => Cursors.Default;

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is null) return;
        Invalidate(_hover.Bounds);
        _hover = null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) _pressed = ButtonAt(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressed;
        _pressed = null;
        if (e.Button == MouseButtons.Left && pressed is not null && pressed == ButtonAt(e.Location)) pressed.Click();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DropFonts();
        base.Dispose(disposing);
    }
}
