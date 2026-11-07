using Chronicle.Plugin.IGDB;

namespace Chronicle.Plugin.IGDB.Tests;

public class QueriesTests
{
    [Fact]
    public void ExactName_asks_for_the_name_or_an_alternative_name_most_rated_first()
    {
        var q = Queries.ExactName(["Final Fantasy 7", "Final Fantasy VII"], 10);
        Assert.Contains("where (name ~ \"Final Fantasy 7\" | alternative_names.name ~ \"Final Fantasy 7\" | name ~ \"Final Fantasy VII\" | alternative_names.name ~ \"Final Fantasy VII\")", q);
        Assert.Contains("sort total_rating_count desc", q);
        Assert.EndsWith("limit 10;", q);
    }

    [Fact]
    public void ExactName_drops_repeats_and_refuses_an_empty_title()
    {
        var q = Queries.ExactName(["Hades", "hades", "  "], 5);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(q, "(?<!alternative_names\\.)name ~").Count);
        Assert.Throws<ArgumentException>(() => Queries.ExactName(["", "\"\\;"], 5));
    }

    [Theory]
    [InlineData("x\"; where id > 0; fields *; limit 500; \"")]
    [InlineData("a\\\" ; drop")]
    [InlineData("line\nbreak\" ; fields *")]
    public void A_hostile_title_becomes_one_harmless_string_in_every_query(string hostile)
    {
        foreach (var q in new[] { Queries.ExactName([hostile], 10), Queries.Fuzzy(hostile, 10) })
        {
            var quoted = System.Text.RegularExpressions.Regex.Matches(q, "\"([^\"]*)\"");
            Assert.All(quoted.Select(m => m.Groups[1].Value), v => Assert.DoesNotContain(";", v));
            Assert.DoesNotContain('\n', q);
            Assert.DoesNotContain("\\", q);
            // exactly the clauses the plugin wrote, nothing more
            var outside = System.Text.RegularExpressions.Regex.Replace(q, "\"[^\"]*\"", "\"\"");   // the clauses, with the quoted text taken out
            Assert.Equal(1, outside.Split("limit ").Length - 1);
            Assert.Equal(q.StartsWith("search") ? 0 : 1, outside.Split("where ").Length - 1);
        }
    }

    [Fact]
    public void Fuzzy_search_and_ids_are_well_formed()
    {
        Assert.Equal("search \"Doom\"; fields " + Queries.Lean + "; limit 7;", Queries.Fuzzy("Doom", 7));
        Assert.Equal("fields " + Queries.Lean + "; where id = 1942;", Queries.ById(1942));
        Assert.Throws<ArgumentException>(() => Queries.Fuzzy(" ; ", 5));
    }

    [Theory]
    [InlineData("the-witcher-3-wild-hunt", true)]
    [InlineData("doom--2016", true)]
    [InlineData("", false)]
    [InlineData("Has Space", false)]
    [InlineData("a\"b", false)]
    [InlineData("UPPER", false)]
    [InlineData("under_score", false)]
    public void Slugs_are_lower_case_letters_digits_and_hyphens(string slug, bool ok)
    {
        Assert.Equal(ok, Queries.IsSlug(slug));
        if (!ok) Assert.Throws<ArgumentException>(() => Queries.BySlug(slug));
        else Assert.Contains($"slug = \"{slug}\"", Queries.BySlug(slug));
    }

    [Fact]
    public void A_steam_lookup_takes_digits_only()
    {
        Assert.Contains("external_game_source = 1 & uid = \"22380\"", Queries.BySteam(1, "22380"));
        Assert.Throws<ArgumentException>(() => Queries.BySteam(1, "22380\" | uid = \"1"));
        Assert.Throws<ArgumentException>(() => Queries.BySteam(1, ""));
    }

    [Fact]
    public void The_full_query_sends_fields_star_and_every_group_and_asks_for_extras_in_one_request()
    {
        var q = Queries.Full(16, new HashSet<string>(), timeToBeat: true);
        Assert.Contains("query games \"game\" { fields *, cover.*", q);
        Assert.Contains("involved_companies.company.logo.*", q);
        Assert.Contains("game_localizations.cover.*", q);
        Assert.Contains("age_ratings.rating_content_descriptions.*", q);
        Assert.Contains("where id = 16;", q);
        Assert.Contains("query game_time_to_beats \"ttb\" { fields *; where game_id = 16; };", q);
        Assert.Contains("query characters \"characters\" { fields *, mug_shot.*, character_gender.*, character_species.*; where games = (16); limit 500; offset 0; };", q);
        Assert.DoesNotContain("query game_time_to_beats", Queries.Full(16, new HashSet<string>(), timeToBeat: false));
    }

    [Fact]
    public void A_disabled_group_is_left_out_and_nothing_deprecated_is_ever_named()
    {
        var q = Queries.Full(16, new HashSet<string> { "company_logos", "media" }, true);
        Assert.DoesNotContain("company.logo", q);
        Assert.DoesNotContain("screenshots", q);
        Assert.Contains("involved_companies.company.*", q);
        // a deprecated field is never NAMED: whole words only (game_status, rating_category, collections... are fine)
        var deprecated = new System.Text.RegularExpressions.Regex(@"(?<![\w.])(category|follows|status|collection|artwork_type|media)(?![\w.])");
        foreach (var body in new[] { q, Queries.Full(1, new HashSet<string>(), true), Queries.Lean, Queries.ExactName(["x"], 1), Queries.Fuzzy("x", 1) })
            Assert.False(deprecated.IsMatch(body), body);
    }

    [Fact]
    public void Every_expansion_group_is_named_once_and_nonempty()
    {
        Assert.Equal(Queries.Groups.Length, Queries.Groups.Select(g => g.Name).Distinct().Count());
        Assert.All(Queries.Groups, g => Assert.NotEmpty(g.Fields));
    }

    [Theory]
    [InlineData("""[{"title":"Invalid Field","status":400,"cause":"Invalid field name: 'this_field_does_not_exist'"}]""", "this_field_does_not_exist")]
    [InlineData("""[{"cause":"Invalid field name: 'involved_companies.company.logo'"}]""", "involved_companies.company.logo")]
    [InlineData("""[{"title":"Syntax Error","cause":"Mismatched input"}]""", null)]
    [InlineData("", null)]
    public void The_rejected_field_is_read_from_the_400_body(string body, string? field) =>
        Assert.Equal(field, Queries.FieldFromError(body));

    [Theory]
    [InlineData("involved_companies.company.logo", "company_logos")]
    [InlineData("involved_companies.company", "companies")]
    [InlineData("involved_companies", "companies")]
    [InlineData("screenshots", "media")]
    [InlineData("release_dates.release_region", "release_extra")]
    [InlineData("release_dates", "releases")]
    [InlineData("game_localizations.cover", "names")]
    [InlineData("similar_games", "related")]
    [InlineData("no_such_thing", null)]
    public void The_most_specific_group_owns_a_rejected_field(string field, string? group) =>
        Assert.Equal(group, Queries.GroupOf(field));
}

