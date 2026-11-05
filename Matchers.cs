using System.Text.Json;

namespace Chronicle.Plugin.IGDB;

/// <summary>What a library title says about the game: its forms for comparing, the year, and a platform hint.</summary>
internal sealed record Wanted(
    string Title,
    string Stripped,
    string? Main,
    int? Year,
    string? Platform,
    bool HasEditionWord,
    bool SaysDlc,
    string? ReworkBase = null)
{
    private static readonly HashSet<string> DlcWords = ["dlc", "expansion", "bundle", "pack", "soundtrack", "episode", "addon", "update"];

    // "Resident Evil 4 Remake", "Dead Space (Remake)", "Skyrim - Remastered": a store's way of saying which release it is. IGDB names the
    // 2023 remake "Resident Evil 4" and records the difference in the game's TYPE (Remake / Remaster).
    private static readonly System.Text.RegularExpressions.Regex Rework = new(
        @"^(?<base>.+?)(?:\s*[\(\[]\s*(?:remake|remastered|remaster)\s*[\)\]]|\s*[-:–—]\s*(?:remake|remastered|remaster)|\s+(?:remake|remastered|remaster))\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Reads a library title (already free of a trailing year and platform tag) and its hints.</summary>
    public static Wanted From(string title, int? year, string? platform, Settings s)
    {
        var stripped = TextRules.StripEdition(title, s.EditionWords, s.NeverStripWords);
        var tokens = TextRules.Tokens(title);
        var saysDlc = tokens.Any(DlcWords.Contains) || (tokens.Contains("season") && tokens.Contains("pass"));   // whole words: not "Backpack Hero"
        var rework = Rework.Match(stripped);
        return new Wanted(title, stripped, TextRules.MainTitle(stripped), year, platform,
            !string.Equals(stripped, title, StringComparison.Ordinal), saysDlc,
            rework.Success ? rework.Groups["base"].Value.Trim() : null);
    }
}

/// <summary>The few facts about an IGDB game that scoring needs, read from a lean (or full) record.</summary>
internal sealed record GameBrief(
    long Id,
    string Name,
    IReadOnlyList<string> AltNames,
    int? FirstYear,
    IReadOnlyList<int> Years,
    string? TypeName,
    bool IsVersion,
    IReadOnlyList<string> PlatformKeys,
    int RatingCount)
{
    public static GameBrief? From(JsonElement game)
    {
        var id = game.Long("id");
        var name = game.Str("name");
        if (id is null || string.IsNullOrWhiteSpace(name)) return null;

        var years = new List<int>();
        int? first = null;
        var ts = game.Long("first_release_date");
        if (ts is not null && MetadataMapper.YearOf(ts.Value) is { } fy) { first = fy; years.Add(fy); }
        foreach (var rd in game.Objs("release_dates"))
        {
            var y = rd.Long("y");
            if (y is >= 1950 and <= 2100) years.Add((int)y.Value);
            else if (rd.Long("date") is { } d && MetadataMapper.YearOf(d) is { } dy) years.Add(dy);
        }

        var alts = new List<string>();
        foreach (var a in game.Objs("alternative_names"))
        {
            var n = a.Str("name");
            var comment = a.Str("comment");
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (comment is not null && comment.Contains("executable", StringComparison.OrdinalIgnoreCase)) continue;
            alts.Add(n);
        }
        foreach (var l in game.Objs("game_localizations"))
            if (l.Str("name") is { Length: > 0 } ln) alts.Add(ln);

        var platforms = new List<string>();
        foreach (var p in game.Objs("platforms"))
        {
            if (p.Str("name") is { Length: > 0 } pn) platforms.Add(TextRules.Normalize(pn));
            if (p.Str("abbreviation") is { Length: > 0 } pa) platforms.Add(TextRules.Normalize(pa));
        }

        return new GameBrief(
            id.Value, name.Trim(), alts, first, years.Distinct().ToList(), game.Obj("game_type")?.Str("type"),
            game.Has("version_parent"), platforms.Distinct().ToList(),
            (int)Math.Clamp(game.Long("total_rating_count") ?? game.Long("rating_count") ?? 0, 0, int.MaxValue));
    }
}

/// <summary>A scored candidate (or a refusal).</summary>
internal sealed record Match(int Score, string Reason);

/// <summary>
/// Scoring of an IGDB game against a library title (the rules of the design's section 6.4). The score is 0 to 100; Chronicle's
/// own gate is 50. A raw score and a cap are kept apart and no later bonus lifts a cap. Nothing similar is ever accepted: a
/// candidate whose title is not the library's (after the allowed foldings) is refused, whatever IGDB's search ranked it.
/// </summary>
internal static class Matchers
{
    /// <summary>The score under which a candidate is not worth fetching in full (Chronicle's default confidence gate).</summary>
    public const int FetchGate = 50;

    public static Match? Score(Wanted want, GameBrief cand, Settings s)
    {
        var exactOnly = IsExactOnlyType(cand, s);

        var libLit = TextRules.Normalize(want.Title);
        var libStripLit = TextRules.Normalize(want.Stripped);
        var libFold = TextRules.Normalize(want.Title, foldNumerals: true);
        var libStripFold = TextRules.Normalize(want.Stripped, foldNumerals: true);
        var candLit = TextRules.Normalize(cand.Name);
        var candStripLit = TextRules.Normalize(TextRules.StripEdition(cand.Name, s.EditionWords, s.NeverStripWords));
        var candFold = TextRules.Normalize(cand.Name, foldNumerals: true);
        var candStripFold = TextRules.Normalize(TextRules.StripEdition(cand.Name, s.EditionWords, s.NeverStripWords), foldNumerals: true);

        int raw;
        int? cap = null;
        string how;
        if (candLit == libLit)
        {
            raw = 60; how = "title equal";
        }
        else if (EditionEqual(candLit, candStripLit, libLit, libStripLit))
        {
            // Below an exact title (60) so the exact game always beats its base game ("Persona 5 Royal" over "Persona 5").
            raw = 50; how = "title equal apart from an edition word";
        }
        else if (want.ReworkBase is { Length: > 0 } rb && IsRemakeOrRemaster(cand) &&
                 (candLit == TextRules.Normalize(rb) || candStripLit == TextRules.Normalize(rb)))
        {
            raw = 55; how = "title equal apart from the word Remake or Remastered, which IGDB records as the game's type";
        }
        else if (exactOnly)
        {
            return null;   // a DLC, mod, bundle... is matched only on its exact title
        }
        else if (candFold == libFold || EditionEqual(candFold, candStripFold, libFold, libStripFold))
        {
            raw = 52; how = "title equal after reading Roman numerals as numbers";
        }
        else if (cand.AltNames.Any(a => AltEquals(a, libLit, libFold, libStripFold, s)))
        {
            raw = 50; how = "an alternative name equals the title";
        }
        else if (want.Main is { Length: > 0 } wm && TextRules.MainTitle(cand.Name) is { Length: > 0 } cm &&
                 TextRules.Normalize(wm, true) == TextRules.Normalize(cm, true))
        {
            raw = 40; cap = 45; how = "only the main title is equal (the subtitles differ)";
        }
        else if (want.Main is { Length: > 0 } wm2 && TextRules.Normalize(wm2, true) == candFold && TextRules.MainTitle(want.Stripped) is not null)
        {
            raw = 40; cap = 45; how = "the library title has a subtitle the game lacks";
        }
        else if (TextRules.MainTitle(cand.Name) is { Length: > 0 } cm2 && TextRules.Normalize(cm2, true) == libStripFold)
        {
            raw = 40; cap = 45; how = "the game has a subtitle the library title lacks";
        }
        else
        {
            return null;
        }

        var reasons = new List<string> { how };

        // Year: agreement is worth points; a clear disagreement caps the score below the gate.
        if (want.Year is { } wy && cand.Years.Count > 0)
        {
            if (cand.Years.Any(y => Math.Abs(y - wy) <= 1))
            {
                raw += 10; reasons.Add("year agrees");
            }
            else if (cand.Years.All(y => Math.Abs(y - wy) > 2) && !want.HasEditionWord && !IsRework(cand))
            {
                cap = Math.Min(cap ?? 100, 40); reasons.Add($"year {wy} is not near {string.Join('/', cand.Years)} (capped)");
            }
        }

        // Platform hint: a bonus when it fits, never a penalty (IGDB's platform lists are incomplete).
        if (want.Platform is { } plat && cand.PlatformKeys.Contains(TextRules.Normalize(plat)))
        {
            raw += 8; reasons.Add("platform agrees");
        }

        // Type.
        if (string.Equals(cand.TypeName, "Main Game", StringComparison.OrdinalIgnoreCase)) { raw += 5; reasons.Add("main game"); }
        if ((exactOnly || cand.IsVersion) && !(want.SaysDlc || want.HasEditionWord))
        {
            // a DLC, bundle, mod... or a version of another game (an edition record): a penalty unless the library title says so itself
            raw -= 15; reasons.Add($"{cand.TypeName ?? "version"} (penalty)");
        }

        // Popularity breaks ties between games sharing a name; it never lifts a cap.
        if (cand.RatingCount > 0)
        {
            var pop = Math.Min(8, (int)Math.Round(Math.Log10(1 + cand.RatingCount) * 2.7));
            if (pop > 0) { raw += pop; reasons.Add($"{cand.RatingCount} ratings"); }
        }

        var score = Math.Clamp(Math.Min(raw, cap ?? 100), 0, 100);
        return new Match(score, string.Join(", ", reasons));
    }

    /// <summary>
    /// Equal once an edition marker is removed from ONE side. Never when both sides lost something: "Pokemon Gold Version" and "Pokemon
    /// Platinum Version" are different games even though each is "Pokemon" plus a word that can name an edition.
    /// </summary>
    private static bool EditionEqual(string candFull, string candStripped, string libFull, string libStripped) =>
        (candFull == libStripped && Removed(libFull, libStripped)) ||    // the library title carries an edition word, the game is the base
        (candStripped == libFull && Removed(candFull, candStripped));    // the game's title carries one, the library's is the base

    private static bool Removed(string full, string stripped) =>
        full.Length > stripped.Length && full.StartsWith(stripped, StringComparison.Ordinal);

    private static bool IsRemakeOrRemaster(GameBrief c) =>
        c.TypeName is not null && (c.TypeName.Equals("Remake", StringComparison.OrdinalIgnoreCase) || c.TypeName.Equals("Remaster", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A SIMILAR title, for an interactive search only (a person typing into Add Media, who will choose from a list). Never a match:
    /// the score stays under Chronicle's gate (50), so nothing is ever accepted automatically on it. A candidate qualifies when it
    /// contains every word of what was typed ("witcher 3" finds "The Witcher 3: Wild Hunt").
    /// </summary>
    public static Match? ScoreSimilar(Wanted want, GameBrief cand)
    {
        var typed = TextRules.Tokens(want.Stripped).Where(t => t is not ("the" or "a" or "an")).ToList();
        if (typed.Count == 0) return null;
        var has = TextRules.Tokens(cand.Name).Concat(cand.AltNames.SelectMany(a => TextRules.Tokens(a))).ToHashSet();
        var folded = has.Select(t => TextRules.TryRoman(t, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : t).ToHashSet();
        if (!typed.All(t => has.Contains(t) || folded.Contains(TextRules.TryRoman(t, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : t)))
            return null;
        var pop = cand.RatingCount > 0 ? Math.Min(15, (int)Math.Round(Math.Log10(1 + cand.RatingCount) * 4)) : 0;
        var bonus = string.Equals(cand.TypeName, "Main Game", StringComparison.OrdinalIgnoreCase) ? 4 : 0;
        return new Match(Math.Min(49, 25 + pop + bonus), "a similar title (offered to choose from; never accepted automatically)");
    }

    private static bool AltEquals(string alt, string libLit, string libFold, string libStripFold, Settings s)
    {
        var a = TextRules.Normalize(alt);
        var f = TextRules.Normalize(alt, foldNumerals: true);
        return a == libLit || f == libFold || f == libStripFold;
    }

    private static bool IsRework(GameBrief c) =>
        c.TypeName is not null &&
        (c.TypeName.Equals("Remaster", StringComparison.OrdinalIgnoreCase) || c.TypeName.Equals("Remake", StringComparison.OrdinalIgnoreCase) ||
         c.TypeName.Equals("Port", StringComparison.OrdinalIgnoreCase) || c.TypeName.Equals("Expanded Game", StringComparison.OrdinalIgnoreCase));

    private static bool IsExactOnlyType(GameBrief c, Settings s)
    {
        if (c.TypeName is null) return false;
        if (s.ExactTitleOnlyTypes.Any(t => string.Equals(t, c.TypeName, StringComparison.OrdinalIgnoreCase))) return true;
        return !KnownMatchableTypes.Contains(c.TypeName);   // a type this plugin has never heard of is exact-title-only (and logged by the provider)
    }

    /// <summary>The types that are the game itself (measured in IGDB's game_types table, 2026-11-05).</summary>
    public static readonly HashSet<string> KnownMatchableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Main Game", "Remake", "Remaster", "Expanded Game", "Port", "Standalone Expansion",
    };
}
