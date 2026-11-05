using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.IGDB;

/// <summary>
/// Everything about game TITLES as text: folding typography, comparing, edition words, numerals, platform tags, subtitles, and
/// the one place a person's text is made safe to put inside an Apicalypse string.
///
/// One normaliser, the family used by the Open Library, Deezer and TheAudioDB plugins: fold accents and typographic characters,
/// treat "&amp;" and "and" as equal, drop a leading article, drop everything that is not a letter or a digit.
/// </summary>
internal static class TextRules
{
    /// <summary>Words that mark an EDITION of the same game ("Deluxe Edition"), ignored when comparing titles. A setting.</summary>
    public static readonly IReadOnlyList<string> DefaultEditionWords =
    [
        // Only words that mean "the same game, packaged differently". Words that are often part of another game's own title
        // ("Pokemon Gold", "Persona 5 Royal", "Tomb Raider: Anniversary", "Super Smash Bros. Ultimate") are NOT here: removing them
        // would make different games equal. A bare trailing word is removed only when "Edition" or "Version" follows it (StripEdition).
        "game of the year", "goty", "deluxe", "ultimate", "complete", "definitive", "collector's", "digital deluxe",
        "standard", "premium", "enhanced", "limited",
    ];

    /// <summary>Words that mark a DIFFERENT RELEASE, never removed whatever the edition words say. A setting.</summary>
    public static readonly IReadOnlyList<string> DefaultNeverStripWords =
    [
        "remastered", "remaster", "remake", "hd", "redux", "reloaded", "director's cut", "part", "episode", "chapter", "season",
        "vr", "online", "classic", "reboot",
    ];

    /// <summary>
    /// Types that are matched only on an exact title (and carry a penalty): the record is a piece of, or a wrapper round, another
    /// game. Nothing is excluded: a person tracking a mod or an update pack can still match it by its exact title. A setting.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExactTitleOnlyTypes =
        ["DLC", "Expansion", "Bundle", "Fork", "Mod", "Episode", "Season", "Pack / Addon", "Update"];

    /// <summary>
    /// "tag = platform name or abbreviation as IGDB spells it": a trailing "(PS4)" or "[PC]" on a library title is a platform
    /// hint, not part of the name. A setting, so a person can add their own.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPlatformAliases =
    [
        "pc = PC (Microsoft Windows)", "windows = PC (Microsoft Windows)", "win = PC (Microsoft Windows)", "steam = PC (Microsoft Windows)",
        "mac = Mac", "macos = Mac", "osx = Mac", "linux = Linux",
        "ps1 = PlayStation", "psx = PlayStation", "ps2 = PlayStation 2", "ps3 = PlayStation 3", "ps4 = PlayStation 4", "ps5 = PlayStation 5",
        "psp = PlayStation Portable", "vita = PlayStation Vita", "psvita = PlayStation Vita", "psvr = PlayStation VR", "psvr2 = PlayStation VR2",
        "xbox = Xbox", "xbox360 = Xbox 360", "x360 = Xbox 360", "xboxone = Xbox One", "xone = Xbox One",
        "xboxseries = Xbox Series X|S", "xsx = Xbox Series X|S", "series x = Xbox Series X|S",
        "switch = Nintendo Switch", "ns = Nintendo Switch", "switch2 = Nintendo Switch 2",
        "wii = Wii", "wiiu = Wii U", "gamecube = Nintendo GameCube", "gc = Nintendo GameCube", "n64 = Nintendo 64",
        "nes = Nintendo Entertainment System", "snes = Super Nintendo Entertainment System", "gb = Game Boy", "gbc = Game Boy Color",
        "gba = Game Boy Advance", "nds = Nintendo DS", "ds = Nintendo DS", "3ds = Nintendo 3DS",
        "dreamcast = Dreamcast", "dc = Dreamcast", "saturn = Sega Saturn", "genesis = Sega Mega Drive/Genesis", "megadrive = Sega Mega Drive/Genesis",
        "ios = iOS", "android = Android", "stadia = Google Stadia", "quest = Oculus Quest", "gog = PC (Microsoft Windows)",
    ];

    // ── folding and comparing ─────────────────────────────────────────────────

