namespace Sheron.Win.Tests;

/// <summary>
/// Windows lets a program duplicate a screen only once at a time (a second DuplicateOutput fails with E_INVALIDARG),
/// so every test that opens the screen joins this collection and they run one after another.
/// </summary>
public static class DesktopDuplication
{
    public const string Collection = "Desktop duplication";
}
