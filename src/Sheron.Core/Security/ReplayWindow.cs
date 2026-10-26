namespace Sheron.Core.Security;

/// <summary>
/// Which packet counters have come (TRD › Security › Sealed packet): the highest seen and the
/// <see cref="Size"/> before it. A counter seen before, or older than the window, is refused.
/// </summary>
public sealed class ReplayWindow
{
    public const int Size = 2048;

    /// <summary>Bit i set: counter (<see cref="_highest"/> − i) has come.</summary>
    private readonly ulong[] _seen = new ulong[Size / 64];
    private ulong _highest;
    private bool _any;

    /// <summary>True when <paramref name="counter"/> has not come yet and is not too old.</summary>
    public bool IsNew(ulong counter)
    {
        if (!_any || counter > _highest) return true;
        ulong back = _highest - counter;
        return back < Size && (_seen[back / 64] & (1UL << (int)(back % 64))) == 0;
    }

    /// <summary>Records <paramref name="counter"/>; call only after the packet checked out.</summary>
    public void Mark(ulong counter)
    {
        if (!_any || counter > _highest)
        {
            Shift(_any ? counter - _highest : Size);
            _highest = counter;
            _any = true;
            _seen[0] |= 1;
            return;
        }
        ulong back = _highest - counter;
        if (back < Size) _seen[back / 64] |= 1UL << (int)(back % 64);
    }

    /// <summary>Moves every bit <paramref name="by"/> places up (older); bits past the end fall off.</summary>
    private void Shift(ulong by)
    {
        if (by >= Size)
        {
            Array.Clear(_seen);
            return;
        }
        int words = (int)(by / 64), bits = (int)(by % 64);
        for (int i = _seen.Length - 1; i >= 0; i--)
        {
            int from = i - words;
            ulong value = from >= 0 ? _seen[from] << bits : 0;
            if (bits > 0 && from - 1 >= 0) value |= _seen[from - 1] >> (64 - bits);
            _seen[i] = value;
        }
    }
}
