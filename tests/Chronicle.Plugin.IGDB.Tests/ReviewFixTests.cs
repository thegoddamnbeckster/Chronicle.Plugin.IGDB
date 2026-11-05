using System.Net;
using System.Text.Json;
using Chronicle.Plugin.IGDB;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.IGDB.Tests;

/// <summary>Regression tests for what the independent code review found (design section 21).</summary>
public class MatchingReviewTests
{
    private static readonly Settings S = Settings.Default;
    private static Wanted W(string title, int? year = null) => Wanted.From(title, year, null, S);

    private static GameBrief G(string name, int? year = null, string type = "Main Game", int ratings = 0, bool version = false) =>
        new(1, name, [], year, year is null ? [] : [year.Value], type, version, [], ratings);

    private static int Sc(Wanted w, GameBrief g) => Matchers.Score(w, g, S)?.Score ?? -1;

    [Theory]
    [InlineData("Resident Evil 4 Remake", "Resident Evil 4", "Remake")]
    [InlineData("Dead Space (Remake)", "Dead Space", "Remake")]
    [InlineData("Dead Space [Remake]", "Dead Space", "Remake")]
    [InlineData("Skyrim - Remastered", "Skyrim", "Remaster")]
    [InlineData("Resident Evil 4 Remastered", "Resident Evil 4", "Remake")]   // a store may say either
    public void A_stores_remake_or_remastered_label_matches_the_game_IGDB_records_as_a_remake_or_remaster(string library, string igdbName, string type)
    {
        Assert.True(Sc(W(library), G(igdbName, 2023, type)) >= Matchers.FetchGate, library);
    }

    [Theory]
    [InlineData("Resident Evil 4 Remake", "Resident Evil 4", "Main Game")]   // the 2005 original is not the remake
    [InlineData("Resident Evil 4 Remake", "Resident Evil 4", "Port")]
    [InlineData("Resident Evil 4 Remake", "Resident Evil 5", "Remake")]
    public void A_remake_label_does_not_match_a_game_that_is_not_a_remake_of_that_name(string library, string igdbName, string type) =>
        Assert.Equal(-1, Sc(W(library), G(igdbName, 2005, type)));

    [Fact]
    public void The_remake_base_is_one_of_the_names_asked_for()
    {
        Assert.Equal("Resident Evil 4", W("Resident Evil 4 Remake").ReworkBase);
        Assert.Null(W("Resident Evil 4").ReworkBase);
        Assert.Null(W("Remake Dungeon Quest").ReworkBase);   // only a trailing label counts
    }

    [Fact]
    public void An_edition_word_that_is_part_of_another_games_name_does_not_make_two_games_equal()
    {
        // "Pokemon Gold" is not "Pokemon Platinum Version"
        Assert.Equal(-1, Sc(W("Pokemon Gold"), G("Pokémon Platinum Version", 2008)));
        Assert.True(Sc(W("Pokemon Gold"), G("Pokémon Gold Version", 1999)) >= Matchers.FetchGate);
        Assert.Equal(-1, Sc(W("Pokemon Gold"), G("Pokémon Silver Version", 1999)));
        // "Tomb Raider" is not its remake "Tomb Raider: Anniversary"
        Assert.True(Sc(W("Tomb Raider"), G("Tomb Raider: Anniversary", 2007, "Remake")) < Matchers.FetchGate);
        // the base game of a series is not "Super Smash Bros. Ultimate"
        Assert.Equal(-1, Sc(W("Super Smash Bros. Ultimate"), G("Super Smash Bros.", 1999)));
        Assert.Equal("Super Smash Bros. Ultimate", W("Super Smash Bros. Ultimate").Stripped);
    }

