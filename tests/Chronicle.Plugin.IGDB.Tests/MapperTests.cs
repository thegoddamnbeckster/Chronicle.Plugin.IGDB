using System.Text.Json;
using Chronicle.Plugin.IGDB;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.IGDB.Tests;

public class MapperTests
{
    private static readonly Settings S = Settings.Default;

    private static (JsonElement Game, JsonElement Ttb, JsonElement Chars) Parts()
    {
        using var doc = JsonDocument.Parse(Harness.FullAnswer());
        var root = doc.RootElement;
        return (root[0].GetProperty("result")[0].Clone(), root[1].GetProperty("result").Clone(), root[2].GetProperty("result").Clone());
    }

    private static MediaMetadata Full(Settings? s = null)
    {
        var (g, t, c) = Parts();
        return MetadataMapper.Map(g, t, c, true, s ?? S);
    }

    [Fact]
    public void A_real_game_maps_its_core_fields()
    {
        var m = Full();
        Assert.Equal("16", m.ExternalId);
        Assert.Equal("igdb", m.Source);
        Assert.Equal("Fallout: New Vegas", m.Title);
        Assert.Equal(2010, m.Year);
        Assert.False(string.IsNullOrWhiteSpace(m.Overview));
        Assert.Equal("https://www.igdb.com/games/fallout-new-vegas", m.SourceUrl);
        Assert.Contains("Role-playing (RPG)", m.Genres);
        Assert.Equal(8.5, m.Rating);
        Assert.Null(m.RuntimeMinutes);
    }

    [Fact]
    public void The_series_a_game_belongs_to_is_given_in_the_shape_chronicle_groups_by()
    {
        var m = Full();
        var c = m.ExtendedData!.Value.GetProperty("belongsToCollection");
        Assert.Equal(3, c.GetProperty("id").GetInt64());
        Assert.Equal("Fallout", c.GetProperty("name").GetString());
        Assert.Contains("Series: Fallout", m.Tags);
    }

    [Fact]
    public void A_game_in_no_series_gets_no_collection_so_it_stays_at_the_top_level()
    {
        var g = JsonDocument.Parse("{\"id\":5,\"name\":\"Lonely Game\"}").RootElement;
        var m = MetadataMapper.Map(g, null, null, false, S);
        Assert.False(m.ExtendedData!.Value.TryGetProperty("belongsToCollection", out _));
    }

    [Fact]
    public void A_series_with_no_name_or_id_is_not_offered_and_the_next_usable_one_is()
    {
        var g = JsonDocument.Parse("{\"id\":5,\"name\":\"G\",\"collections\":[{\"id\":1},{\"name\":\"No id\"},{\"id\":9,\"name\":\"Real series\"}]}").RootElement;
        var m = MetadataMapper.Map(g, null, null, false, S);
        Assert.Equal(9, m.ExtendedData!.Value.GetProperty("belongsToCollection").GetProperty("id").GetInt64());
    }

    [Fact]
    public void Companies_become_the_games_credits_so_the_developers_section_is_not_empty()
    {
        var m = Full();
        Assert.NotEmpty(m.Cast);
        Assert.All(m.Cast, c => Assert.Equal("Developer", c.Role));
        // Publishers are crew, and every company the record names is credited, not just developers.
        Assert.Contains(m.Crew, c => c.Job == "Publisher");
        Assert.True(m.Cast.Count + m.Crew.Count >= 2);
    }

    [Fact]
    public void Pictures_use_documented_sizes_and_nothing_is_dropped()
    {
        var (g, _, c) = Parts();
        var m = Full();
        Assert.StartsWith("https://images.igdb.com/igdb/image/upload/t_cover_big_2x/co1u60.jpg", m.PosterUrl);
        Assert.StartsWith("https://images.igdb.com/igdb/image/upload/t_1080p/", m.BackdropUrl);
        var shots = g.GetProperty("screenshots").GetArrayLength();
        var arts = g.GetProperty("artworks").GetArrayLength();
        Assert.Equal(shots, m.AdditionalImages.Count(i => i.Type == "Screenshot"));
        Assert.Equal(arts, m.AdditionalImages.Count(i => i.Type == "Fanart"));
        var mugs = c.EnumerateArray().Count(x => x.TryGetProperty("mug_shot", out var ms) && ms.ValueKind == JsonValueKind.Object);
        Assert.Equal(mugs, m.AdditionalImages.Count(i => i.Type == "Character"));
        Assert.All(m.AdditionalImages, i => Assert.StartsWith("https://images.igdb.com/", i.Url));
        Assert.DoesNotContain(m.AdditionalImages, i => i.Url.StartsWith("//"));
    }

