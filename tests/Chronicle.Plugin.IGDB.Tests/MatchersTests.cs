using System.Text.Json;
using Chronicle.Plugin.IGDB;

namespace Chronicle.Plugin.IGDB.Tests;

public class MatchersTests
{
    private static readonly Settings S = Settings.Default;

    private static Wanted W(string title, int? year = null, string? platform = null) => Wanted.From(title, year, platform, S);

    private static GameBrief G(string name, int? year = null, string type = "Main Game", int ratings = 0, string[]? alts = null,
        bool version = false, string[]? platforms = null, int[]? years = null) =>
        new(1, name, alts ?? [], year, years ?? (year is null ? [] : [year.Value]), type, version,
            (platforms ?? []).Select(p => TextRules.Normalize(p)).ToList(), ratings);

    private static int Sc(Wanted w, GameBrief g) => Matchers.Score(w, g, S)?.Score ?? -1;

    [Fact]
    public void An_exact_title_with_the_right_year_passes_the_gate_comfortably()
    {
        var s = Sc(W("Hades", 2020), G("Hades", 2020, ratings: 800));
        Assert.InRange(s, 80, 100);
    }

    [Fact]
    public void An_exact_title_without_a_year_still_passes()
    {
        Assert.True(Sc(W("Hades"), G("Hades", 2020)) >= Matchers.FetchGate);
    }

    [Fact]
    public void A_similar_title_is_never_accepted()
    {
        Assert.Equal(-1, Sc(W("Hades"), G("Hades II", 2025)));
        Assert.Equal(-1, Sc(W("Doom"), G("Doom Eternal", 2020)));
        Assert.Equal(-1, Sc(W("Portal"), G("Portal Maze 2", 2015)));
        Assert.Equal(-1, Sc(W("Mega Man X"), G("Mega Man 10", 2010)));
    }

    [Fact]
    public void A_title_that_runs_on_after_a_space_is_another_game()
    {
        Assert.Equal(-1, Sc(W("Get Born"), G("Get Born Live", 2010)));
    }

    [Fact]
    public void Edition_words_are_ignored_on_either_side()
    {
        Assert.InRange(Sc(W("The Witcher 3: Wild Hunt - Game of the Year Edition"), G("The Witcher 3: Wild Hunt", 2015)), 55, 100);
        Assert.InRange(Sc(W("Dishonored"), G("Dishonored: Definitive Edition", 2015)), 50, 100);
    }

    [Fact]
    public void A_remaster_or_remake_is_not_the_original()
    {
        Assert.Equal(-1, Sc(W("Resident Evil 2"), G("Resident Evil 2 Remake", 2019)));
        Assert.Equal(-1, Sc(W("Resident Evil 2 (Remake)"), G("Resident Evil 2", 1998)));
    }

    [Fact]
    public void Roman_and_arabic_numbers_match_after_folding_but_score_below_a_literal_match()
    {
        var literal = Sc(W("Final Fantasy VII", 1997), G("Final Fantasy VII", 1997));
        var folded = Sc(W("Final Fantasy 7", 1997), G("Final Fantasy VII", 1997));
        Assert.True(folded >= Matchers.FetchGate);
        Assert.True(folded < literal);
    }

    [Fact]
    public void An_alternative_name_can_carry_the_match()
    {
        var m = Matchers.Score(W("Final Fantasy 7"), G("Final Fantasy VII: Remastered Cut", 1997, alts: ["Final Fantasy 7"]), S);
        Assert.NotNull(m);
        Assert.Contains("alternative name", m!.Reason);
    }

    [Fact]
    public void Only_the_main_title_equal_is_capped_below_the_gate_in_both_directions()
    {
        Assert.True(Sc(W("Fallout"), G("Fallout: New Vegas", 2010, ratings: 5000)) < Matchers.FetchGate);
        Assert.True(Sc(W("Fallout: New Vegas"), G("Fallout", 1997, ratings: 5000)) < Matchers.FetchGate);
        Assert.True(Sc(W("Fallout: New Vegas"), G("Fallout: Something Else", 2010, ratings: 5000)) < Matchers.FetchGate);
    }

    [Fact]
    public void A_year_that_disagrees_caps_the_score_below_the_gate()
    {
        Assert.True(Sc(W("Doom", 1993), G("Doom", 2016, ratings: 3000)) < Matchers.FetchGate);
        Assert.InRange(Sc(W("Doom", 2016), G("Doom", 2016, ratings: 3000)), 80, 100);
        Assert.InRange(Sc(W("Doom", 2017), G("Doom", 2016, ratings: 3000)), 80, 100);   // within one year
    }

    [Fact]
    public void A_release_date_year_counts_as_well_as_the_first_release()
    {
        Assert.InRange(Sc(W("Skyrim", 2016), G("Skyrim", 2011, years: [2011, 2016])), 70, 100);
    }

