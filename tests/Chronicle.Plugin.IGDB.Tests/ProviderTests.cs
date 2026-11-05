using System.Net;
using System.Text.Json;
using Chronicle.Plugin.IGDB;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.IGDB.Tests;

public class ProviderTests
{
    private const string Exact = "alternative_names.name ~";   // marks the exact-name query
    private const string Fuzzy = "search \"";                    // marks the fuzzy search

    /// <summary>A fake IGDB that knows Fallout: New Vegas: the exact-name query, the fuzzy search and the full multiquery.</summary>
    private static FakeIgdb Fallout(Func<FakeIgdb.Reply>? multi = null) =>
        new FakeIgdb().Token()
            .OnApi("games", b => b.Contains(Exact) && b.Contains("Fallout: New Vegas"), () => new FakeIgdb.Reply(HttpStatusCode.OK, Harness.LeanFallout()))
            .OnApi("games", b => b.StartsWith(Fuzzy), () => new FakeIgdb.Reply(HttpStatusCode.OK, Harness.LeanFallout()))
            .OnApi("games", b => b.Contains(Exact), () => new FakeIgdb.Reply(HttpStatusCode.OK, "[]"))
            .OnApi("multiquery", _ => true, multi ?? (() => new FakeIgdb.Reply(HttpStatusCode.OK, Harness.FullAnswer())));

    // ── search: the enrichment pass ───────────────────────────────────────────

    [Fact]
    public async Task Enrichment_finds_the_game_by_exact_name_and_returns_it_complete_in_two_requests()
    {
        var fake = Fallout();
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(Harness.Ctx("Fallout: New Vegas", 2010));

        Assert.Equal(2, fake.Api.Count());
        Assert.Contains("games", fake.Api.First().Url);
        Assert.EndsWith("/multiquery", fake.Api.Last().Url);
        var top = found[0];
        Assert.Equal("16", top.Metadata.ExternalId);
        Assert.InRange(top.Score, 80, 100);
        var x = top.Metadata.ExtendedData!.Value;
        Assert.False(x.GetProperty("partial").GetBoolean());
        Assert.True(x.GetProperty("extras").GetBoolean());
        Assert.Equal(36, x.GetProperty("characters").GetArrayLength());
    }

    [Fact]
    public async Task The_follow_up_fetch_by_id_is_answered_from_the_search_with_no_request()
    {
        var fake = Fallout();
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(Harness.Ctx("Fallout: New Vegas", 2010));
        var before = fake.Api.Count();
        var byId = await p.GetByIdAsync(found[0].Metadata.ExternalId);
        Assert.Equal(before, fake.Api.Count());
        Assert.Equal(found[0].Metadata.Title, byId.Title);
        Assert.Equal(found[0].Metadata.Tags, byId.Tags);
    }

