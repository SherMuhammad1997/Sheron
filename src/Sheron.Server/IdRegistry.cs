using System.Security.Cryptography;
using Sheron.Core.Rendezvous;

namespace Sheron.Server;

/// <summary>
/// The server's one file of devices (TRD › Server › Rendezvous protocol): ID, device public key and last-seen time. The
/// same key always gets the same ID; IDs are random and never reused: 4 digits while any are free, then with letters
/// (PRD decision 4 Nov). Lines with an ID of another form (the 9-digit IDs before 4 Nov) are dropped. Thread-safe.
/// </summary>
internal sealed class IdRegistry
{
    private readonly string? _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, uint> _idByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, (byte[] Key, long LastSeen)> _byId = [];

    /// <param name="path">The registry file; null keeps it in memory only (tests).</param>
    public IdRegistry(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        foreach (string line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !uint.TryParse(parts[0], out uint id) || !RendezvousWire.IsId(id)
                || !long.TryParse(parts[2], out long lastSeen)) continue;
            byte[] key;
            try
            {
                key = Convert.FromHexString(parts[1]);
            }
            catch (FormatException)
            {
                continue;
            }
            if (key.Length != RendezvousWire.PublicKeySize) continue;
            _idByKey[parts[1]] = id;
            _byId[id] = (key, lastSeen);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _byId.Count;
        }
    }

    /// <summary>The ID of this key: the one it had, or a new random one (saved at once).</summary>
    public uint IdFor(byte[] publicKey, DateTimeOffset now)
    {
        string hex = Convert.ToHexString(publicKey);
        lock (_lock)
        {
            if (_idByKey.TryGetValue(hex, out uint known))
            {
                _byId[known] = (publicKey, now.ToUnixTimeSeconds());
                return known;
            }
            uint id = NewId();
            _idByKey[hex] = id;
            _byId[id] = (publicKey, now.ToUnixTimeSeconds());
            SaveLocked();
            return id;
        }
    }

    /// <summary>A free ID at random: one of digits only while any is free, then one with letters.</summary>
    private uint NewId()
    {
        if (_byId.Keys.Count(RendezvousWire.IsDigitsOnly) < RendezvousWire.DigitIds)
        {
            var free = Enumerable.Range(0, RendezvousWire.DigitIds).Select(RendezvousWire.DigitId).Where(id => !_byId.ContainsKey(id)).ToList();
            return free[RandomNumberGenerator.GetInt32(free.Count)];
        }
        uint other;
        do other = (uint)RandomNumberGenerator.GetInt32(1, (int)RendezvousWire.IdCount + 1);
        while (_byId.ContainsKey(other) || RendezvousWire.IsDigitsOnly(other));
        return other;
    }

    public bool Knows(uint id)
    {
        lock (_lock) return _byId.ContainsKey(id);
    }

    public byte[]? KeyOf(uint id)
    {
        lock (_lock) return _byId.TryGetValue(id, out var entry) ? entry.Key : null;
    }

    public void Touch(uint id, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_byId.TryGetValue(id, out var entry)) _byId[id] = (entry.Key, now.ToUnixTimeSeconds());
        }
    }

    /// <summary>Writes the whole file anew: to a new file first, then moved over the old one, so a crash never leaves half a file.</summary>
    public void Save()
    {
        lock (_lock) SaveLocked();
    }

    private void SaveLocked()
    {
        if (_path is null) return;
        string fresh = _path + ".new";
        File.WriteAllLines(fresh, _byId.OrderBy(e => e.Key)
            .Select(e => $"{e.Key} {Convert.ToHexString(e.Value.Key)} {e.Value.LastSeen}"));
        File.Move(fresh, _path, overwrite: true);
    }
}