    /// <summary>Accents removed, typographic characters folded to ASCII, trademark signs removed, "&amp;" spelled "and".</summary>
    public static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.NonSpacingMark: continue;
            }
            switch (c)
            {
                case '‘': case '’': case 'ʼ': case '′': sb.Append('\''); break;
                case '“': case '”': case '″': sb.Append('"'); break;
                case '‐': case '‑': case '‒': case '–': case '—': case '―': case '−': sb.Append('-'); break;
                case ' ': case ' ': case ' ': sb.Append(' '); break;
                case '™': case '®': case '©': break;
                case '&': sb.Append(" and "); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>The words of a title, lower case, apostrophes removed (so "Baldur's" is "baldurs"), everything else that is not a letter or digit a break.</summary>
    public static List<string> Tokens(string? s)
    {
        var folded = Fold(s).ToLowerInvariant().Replace("'", string.Empty);
        var tokens = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in folded)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    private static readonly HashSet<string> Articles = ["the", "a", "an"];

    /// <summary>The comparable form of a title. <paramref name="foldNumerals"/> makes "VII" and "7" equal.</summary>
    public static string Normalize(string? title, bool foldNumerals = false)
    {
        var t = Tokens(title);
        if (t.Count > 1 && Articles.Contains(t[0])) t.RemoveAt(0);
        if (foldNumerals)
            for (var i = 0; i < t.Count; i++)
                if (TryRoman(t[i], out var n)) t[i] = n.ToString(CultureInfo.InvariantCulture);
        return string.Concat(t);
    }

    /// <summary>
    /// Roman numerals II to IX and XI to XX. A lone I, V or X is never read as a numeral: "Mega Man X" is not "Mega Man 10", and
    /// "I Am Alive" is not "1 Am Alive". (Titles IGDB spells with those letters are found through its alternative names.)
    /// </summary>
    public static bool TryRoman(string token, out int value)
    {
        value = 0;
        if (token.Length < 2 || token.Length > 4) return false;
        value = token switch
        {
            "ii" => 2, "iii" => 3, "iv" => 4, "vi" => 6, "vii" => 7, "viii" => 8, "ix" => 9,
            "xi" => 11, "xii" => 12, "xiii" => 13, "xiv" => 14, "xv" => 15, "xvi" => 16, "xvii" => 17, "xviii" => 18, "xix" => 19, "xx" => 20,
            _ => 0,
        };
        return value != 0;
    }

    private static readonly string[] RomanFor =
        ["", "", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX"];

    /// <summary>
    /// The title with every sequel number written the other way ("Baldur's Gate 3" -> "Baldur's Gate III", "Final Fantasy VII" ->
    /// "Final Fantasy 7"), or null when it has none. Only numbers 2 to 20 and the numerals TryRoman accepts are swapped.
    /// </summary>
    public static string? SwapNumerals(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        // ONE number is swapped: the LAST one, which is the sequel number ("Left 4 Dead 2" -> "Left 4 Dead II", never "Left IV Dead II").
        // 10 is never written X: a lone X is not read as a numeral (see TryRoman), so that variant could only find "Mega Man X".
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = words.Length - 1; i >= 0; i--)
        {
            var w = words[i];
            var core = w.Trim(':', ',', '.', '!', '?', ';', '(', ')', '[', ']');
            if (core.Length == 0) continue;
            var lead = w[..w.IndexOf(core, StringComparison.Ordinal)];
            var tail = w[(lead.Length + core.Length)..];
            if (core.Length is >= 1 and <= 2 && core.All(char.IsDigit) && int.TryParse(core, out var n))
            {
                if (n is < 2 or > 20 || n == 10) return null;
                words[i] = lead + RomanFor[n] + tail;
                return string.Join(' ', words);
            }
            if (core.All(char.IsLetter) && TryRoman(core.ToLowerInvariant(), out var r))
            {
                words[i] = lead + r.ToString(CultureInfo.InvariantCulture) + tail;
                return string.Join(' ', words);
            }
        }
        return null;
    }

    // ── titles: year, platform tag, subtitle, edition ─────────────────────────

    private static readonly Regex YearSuffix = new(@"^(?<name>.*?)\s*[\(\[]\s*(?<year>(?:19|20)\d\d)\s*[\)\]]\s*$", RegexOptions.Compiled);

    /// <summary>"Ubik (1969)" -> ("Ubik", 1969).</summary>
    public static (string Name, int? Year) SplitYear(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        var m = YearSuffix.Match(n);
        return m.Success && m.Groups["name"].Value.Trim().Length > 0
            ? (m.Groups["name"].Value.Trim(), int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture))
            : (n, null);
    }

    private static readonly Regex PlatformSuffix = new(
        @"^(?<name>.*?)\s*(?:[\(\[]\s*(?<tag>[^\(\)\[\]]{1,24}?)\s*[\)\]]|\s-\s+(?<tag2>[A-Za-z0-9 ]{1,20}))\s*$", RegexOptions.Compiled);

    /// <summary>Parses "tag = IGDB platform name" lines into a lookup from the normalised tag to the platform name.</summary>
    public static Dictionary<string, string> ParseAliases(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var tag = Normalize(line[..i]);
            var target = line[(i + 1)..].Trim();
            if (tag.Length > 0 && target.Length > 0) map[tag] = target;
        }
        return map;
    }

    /// <summary>
    /// "Doom (PS4)" -> ("Doom", "PlayStation 4"). A trailing bracketed or dash-separated token is a platform hint ONLY when it is
    /// one of the known tags; any other ("(Remastered)", "- Deluxe") stays part of the title.
    /// </summary>
    public static (string Name, string? Platform) SplitPlatform(string name, IReadOnlyDictionary<string, string> aliases)
    {
        var m = PlatformSuffix.Match(name);
        if (!m.Success) return (name, null);
        var tag = m.Groups["tag"].Success ? m.Groups["tag"].Value : m.Groups["tag2"].Value;
        var rest = m.Groups["name"].Value.Trim();
        if (rest.Length == 0) return (name, null);
        return aliases.TryGetValue(Normalize(tag), out var platform) ? (rest, platform) : (name, null);
    }

    /// <summary>The part of a title before its first subtitle separator (": " or " - "), or null when it has none.</summary>
    public static string? MainTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = title.Trim();
        var cut = -1;
        foreach (var sep in new[] { ": ", " - ", " – ", " — " })
        {
            var i = t.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0 && (cut < 0 || i < cut)) cut = i;
        }
        return cut > 0 ? t[..cut].Trim() : null;
    }

    private static readonly Regex TrailingBracket = new(@"^(?<rest>.*?)\s*[\(\[](?<inner>[^\(\)\[\]]*)[\)\]]\s*$", RegexOptions.Compiled);

    private static bool ContainsPhrase(List<string> tokens, List<string> phrase)
    {
        if (phrase.Count == 0 || phrase.Count > tokens.Count) return false;
        for (var i = 0; i + phrase.Count <= tokens.Count; i++)
        {
            var ok = true;
            for (var k = 0; k < phrase.Count && ok; k++) ok = tokens[i + k] == phrase[k];
            if (ok) return true;
        }
        return false;
    }

    private static bool IsSequelNumber(string token) =>
        TryRoman(token, out _) || (token.Length <= 2 && token.All(char.IsDigit) && int.TryParse(token, out var n) && n is >= 1 and <= 20);

    /// <summary>True when the phrase contains an edition word and nothing that marks a different release.</summary>
    private static bool IsEditionPhrase(string phrase, IReadOnlyList<string> edition, IReadOnlyList<string> never)
    {
        var tokens = Tokens(phrase);
        if (tokens.Count == 0) return false;
        if (tokens.Any(IsSequelNumber)) return false;
        foreach (var n in never)
            if (ContainsPhrase(tokens, Tokens(n))) return false;
        foreach (var e in edition)
            if (ContainsPhrase(tokens, Tokens(e))) return true;
        return false;
    }

    /// <summary>
    /// The title without a trailing EDITION marker: a bracketed one ("(Deluxe Edition)"), a dashed or colon one (": Game of the Year
    /// Edition") or trailing words ("Dishonored Definitive Edition"). A qualifier that names a different release (Remastered,
    /// Remake, Redux, Director's Cut, Part 2, a sequel number) is never removed, whatever the edition words say.
    /// </summary>
    public static string StripEdition(string? title, IReadOnlyList<string> edition, IReadOnlyList<string> never)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length == 0) return t;

        // 1. trailing bracketed groups
        while (true)
        {
            var m = TrailingBracket.Match(t);
            if (!m.Success || m.Groups["rest"].Value.Trim().Length == 0) break;
            if (!IsEditionPhrase(m.Groups["inner"].Value, edition, never)) break;
            t = m.Groups["rest"].Value.Trim();
        }

        // 2. a trailing " - xxx" or ": xxx" segment
        var cut = -1;
        foreach (var sep in new[] { " - ", ": ", " – ", " — " })
        {
            var i = t.LastIndexOf(sep, StringComparison.Ordinal);
            if (i > cut) cut = i;
        }
        if (cut > 0)
        {
            var seg = t[(cut + 1)..].TrimStart('-', ':', '–', '—', ' ');
            if (IsEditionPhrase(seg, edition, never)) t = t[..cut].Trim();
        }

        // 3. trailing words: "Edition" / "Version" and edition phrases
        if (t.EndsWith(')') || t.EndsWith(']')) return t;   // a bracketed qualifier that step 1 kept names a different release: leave the title whole
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var phrases = edition.Select(e => (Words: Tokens(e), Raw: e)).Where(p => p.Words.Count > 0).OrderByDescending(p => p.Words.Count).ToList();
        var sawMarker = false;   // "Edition" or "Version" was removed just before: the words before it are edition words, not part of the name
        while (words.Count > 1)
        {
            var tail = Tokens(string.Join(' ', words));
            var last = Tokens(words[^1]);
            if (last.Count == 1 && (last[0] == "edition" || last[0] == "version"))
            {
                words.RemoveAt(words.Count - 1);
                sawMarker = true;
                continue;
            }
            var removed = false;
            foreach (var p in phrases)
            {
                if (tail.Count <= p.Words.Count) continue;
                // A bare trailing word ("Super Smash Bros. Ultimate") is part of the name unless an edition marker came after it;
                // a longer phrase ("Game of the Year") and "GOTY" are unambiguous.
                if (!sawMarker && p.Words.Count == 1 && p.Words[0] != "goty") continue;
                if (tail.Skip(tail.Count - p.Words.Count).SequenceEqual(p.Words))
                {
                    var wordsToDrop = 0;
                    var count = 0;
                    for (var i = words.Count - 1; i >= 0 && count < p.Words.Count; i--)
                    {
                        count += Math.Max(1, Tokens(words[i]).Count);
                        wordsToDrop++;
                    }
                    if (words.Count - wordsToDrop >= 1)
                    {
                        words.RemoveRange(words.Count - wordsToDrop, wordsToDrop);
                        removed = true;
                    }
                    break;
                }
            }
            if (!removed) break;
        }
        var result = string.Join(' ', words).Trim().TrimEnd('-', ':', ',').Trim();
        return result.Length == 0 ? t : result;
    }

    // ── safety ────────────────────────────────────────────────────────────────

    public const int MaxQueryTextLength = 200;

    /// <summary>
    /// Makes a person's text safe to place inside an Apicalypse string. IGDB's behaviour was measured to be benign (an escaped quote
    /// does not break the string, a hostile string is read as plain text), but nothing here relies on its escaping rules: the
    /// characters that could end or alter the string (", \, ;), every control character and every line break are REMOVED, runs of
    /// white space collapse, and the text is capped. Nothing else from an item ever reaches a query body.
    /// </summary>
    public static string CleanQueryText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = QueryFold(text);
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))   // tabs and line breaks are white space: they become a single space
            {
                space = sb.Length > 0;
                continue;
            }
            if (c is '"' or '\\' or ';' || char.IsControl(c) || c == '\u2028' || c == '\u2029' || c == '\uFFFD')
                continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        var s = sb.ToString();
        return s.Length > MaxQueryTextLength ? s[..MaxQueryTextLength].TrimEnd() : s;
    }

    /// <summary>
    /// Typography that never appears in IGDB's own titles, made plain before a title is asked for: curly quotes and apostrophes,
    /// the various dashes, non-breaking spaces, and the trademark signs a store adds ("Assassin's Creed® Odyssey"). Accents are
    /// NOT removed: IGDB spells "Pokémon" with its accent.
    /// </summary>
    public static string QueryFold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '‘': case '’': case 'ʼ': case '′': sb.Append('\''); break;
                case '“': case '”': case '″': break;   // a double quote is removed anyway
                case '‐': case '‑': case '‒': case '–': case '—': case '―': case '−': sb.Append('-'); break;
                case ' ': case ' ': case ' ': sb.Append(' '); break;
                case '™': case '®': case '©': break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public static List<string> ParseList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ── text cleaning (7.3) ───────────────────────────────────────────────────

    private static readonly Regex Spaces = new(@"[ \t ]{2,}", RegexOptions.Compiled);
    private static readonly Regex EmphasisMarks = new(@"(\*\*|__)(.+?)\1", RegexOptions.Compiled);

    /// <summary>Line endings made "\n", non-breaking and doubled spaces collapsed, paragraphs kept, Markdown emphasis removed.</summary>
    public static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ');
        t = EmphasisMarks.Replace(t, "$2");
        t = Spaces.Replace(t, " ");
        t = string.Join("\n", t.Split('\n').Select(l => l.Trim()));
        t = Regex.Replace(t, "\n{3,}", "\n\n").Trim();
        return t.Length == 0 ? null : t;
    }
}