    [Fact]
    public void Every_keyword_and_the_series_are_tags()
    {
        var (g, _, _) = Parts();
        var m = Full();
        var keywords = g.GetProperty("keywords").GetArrayLength();
        Assert.True(keywords > 100);
        foreach (var k in g.GetProperty("keywords").EnumerateArray())
            Assert.Contains(k.GetProperty("name").GetString(), m.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(m.Tags, t => t.StartsWith("Series: ", StringComparison.Ordinal) || t.StartsWith("Franchise: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Executable_names_leave_the_alternative_names_but_are_kept_in_their_own_field()
    {
        var game = JsonDocument.Parse("""
            {"id":5,"name":"Foo","alternative_names":[
              {"name":"Foo Acronym","comment":"Acronym"},
              {"name":"foo.exe","comment":"Windows Executable"},
              {"name":"bar.exe","comment":"windows executable"},
              {"name":"Foo Dos","comment":null},
              {"name":"Foo","comment":"Same"}]}
            """).RootElement;
        var m = MetadataMapper.Map(game, null, null, false, S);
        Assert.Equal(["Foo Acronym", "Foo Dos"], m.AlternateNames);
        var exe = m.ExtendedData!.Value.GetProperty("executable_names").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["foo.exe", "bar.exe"], exe);
    }

    [Fact]
    public void The_complete_record_is_kept_once_verbatim_and_extras_are_merged()
    {
        var (g, _, c) = Parts();
        var x = Full().ExtendedData!.Value;
        Assert.True(x.GetProperty("extras").GetBoolean());
        Assert.False(x.GetProperty("partial").GetBoolean());
        var kept = x.GetProperty("igdb");
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(g.GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(kept.GetRawText())));
        Assert.Equal(g.GetProperty("keywords").GetArrayLength(), kept.GetProperty("keywords").GetArrayLength());
        Assert.Equal(c.GetArrayLength(), x.GetProperty("characters").GetArrayLength());
        Assert.Equal(189774, x.GetProperty("time_to_beat").GetProperty("normally").GetInt64());
    }

    [Fact]
    public void Derived_summaries_cover_companies_stores_websites_and_scores()
    {
        var x = Full().ExtendedData!.Value;
        Assert.Contains("Obsidian Entertainment", x.GetProperty("developers").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("Bandai Namco Entertainment", x.GetProperty("publishers").EnumerateArray().Select(e => e.GetString()));
        Assert.True(x.GetProperty("company_logos").GetArrayLength() > 0);
        Assert.True(x.GetProperty("external_ids").EnumerateObject().Any(p => p.Name == "Steam"));
        Assert.True(x.GetProperty("websites").GetArrayLength() > 5);
        var variants = x.GetProperty("ratings").EnumerateArray().Select(r => r.GetProperty("variant").GetString()).ToList();
        Assert.Equal(["total", "users", "critics"], variants);
        Assert.All(x.GetProperty("ratings").EnumerateArray(), r => Assert.Equal(100, r.GetProperty("scale").GetInt32()));
        Assert.Equal("Main Game", x.GetProperty("game_type").GetString());
    }

    [Fact]
    public void No_cross_reference_ids_are_ever_published()
    {
        var x = Full().ExtendedData!.Value;
        Assert.False(x.TryGetProperty("ids", out _));
        Assert.True(x.TryGetProperty("external_ids", out _));
    }

    [Theory]
    [InlineData(RatingSource.Total, 8.5)]
    [InlineData(RatingSource.Users, 8.7)]
    [InlineData(RatingSource.Critics, 8.2)]
    public void The_rating_source_picks_the_score_on_a_ten_point_scale(RatingSource source, double expected)
    {
        var (g, t, c) = Parts();
        Assert.Equal(expected, MetadataMapper.Map(g, t, c, true, S with { RatingSource = source }).Rating);
    }

    [Fact]
    public void A_score_is_never_hidden_unless_the_person_sets_a_floor()
    {
        var game = JsonDocument.Parse("""{"id":1,"name":"X","total_rating":90.0,"total_rating_count":1}""").RootElement;
        Assert.Equal(9.0, MetadataMapper.Map(game, null, null, false, S).Rating);
        Assert.Null(MetadataMapper.Map(game, null, null, false, S with { MinimumRatingVotes = 5 }).Rating);
        // no score at all is no rating, never 0
        Assert.Null(MetadataMapper.Map(JsonDocument.Parse("""{"id":1,"name":"X"}""").RootElement, null, null, false, S).Rating);
        Assert.Null(MetadataMapper.Map(JsonDocument.Parse("""{"id":1,"name":"X","total_rating":0}""").RootElement, null, null, false, S).Rating);
    }

    [Fact]
    public void Time_to_beat_fills_the_runtime_only_when_asked()
    {
        var (g, t, c) = Parts();
        Assert.Null(MetadataMapper.Map(g, t, c, true, S).RuntimeMinutes);
        Assert.Equal(3163, MetadataMapper.Map(g, t, c, true, S with { TimeToBeatAsRuntime = true }).RuntimeMinutes);
    }

    [Fact]
    public void The_poster_falls_back_to_a_screenshot_when_there_is_no_cover()
    {
        var game = JsonDocument.Parse("""{"id":1,"name":"X","screenshots":[{"image_id":"abc123"}]}""").RootElement;
        var m = MetadataMapper.Map(game, null, null, false, S);
        Assert.Equal("https://images.igdb.com/igdb/image/upload/t_screenshot_big/abc123.jpg", m.PosterUrl);
        Assert.Equal("https://images.igdb.com/igdb/image/upload/t_1080p/abc123.jpg", m.BackdropUrl);
    }

    [Fact]
    public void A_sparse_record_maps_without_nulls_or_exceptions()
    {
        var m = MetadataMapper.Map(JsonDocument.Parse("""{"id":42,"name":"Sparse"}""").RootElement, null, null, false, S);
        Assert.Equal("42", m.ExternalId);
        Assert.Null(m.Year); Assert.Null(m.Overview); Assert.Null(m.PosterUrl); Assert.Null(m.BackdropUrl);
        Assert.Empty(m.Genres); Assert.Empty(m.Tags); Assert.Empty(m.AdditionalImages); Assert.Null(m.Rating);
        Assert.True(m.ExtendedData!.Value.GetProperty("partial").GetBoolean());
        Assert.False(m.ExtendedData!.Value.GetProperty("extras").GetBoolean());
    }

    [Theory]
    [InlineData("""{"id":1,"name":"X","genres":[1,2,3],"cover":null,"screenshots":"none","keywords":{"a":1},"first_release_date":"soon","total_rating":"high"}""")]
    [InlineData("""{"id":1,"name":"X","genres":[{"name":null},{"name":7},{}],"cover":{"image_id":5},"artworks":[null,1,"x",{"image_id":"bad id!"}]}""")]
    [InlineData("""{"id":1,"name":"X","involved_companies":[{"company":null},{"company":{"name":null}},5],"websites":[{"url":null}],"videos":[{"video_id":"bad id"}]}""")]
    [InlineData("""{"id":1,"name":"X","external_games":[{"uid":5,"external_game_source":7}],"release_dates":[{"platform":"x"}],"game_type":{"id":0}}""")]
    [InlineData("""{"id":1,"name":"X","first_release_date":-99999999999999,"alternative_names":[{"name":5},"x",null]}""")]
    public void Every_odd_shape_is_read_without_throwing(string json)
    {
        var m = MetadataMapper.Map(JsonDocument.Parse(json).RootElement, JsonDocument.Parse("""[1,"x",null,{"normally":"a"}]""").RootElement,
            JsonDocument.Parse("""[1,{"name":5,"mug_shot":3},{"mug_shot":{"image_id":9}}]""").RootElement, true, S with { TimeToBeatAsRuntime = true });
        Assert.Equal("1", m.ExternalId);
        Assert.Null(m.Year);
        Assert.NotNull(m.ExtendedData);
    }

    [Fact]
    public void Image_addresses_are_built_only_from_sane_ids()
    {
        Assert.Equal("https://images.igdb.com/igdb/image/upload/t_thumb/abc_-1.jpg", MetadataMapper.ImageUrl("abc_-1", "thumb"));
        Assert.Null(MetadataMapper.ImageUrl(null, "thumb"));
        Assert.Null(MetadataMapper.ImageUrl("a/b", "thumb"));
        Assert.Null(MetadataMapper.ImageUrl("a b", "thumb"));
        Assert.Null(MetadataMapper.ImageUrl("", "thumb"));
        Assert.Equal("https://www.youtube.com/watch?v=abc123", MetadataMapper.YouTube("abc123"));
        Assert.Null(MetadataMapper.YouTube("a b"));
    }

    [Fact]
    public void Years_come_from_unix_seconds_in_utc()
    {
        Assert.Equal(2010, MetadataMapper.YearOf(1287446400));
        Assert.Equal(1999, MetadataMapper.YearOf(946684799));   // 1999-12-31T23:59:59Z
        Assert.Equal(2000, MetadataMapper.YearOf(946684800));
        Assert.Null(MetadataMapper.YearOf(long.MaxValue));
    }
}
