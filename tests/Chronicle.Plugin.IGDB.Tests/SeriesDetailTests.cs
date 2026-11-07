using System.Net;
using System.Text.Json;
using Chronicle.Plugin.IGDB;

namespace Chronicle.Plugin.IGDB.Tests;

/// <summary>
/// What IGDB holds about a series and a franchise is kept in full, and nothing is cut off by a page limit. A real answer (game 9971,
/// "All Zombies Must Die!", recorded 2026-11-07 with the exact query this plugin sends) shows what comes back: the series' type and
/// its games as objects, and the game's membership kind.
/// </summary>
public class SeriesDetailTests
{
    private static JsonElement[] RealParts()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "series_full_9971.json")));
        var by = doc.RootElement.EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("result").Clone());
        return [by["game"][0].Clone(), by["ttb"], by["characters"], by["memberships"]];
    }

    [Fact]
    public void The_full_query_asks_for_the_series_detail_and_the_memberships()
    {
        var q = Queries.Full(9971, new HashSet<string>(), true);
        Assert.Contains("collections.type.*", q);
        Assert.Contains("collections.games.name", q);
        Assert.Contains("collections.as_parent_relations.child_collection.name", q);
        Assert.Contains("collections.as_child_relations.parent_collection.name", q);
        Assert.Contains("franchises.games.name", q);
        Assert.Contains("query collection_memberships \"memberships\" { fields *, type.*, collection.name; where game = 9971; limit 500; offset 0; };", q);
        // the memberships fields belong to their own sub-query, not to the game's field list
        Assert.DoesNotContain(", collection.name", q.Split('\n')[0]);
    }

    [Fact]
    public void A_rejected_series_field_switches_off_only_the_series_group()
    {
        Assert.Equal("series", Queries.GroupOf("collections.as_parent_relations.child_collection"));
        Assert.Equal("series", Queries.GroupOf("collections.games.platforms"));
        Assert.Equal("memberships", Queries.GroupOf("type"));
        var without = Queries.Full(9971, new HashSet<string> { "series" }, true);
        Assert.DoesNotContain("collections.games.name", without);
        Assert.Contains("collections.*", without);                         // the series themselves stay
        var withoutMemberships = Queries.Full(9971, new HashSet<string> { "memberships" }, true);
        Assert.Contains("query collection_memberships \"memberships\" { fields *; where game = 9971; limit 500; offset 0; };", withoutMemberships);
    }

    [Fact]
    public void A_real_answer_keeps_the_series_type_and_every_game_in_the_series_in_the_complete_record()
    {
        var p = RealParts();
        var m = MetadataMapper.Map(p[0], p[1], p[2], true, Settings.Default, null, p[3]);
        var series = m.ExtendedData!.Value.GetProperty("igdb").GetProperty("collections")[0];
        Assert.Equal("Series", series.GetProperty("type").GetProperty("name").GetString());
        var games = series.GetProperty("games");
        Assert.Equal(2, games.GetArrayLength());
        Assert.Contains(games.EnumerateArray(), g => g.GetProperty("name").GetString() == "All Zombies Must Die! Scorepocalypse"
                                                     && g.TryGetProperty("first_release_date", out _) && g.TryGetProperty("cover", out _));
    }

    [Fact]
    public void A_real_answer_keeps_how_the_game_belongs_to_its_series()
    {
        var p = RealParts();
        var m = MetadataMapper.Map(p[0], p[1], p[2], true, Settings.Default, null, p[3]);
        var memberships = m.ExtendedData!.Value.GetProperty("collection_memberships");
        Assert.Equal(1, memberships.GetArrayLength());
        Assert.Equal("Member", memberships[0].GetProperty("type").GetProperty("name").GetString());
        Assert.Equal("All Zombies Must Die!", memberships[0].GetProperty("collection").GetProperty("name").GetString());
    }

    [Fact]
    public void The_series_a_game_belongs_to_is_still_offered_to_chronicle_as_an_id_and_a_name()
    {
        var p = RealParts();
        var m = MetadataMapper.Map(p[0], p[1], p[2], true, Settings.Default, null, p[3]);
        var b = m.ExtendedData!.Value.GetProperty("belongsToCollection");
        Assert.Equal(1332, b.GetProperty("id").GetInt64());
        Assert.Equal("All Zombies Must Die!", b.GetProperty("name").GetString());
    }

    [Fact]
    public void Without_memberships_nothing_is_added()
    {
        var p = RealParts();
        var m = MetadataMapper.Map(p[0], p[1], p[2], true, Settings.Default);
        Assert.False(m.ExtendedData!.Value.TryGetProperty("collection_memberships", out _));
    }

    // ── nothing is cut off: a sub-query that comes back a full page is read on until a page is short ──────

    private static string Rows(int from, int count, Func<int, string> row) => "[" + string.Join(",", Enumerable.Range(from, count).Select(row)) + "]";
    private static string Character(int i) => "{\"id\":" + i + ",\"name\":\"Character " + i + "\"}";
    private static string Membership(int i) =>
        "{\"id\":" + i + ",\"game\":9971,\"collection\":{\"id\":" + i + ",\"name\":\"Series " + i + "\"},\"type\":{\"id\":1,\"name\":\"Member\"}}";

    private static (FakeIgdb Fake, IgdbMetadataProvider Provider) PagedFake(int firstPage, int secondPage)
    {
        var game = FakeIgdb.FixtureJson("series_full_9971.json").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "game").GetProperty("result")[0].GetRawText();
        var multiquery = "[{\"name\":\"game\",\"result\":[" + game + "]},{\"name\":\"ttb\",\"result\":[]},"
                       + "{\"name\":\"characters\",\"result\":" + Rows(0, firstPage, Character) + "},"
                       + "{\"name\":\"memberships\",\"result\":" + Rows(0, firstPage, Membership) + "}]";
        var fake = new FakeIgdb().Token()
            .OnApi("characters", b => b.Contains("offset 500", StringComparison.Ordinal), () => new FakeIgdb.Reply(HttpStatusCode.OK, Rows(500, secondPage, Character)))
            .OnApi("collection_memberships", b => b.Contains("offset 500", StringComparison.Ordinal), () => new FakeIgdb.Reply(HttpStatusCode.OK, Rows(500, secondPage, Membership)))
            .OnApiAny("multiquery", multiquery);
        var (p, _, _, _) = Harness.Provider(fake);
        return (fake, p);
    }

    [Fact]
    public async Task A_game_with_more_characters_and_memberships_than_one_page_gets_every_one()
    {
        var (fake, p) = PagedFake(firstPage: 500, secondPage: 3);
        var meta = await p.GetByIdAsync("9971");
        var ext = meta.ExtendedData!.Value;
        Assert.Equal(503, ext.GetProperty("characters").GetArrayLength());
        Assert.Equal(503, ext.GetProperty("collection_memberships").GetArrayLength());
        Assert.Equal("Character 502", ext.GetProperty("characters")[502].GetProperty("name").GetString());
        Assert.Single(fake.Api.Where(r => r.Url.EndsWith("/characters") && r.Body.Contains("offset 500")));
        Assert.Single(fake.Api.Where(r => r.Url.EndsWith("/collection_memberships") && r.Body.Contains("offset 500")));
    }

    [Fact]
    public async Task A_list_that_ends_exactly_on_a_page_boundary_is_complete_and_the_next_page_is_empty()
    {
        var (fake, p) = PagedFake(firstPage: 500, secondPage: 0);
        var meta = await p.GetByIdAsync("9971");
        Assert.Equal(500, meta.ExtendedData!.Value.GetProperty("characters").GetArrayLength());
        Assert.Single(fake.Api.Where(r => r.Url.EndsWith("/characters")));
    }

    [Fact]
    public async Task A_short_first_page_asks_for_nothing_more()
    {
        var (fake, p) = PagedFake(firstPage: 36, secondPage: 3);
        var meta = await p.GetByIdAsync("9971");
        Assert.Equal(36, meta.ExtendedData!.Value.GetProperty("characters").GetArrayLength());
        Assert.DoesNotContain(fake.Api, r => r.Url.EndsWith("/characters") || r.Url.EndsWith("/collection_memberships"));
    }
}
