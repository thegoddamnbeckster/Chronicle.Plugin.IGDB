using Chronicle.Plugin.IGDB;

namespace Chronicle.Plugin.IGDB.Tests;

public class TextRulesTests
{
    private static readonly IReadOnlyList<string> Ed = TextRules.DefaultEditionWords;
    private static readonly IReadOnlyList<string> Never = TextRules.DefaultNeverStripWords;

    [Theory]
    [InlineData("Baldur’s Gate", "Baldur's Gate")]
    [InlineData("Sid Meier’s Civilization", "Sid Meier's Civilization")]
    [InlineData("Half‐Life", "Half-Life")]
    [InlineData("Café Dungeon", "Cafe Dungeon")]
    [InlineData("Doom™", "Doom")]
    [InlineData("Tom Clancy® Rainbow Six", "Tom Clancy Rainbow Six")]
    public void Fold_makes_typography_and_marks_plain(string input, string expected) =>
        Assert.Equal(TextRules.Normalize(expected), TextRules.Normalize(input));

    [Fact]
    public void Ampersand_and_and_are_equal() =>
        Assert.Equal(TextRules.Normalize("Ratchet & Clank"), TextRules.Normalize("Ratchet and Clank"));

    [Fact]
    public void A_leading_article_is_dropped_but_a_lone_word_is_kept()
    {
        Assert.Equal(TextRules.Normalize("The Witcher 3"), TextRules.Normalize("Witcher 3"));
        Assert.Equal("a", TextRules.Normalize("A"));
    }

    [Fact]
    public void Roman_numerals_fold_to_numbers_only_when_asked()
    {
        Assert.Equal(TextRules.Normalize("Final Fantasy 7", true), TextRules.Normalize("Final Fantasy VII", true));
        Assert.NotEqual(TextRules.Normalize("Final Fantasy 7"), TextRules.Normalize("Final Fantasy VII"));
        Assert.Equal(TextRules.Normalize("Civilization 6", true), TextRules.Normalize("Civilization VI", true));
    }

    [Fact]
    public void A_lone_I_V_or_X_is_never_a_numeral()
    {
        Assert.NotEqual(TextRules.Normalize("Mega Man X", true), TextRules.Normalize("Mega Man 10", true));
        Assert.NotEqual(TextRules.Normalize("I Am Alive", true), TextRules.Normalize("1 Am Alive", true));
        Assert.False(TextRules.TryRoman("x", out _));
        Assert.True(TextRules.TryRoman("xx", out var n) && n == 20);
    }

    [Theory]
    [InlineData("Baldur's Gate 3", "Baldur's Gate III")]
    [InlineData("Final Fantasy VII", "Final Fantasy 7")]
    [InlineData("Resident Evil 2: Remake", "Resident Evil II: Remake")]
    [InlineData("Civilization VI", "Civilization 6")]
    public void SwapNumerals_writes_sequel_numbers_the_other_way(string input, string expected) =>
        Assert.Equal(expected, TextRules.SwapNumerals(input));

    [Theory]
    [InlineData("Hades")]
    [InlineData("Mega Man X")]
    [InlineData("I Am Alive")]
    [InlineData("")]
    public void SwapNumerals_leaves_titles_without_sequel_numbers(string input) =>
        Assert.Null(TextRules.SwapNumerals(input));

    [Fact]
    public void SplitYear_reads_a_trailing_year()
    {
        Assert.Equal(("Doom", 2016), TextRules.SplitYear("Doom (2016)"));
        Assert.Equal(("Doom", 1993), TextRules.SplitYear("Doom [1993]"));
        Assert.Equal(("Blade Runner 2049", (int?)null), TextRules.SplitYear("Blade Runner 2049"));
        Assert.Equal(("(2016)", (int?)null), TextRules.SplitYear("(2016)"));
    }

    [Fact]
    public void SplitPlatform_reads_a_known_tag_only()
    {
        var aliases = TextRules.ParseAliases(TextRules.DefaultPlatformAliases);
        Assert.Equal(("Doom", "PlayStation 4"), TextRules.SplitPlatform("Doom (PS4)", aliases));
        Assert.Equal(("Hades", "PC (Microsoft Windows)"), TextRules.SplitPlatform("Hades [PC]", aliases));
        Assert.Equal(("Control", "Nintendo Switch"), TextRules.SplitPlatform("Control - Switch", aliases));
        // not a platform: stays part of the title
        Assert.Equal(("Doom (Remastered)", (string?)null), TextRules.SplitPlatform("Doom (Remastered)", aliases));
        Assert.Equal(("Doom - Deluxe", (string?)null), TextRules.SplitPlatform("Doom - Deluxe", aliases));
    }

