using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ReferencePricesTests
{
    [Fact]
    public void Rarer_higher_and_from_bosses_is_dearer()
    {
        long common = ReferencePrices.FromDrops(new[] { new ReferencePrices.Source(20, 0.3, false, false) });
        long rare = ReferencePrices.FromDrops(new[] { new ReferencePrices.Source(20, 0.02, false, false) });
        long high = ReferencePrices.FromDrops(new[] { new ReferencePrices.Source(150, 0.3, false, false) });
        long boss = ReferencePrices.FromDrops(new[] { new ReferencePrices.Source(20, 0.3, true, false) });
        long arch = ReferencePrices.FromDrops(new[] { new ReferencePrices.Source(20, 0.3, false, true) });
        Assert.InRange(common, 100, 400);
        Assert.True(rare > common * 5);
        Assert.True(high > common * 10);
        Assert.InRange(boss, common * 15 - 15, common * 15 + 15);
        Assert.True(arch > common * 10);
    }

    [Fact]
    public void The_easiest_way_to_get_it_sets_the_price()
    {
        var both = new[] { new ReferencePrices.Source(20, 0.3, true, false), new ReferencePrices.Source(20, 0.3, false, false) };
        Assert.Equal(ReferencePrices.FromDrops(new[] { both[1] }), ReferencePrices.FromDrops(both));
    }

    [Fact]
    public void Written_prices_are_read_and_bad_rows_skipped()
    {
        string path = Path.Combine(Path.GetTempPath(), $"prices-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "prices": { "8463": 125000, "x": 3, "12": -1 } }""");
        try { Assert.Equal(new Dictionary<int, long> { [8463] = 125000 }, ReferencePrices.ReadWritten(path)); }
        finally { File.Delete(path); }
    }
}