    [Fact]
    public async Task The_search_asks_for_the_title_and_its_alternative_spelling_in_one_query()
    {
        var fake = new FakeIgdb().Token().OnApi("games", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.OK, "[]"));
        var (p, _, _, _) = Harness.Provider(fake);
        await p.SearchAsync(Harness.Ctx("Baldur's Gate 3"));
        var body = fake.Api.First().Body;
        Assert.Contains("name ~ \"Baldur's Gate 3\"", body);
        Assert.Contains("name ~ \"Baldur's Gate III\"", body);
        Assert.Contains("alternative_names.name ~ \"Baldur's Gate III\"", body);
        Assert.Equal(1, fake.Api.Count(r => r.Body.Contains("where (")));
    }

    [Fact]
    public async Task When_no_exact_name_fits_the_fuzzy_search_is_tried_then_not_found_is_an_empty_list()
    {
        var fake = new FakeIgdb().Token().OnApi("games", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.OK, "[]"));
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(Harness.Ctx("Some Game IGDB Lacks"));
        Assert.Empty(found);
        Assert.Equal(2, fake.Api.Count());
        Assert.StartsWith("fields", fake.Api.First().Body);
        Assert.StartsWith("search \"", fake.Api.Last().Body);
    }

    [Fact]
    public async Task A_similar_but_different_title_is_refused_whatever_the_search_ranked_first()
    {
        // IGDB's fuzzy search returns sequels and spin-offs first; none of them is "Hades"
        var lean = """[{"id":1,"name":"Hades II","first_release_date":1745000000,"game_type":{"type":"Main Game"}},{"id":2,"name":"Hades' Star","game_type":{"type":"Main Game"}}]""";
        var fake = new FakeIgdb().Token().OnApi("games", b => b.StartsWith("fields"), () => new FakeIgdb.Reply(HttpStatusCode.OK, "[]"))
            .OnApi("games", b => b.StartsWith("search"), () => new FakeIgdb.Reply(HttpStatusCode.OK, lean));
        var (p, _, _, _) = Harness.Provider(fake);
        Assert.Empty(await p.SearchAsync(Harness.Ctx("Hades")));
        Assert.DoesNotContain(fake.Api, r => r.Url.EndsWith("multiquery"));
    }

    [Fact]
    public async Task A_candidate_that_scores_below_the_gate_is_not_fetched_in_full()
    {
        var lean = """[{"id":9,"name":"Fallout: New Vegas","first_release_date":1287446400,"game_type":{"type":"Main Game"}}]""";
        var fake = new FakeIgdb().Token().OnApiAny("games", lean);
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(Harness.Ctx("Fallout: New Vegas", 1999));   // the year is wrong: capped
        Assert.True(found.Count == 0 || found[0].Score < Matchers.FetchGate);
        Assert.DoesNotContain(fake.Api, r => r.Url.EndsWith("multiquery"));
    }

    [Fact]
    public async Task The_best_candidate_is_the_one_returned_in_full_and_the_others_stay_lean()
    {
        var doom = FakeIgdb.FixtureJson("P1_fixture_doom.json");
        var fake = new FakeIgdb().Token().OnApi("games", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.OK, doom.GetRawText()))
            .OnApi("multiquery", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.OK,
                "[{\"name\":\"game\",\"result\":[{\"id\":7351,\"name\":\"Doom\",\"first_release_date\":1463011200}]},{\"name\":\"ttb\",\"result\":[]},{\"name\":\"characters\",\"result\":[]}]"));
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(Harness.Ctx("Doom", 2016));
        Assert.Equal("7351", found[0].Metadata.ExternalId);
        Assert.False(found[0].Metadata.ExtendedData!.Value.GetProperty("partial").GetBoolean());
        Assert.All(found.Skip(1), c => Assert.True(c.Metadata.ExtendedData!.Value.GetProperty("partial").GetBoolean()));
        Assert.InRange(found.Count, 1, 5);
        Assert.True(found.SequenceEqual(found.OrderByDescending(c => c.Score)));
    }

    [Fact]
    public async Task A_year_and_a_platform_tag_in_the_title_are_read_not_searched_for()
    {
        var fake = Fallout();
        var (p, _, _, _) = Harness.Provider(fake);
        await p.SearchAsync(Harness.Ctx("Fallout: New Vegas (2010) [PC]", enrichment: true));
        var body = fake.Api.First().Body;
        Assert.DoesNotContain("2010", body);
        Assert.DoesNotContain("[PC]", body);
        Assert.Contains("name ~ \"Fallout: New Vegas\"", body);
    }

    [Fact]
    public async Task The_cleaned_alternative_title_from_Chronicle_is_preferred_to_the_raw_name()
    {
        var fake = Fallout();
        var (p, _, _, _) = Harness.Provider(fake);
        await p.SearchAsync(Harness.Ctx("fallout.new.vegas.[GOG]", alt: ["Fallout: New Vegas"]));
        Assert.Contains("name ~ \"Fallout: New Vegas\"", fake.Api.First().Body);
    }

    [Fact]
    public async Task A_hostile_title_reaches_IGDB_as_one_harmless_string()
    {
        var fake = new FakeIgdb().Token().OnApi("games", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.OK, "[]"));
        var (p, _, _, _) = Harness.Provider(fake);
        await p.SearchAsync(Harness.Ctx("x\"; where id > 0; fields *; limit 500; \" | name ~ \"y"));
        foreach (var r in fake.Api)
        {
            var outside = System.Text.RegularExpressions.Regex.Replace(r.Body, "\"[^\"]*\"", "\"\"");
            Assert.DoesNotContain("limit 500", outside);
            Assert.DoesNotContain("\\", r.Body);
            Assert.Equal(1, outside.Split("limit ").Length - 1);
        }
    }

    [Fact]
    public async Task An_empty_or_blank_name_makes_no_request_and_a_foreign_type_is_not_searched()
    {
        var fake = Fallout();
        var (p, _, _, _) = Harness.Provider(fake);
        Assert.Empty(await p.SearchAsync(Harness.Ctx("")));
        Assert.Empty(await p.SearchAsync(Harness.Ctx("   ")));
        Assert.Empty(await p.SearchAsync(Harness.Ctx("\";\\")));
        Assert.Empty(await p.SearchAsync(Harness.Ctx("Fallout: New Vegas", type: "movies")));
        Assert.Empty(fake.All);
        var _ = await p.SearchAsync(Harness.Ctx("Fallout: New Vegas", type: "games"));   // the alias is the same type
        Assert.NotEmpty(fake.Api);
    }

    // ── search: the interactive path ──────────────────────────────────────────

    [Fact]
    public async Task An_interactive_search_makes_exactly_one_request_and_returns_lean_candidates()
    {
        var fake = Fallout();
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(Harness.Ctx("Fallout: New Vegas", enrichment: false));
        Assert.Single(fake.Api);
        Assert.NotEmpty(found);
        Assert.True(found[0].Metadata.ExtendedData!.Value.GetProperty("partial").GetBoolean());
        Assert.Equal("Fallout: New Vegas", found[0].Metadata.Title);
        Assert.NotNull(found[0].Metadata.PosterUrl);
    }

    [Fact]
    public async Task An_interactive_search_with_nothing_found_tries_the_fuzzy_search_once_and_stops_at_two_requests()
    {
        var fake = new FakeIgdb().Token().OnApi("games", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.OK, "[]"));
        var (p, _, _, _) = Harness.Provider(fake);
        Assert.Empty(await p.SearchAsync(Harness.Ctx("Nothing Like It", enrichment: false)));
        Assert.Equal(2, fake.Api.Count());   // the exact query, then the fuzzy search: two quick requests fit the 3.5 seconds
        Assert.StartsWith("search \"", fake.Api.Last().Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task An_interactive_search_never_throws(HttpStatusCode status)
    {
        var fake = new FakeIgdb().Token().OnApiAny("games", "[]", status);
        var (p, _, _, _) = Harness.Provider(fake);
        Assert.Empty(await p.SearchAsync(Harness.Ctx("Hades", enrichment: false)));
    }

    [Fact]
    public async Task An_interactive_search_that_hangs_returns_nothing_inside_its_deadline()
    {
        var fake = new FakeIgdb { HangApi = true }.Token();
        var (p, _, _, _) = Harness.Provider(fake);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(await p.SearchAsync(Harness.Ctx("Hades", enrichment: false)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4.5), sw.Elapsed.ToString());   // the host gives an interactive search 4 seconds
    }

    // ── search: failures on the enrichment path ───────────────────────────────

    [Fact]
    public async Task A_rate_limit_during_enrichment_is_a_429_pause()
    {
        var fake = new FakeIgdb().Token().OnApiAny("games", "{}", HttpStatusCode.TooManyRequests);
        var (p, _, _, _) = Harness.Provider(fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.SearchAsync(Harness.Ctx("Hades")));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task An_outage_during_enrichment_is_a_pause_with_no_status_never_a_retry_against_the_item()
    {
        var fake = new FakeIgdb().Token().OnApiAny("games", "oops", HttpStatusCode.BadGateway);
        var (p, _, _, _) = Harness.Provider(fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.SearchAsync(Harness.Ctx("Hades")));
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task A_deadline_that_passes_is_a_pause_not_the_hosts_not_found()
    {
        var fake = new FakeIgdb { HangApi = true }.Token();
        var s = Harness.Cfg(x => x with { RequestTimeout = TimeSpan.FromSeconds(20), CallDeadline = TimeSpan.FromMilliseconds(200) });
        var (p, _, _, _) = Harness.Provider(fake, s);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.SearchAsync(Harness.Ctx("Hades")));
        Assert.Null(ex.StatusCode);
        Assert.Contains("did not finish", ex.Message);
    }

    [Fact]
    public async Task A_400_on_a_search_is_never_turned_into_an_empty_list()
    {
        // an empty list would be NotFound, which the host never retries: a deprecated field must not turn a library permanently NotFound
        var fake = new FakeIgdb().Token().OnApiAny("games", """[{"title":"Invalid Field","status":400,"cause":"Invalid field name: 'summary'"}]""", HttpStatusCode.BadRequest);
        var (p, _, _, _) = Harness.Provider(fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.SearchAsync(Harness.Ctx("Hades")));
        Assert.Null(ex.StatusCode);
        Assert.Equal("badrequest", ex.Data[Fail.KindKey]);
    }

    [Fact]
    public async Task Missing_credentials_during_enrichment_is_an_authentication_failure_with_no_request()
    {
        var fake = Fallout();
        var (p, _, _, _) = Harness.Provider(fake, Harness.Cfg(s => s with { ClientId = "", ClientSecret = "" }));
        await Assert.ThrowsAsync<PluginAuthException>(() => p.SearchAsync(Harness.Ctx("Hades")));
        await Assert.ThrowsAsync<PluginAuthException>(() => p.GetByIdAsync("16"));
        Assert.Empty(fake.All);
        Assert.False(await p.HealthCheckAsync());
        Assert.Empty(await p.SearchAsync(Harness.Ctx("Hades", enrichment: false)));
        Assert.Empty(fake.All);
    }

    [Fact]
    public async Task Rejected_credentials_cost_one_call_not_one_per_queued_item()
    {
        var fake = new FakeIgdb().On(r => r.Url.Contains("oauth2"), _ => new FakeIgdb.Reply(HttpStatusCode.Forbidden, "{\"message\":\"invalid client secret\"}"));
        var (p, _, _, _) = Harness.Provider(fake);
        for (var i = 0; i < 50; i++) await Assert.ThrowsAsync<PluginAuthException>(() => p.SearchAsync(Harness.Ctx("Hades " + i)));
        Assert.Single(fake.All);   // one token request, ever, until the settings change
        Assert.False(await p.HealthCheckAsync());
    }

    // ── known ids ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_known_IGDB_id_is_an_exact_lookup_with_no_name_query()
    {
        var fake = Fallout();
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(Harness.Ctx("anything at all", known: new Dictionary<string, string> { ["igdb"] = "16" }));
        Assert.Single(fake.Api);
        Assert.EndsWith("/multiquery", fake.Api.First().Url);
        Assert.Contains("where id = 16;", fake.Api.First().Body);
        Assert.True(found[0].IdentifierMatch);
        Assert.Equal(100, found[0].Score);
    }

    [Fact]
    public async Task A_known_igdb_id_that_no_longer_exists_falls_back_to_the_name()
    {
        var calls = 0;
        var fake = Fallout(() => ++calls == 1
            ? new FakeIgdb.Reply(HttpStatusCode.OK, "[{\"name\":\"game\",\"result\":[]},{\"name\":\"ttb\",\"result\":[]},{\"name\":\"characters\",\"result\":[]}]")
            : new FakeIgdb.Reply(HttpStatusCode.OK, Harness.FullAnswer()));
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(Harness.Ctx("Fallout: New Vegas", 2010, known: new Dictionary<string, string> { ["igdb"] = "999999" }));
        Assert.Equal("16", found[0].Metadata.ExternalId);
        Assert.False(found[0].IdentifierMatch);
    }

    [Fact]
    public async Task A_known_Steam_id_is_looked_up_through_external_games()
    {
        var steamHit = """[{"id":1,"uid":"22380","game":{"id":16,"name":"Fallout: New Vegas","game_type":{"type":"Main Game"},"first_release_date":1287446400}},{"id":2,"uid":"22380","game":{"id":99,"name":"FNV Bundle","game_type":{"type":"Bundle"},"first_release_date":1100000000}}]""";
        var fake = new FakeIgdb().Token()
            .OnApi("external_game_sources", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.OK, """[{"id":3,"name":"GiantBomb"},{"id":1,"name":"Steam"}]"""))
            .OnApi("external_games", b => b.Contains("external_game_source = 1 & uid = \"22380\""), () => new FakeIgdb.Reply(HttpStatusCode.OK, steamHit))
            .OnApi("multiquery", b => b.Contains("where id = 16;"), () => new FakeIgdb.Reply(HttpStatusCode.OK, Harness.FullAnswer()));
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(Harness.Ctx("whatever", known: new Dictionary<string, string> { ["steam"] = "steam:22380" }));
        Assert.Equal("16", found[0].Metadata.ExternalId);   // the main game, not the bundle that shares the uid
        Assert.True(found[0].IdentifierMatch);
        Assert.Equal(3, fake.Api.Count());
        // the source id is remembered: a second lookup does not ask for it again
        await p.SearchAsync(Harness.Ctx("whatever", known: new Dictionary<string, string> { ["steam"] = "22380" }));
        Assert.Single(fake.Api.Where(r => r.Url.EndsWith("external_game_sources")));
    }

    [Fact]
    public async Task Standalone_other_sources_ids_are_optional_and_read_defensively()
    {
        var results = new List<(string Id, int Score)>();
        foreach (var known in new IReadOnlyDictionary<string, string>?[]
        {
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["tmdb"] = "550", ["lastfm"] = "x", ["musicbrainz"] = "artist:abc" },
            new Dictionary<string, string> { ["igdb"] = "abc", ["steam"] = "not digits" },
            new Dictionary<string, string> { ["igdb"] = "-3", ["steam"] = "0" },
            new Dictionary<string, string> { ["igdb"] = "", ["steam"] = "" },
            new Dictionary<string, string> { ["igdb"] = "1.5", ["steam"] = "99999999999999999999999999" },
        })
        {
            var fake = Fallout();
            var (p, _, _, _) = Harness.Provider(fake);
            var found = await p.SearchAsync(Harness.Ctx("Fallout: New Vegas", 2010, known: known));
            Assert.DoesNotContain(fake.Api, r => r.Url.EndsWith("external_games"));
            results.Add((found[0].Metadata.ExternalId, found[0].Score));
        }
        Assert.Single(results.Distinct());   // the same candidate with the same score every time
    }

    // ── fetch by id ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Fetching_by_id_returns_the_complete_game_in_one_request()
    {
        var fake = Fallout();
        var (p, _, _, _) = Harness.Provider(fake);
        var m = await p.GetByIdAsync("igdb:16");
        Assert.Single(fake.Api);
        Assert.Equal("Fallout: New Vegas", m.Title);
        Assert.Equal(36, m.ExtendedData!.Value.GetProperty("characters").GetArrayLength());
    }

    [Fact]
    public async Task Fetching_an_address_asks_IGDB_for_its_slug()
    {
        var fake = new FakeIgdb().Token()
            .OnApi("games", b => b.Contains("slug = \"fallout-new-vegas\""), () => new FakeIgdb.Reply(HttpStatusCode.OK, "[{\"id\":16,\"name\":\"Fallout: New Vegas\"}]"))
            .OnApi("multiquery", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.OK, Harness.FullAnswer()));
        var (p, _, _, _) = Harness.Provider(fake);
        var m = await p.GetByIdAsync("https://www.igdb.com/games/Fallout-New-Vegas");
        Assert.Equal("16", m.ExternalId);
        Assert.Equal(2, fake.Api.Count());
    }

    [Fact]
    public async Task A_game_IGDB_does_not_have_is_a_KeyNotFound_so_a_stored_id_is_cleared()
    {
        var fake = new FakeIgdb().Token().OnApi("multiquery", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.OK,
            "[{\"name\":\"game\",\"result\":[]},{\"name\":\"ttb\",\"result\":[]},{\"name\":\"characters\",\"result\":[]}]"));
        var (p, _, _, _) = Harness.Provider(fake);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => p.GetByIdAsync("999999"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("tmdb:550")]
    [InlineData("https://evil.test/games/hades")]
    public async Task An_id_in_no_known_form_is_an_ArgumentException_before_any_request(string id)
    {
        var fake = Fallout();
        var (p, _, _, _) = Harness.Provider(fake);
        await Assert.ThrowsAsync<ArgumentException>(() => p.GetByIdAsync(id));
        Assert.Empty(fake.All);
    }

    [Fact]
    public async Task A_malformed_multiquery_answer_is_a_pause_never_a_not_found()
    {
        foreach (var body in new[] { "{}", "[1,2]", "[{\"name\":\"ttb\",\"result\":[]}]", "[{\"name\":\"game\",\"result\":{}}]" })
        {
            var fake = new FakeIgdb().Token().OnApi("multiquery", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.OK, body));
            var (p, _, _, _) = Harness.Provider(fake);
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("16"));
            Assert.Null(ex.StatusCode);
        }
    }

    // ── field drift ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_field_IGDB_drops_costs_only_its_group_and_one_retry()
    {
        var calls = 0;
        var fake = new FakeIgdb().Token().OnApi("multiquery", _ => true, () => ++calls == 1
            ? new FakeIgdb.Reply(HttpStatusCode.BadRequest, """[{"title":"Invalid Field","status":400,"cause":"Invalid field name: 'involved_companies.company.logo'"}]""")
            : new FakeIgdb.Reply(HttpStatusCode.OK, Harness.FullAnswer()));
        var (p, _, _, _) = Harness.Provider(fake);
        var m = await p.GetByIdAsync("16");
        Assert.Equal(2, fake.Api.Count());
        Assert.Contains("company.logo", fake.Api.First().Body);
        Assert.DoesNotContain("company.logo", fake.Api.Last().Body);
        Assert.Contains("involved_companies.company.*", fake.Api.Last().Body);   // the rest of the group's neighbours survive
        Assert.Equal(["company_logos"], p.DisabledGroups);
        Assert.Equal("Fallout: New Vegas", m.Title);

        // the next game does not repeat the mistake
        await p.GetByIdAsync("17");
        Assert.DoesNotContain("company.logo", fake.Api.Last().Body);
        Assert.Equal(3, fake.Api.Count());
    }

    [Fact]
    public async Task A_rejected_field_in_no_known_group_switches_every_expansion_off_and_keeps_fields_star()
    {
        var calls = 0;
        var fake = new FakeIgdb().Token().OnApi("multiquery", _ => true, () => ++calls == 1
            ? new FakeIgdb.Reply(HttpStatusCode.BadRequest, """[{"title":"Invalid Field","cause":"Invalid field name: 'something_new'"}]""")
            : new FakeIgdb.Reply(HttpStatusCode.OK, Harness.FullAnswer()));
        var (p, _, _, _) = Harness.Provider(fake);
        await p.GetByIdAsync("16");
        Assert.Contains("fields *;", fake.Api.Last().Body);
        Assert.DoesNotContain("cover.*", fake.Api.Last().Body);
        Assert.Equal(Queries.Groups.Length, p.DisabledGroups.Count);
    }

    [Fact]
    public async Task A_second_rejection_is_a_pause_never_an_empty_result()
    {
        var fake = new FakeIgdb().Token().OnApi("multiquery", _ => true, () =>
            new FakeIgdb.Reply(HttpStatusCode.BadRequest, """[{"title":"Invalid Field","cause":"Invalid field name: 'summary'"}]"""));
        var (p, _, _, _) = Harness.Provider(fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("16"));
        Assert.Null(ex.StatusCode);
        Assert.Equal(2, fake.Api.Count());   // one retry, no more
    }

    // ── poison items ──────────────────────────────────────────────────────────

    [Fact]
    public async Task One_record_that_keeps_failing_is_counted_against_its_item_so_it_cannot_stall_the_queue()
    {
        var fail = true;
        var fake = new FakeIgdb().Token()
            .OnApi("multiquery", _ => true, () => fail
                ? new FakeIgdb.Reply(HttpStatusCode.InternalServerError, "boom")
                : new FakeIgdb.Reply(HttpStatusCode.OK, Harness.FullAnswer()))
            .OnApi("games", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.OK, "[{\"id\":1}]"));   // IGDB answers other requests fine
        var (p, client, _, clock) = Harness.Provider(fake);

        var first = await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("16"));
        Assert.Null(first.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(await p.HealthCheckAsync());   // IGDB works for others...
        var second = await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("16"));   // ...and this record fails again
        Assert.Null(second.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(await p.HealthCheckAsync());
        var third = await Assert.ThrowsAsync<InvalidDataException>(() => p.GetByIdAsync("16"));   // a plain exception: the host counts a retry and ends in Exhausted
        Assert.Contains("16", third.Message);

        // a good answer clears the count
        fail = false;
        clock.Advance(TimeSpan.FromMinutes(10));
        await p.GetByIdAsync("16");
    }

    [Fact]
    public async Task Fail_fast_calls_during_a_cool_down_do_not_count_against_an_item()
    {
        var fake = new FakeIgdb().Token().OnApi("multiquery", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.InternalServerError, "boom"));
        var (p, _, _, _) = Harness.Provider(fake);
        await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("16"));
        for (var i = 0; i < 10; i++) await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("16"));   // the cool-down answers, nothing is sent
        Assert.Single(fake.Api);
    }

    [Fact]
    public async Task A_rate_limit_never_counts_against_an_item()
    {
        var fake = new FakeIgdb().Token().OnApi("multiquery", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.TooManyRequests, "{}"));
        var (p, _, _, clock) = Harness.Provider(fake);
        for (var i = 0; i < 6; i++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("16"));
            clock.Advance(TimeSpan.FromMinutes(10));
        }
    }

    // ── health, images ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_health_check_reports_ok_and_failure_without_throwing()
    {
        var ok = new FakeIgdb().Token().OnApiAny("games", "[{\"id\":1}]");
        Assert.True(await Harness.Provider(ok).Provider.HealthCheckAsync());

        var down = new FakeIgdb().Token().OnApiAny("games", "x", HttpStatusCode.ServiceUnavailable);
        var (p, _, _, _) = Harness.Provider(down);
        Assert.False(await p.HealthCheckAsync());
        var before = down.All.Count;
        Assert.False(await p.HealthCheckAsync());   // cooling down: it does not ask again
        Assert.Equal(before, down.All.Count);
    }

    [Theory]
    [InlineData("http://images.igdb.com/igdb/image/upload/t_thumb/a.jpg")]
    [InlineData("https://images.igdb.com.evil.test/a.jpg")]
    [InlineData("https://evil.test/images.igdb.com/a.jpg")]
    [InlineData("https://user:pw@images.igdb.com/a.jpg")]
    [InlineData("https://images.igdb.com:8443/a.jpg")]
    [InlineData("//images.igdb.com/igdb/image/upload/t_thumb/a.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task Images_are_fetched_only_from_images_igdb_com_over_https(string url)
    {
        var (p, _, _, _) = Harness.Provider(Fallout());
        await Assert.ThrowsAsync<ArgumentException>(() => p.GetImageAsync(url));
    }
}
