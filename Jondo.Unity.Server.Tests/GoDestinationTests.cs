using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server.Managers;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// Where a GO to a subarea lands (Managers/Stasis/GoDestination.cs): its zaap first, else an
/// outdoor map of its own on the main world map; never a room, an inside, another world or
/// another subarea's map. The last test reads the installed world.db read-only when there is one.
/// </summary>
public sealed class GoDestinationTests(ITestOutputHelper output)
{
    private static MapInfo M(long id, int x, int y, int sub, bool outdoor = true, int world = 1)
        => new() { MapId = id, PosX = x, PosY = y, SubAreaId = sub, Outdoor = outdoor, WorldMap = world, Name = "" };

    [Fact]
    public void TheZaapComesFirstThenTheMiddleOfTheSubareaAndNeverAnotherPlace()
    {
        var maps = new[]
        {
            M(1, 0, 0, 10), M(2, 1, 0, 10), M(3, 2, 0, 10), M(4, 1, 0, 10, outdoor: false),
            M(5, 1, 0, 10, world: 13), M(6, 1, 0, 10, world: -1), M(7, 1, 0, 11), M(8, 1, 1, 10),
        };
        var got = GoDestination.Candidates(10, maps, new long[] { 3, 7 }, id => id == 8);
        Assert.Equal(3, got[0]);                    // the zaap of the subarea; 7's is another subarea's
        Assert.Equal(2, got[1]);                    // then the middle of the subarea
        Assert.DoesNotContain(4L, got);             // an inside
        Assert.DoesNotContain(5L, got);             // a dimension
        Assert.DoesNotContain(6L, got);
        Assert.DoesNotContain(7L, got);
        Assert.DoesNotContain(8L, got);             // a dungeon room
        Assert.Empty(GoDestination.Candidates(12, maps, Array.Empty<long>(), _ => false));
        // A subarea made only of insides has nowhere to land.
        Assert.Empty(GoDestination.Candidates(20, new[] { M(9, 0, 0, 20, outdoor: false) }, new long[] { 9 }, _ => false));
    }

    [Fact]
    public void TheInstalledWorldSendsTheFieldsToTheFields()
    {
        string db = @"C:\Program Files\JondoEmu\bases\world.db";
        if (!File.Exists(db)) return;
        using var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
        c.Open();
        var maps = new List<MapInfo>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT p.MapId, p.PosX, p.PosY, p.SubAreaId, p.Outdoor, t.Data FROM MapPositions p LEFT JOIN MapTemplates t ON p.MapId = t.Id;";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                int world = 1;
                if (!r.IsDBNull(5))
                    try
                    {
                        using var doc = JsonDocument.Parse(r.GetString(5));
                        if (doc.RootElement.TryGetProperty("worldMap", out var w) && w.ValueKind == JsonValueKind.Number) world = w.GetInt32();
                    }
                    catch (JsonException) { }
                int sub = r.GetInt32(3);
                maps.Add(M(r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), sub == 444 ? 20663 : sub, r.GetInt32(4) == 1, world));
            }
        }
        var byId = maps.ToDictionary(m => m.MapId);
        List<long> Old(int sub)
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT MapId FROM MapSubareas WHERE SubAreaId = $s ORDER BY MapId;";
            q.Parameters.AddWithValue("$s", sub);
            var list = new List<long>();
            using var r = q.ExecuteReader();
            while (r.Read()) list.Add(r.GetInt64(0));
            return list;
        }
        string Show(long id) => byId.TryGetValue(id, out var m) ? $"{id} [{m.PosX},{m.PosY}] sub {m.SubAreaId} out={m.Outdoor} wm={m.WorldMap}" : $"{id} (no position)";

        // 68 Campos de Cania, 98 Campos de Astrub, 444 Campos, 95 Ciudad de Astrub, 1 Puerto de Madrestam.
        foreach (int sub in new[] { 68, 98, 444, 95, 1, 485 })
        {
            var now = GoDestination.Candidates(sub, maps, Array.Empty<long>(), _ => false);
            var old = Old(sub);
            output.WriteLine($"sub {sub}: before {(old.Count > 0 ? Show(old[0]) : "-")}  |  after {(now.Count > 0 ? Show(now[0]) : "nowhere")}");
            Assert.All(now, id => Assert.True(byId[id].SubAreaId == sub && byId[id].WorldMap == 1 && byId[id].Outdoor));
        }
        Assert.NotEmpty(GoDestination.Candidates(68, maps, Array.Empty<long>(), _ => false));
        Assert.NotEmpty(GoDestination.Candidates(98, maps, Array.Empty<long>(), _ => false));
        Assert.Empty(GoDestination.Candidates(444, maps, Array.Empty<long>(), _ => false));   // the Orbalantyr's rooms
    }
}
