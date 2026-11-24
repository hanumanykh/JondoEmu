using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The Système's missions (Managers/Stasis/Missions.cs): the real missions.json parses into
/// chapters covering 1 to 300, targets are drawn from the world near the level and stay put,
/// progress is counted and read live, a claim pays and moves on, the chapter's last mission pays
/// its bonus, tracks come by batch of ten levels, eggs are skipped until the Pension exists, and
/// the slot words of the commands.
/// </summary>
/// <remarks>
/// The world is a small fake (<see cref="FakeWorld"/>): a few zones, monsters and dungeons with
/// levels, so the draws do not need world.db. The content tests read the checkout's file; the
/// rule tests install their own short file and put the real one back.
/// </remarks>
public sealed class StasisMissionsTests
{
    private static long _nextId = 9_500_000_000L;

    private sealed class FakeWorld : IMissionWorld
    {
        public IReadOnlyList<(int SubArea, int Level)> Zones { get; } = new List<(int, int)>
        {
            (10, 5), (11, 12), (12, 18), (20, 30), (21, 35), (30, 55),
        };
        public bool HasZaap(int subArea) => subArea % 2 == 0;
        public string ZoneName(int subArea) => "Zone " + subArea;
        public IReadOnlyList<int> Roster(int subArea) => new[] { subArea * 100 + 1, subArea * 100 + 2 };
        public string MonsterName(int monster) => "Monstre " + monster;
        public IReadOnlyList<(int Id, string Name, int Level)> Dungeons { get; } = new List<(int, string, int)>
        {
            (1, "Donjon des Bouftous", 12), (2, "Donjon des Larves", 35),
        };
        public IReadOnlyList<(int Id, string Name, IReadOnlyList<int> SubAreas)> ExplorationAchievements { get; } =
            new List<(int, string, IReadOnlyList<int>)> { (500, "Explorateur des prés", new[] { 10 }) };
        public long ExperienceSpan(int level) => 1000;
        public long Orbes(long kamas) => kamas / 10;
        public string OrbeLabel => "Jondo Coins";
    }

    private static readonly FakeWorld World = new();
    private static readonly object Gate = new();

    private static MissionProfile NewProfile() => new() { CharacterId = Interlocked.Increment(ref _nextId) };

