using System.Text.Json;
using Sheron.Core.Rendezvous;

namespace Sheron.Core.Session;

/// <summary>One PC in the Recent list: its ID as shown (or IP address), its name as last seen, and when it was last connected.</summary>
public sealed record RecentPc(string Target, string Name, DateTimeOffset LastUsed)
{
    /// <summary>The PC's ID, when <see cref="Target"/> is one (not an IP address).</summary>
    public uint? Id => RendezvousWire.TryParseId(Target, out uint id) ? id : null;
}

/// <summary>
/// The home window's Recent list (PRD F2; TRD › Files): the last 10 PCs connected to, newest first, kept in one JSON
/// file per Windows user. Written to a new file, then moved over the old one; a file that cannot be read gives an
/// empty list. Not thread-safe: the UI thread owns it.
/// </summary>
public sealed class RecentList
{
    public const int MaxItems = 10;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _path;
    private readonly List<RecentPc> _items;

    private RecentList(string path, List<RecentPc> items)
    {
        _path = path;
        _items = items;
    }

    public IReadOnlyList<RecentPc> Items => _items;

    public static RecentList Load(string path)
    {
        List<RecentPc> items = [];
        try
        {
            if (File.Exists(path))
                items = JsonSerializer.Deserialize<List<RecentPc>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            items = [];
        }
        // Anything odd in the file is left out, not trusted.
        items = items.Where(i => i is { Target.Length: > 0 and <= 64 }).Select(i => i with { Name = PcName.Clean(i.Name) })
            .GroupBy(i => i.Target).Select(g => g.First())
            .OrderByDescending(i => i.LastUsed).Take(MaxItems).ToList();
        return new RecentList(path, items);
    }

    /// <summary>
    /// A session with <paramref name="target"/> (an ID as shown, or an IP address) started: it goes to the top. A name
    /// that is empty keeps the one it had.
    /// </summary>
    public void Add(string target, string name, DateTimeOffset when)
    {
        name = PcName.Clean(name);
        int at = _items.FindIndex(i => i.Target == target);
        if (at >= 0)
        {
            if (name.Length == 0) name = _items[at].Name;
            _items.RemoveAt(at);
        }
        _items.Insert(0, new RecentPc(target, name, when));
        if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
    }

    /// <summary>The PC's name as it said when let in.</summary>
    public void Rename(string target, string name)
    {
        name = PcName.Clean(name);
        int at = _items.FindIndex(i => i.Target == target);
        if (at >= 0 && name.Length > 0) _items[at] = _items[at] with { Name = name };
    }

    /// <summary>Writes the list; false when the file could not be written (the list stays in memory).</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temporary = _path + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_items, Json));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