    [Fact]
    public void The_year_cap_is_waived_for_a_remaster_or_an_edition_title()
    {
        Assert.True(Sc(W("Skyrim", 2016), G("Skyrim", 2011, type: "Remaster")) >= Matchers.FetchGate);
        Assert.True(Sc(W("Skyrim Special Edition", 2016), G("Skyrim Special Edition", 2011)) >= Matchers.FetchGate);
    }

    [Fact]
    public void Popularity_breaks_ties_but_never_lifts_a_cap()
    {
        Assert.True(Sc(W("Doom"), G("Doom", 2016, ratings: 3000)) > Sc(W("Doom"), G("Doom", 1993, ratings: 5)));
        Assert.True(Sc(W("Doom", 1993), G("Doom", 2016, ratings: 1_000_000)) <= 40);
    }

    [Fact]
    public void A_platform_hint_is_a_bonus_never_a_penalty()
    {
        var with = Sc(W("Doom", 2016, "PlayStation 4"), G("Doom", 2016, platforms: ["PlayStation 4", "PC (Microsoft Windows)"]));
        var without = Sc(W("Doom", 2016, "PlayStation 4"), G("Doom", 2016, platforms: ["PC (Microsoft Windows)"]));
        var none = Sc(W("Doom", 2016), G("Doom", 2016, platforms: ["PC (Microsoft Windows)"]));
        Assert.True(with > without);
        Assert.Equal(none, without);
    }

    [Theory]
    [InlineData("DLC")]
    [InlineData("Expansion")]
    [InlineData("Bundle")]
    [InlineData("Mod")]
    [InlineData("Update")]
    [InlineData("Pack / Addon")]
    public void An_exact_title_only_type_matches_on_its_exact_title_with_a_penalty(string type)
    {
        var main = Sc(W("Doom Eternal: The Ancient Gods"), G("Doom Eternal: The Ancient Gods", 2020, type: "Main Game"));
        var dlc = Sc(W("Doom Eternal: The Ancient Gods"), G("Doom Eternal: The Ancient Gods", 2020, type: type));
        Assert.True(dlc > 0, "nothing is excluded");
        Assert.True(dlc < main);
        // ...and a merely similar title of such a type is refused
        Assert.Equal(-1, Sc(W("Doom Eternal"), G("Doom Eternal: The Ancient Gods", 2020, type: type)));
        Assert.Equal(-1, Sc(W("Final Fantasy 7"), G("Final Fantasy VII", 1997, type: type)));
    }

    [Fact]
    public void A_library_title_that_says_dlc_is_not_penalised_for_matching_a_dlc()
    {
        var plain = Sc(W("Blood and Wine DLC"), G("Blood and Wine DLC", 2016, type: "DLC"));
        Assert.True(plain >= Matchers.FetchGate);
    }

    [Fact]
    public void A_game_type_this_plugin_does_not_know_is_matched_on_an_exact_title_only()
    {
        Assert.True(Sc(W("Foo"), G("Foo", 2020, type: "Brand New Type")) > 0);
        Assert.Equal(-1, Sc(W("Foo 2"), G("Foo II", 2020, type: "Brand New Type")));
    }

    [Fact]
    public void A_version_of_a_game_is_penalised_unless_the_library_title_says_edition()
    {
        var version = Sc(W("Foo"), G("Foo", 2020, version: true));
        var main = Sc(W("Foo"), G("Foo", 2020));
        Assert.True(version < main);
    }

    [Fact]
    public void Brief_reads_a_real_record_and_skips_executable_names()
    {
        var game = FakeIgdb.FixtureJson("P13_expanded_game.json");
        var brief = GameBrief.From(game)!;
        Assert.Equal(16, brief.Id);
        Assert.Equal("Main Game", brief.TypeName);
        Assert.Equal(2010, brief.FirstYear);
        Assert.Contains("FNV", brief.AltNames);
        Assert.NotEmpty(brief.PlatformKeys);
        Assert.True(brief.RatingCount > 1000);
    }

    [Fact]
    public void Brief_tolerates_missing_and_oddly_typed_fields()
    {
        var odd = JsonDocument.Parse("""{"id":9,"name":"Odd","first_release_date":"soon","game_type":7,"alternative_names":[1,2],"platforms":null,"release_dates":[{"y":"x"}]}""").RootElement;
        var brief = GameBrief.From(odd);
        Assert.NotNull(brief);
        Assert.Null(brief!.FirstYear);
        Assert.Null(GameBrief.From(JsonDocument.Parse("""{"name":"No id"}""").RootElement));
        Assert.Null(GameBrief.From(JsonDocument.Parse("""{"id":3}""").RootElement));
    }

    [Fact]
    public void The_main_game_type_with_id_zero_is_read_as_a_main_game()
    {
        var game = JsonDocument.Parse("""{"id":5,"name":"Zero","game_type":{"id":0,"type":"Main Game"}}""").RootElement;
        Assert.Equal("Main Game", GameBrief.From(game)!.TypeName);
    }
}
