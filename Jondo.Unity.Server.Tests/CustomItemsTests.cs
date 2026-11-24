using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class CustomItemsTests
{
    private static Dictionary<int, CustomItems.Custom> Read(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"custom-items-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try { return CustomItems.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Named_and_numbered_characteristics_become_fixed_effects()
    {
        var items = Read("""
            { "items": [ { "base": 2474, "skin": 1234,
                           "effects": { "vitalite": 60, "PA": 1, "118": 25, "sagesse": -5, "nimporte": 3 } } ] }
            """);

        var custom = Assert.Single(items).Value;
        Assert.Equal(2474, custom.Base);
        Assert.Equal(1234, custom.Skin);
        Assert.Equal(new[] { (125, 60L), (111, 1L), (118, 25L), (124, -5L) },
                     custom.Effects!.Select(e => (e.Effect, e.Value)));
        Assert.Equal("[[125,60,0,0],[111,1,0,0],[118,25,0,0],[124,-5,0,0]]", custom.Json);
    }

    [Fact]
    public void Without_effects_the_base_keeps_its_own_rolls()
    {
        var custom = Assert.Single(Read("""{ "items": [ { "base": 8463, "skin": 99 } ] }""")).Value;
        Assert.Null(custom.Effects);
        Assert.Null(custom.Json);
    }

    [Fact]
    public void A_broken_file_or_a_row_without_base_gives_nothing()
    {
        Assert.Empty(Read("{ not json"));
        Assert.Empty(Read("""{ "items": [ { "skin": 5 } ] }"""));
    }
}