    [Theory]
    [InlineData("The Witcher 3: Wild Hunt - Game of the Year Edition", "The Witcher 3: Wild Hunt")]
    [InlineData("Dishonored Definitive Edition", "Dishonored")]
    [InlineData("Skyrim (Deluxe Edition)", "Skyrim")]
    [InlineData("Borderlands: Game of the Year Edition", "Borderlands")]
    [InlineData("Fallout 4 [Digital Deluxe Edition]", "Fallout 4")]
    [InlineData("Hitman GOTY", "Hitman")]
    [InlineData("Dishonored Complete Collection", "Dishonored Complete Collection")]
    public void StripEdition_removes_edition_markers(string input, string expected) =>
        Assert.Equal(expected, TextRules.StripEdition(input, Ed, Never));

    [Theory]
    [InlineData("Resident Evil 2 (Remake)")]
    [InlineData("Foo (Remastered)")]
    [InlineData("Foo: Director's Cut")]
    [InlineData("Foo HD")]
    [InlineData("Foo Redux")]
    [InlineData("Foo (Deluxe Remastered Edition)")]
    [InlineData("Foo - Part 2")]
    [InlineData("Hades II")]
    [InlineData("Foo (Deluxe 2)")]
    public void StripEdition_never_removes_a_different_release(string input) =>
        Assert.Equal(input, TextRules.StripEdition(input, Ed, Never));

    [Fact]
    public void StripEdition_stops_at_a_never_word_but_still_drops_the_word_edition()
    {
        Assert.Equal("Foo Remastered", TextRules.StripEdition("Foo Remastered Edition", Ed, Never));
        Assert.Equal("Hades", TextRules.StripEdition("Hades", Ed, Never));
        Assert.Equal("Deluxe", TextRules.StripEdition("Deluxe", Ed, Never));   // never empties a title
    }

    [Fact]
    public void MainTitle_is_the_part_before_a_subtitle()
    {
        Assert.Equal("Fallout", TextRules.MainTitle("Fallout: New Vegas"));
        Assert.Equal("God of War", TextRules.MainTitle("God of War - Ragnarok"));
        Assert.Null(TextRules.MainTitle("Hades"));
        Assert.Null(TextRules.MainTitle("Half-Life 2"));
    }

    [Theory]
    [InlineData("x\"; where id > 0; fields *; limit 500; \"", "x where id > 0 fields * limit 500")]
    [InlineData("a\\\"b", "ab")]
    [InlineData("line1\nline2\r\nline3", "line1 line2 line3")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("tab\tand\u0000nul", "tab andnul")]
    public void CleanQueryText_removes_what_could_alter_a_query(string input, string expected) =>
        Assert.Equal(expected, TextRules.CleanQueryText(input));

    [Fact]
    public void CleanQueryText_caps_the_length_and_drops_broken_characters()
    {
        Assert.Equal(TextRules.MaxQueryTextLength, TextRules.CleanQueryText(new string('a', 5000)).Length);
        Assert.Equal("blink182", TextRules.CleanQueryText("blink�182"));
        Assert.Equal(string.Empty, TextRules.CleanQueryText("\";\\"));
    }

    [Fact]
    public void CleanText_tidies_text_and_keeps_paragraphs()
    {
        Assert.Equal("One two.\n\nThree **x**".Replace("**x**", "x"), TextRules.CleanText("One  two.\r\n\r\n\r\n\r\nThree **x**"));
        Assert.Null(TextRules.CleanText("   "));
        Assert.Null(TextRules.CleanText(null));
    }

    [Fact]
    public void ParseAliases_reads_tag_equals_platform_lines()
    {
        var map = TextRules.ParseAliases(["ps5 = PlayStation 5", "bad line", " = nothing", "x ="]);
        Assert.Single(map);
        Assert.Equal("PlayStation 5", map["ps5"]);
    }
}
