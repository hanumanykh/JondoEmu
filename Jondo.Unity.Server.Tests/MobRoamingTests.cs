using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Server.Tests;

/// <summary>
/// The choice of a monster group's next walk (<see cref="MobSpawnManager.ChooseWalk"/>): short,
/// over free cells only, and never far from where the group was first seen.
/// </summary>
public sealed class MobRoamingTests
{
    /// <summary>A cell well inside the map, with room on every side.</summary>
    private const int Center = 300;

    /// <summary>Every cell within <paramref name="radius"/> steps of <paramref name="cell"/>.</summary>
    private static HashSet<int> Around(int cell, int radius)
    {
        var (x, y) = MapGeometry.CellToPoint(cell);
        var cells = new HashSet<int>();
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                int c = MapGeometry.PointToCell(x + dx, y + dy);
                if (MapGeometry.Distance(cell, c) <= radius) cells.Add(c);
            }
        return cells;
    }

    [Fact]
    public void A_walk_is_one_to_four_steps_over_free_cells_and_stays_near_home()
    {
        var open = Around(Center, 9);
        for (int seed = 0; seed < 200; seed++)
        {
            var path = MobSpawnManager.ChooseWalk(Center, Center, open, open, new Random(seed));
            Assert.NotNull(path);
            Assert.Equal(Center, path![0]);
            Assert.InRange(path.Count - 1, 1, MobSpawnManager.RoamMaxSteps);
            Assert.All(path.Skip(1), c => Assert.Contains(c, open));
            for (int i = 1; i < path.Count; i++) Assert.Equal(1, MapGeometry.Distance(path[i - 1], path[i]));
            Assert.True(MapGeometry.Distance(Center, path[^1]) <= MobSpawnManager.RoamRadius);
        }
    }

    [Fact]
    public void A_group_away_from_home_never_ends_farther_than_it_stands()
    {
        var open = Around(Center, 12);
        var (x, y) = MapGeometry.CellToPoint(Center);
        int far = MapGeometry.PointToCell(x + 7, y);
        for (int seed = 0; seed < 100; seed++)
        {
            var path = MobSpawnManager.ChooseWalk(far, Center, open, open, new Random(seed));
            Assert.NotNull(path);
            Assert.True(MapGeometry.Distance(Center, path![^1]) < 7);
        }
    }

    [Fact]
    public void A_taken_cell_is_neither_crossed_nor_reached()
    {
        var open = Around(Center, 6);
        var taken = Around(Center, 1);
        taken.Remove(Center);
        var allowed = new HashSet<int>(open);
        allowed.ExceptWith(taken);
        for (int seed = 0; seed < 50; seed++)
            Assert.Null(MobSpawnManager.ChooseWalk(Center, Center, allowed, open, new Random(seed)));
    }

    [Fact]
    public void A_walk_ends_only_where_a_group_may_stand()
    {
        var open = Around(Center, 6);
        var (x, y) = MapGeometry.CellToPoint(Center);
        int spot = MapGeometry.PointToCell(x + 2, y);
        var spots = new HashSet<int> { spot };
        var path = MobSpawnManager.ChooseWalk(Center, Center, open, spots, new Random(1));
        Assert.NotNull(path);
        Assert.Equal(spot, path![^1]);
        Assert.Equal(3, path.Count);
    }
}
