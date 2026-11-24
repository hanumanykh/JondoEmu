using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>content/world/events.json as Managers.WorldHappenings reads it.</summary>
public sealed class WorldHappeningsTests
{
    private static WorldHappenings.Settings Read(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"world-events-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try { return WorldHappenings.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Bosses_escorts_timings_and_texts_are_read()
    {
        var s = Read("""
            { "_comment": ["x"], "enabled": true,
              "bosses": { "minMinutes": 10, "maxMinutes": 20, "lifetimeMinutes": 7, "levelSpread": 4,
                          "sighted": "Vu {name} à {zone}",
                          "list": [ { "monster": 147, "grade": 1, "level": 30, "name": "Bouftou Royal",
                                      "escort": [ { "monster": 101, "grade": 4 } ], "beaten": "Fini" },
                                    { "monster": 0, "name": "sans monstre" },
                                    { "monster": 5 } ] },
              "zones": { "enabled": false, "everyMinutes": 30 } }
            """);

        Assert.True(s.Enabled);
        Assert.Equal((10, 20, 7, 4), (s.BossMinMinutes, s.BossMaxMinutes, s.BossLifetimeMinutes, s.LevelSpread));
        var boss = Assert.Single(s.Bosses);
        Assert.Equal((147, 1, 30, "Bouftou Royal"), (boss.Monster, boss.Grade, boss.Level, boss.Name));
        Assert.Equal(new[] { (101, 4) }, boss.Escort);
        Assert.Equal("Fini", boss.Beaten);
        Assert.Null(boss.Sighted);
        Assert.Equal("Vu Bouftou Royal à Cania", WorldHappenings.Fill(s.SightedText, boss.Name, "Cania"));
        Assert.False(s.ZonesEnabled);
        Assert.Equal(30, s.ZoneEveryMinutes);
    }

    [Fact]
    public void Missing_keys_take_their_defaults_and_max_never_falls_below_min()
    {
        var s = Read("""{ "bosses": { "minMinutes": 50, "maxMinutes": 5 } }""");
        Assert.True(s.Enabled && s.BossesEnabled && s.ZonesEnabled);
        Assert.Equal(50, s.BossMaxMinutes);
        Assert.Equal(60, s.ZoneEveryMinutes);
        Assert.Empty(s.Bosses);
        Assert.Contains("{zone}", s.SightedText);
    }

    [Fact]
    public void The_authored_file_parses_with_its_bosses()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "content", "world", "events.json"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var s = WorldHappenings.Load(Path.Combine(dir!.FullName, "content", "world", "events.json"));
        Assert.InRange(s.Bosses.Count, 4, 6);
        Assert.All(s.Bosses, b => Assert.False(string.IsNullOrWhiteSpace(b.Name)));
        Assert.Equal("Un Bouftou Royal a été aperçu près de Cania !",
                     WorldHappenings.Fill(s.SightedText, "Bouftou Royal", "Cania"));
    }
}
