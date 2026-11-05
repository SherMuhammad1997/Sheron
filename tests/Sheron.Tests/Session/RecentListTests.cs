using Sheron.Core.Rendezvous;
using Sheron.Core.Session;

namespace Sheron.Tests.Session;

/// <summary>Plan step 15a: the home's Recent list (PRD F2; TRD › Files › Recent list).</summary>
public class RecentListTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("sheron-recent-").FullName;
    private string File => Path.Combine(_folder, "Sheron", "recent.json");
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_newest_comes_first_a_PC_is_listed_once_and_only_10_are_kept()
    {
        var list = RecentList.Load(File);
        for (int i = 0; i < 12; i++) list.Add(RendezvousWire.Show(RendezvousWire.DigitId(i)), $"PC {i}", Start.AddMinutes(i));
        list.Add("0003", "", Start.AddHours(1));

        Assert.Equal(10, list.Items.Count);
        Assert.Equal(("0003", "PC 3"), (list.Items[0].Target, list.Items[0].Name));   // an empty name keeps the old one
        Assert.Equal(["0003", "0011", "0010"], list.Items.Take(3).Select(i => i.Target));
        Assert.DoesNotContain(list.Items, i => i.Target is "0000" or "0001");
        Assert.Equal(RendezvousWire.DigitId(3), list.Items[0].Id);
    }

    [Fact]
    public void It_is_kept_in_its_file_and_IP_addresses_have_no_ID()
    {
        var list = RecentList.Load(File);
        list.Add("1920", "Sher-Laptop", Start);
        list.Add("192.168.1.20:47920", "", Start.AddMinutes(1));
        list.Rename("1920", "Sher's laptop");
        Assert.True(list.Save());

        var again = RecentList.Load(File);
        Assert.Equal(list.Items, again.Items);
        Assert.Null(again.Items[0].Id);
        Assert.Equal("Sher's laptop", again.Items[1].Name);
    }

    [Fact]
    public void A_damaged_file_gives_an_empty_list()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "{ not json");

        Assert.Empty(RecentList.Load(File).Items);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
