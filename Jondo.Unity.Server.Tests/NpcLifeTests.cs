using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class NpcLifeTests
{
    [Fact]
    public void Rows_are_read_clamped_and_filtered()
    {
        var entries = NpcLife.Parse("""
            { "_comment": ["ignored"],
              "npcs": [
                { "npc": 7870, "map": 191105026, "radius": 9, "name": "Astrubien", "lines": ["Bonjour", "  ", "Salut"] },
                { "npc": 6831, "radius": 0, "lines": ["Seulement parler"] },
                { "npc": 1234, "radius": 0 },
                { "map": 1, "radius": 2 }
              ] }
            """);

        Assert.Equal(2, entries.Count);
        var walker = entries[0];
        Assert.Equal(7870, walker.Npc);
        Assert.Equal(191105026, walker.Map);
        Assert.Equal(NpcLife.MaxRadius, walker.Radius);
        Assert.Equal("Astrubien", walker.Name);
        Assert.Equal(new[] { "Bonjour", "Salut" }, walker.Lines);

        Assert.Equal(0, entries[1].Map);
        Assert.Equal(0, entries[1].Radius);
    }

    [Fact]
    public void The_map_row_wins_over_the_any_map_row()
    {
        var entries = NpcLife.Parse("""
            { "npcs": [ { "npc": 5, "radius": 1 }, { "npc": 5, "map": 42, "radius": 3 } ] }
            """);
        Assert.Equal(3, NpcLife.EntryFor(entries, 42, 5)!.Radius);
        Assert.Equal(1, NpcLife.EntryFor(entries, 7, 5)!.Radius);
        Assert.Null(NpcLife.EntryFor(entries, 42, 6));
    }

    [Fact]
    public void The_shipped_file_parses()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "content", "npcs", "life.json");
        if (!File.Exists(path)) return;
        Assert.NotEmpty(NpcLife.Parse(File.ReadAllText(path)));
    }
}
