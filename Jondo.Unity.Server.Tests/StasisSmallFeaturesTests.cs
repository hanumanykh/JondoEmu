using System;
using System.Collections.Generic;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Managers.Economy;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The smaller Stasis pieces: the dungeon search bonus, «salle complète», the Recrue buff, the
/// archmonster training zones' settings and the F9 window's tabs.
/// </summary>
[Collection("Farm settings")]
public sealed class StasisSmallFeaturesTests : IDisposable
{
    private readonly DateTime _now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    public StasisSmallFeaturesTests()
    {
        Farm.Current = new Farm.Settings();
        Farm.Clock = () => _now;
        FarmProfiles.Persist = false;
        FarmProfiles.Forget();
        DungeonRules.Clear();
    }

    public void Dispose()
    {
        Farm.Clock = () => DateTime.UtcNow;
        FarmProfiles.Forget();
        DungeonRules.Clear();
    }

    [Theory]
    [InlineData(1, false, 0)]
    [InlineData(2, false, 25)]
    [InlineData(3, false, 25)]
    [InlineData(4, false, 50)]
    [InlineData(2, true, 15)]
    [InlineData(4, true, 30)]
    public void SearchBonusFollowsTheSpecFormats(int size, bool preformed, int percent)
        => Assert.Equal(percent, DungeonRules.SearchBonusPercent(size, preformed));

    [Fact]
    public void MatchmakingGroupRemembersPreformed()
    {
        Matchmaking.Clear();
        var from = new Dictionary<long, (long, int)> { [1] = (10, 1), [2] = (10, 2) };
        Matchmaking.Made(77, 5, from, preformed: true);
        var group = Matchmaking.GroupOf(77)!;
        Assert.True(group.Preformed);
        Assert.Equal(2, group.Size);
        Matchmaking.Clear();
    }

    [Fact]
    public void FullRoomFightsAsFour()
    {
        Assert.Equal(1, DungeonRules.FightersFor(1, fullRoom: false));
        Assert.Equal(MobSpawnManager.DungeonMinimum, DungeonRules.FightersFor(1, fullRoom: true));
        Assert.Equal(6, DungeonRules.FightersFor(6, fullRoom: true));
        DungeonRules.SetFullRoom(42, true);
        Assert.True(DungeonRules.FullRoom(42));
        DungeonRules.SetFullRoom(42, false);
        Assert.False(DungeonRules.FullRoom(42));
    }

    [Fact]
    public void RecrueIsCheaperThanDensiteAndExclusive()
    {
        Assert.Equal(ZoneCounter.Recruit, ZoneCounter.Parse("Recrue"));
        Assert.Contains(ZoneCounter.Recruit, ZoneCounter.All);
        Assert.True(ZoneCounter.Price(ZoneCounter.Recruit, 100) < ZoneCounter.Price(ZoneCounter.Density, 100));
        Assert.Equal(ZoneCounter.Density, ZoneCounter.ExclusiveWith(ZoneCounter.Recruit));
        Assert.Equal(ZoneCounter.Recruit, ZoneCounter.ExclusiveWith(ZoneCounter.Density));
        Assert.Null(ZoneCounter.ExclusiveWith(ZoneCounter.Study));
        Assert.Equal(100, Farm.Current.Counter.RecruitProspecting);

        var profile = FarmProfiles.Of(9001);
        ZoneCounter.Extend(profile, 95, ZoneCounter.Recruit, _now);
        Assert.True(ZoneCounter.IsActive(profile, 95, ZoneCounter.Recruit, _now.AddMinutes(29)));
        Assert.False(ZoneCounter.IsActive(profile, 95, ZoneCounter.Recruit, _now.AddMinutes(31)));
    }

    [Fact]
    public void TrainingZonesComeBackInMinutes()
    {
        Farm.Current.Archmonsters.TrainingSubAreas = new List<int> { 95, 1 };
        Assert.True(Archimonsters.IsTrainingZone(95));
        Assert.True(Archimonsters.IsTrainingZone(1));
        Assert.False(Archimonsters.IsTrainingZone(500));
        for (int i = 0; i < 50; i++)
            Assert.InRange(Archimonsters.TrainingAwayTime(), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ShippedFarmFileHasAstrubAndMadrestam()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "content", "economy", "farm.json");
        if (!System.IO.File.Exists(path)) return;
        var settings = Farm.Read(path);
        Assert.Contains(95, settings.Archmonsters.TrainingSubAreas);   // Ciudad de Astrub
        Assert.Contains(1, settings.Archmonsters.TrainingSubAreas);    // Puerto de Madrestam
        Assert.True(settings.Archmonsters.TrainingRespawnMaxMinutes <= 10);
    }

    [Fact]
    public void PanelTabsOnlyTypeTheirCommands()
    {
        foreach (string tab in new[] { "jauge", "comptoir", "maree", "paliers", "event", "fissure" })
            Assert.True(CommandHandler.PanelTabs.ContainsKey(tab), tab);
        foreach (var (_, def) in CommandHandler.PanelTabs)
            foreach (string line in def.Lines)
                Assert.Contains(line.Split(' ')[0], CommandHandler.PanelCommands);
        Assert.DoesNotContain(".kamas", CommandHandler.PanelCommands);
        Assert.Contains(".donjon", CommandHandler.UsageKeys.Keys);
    }
}
