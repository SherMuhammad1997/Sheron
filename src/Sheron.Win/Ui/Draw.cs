using System.Drawing.Drawing2D;

namespace Sheron.Win.Ui;

/// <summary>Drawing helpers for the custom-painted screens (CLAUDE.md › Windows UI): rounded shapes, text, icons, the logo.</summary>
internal static class Draw
{
    public const TextFormatFlags OneLine = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    public const TextFormatFlags Centred = OneLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
    public const TextFormatFlags LeftMiddle = OneLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
    public const TextFormatFlags Wrapped = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    public static GraphicsPath Round(RectangleF area, float radius)
    {
        var path = new GraphicsPath();
        float r = Math.Min(radius, Math.Min(area.Width, area.Height) / 2);
        if (r <= 0)
        {
            path.AddRectangle(area);
            return path;
        }
        float d = 2 * r;
        path.AddArc(area.Left, area.Top, d, d, 180, 90);
        path.AddArc(area.Right - d, area.Top, d, d, 270, 90);
        path.AddArc(area.Right - d, area.Bottom - d, d, d, 0, 90);
        path.AddArc(area.Left, area.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A rounded box: filled with <paramref name="fill"/> (if any) and edged with <paramref name="edge"/> (if any), 1 device pixel inside.</summary>
    public static void Box(Graphics g, Rectangle area, float radius, Color? fill, Color? edge, float edgeWidth = 1)
    {
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var inside = new RectangleF(area.X + edgeWidth / 2, area.Y + edgeWidth / 2, area.Width - edgeWidth, area.Height - edgeWidth);
        using var path = Round(inside, radius);
        if (fill is { } f)
            using (var brush = new SolidBrush(f)) g.FillPath(brush, path);
        if (edge is { } e)
            using (var pen = new Pen(e, edgeWidth)) g.DrawPath(pen, path);
        g.SmoothingMode = smoothing;
    }

    public static void Dot(Graphics g, Point centre, int diameter, Color color)
    {
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(color))
            g.FillEllipse(brush, centre.X - diameter / 2f, centre.Y - diameter / 2f, diameter, diameter);
        g.SmoothingMode = smoothing;
    }

    public static void Text(Graphics g, string text, Font font, Rectangle area, Color color, TextFormatFlags flags = LeftMiddle) =>
        TextRenderer.DrawText(g, text, font, area, color, flags);

    public static Size Measure(string text, Font font) => TextRenderer.MeasureText(text, font, Size.Empty, OneLine);

    /// <summary>
    /// Text with letter spacing, as the boards' small capitals ("THIS PC", 0.12 em): GDI has none, so each letter is
    /// drawn on its own. Returns the width drawn.
    /// </summary>
    public static int Spaced(Graphics g, string text, Font font, Point at, Color color, float spacing)
    {
        float x = at.X;
        foreach (char c in text)
        {
            string letter = c.ToString();
            TextRenderer.DrawText(g, letter, font, new Point((int)Math.Round(x), at.Y), color, OneLine);
            x += Measure(letter, font).Width + spacing;
        }
        return (int)Math.Round(x - spacing - at.X);
    }

    /// <summary>
    /// Logo 10, "Wordmark" (PRD decision 1 Nov; TRD › Windows UI › Logo): "sher", the o drawn as a small screen in amber,
    /// then "n". <paramref name="font"/> is Plex Sans SemiBold; the screen is sized from it as on the logo board.
    /// Returns the width drawn.
    /// </summary>
    public static int Wordmark(Graphics g, Font font, Point at, Color ink, Color accent)
    {
        float em = font.Size;
        var sher = Measure("sher", font);
        TextRenderer.DrawText(g, "sher", font, at, ink, OneLine);
        // On the board (30 px type): the screen is 20 × 16 with a 3 px corner, its stand 8 × 3, 2 px gaps.
        float gap = em * 0.07f;
        float baseline = at.Y + font.FontFamily.GetCellAscent(font.Style) * em / font.FontFamily.GetEmHeight(font.Style);
        var screen = new RectangleF(at.X + sher.Width + gap, baseline - em * 0.667f, em * 0.667f, em * 0.533f);
        var stand = new RectangleF(screen.Left + (screen.Width - em * 0.267f) / 2, screen.Bottom + em * 0.067f, em * 0.267f, em * 0.1f);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(accent))
        {
            using (var path = Round(screen, em * 0.1f)) g.FillPath(brush, path);
            using (var path = Round(stand, em * 0.05f)) g.FillPath(brush, path);
        }
        g.SmoothingMode = smoothing;
        int nAt = (int)Math.Round(screen.Right + gap);
        TextRenderer.DrawText(g, "n", font, new Point(nAt, at.Y), ink, OneLine);
        return nAt + Measure("n", font).Width - at.X;
    }

    /// <summary>The copy icon of the boards (two overlapping squares), in a 24-unit box scaled into <paramref name="area"/>.</summary>
    public static void CopyIcon(Graphics g, Rectangle area, Color color) => Icon(g, area, color, (draw, s) =>
    {
        using var front = Round(new RectangleF(9 * s, 9 * s, 12 * s, 12 * s), 2 * s);
        draw.Path(front);
        draw.Lines([new(15 * s, 5 * s), new(15 * s, 4 * s), new(14 * s, 3 * s), new(4 * s, 3 * s), new(3 * s, 4 * s), new(3 * s, 14 * s), new(4 * s, 15 * s), new(5 * s, 15 * s)]);
    });

    /// <summary>A screen with a stand (the incoming request's icon).</summary>
    public static void ScreenIcon(Graphics g, Rectangle area, Color color) => Icon(g, area, color, (draw, s) =>
    {
        using var screen = Round(new RectangleF(3 * s, 4 * s, 18 * s, 12 * s), 2 * s);
        draw.Path(screen);
        draw.Lines([new(8 * s, 20 * s), new(16 * s, 20 * s)]);
        draw.Lines([new(12 * s, 16 * s), new(12 * s, 20 * s)]);
    });

    /// <summary>The footer's lock.</summary>
    public static void LockIcon(Graphics g, Rectangle area, Color color) => Icon(g, area, color, (draw, s) =>
    {
        using var body = Round(new RectangleF(4 * s, 11 * s, 16 * s, 10 * s), 2 * s);
        draw.Path(body);
        using var shackle = new GraphicsPath();
        shackle.AddLine(8 * s, 11 * s, 8 * s, 7 * s);
        shackle.AddArc(8 * s, 3 * s, 8 * s, 8 * s, 180, 180);
        shackle.AddLine(16 * s, 7 * s, 16 * s, 11 * s);
        draw.Path(shackle);
    });

    private sealed class Pencil(Graphics g, Pen pen, PointF origin)
    {
        public void Path(GraphicsPath path)
        {
            var state = g.Save();
            g.TranslateTransform(origin.X, origin.Y);
            g.DrawPath(pen, path);
            g.Restore(state);
        }

        public void Lines(PointF[] points) => g.DrawLines(pen, points.Select(p => new PointF(p.X + origin.X, p.Y + origin.Y)).ToArray());
    }

    private static void Icon(Graphics g, Rectangle area, Color color, Action<Pencil, float> draw)
    {
        float s = Math.Min(area.Width, area.Height) / 24f;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, Math.Max(1f, 1.8f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        draw(new Pencil(g, pen, new PointF(area.X + (area.Width - 24 * s) / 2, area.Y + (area.Height - 24 * s) / 2)), s);
        g.SmoothingMode = smoothing;
    }
}