    [Fact]
    public void The_exact_game_beats_its_base_game()
    {
        var royal = Sc(W("Persona 5 Royal"), G("Persona 5 Royal", 2019, "Expanded Game", ratings: 300));
        var baseGame = Sc(W("Persona 5 Royal"), G("Persona 5", 2016, ratings: 800));
        Assert.True(royal > baseGame, $"royal {royal} base {baseGame}");
        // a bare edition word at the end is part of the name, but "Edition" after it makes it one
        Assert.Equal("Dishonored", W("Dishonored Definitive Edition").Stripped);
        Assert.Equal("Hitman", W("Hitman GOTY").Stripped);
        Assert.Equal("Hitman Ultimate", W("Hitman Ultimate").Stripped);
    }

    [Fact]
    public void Dlc_words_are_whole_words_not_pieces_of_other_words()
    {
        Assert.False(W("Backpack Hero").SaysDlc);
        Assert.False(W("Updater Simulator").SaysDlc);
        Assert.False(W("Bundled Up").SaysDlc);
        Assert.True(W("Skyrim Dragonborn DLC").SaysDlc);
        Assert.True(W("Fallout 4 Season Pass").SaysDlc);
        // so a Bundle with the same name as a game is penalised below the game
        Assert.True(Sc(W("Backpack Hero"), G("Backpack Hero", 2023, "Bundle")) < Sc(W("Backpack Hero"), G("Backpack Hero", 2023)));
    }

    [Theory]
    [InlineData("Left 4 Dead 2", "Left 4 Dead II")]
    [InlineData("Baldur's Gate 3", "Baldur's Gate III")]
    [InlineData("Final Fantasy VII", "Final Fantasy 7")]
    [InlineData("Civilization VI: Gathering Storm", "Civilization 6: Gathering Storm")]
    public void Only_the_last_number_is_written_the_other_way(string title, string expected) =>
        Assert.Equal(expected, TextRules.SwapNumerals(title));

    [Theory]
    [InlineData("Mega Man 10")]
    [InlineData("FIFA 1")]
    [InlineData("Hades")]
    public void Ten_and_one_are_never_swapped(string title) => Assert.Null(TextRules.SwapNumerals(title));

    [Fact]
    public void Store_typography_is_made_plain_before_a_title_is_asked_for()
    {
        Assert.Equal("Assassin's Creed Odyssey", TextRules.CleanQueryText("Assassin’s Creed® Odyssey"));
        Assert.Equal("Half-Life", TextRules.CleanQueryText("Half‐Life™"));
        Assert.Equal("Pokémon", TextRules.CleanQueryText("Pokémon"));   // accents stay: IGDB spells them
    }

    [Fact]
    public void A_similar_title_is_offered_only_below_the_gate_and_only_when_every_typed_word_is_in_it()
    {
        var witcher = G("The Witcher 3: Wild Hunt", 2015, ratings: 3000);
        var m = Matchers.ScoreSimilar(W("witcher 3"), witcher)!;
        Assert.InRange(m.Score, 1, Matchers.FetchGate - 1);
        Assert.Contains("never accepted automatically", m.Reason);
        Assert.NotNull(Matchers.ScoreSimilar(W("Witcher III"), witcher));   // numerals are read either way
        Assert.Null(Matchers.ScoreSimilar(W("witcher 2"), witcher));
        Assert.Null(Matchers.ScoreSimilar(W("zelda"), witcher));
        // strict scoring never reaches the gate on it (a main-title match is capped at 45)
        Assert.InRange(Sc(W("witcher 3"), witcher), 1, Matchers.FetchGate - 1);
    }
}

public class ProviderReviewTests
{
    private static FakeIgdb Fake() => new FakeIgdb().Token();
    private static string Empty => "[]";

    private static FakeIgdb.Reply Ok(string json) => new(HttpStatusCode.OK, json);

    [Fact]
    public async Task A_colon_less_store_name_is_found_by_the_fuzzy_search_on_the_whole_title()
    {
        var fake = Fake()
            .OnApi("games", b => b.Contains("alternative_names.name ~"), () => Ok(Empty))
            .OnApi("games", b => b.StartsWith("search \"Batman Arkham City\""), () => Ok(Harness.LeanFallout().Replace("Fallout: New Vegas", "Batman: Arkham City")))
            .OnApi("multiquery", _ => true, () => Ok(Harness.FullAnswer()));
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(Harness.Ctx("Batman Arkham City"));
        Assert.Equal(3, fake.Api.Count());   // the exact query, the fuzzy search on the whole title, the full fetch
        Assert.True(found[0].Score >= Matchers.FetchGate);
    }