public class IdFormsTests
{
    [Theory]
    [InlineData("1942", "Igdb", 1942)]
    [InlineData("igdb:1942", "Igdb", 1942)]
    [InlineData("IGDB: 7 ", "Igdb", 7)]
    [InlineData("game:55", "Igdb", 55)]
    [InlineData("steam:22380", "Steam", 22380)]
    public void Numbers_and_prefixed_ids_are_read(string input, string kind, long number)
    {
        var p = ParsedId.Parse(input);
        Assert.Equal(Enum.Parse<IdKind>(kind), p.Kind);
        Assert.Equal(number, p.Number);
    }

    [Theory]
    [InlineData("https://www.igdb.com/games/the-witcher-3-wild-hunt", "the-witcher-3-wild-hunt")]
    [InlineData("https://igdb.com/games/doom--2016/", "doom--2016")]
    [InlineData("igdb.com/games/hades", "hades")]
    [InlineData("https://www.igdb.com/games/Hades?utm=1#x", "hades")]
    [InlineData("HTTPS://WWW.IGDB.COM/games/portal-2", "portal-2")]
    public void A_game_page_address_is_read_for_its_slug_only(string input, string slug)
    {
        var p = ParsedId.Parse(input);
        Assert.Equal(IdKind.Slug, p.Kind);
        Assert.Equal(slug, p.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    [InlineData("12345678901234567890123")]
    [InlineData("abc")]
    [InlineData("steam:abc")]
    [InlineData("steam:")]
    [InlineData("steam:0")]
    [InlineData("tmdb:550")]
    [InlineData("http://www.igdb.com/games/hades")]
    [InlineData("https://igdb.com.evil.test/games/hades")]
    [InlineData("https://evil.test/igdb.com/games/hades")]
    [InlineData("https://user:pw@www.igdb.com/games/hades")]
    [InlineData("https://www.igdb.com:8443/games/hades")]
    [InlineData("https://www.igdb.com/")]
    [InlineData("https://www.igdb.com/characters/tabitha")]
    [InlineData("https://www.igdb.com/companies/obsidian")]
    [InlineData("https://www.igdb.com/platforms/ps4")]
    [InlineData("https://www.igdb.com/games")]
    [InlineData("https://www.igdb.com/games/has space")]
    [InlineData("https://www.igdb.com/games/under_score")]
    public void Anything_else_is_refused_with_a_message_that_lists_the_forms(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => ParsedId.Parse(input));
        Assert.True(ex.Message.Length > 20);
    }

    [Fact]
    public void Known_ids_are_read_defensively_and_never_throw()
    {
        Assert.Equal(1942, ParsedId.KnownIgdb(new Dictionary<string, string> { ["igdb"] = "1942" }));
        Assert.Equal(1942, ParsedId.KnownIgdb(new Dictionary<string, string> { ["igdb"] = "igdb:1942" }));
        Assert.Null(ParsedId.KnownIgdb(null));
        Assert.Null(ParsedId.KnownIgdb(new Dictionary<string, string>()));
        Assert.Null(ParsedId.KnownIgdb(new Dictionary<string, string> { ["tmdb"] = "1942" }));
        foreach (var bad in new[] { "", " ", "-3", "0", "1.5", "abc", "1e5", "99999999999999999999999" })
            Assert.Null(ParsedId.KnownIgdb(new Dictionary<string, string> { ["igdb"] = bad }));
        Assert.Equal("22380", ParsedId.KnownSteam(new Dictionary<string, string> { ["steam"] = "steam:22380" }));
        foreach (var bad in new[] { "", "abc", "0", "-1", "12 34" })
            Assert.Null(ParsedId.KnownSteam(new Dictionary<string, string> { ["steam"] = bad }));
    }
}
