namespace Sheron.Core.Net;

/// <summary>The first byte of every plain packet (TRD › Packets).</summary>
public enum PacketKind : byte
{
    Video = 1,
    Reliable = 2,
    Ack = 3,
}

public static class PacketLimits
{
    /// <summary>Largest packet on the wire, so packets fit through VPNs and mobile links (TRD › Network).</summary>
    public const int MaxDatagram = 1200;

    /// <summary>Kept for encryption (Plan step 7).</summary>
    public const int SealOverhead = 32;

    /// <summary>Largest plain packet.</summary>
    public const int MaxPlain = MaxDatagram - SealOverhead;
}