    [Fact]
    public async Task The_main_title_alone_is_a_last_resort_after_the_whole_title()
    {
        var fake = Fake().OnApi("games", _ => true, () => Ok(Empty));
        var (p, _, _, _) = Harness.Provider(fake);
        await p.SearchAsync(Harness.Ctx("Some Game: With A Subtitle"));
        var bodies = fake.Api.Select(r => r.Body).ToList();
        Assert.Equal(3, bodies.Count);
        Assert.StartsWith("search \"Some Game: With A Subtitle\"", bodies[1]);
        Assert.StartsWith("search \"Some Game\"", bodies[2]);
    }

    [Fact]
    public async Task A_person_typing_into_Add_Media_is_offered_similar_titles_to_choose_from_but_enrichment_is_not()
    {
        var lean = """[{"id":1942,"name":"The Witcher 3: Wild Hunt","first_release_date":1431907200,"game_type":{"type":"Main Game"},"total_rating_count":3000}]""";
        var fake = Fake().OnApi("games", b => b.Contains("alternative_names.name ~"), () => Ok(Empty)).OnApi("games", b => b.StartsWith("search"), () => Ok(lean));
        var (p, _, _, _) = Harness.Provider(fake);

        var typed = await p.SearchAsync(Harness.Ctx("witcher 3", enrichment: false));
        Assert.Equal("The Witcher 3: Wild Hunt", typed[0].Metadata.Title);
        Assert.True(typed[0].Score < Matchers.FetchGate);   // listed, never accepted automatically

        var enriching = await p.SearchAsync(Harness.Ctx("witcher 3", enrichment: true));
        Assert.All(enriching, c => Assert.True(c.Score < Matchers.FetchGate));   // strict: nothing here is good enough for Chronicle to accept
        Assert.DoesNotContain(fake.Api, r => r.Url.EndsWith("multiquery"));   // and nothing is fetched in full
    }

    [Fact]
    public async Task The_remake_label_asks_for_the_base_title_and_finds_the_remake_record()
    {
        var lean = """[{"id":7,"name":"Resident Evil 4","first_release_date":1679443200,"game_type":{"type":"Remake"},"total_rating_count":900}]""";
        var fake = Fake().OnApi("games", b => b.Contains("name ~ \"Resident Evil 4\""), () => Ok(lean))
            .OnApi("multiquery", _ => true, () => Ok("[{\"name\":\"game\",\"result\":[{\"id\":7,\"name\":\"Resident Evil 4\",\"game_type\":{\"type\":\"Remake\"}}]},{\"name\":\"ttb\",\"result\":[]},{\"name\":\"characters\",\"result\":[]}]"));
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(Harness.Ctx("Resident Evil 4 Remake", 2023));
        Assert.Equal("7", found[0].Metadata.ExternalId);
        Assert.True(found[0].Score >= Matchers.FetchGate);
    }

    // ── field drift ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_400_that_names_no_field_degrades_the_record_visibly_and_the_groups_come_back_after_an_hour()
    {
        var calls = 0;
        var fake = Fake().OnApi("multiquery", _ => true, () => ++calls == 1
            ? new FakeIgdb.Reply(HttpStatusCode.BadRequest, """[{"title":"Syntax Error","cause":"Mismatched input"}]""")
            : Ok(Harness.FullAnswer()));
        var (p, _, _, clock) = Harness.Provider(fake);

        var m = await p.GetByIdAsync("16");
        var x = m.ExtendedData!.Value;
        Assert.True(x.GetProperty("partial").GetBoolean());   // a re-sync will replace it
        Assert.True(x.GetProperty("degraded").GetArrayLength() > 5);
        Assert.Equal(Queries.Groups.Length, p.DisabledGroups.Count);

        await p.GetByIdAsync("17");
        Assert.DoesNotContain("cover.*", fake.Api.Last().Body);   // still off

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Empty(p.DisabledGroups);
        await p.GetByIdAsync("18");
        Assert.Contains("cover.*", fake.Api.Last().Body);   // asked for again
        var healed = (await p.GetByIdAsync("19")).ExtendedData!.Value;
        Assert.False(healed.GetProperty("partial").GetBoolean());
        Assert.False(healed.TryGetProperty("degraded", out _));
    }