    private static MissionsFile RealFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content", "progression");
            if (File.Exists(Path.Combine(candidate, "missions.json"))) return Missions.Load(candidate);
        }
        throw new InvalidOperationException("content/progression/missions.json not found above the test binaries.");
    }

    private static MissionsFile SmallFile() => new()
    {
        TrackEvery = 10, TracksShown = 2, TrackBonusKamasPerLevel = 100,
        Chapters =
        {
            new ChapterDefinition
            {
                Id = "c1", Title = "Un", MinLevel = 1, MaxLevel = 20, RewardKamas = 1000, RewardXpShare = 0.1, FinalKamas = 5000, FinalXpShare = 0.5,
                Missions = { new MissionTemplate { Kind = "fights", Count = 2 }, new MissionTemplate { Kind = "hatch" }, new MissionTemplate { Kind = "kills", Count = 3 } },
            },
            new ChapterDefinition
            {
                Id = "c2", Title = "Deux", MinLevel = 20, MaxLevel = 40, RewardKamas = 2000,
                Missions = { new MissionTemplate { Kind = "dungeon" }, new MissionTemplate { Kind = "level", Level = 40 } },
            },
        },
        Tracks =
        {
            new TrackDefinition { Id = "a", Title = "Piste A", XpShare = 0.05, Missions = { new MissionTemplate { Kind = "orbes", Count = 100 } } },
            new TrackDefinition { Id = "b", Title = "Piste B", Missions = { new MissionTemplate { Kind = "fights", Count = 1 }, new MissionTemplate { Kind = "fights", Count = 1 } } },
            new TrackDefinition { Id = "c", Title = "Piste C", Missions = { new MissionTemplate { Kind = "achievement" } } },
        },
    };

    /// <summary>Runs with the small file installed, the real one put back after.</summary>
    private static void WithSmallFile(Action action)
    {
        lock (Gate)
        {
            var before = Missions.Current;
            Missions.Current = SmallFile();
            try { action(); }
            finally { Missions.Current = before; }
        }
    }

    [Fact]
    public void TheRealFileCoversEveryLevelWithChaptersAndTracks()
    {
        var file = RealFile();
        Assert.Equal(10, file.TrackEvery);
        Assert.Equal(2, file.TracksShown);
        Assert.Equal(1, file.Chapters[0].MinLevel);
        Assert.Equal(300, file.Chapters[^1].MaxLevel);
        for (int i = 1; i < file.Chapters.Count; i++) Assert.Equal(file.Chapters[i - 1].MaxLevel, file.Chapters[i].MinLevel);
        Assert.All(file.Chapters, c => Assert.All(c.Missions, m => Assert.True(Missions.Kinds.ContainsKey(m.Kind), m.Kind)));
        Assert.All(file.Tracks, t => Assert.InRange(t.Missions.Count, 3, 5));
        Assert.True(file.Tracks.Count >= 4);
    }

    [Fact]
    public void AChapterMissionIsDrawnNearTheLevelAndEggsAreSkipped()
    {
        WithSmallFile(() =>
        {
            var p = NewProfile();
            Assert.True(Missions.Fill(p, 12, _ => false, World));
            var m = p.Slots[Missions.ChapterSlot];
            Assert.Equal("fights", m.Kind);
            Assert.Contains(m.Target, new long[] { 10, 11, 12 });
            Assert.Equal(m.Target, m.Zone);
            Assert.Contains("Zone " + m.Target, m.Title);
            // Drawn once: filling again changes nothing.
            Assert.False(Missions.Fill(p, 12, _ => false, World));

            // Progress elsewhere does not count; in the zone it does.
            Assert.Empty(Missions.Record(p, "fights", 999, 1));
            Missions.Record(p, "fights", m.Target, 1);
            var done = Missions.Record(p, "fights", m.Target, 5);
            Assert.Single(done);
            Assert.Equal(2, m.Progress);

            // Claimed: reward, and the egg mission skipped (no Pension) for the kills.
            Assert.Equal("ok", Missions.Claim(p, Missions.ChapterSlot, 12, _ => false, 1.0, out var claim, World));
            Assert.Equal(1000, claim!.Kamas);
            Assert.Equal(100, claim.Experience);
            Assert.Equal("", claim.Finished);
            Assert.Equal("kills", p.Slots[Missions.ChapterSlot].Kind);
            Assert.Equal(2, p.Slots[Missions.ChapterSlot].Step);
        });
    }

    [Fact]
    public void TheLastMissionPaysTheChapterBonusAndOpensTheNext()
    {
        WithSmallFile(() =>
        {
            var p = NewProfile();
            Missions.Fill(p, 15, _ => false, World);
            var first = p.Slots[Missions.ChapterSlot];
            Missions.Record(p, "fights", first.Target, 2);
            Missions.Claim(p, Missions.ChapterSlot, 15, _ => false, 1.0, out _, World);
            var kills = p.Slots[Missions.ChapterSlot];
            Assert.Equal("mission-en-cours", Missions.Claim(p, Missions.ChapterSlot, 15, _ => false, 1.0, out _, World));
            Missions.Record(p, "kills", kills.Target, 3);
            // Mémoire de la Route doubles the experience.
            Assert.Equal("ok", Missions.Claim(p, Missions.ChapterSlot, 15, _ => false, 2.0, out var claim, World));
            Assert.Equal(1000 + 5000, claim!.Kamas);
            Assert.Equal(200 + 1000, claim.Experience);
            Assert.Equal("Un", claim.Finished);
            Assert.Equal(1, p.ChaptersDone);
            // Chapter 2 starts at 20: a level gate first.
            var gate = p.Slots[Missions.ChapterSlot];
            Assert.Equal("c2", gate.Source);
            Assert.Equal(-1, gate.Step);
            Assert.Equal("level", gate.Kind);
            Assert.Empty(Missions.Refresh(p, 19, _ => false));
            Assert.Single(Missions.Refresh(p, 20, _ => false));
            Assert.Equal("ok", Missions.Claim(p, Missions.ChapterSlot, 20, _ => false, 1.0, out var gateClaim, World));
            Assert.Equal(0, gateClaim!.Kamas);
            Assert.Equal("dungeon", p.Slots[Missions.ChapterSlot].Kind);
        });
    }

    [Fact]
    public void AnOutlevelledChapterIsSkippedForTheOneOfTheLevel()
    {
        WithSmallFile(() =>
        {
            var p = NewProfile();
            Missions.Fill(p, 30, _ => false, World);
            var m = p.Slots[Missions.ChapterSlot];
            Assert.Equal("c2", m.Source);
            Assert.Equal(2, m.Target); // the band's dungeon near 30
        });
    }

    [Fact]
    public void TracksComeByBatchAndAFinishedOnePaysItsBonus()
    {
        WithSmallFile(() =>
        {
            var p = NewProfile();
            Missions.Fill(p, 5, _ => false, World);
            var one = p.Slots[Missions.TrackSlot(0)];
            var two = p.Slots[Missions.TrackSlot(1)];
            Assert.NotEqual(one.Source, two.Source);
            Assert.Equal(0, one.Batch);

            // Finish whichever track slot 1 holds, mission by mission.
            string slot = Missions.TrackSlot(0);
            for (int guard = 0; guard < 5 && p.Slots[slot].Kind != Missions.KindDone; guard++)
            {
                var m = p.Slots[slot];
                if (m.Kind == "achievement") Missions.Refresh(p, 5, id => id == m.Target);
                else Missions.Record(p, m.Kind, m.Target, m.Need);
                Assert.Equal("ok", Missions.Claim(p, slot, 5, id => id == 500, 1.0, out var claim, World));
                if (p.Slots[slot].Kind == Missions.KindDone) Assert.Equal(500, claim!.Kamas);
            }
            Assert.Equal(Missions.KindDone, p.Slots[slot].Kind);
            Assert.Equal("mission-inconnue", Missions.Claim(p, slot, 5, _ => false, 1.0, out _, World));

            // Same batch: stays done. Next batch: a new track.
            Missions.Fill(p, 9, _ => false, World);
            Assert.Equal(Missions.KindDone, p.Slots[slot].Kind);
            Missions.Fill(p, 10, _ => false, World);
            Assert.NotEqual(Missions.KindDone, p.Slots[slot].Kind);
            Assert.Equal(1, p.Slots[slot].Batch);
        });
    }

    [Fact]
    public void ACompleteTrackMissionIsNotTakenAwayByTheNextBatch()
    {
        WithSmallFile(() =>
        {
            var p = NewProfile();
            Missions.Fill(p, 8, _ => false, World);
            var m = p.Slots[Missions.TrackSlot(1)];
            if (m.Kind == "achievement") Missions.Refresh(p, 8, _ => true);
            else Missions.Record(p, m.Kind, m.Target, m.Need);
            Assert.True(m.Complete);
            Missions.Fill(p, 12, _ => false, World);
            Assert.Same(m, p.Slots[Missions.TrackSlot(1)]);
        });
    }

    [Fact]
    public void TheHatchKindIsPluggable()
    {
        var before = Missions.HatchAvailable;
        try
        {
            var template = new MissionTemplate { Kind = "hatch", Count = 2 };
            var draw = new Missions.Draw(25, 20, 40, 1, World, _ => false);
            Missions.HatchAvailable = () => false;
            Assert.Null(Missions.Resolve(template, draw));
            Missions.HatchAvailable = () => true;
            var m = Missions.Resolve(template, draw);
            Assert.NotNull(m);
            Assert.Equal(2, m!.Need);
            Assert.Contains("œuf", m.Title);
        }
        finally
        {
            Missions.HatchAvailable = before;
        }
    }

    [Fact]
    public void OrbesAreCountedInTheOrbesCurrency()
    {
        var m = Missions.Resolve(new MissionTemplate { Kind = "orbes", Count = 1500 }, new Missions.Draw(12, 1, 20, 4, World, _ => false));
        Assert.NotNull(m);
        Assert.Equal(150, m!.Need);
        Assert.Contains("150 Jondo Coins", m.Title);
    }

    [Theory]
    [InlineData("", "chapitre")]
    [InlineData("chapitre", "chapitre")]
    [InlineData("1", "piste1")]
    [InlineData("piste 2", "piste2")]
    [InlineData("Piste1", "piste1")]
    public void SlotWords(string word, string slot) => Assert.Equal(slot, CommandHandler.SlotFromWord(word));

    [Fact]
    public void TheCommandsHaveUsageLines()
    {
        foreach (var command in new[] { ".missions", ".go", ".recuperer" })
            Assert.True(CommandHandler.UsageKeys.ContainsKey(command), command);
    }
}
