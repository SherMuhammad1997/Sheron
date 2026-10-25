namespace Sheron.Core.Capture;

/// <summary>Pointer image kinds, numbered as Windows reports them in DXGI_OUTDUPL_POINTER_SHAPE_TYPE.</summary>
public enum PointerShapeKind
{
    /// <summary>1 bit per pixel: an AND mask on top of an XOR mask, so the image is twice <see cref="PointerShape.Height"/> tall.</summary>
    Monochrome = 1,
    /// <summary>32-bit BGRA with alpha.</summary>
    Color = 2,
    /// <summary>32-bit BGRX; the top byte says whether the pixel replaces or XORs the screen.</summary>
    MaskedColor = 4,
}

/// <summary>The pointer image as captured; the viewer draws it itself so the pointer never lags (TRD › Input path).</summary>
public sealed record PointerShape(PointerShapeKind Kind, int Width, int Height, int Pitch, int HotSpotX, int HotSpotY, byte[] Pixels);

/// <summary>Where the pointer is on the captured screen, in pixels.</summary>
public readonly record struct PointerPosition(bool Visible, int X, int Y);