    [Fact]
    public async Task A_renamed_characters_field_costs_only_the_characters_details_not_the_game()
    {
        var calls = 0;
        var fake = Fake().OnApi("multiquery", _ => true, () => ++calls == 1
            ? new FakeIgdb.Reply(HttpStatusCode.BadRequest, """[{"title":"Invalid Field","cause":"Invalid field name: 'mug_shot'"}]""")
            : Ok(Harness.FullAnswer()));
        var (p, _, _, _) = Harness.Provider(fake);
        var m = await p.GetByIdAsync("16");
        Assert.Equal(["characters"], p.DisabledGroups);
        var retry = fake.Api.Last().Body;
        Assert.Contains("cover.*", retry);   // the game keeps every expansion
        Assert.Contains("query characters \"characters\" { fields *; where games = (16); limit 500; };", retry);
        Assert.Equal(["characters"], m.ExtendedData!.Value.GetProperty("degraded").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void The_game_and_the_characters_expansions_are_separate_groups_and_franchise_is_expanded()
    {
        Assert.Contains(Queries.Groups, g => g.Name == "characters");
        Assert.Contains("franchise.*", Queries.Groups.SelectMany(g => g.Fields));
        var q = Queries.Full(1, new HashSet<string>(), true);
        Assert.Contains("mug_shot.*", q);
        Assert.DoesNotContain("mug_shot.*", q[..q.IndexOf("query game_time_to_beats", StringComparison.Ordinal)]);   // not in the game query
    }

    // ── the poison counter and the outage ─────────────────────────────────────

    [Fact]
    public async Task A_long_outage_never_exhausts_an_item()
    {
        var fake = Fake().OnApi("multiquery", _ => true, () => new FakeIgdb.Reply(HttpStatusCode.ServiceUnavailable, "down"));
        var (p, _, _, clock) = Harness.Provider(fake);
        for (var i = 0; i < 12; i++)   // three hours of 15-minute retries
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("16"));
            Assert.Null(ex.StatusCode);
            clock.Advance(TimeSpan.FromMinutes(15));
        }
    }

    [Fact]
    public async Task A_token_service_outage_is_never_counted_against_an_item()
    {
        var fake = new FakeIgdb().On(r => r.Url.Contains("oauth2"), _ => new FakeIgdb.Reply(HttpStatusCode.BadGateway, "bad gateway"));
        var (p, _, _, clock) = Harness.Provider(fake);
        for (var i = 0; i < 6; i++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("16"));
            clock.Advance(TimeSpan.FromMinutes(15));
        }
    }

    // ── the unexpected, the slow, the odd credentials ─────────────────────────

    [Fact]
    public async Task An_unexpected_exception_becomes_a_pause_never_a_permanent_Skipped()
    {
        var fake = Fake().OnApi("games", _ => true, () => throw new InvalidOperationException("something nobody planned for"));
        var (p, _, _, _) = Harness.Provider(fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.SearchAsync(Harness.Ctx("Hades")));
        Assert.Null(ex.StatusCode);
        Assert.IsNotType<InvalidOperationException>(ex);
    }

    [Fact]
    public async Task A_deadline_that_passes_opens_a_short_cool_down_so_the_hosts_retries_do_not_each_wait_again()
    {
        var fake = new FakeIgdb { HangApi = true }.Token();
        var s = Harness.Cfg(x => x with { RequestTimeout = TimeSpan.FromSeconds(20), CallDeadline = TimeSpan.FromMilliseconds(150) });
        var (p, client, _, _) = Harness.Provider(fake, s);
        await Assert.ThrowsAsync<HttpRequestException>(() => p.SearchAsync(Harness.Ctx("Hades")));
        Assert.True(client.CoolingDown);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<HttpRequestException>(() => p.SearchAsync(Harness.Ctx("Hades")));
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100), "the second call failed at once");
    }

    [Theory]
    [InlineData("sec ret")]
    [InlineData("secret\n")]
    [InlineData("sécret1234")]
    [InlineData("a​b")]
    public async Task A_credential_that_cannot_be_a_Twitch_credential_is_an_authentication_failure_with_no_request(string secret)
    {
        var fake = Fake().OnApiAny("games", Empty);
        var (p, _, _, _) = Harness.Provider(fake, Harness.Cfg(s => s with { ClientSecret = secret }));
        var ex = await Assert.ThrowsAsync<PluginAuthException>(() => p.SearchAsync(Harness.Ctx("Hades")));
        Assert.Contains("character", ex.Message);
        Assert.Empty(fake.All);
    }

    [Fact]
    public async Task A_search_that_found_nothing_is_remembered_for_a_minute_not_ten()
    {
        var fake = Fake().OnApi("games", _ => true, () => Ok(Empty));
        var (p, _, _, _) = Harness.Provider(fake);
        await p.SearchAsync(Harness.Ctx("Nothing Here", enrichment: false));
        var after = fake.Api.Count();
        await p.SearchAsync(Harness.Ctx("Nothing Here", enrichment: false));   // inside the minute: answered from memory
        Assert.Equal(after, fake.Api.Count());
    }
}

public class CredentialReviewTests
{
    [Fact]
    public async Task Saving_the_same_settings_again_clears_the_latch_because_the_Twitch_side_may_have_been_fixed()
    {
        var fake = new FakeIgdb().On(r => r.Url.Contains("oauth2"), _ => new FakeIgdb.Reply(HttpStatusCode.Forbidden, "{}"));
        var (client, _, _) = Harness.Client(fake);
        await Assert.ThrowsAsync<PluginAuthException>(() => client.QueryAsync("games", "fields id;", default));
        Assert.True(client.CredentialsRejected);

        client.Configure(Harness.Cfg());   // identical credentials
        Assert.False(client.CredentialsRejected);
        await Assert.ThrowsAsync<PluginAuthException>(() => client.QueryAsync("games", "fields id;", default));
        Assert.Equal(2, fake.Tokens.Count());   // it asked Twitch again
    }

    [Fact]
    public async Task A_latch_expires_after_fifteen_minutes_so_one_request_probes_again()
    {
        var fake = new FakeIgdb().On(r => r.Url.Contains("oauth2"), _ => new FakeIgdb.Reply(HttpStatusCode.Forbidden, "{}"));
        var (client, _, clock) = Harness.Client(fake);
        await Assert.ThrowsAsync<PluginAuthException>(() => client.QueryAsync("games", "fields id;", default));
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True(client.CredentialsRejected);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(client.CredentialsRejected);
        await Assert.ThrowsAsync<PluginAuthException>(() => client.QueryAsync("games", "fields id;", default));
        Assert.Equal(2, fake.Tokens.Count());
    }

    [Fact]
    public async Task A_stale_rejection_never_latches_credentials_saved_while_it_was_in_flight()
    {
        IgdbClient? client = null;
        var fake = new FakeIgdb().On(r => r.Url.Contains("oauth2"), r =>
        {
            client!.Configure(Harness.Cfg(s => s with { ClientSecret = "CORRECTED-SECRET-123456" }));   // the person saves a corrected secret meanwhile
            return new FakeIgdb.Reply(HttpStatusCode.Forbidden, "{}");   // the old, mistyped one is refused
        });
        (client, _, _) = Harness.Client(fake);
        await Assert.ThrowsAsync<PluginAuthException>(() => client.QueryAsync("games", "fields id;", default));
        Assert.False(client.CredentialsRejected);   // the new pair was not judged by the old answer
    }

    [Fact]
    public async Task A_token_minted_for_old_credentials_is_not_kept_when_the_settings_changed_meanwhile()
    {
        IgdbClient? client = null;
        var n = 0;
        var fake = new FakeIgdb().On(r => r.Url.Contains("oauth2"), r =>
        {
            if (++n == 1) client!.Configure(Harness.Cfg(s => s with { ClientSecret = "CORRECTED-SECRET-123456" }));
            return new FakeIgdb.Reply(HttpStatusCode.OK, $"{{\"access_token\":\"token-{n}-AAAAAAAAAAAA\",\"expires_in\":5400000}}");
        }).OnApiAny("games", "[]");
        (client, _, _) = Harness.Client(fake);
        await client.QueryAsync("games", "fields id;", default);
        Assert.Single(fake.Tokens);   // the first token was minted for the old pair and is not kept...
        Assert.Equal("Bearer token-1-AAAAAAAAAAAA", fake.Api.First().Authorization);   // (the call already in flight used what it had)
        await client.QueryAsync("games", "fields id;", default);
        Assert.Equal(2, fake.Tokens.Count());   // ...so the next call minted one for the new pair and used that
        Assert.Equal("Bearer token-2-AAAAAAAAAAAA", fake.Api.Last().Authorization);
    }

    [Theory]
    [InlineData(3600, 3240, 3300)]    // a short token is used for 90% of its life
    [InlineData(86400, 77760, 77800)]
    public async Task A_short_lived_token_is_used_for_ninety_percent_of_its_life_not_a_minute(int expiresIn, int usedFor, int gone)
    {
        var fake = new FakeIgdb().On(r => r.Url.Contains("oauth2"), _ => new FakeIgdb.Reply(HttpStatusCode.OK, $"{{\"access_token\":\"token-AAAAAAAAAAAA\",\"expires_in\":{expiresIn}}}")).OnApiAny("games", "[]");
        var (client, _, clock) = Harness.Client(fake);
        await client.QueryAsync("games", "fields id;", default);
        clock.Advance(TimeSpan.FromSeconds(usedFor - 60));
        await client.QueryAsync("games", "fields id;", default);
        Assert.Single(fake.Tokens);
        clock.Advance(TimeSpan.FromSeconds(gone - usedFor + 60));
        await client.QueryAsync("games", "fields id;", default);
        Assert.Equal(2, fake.Tokens.Count());
    }

    [Fact]
    public async Task A_missing_expires_in_is_not_a_reason_to_mint_every_minute()
    {
        var fake = new FakeIgdb().On(r => r.Url.Contains("oauth2"), _ => new FakeIgdb.Reply(HttpStatusCode.OK, "{\"access_token\":\"token-AAAAAAAAAAAA\"}")).OnApiAny("games", "[]");
        var (client, _, clock) = Harness.Client(fake);
        for (var i = 0; i < 5; i++) { await client.QueryAsync("games", "fields id;", default); clock.Advance(TimeSpan.FromMinutes(10)); }
        Assert.Single(fake.Tokens);
    }

    [Fact]
    public async Task One_callers_401_does_not_throw_away_a_token_another_caller_just_minted()
    {
        var tokenCount = 0;
        var fake = new FakeIgdb()
            .On(r => r.Url.Contains("oauth2"), _ => new FakeIgdb.Reply(HttpStatusCode.OK, $"{{\"access_token\":\"token-{++tokenCount}-AAAAAAAAAAAA\",\"expires_in\":5400000}}"))
            .On(r => r.Url.EndsWith("/games"), r => r.Authorization!.Contains("token-1-") ? new FakeIgdb.Reply(HttpStatusCode.Unauthorized, "{}") : new FakeIgdb.Reply(HttpStatusCode.OK, "[]"));
        var (client, _, _) = Harness.Client(fake);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.QueryAsync("games", "fields id;", default)));
        Assert.Equal(2, fake.Tokens.Count());   // the rejected token was replaced once, not once per caller
    }
}
