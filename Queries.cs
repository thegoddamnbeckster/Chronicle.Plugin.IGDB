using System.Globalization;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.IGDB;

/// <summary>
/// Builds the Apicalypse bodies. A body is made only from constants and from values that were validated first: ids are integers
/// formatted invariantly, a Steam app id is digits, a slug is <c>[a-z0-9-]+</c> (measured on 2,500 real slugs), and a title has
/// passed <see cref="TextRules.CleanQueryText"/>, which removes the characters that could end or alter the string.
/// </summary>
internal static class Queries
{
    /// <summary>The lean field set: enough to list, score and (for an interactive search) show a candidate. Measured 2 KB for 10 games.</summary>
    public static readonly string[] LeanFields =
    [
        "name", "slug", "url", "summary", "first_release_date", "game_type.type", "version_parent", "parent_game",
        "total_rating", "total_rating_count", "rating", "rating_count", "aggregated_rating", "aggregated_rating_count",
        "alternative_names.name", "alternative_names.comment", "platforms.name", "platforms.abbreviation", "release_dates.y",
        "release_dates.date", "cover.image_id", "genres.name",
    ];

    public static string Lean => string.Join(",", LeanFields);

    public static string LeanPrefixed(string prefix) => string.Join(",", LeanFields.Select(f => prefix + f));

    // ── candidate queries ─────────────────────────────────────────────────────

    /// <summary>
    /// The exact-name query, with the exact-alternative-name query folded in as an OR (measured: one request finds "Final Fantasy
    /// 7" through the alternative name of "Final Fantasy VII"). Case-insensitive equality (<c>~ "text"</c>), most-rated first.
    /// </summary>
    public static string ExactName(IEnumerable<string> variants, int limit)
    {
        var terms = new List<string>();
        foreach (var v in variants.Select(TextRules.CleanQueryText).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            terms.Add($"name ~ \"{v}\"");
            terms.Add($"alternative_names.name ~ \"{v}\"");
        }
        if (terms.Count == 0) throw new ArgumentException("An empty title makes no request.");
        return $"fields {Lean}; where ({string.Join(" | ", terms)}); sort total_rating_count desc; limit {limit.ToString(CultureInfo.InvariantCulture)};";
    }

    /// <summary>IGDB's fuzzy search: the fallback when no exact name or alternative name fits.</summary>
    public static string Fuzzy(string text, int limit)
    {
        var t = TextRules.CleanQueryText(text);
        if (t.Length == 0) throw new ArgumentException("An empty title makes no request.");
        return $"search \"{t}\"; fields {Lean}; limit {limit.ToString(CultureInfo.InvariantCulture)};";
    }

    public static string ById(long id) => $"fields {Lean}; where id = {id.ToString(CultureInfo.InvariantCulture)};";

    private static readonly Regex SlugShape = new("^[a-z0-9-]{1,200}$", RegexOptions.Compiled);

    public static bool IsSlug(string? s) => s is not null && SlugShape.IsMatch(s);

    public static string BySlug(string slug)
    {
        if (!IsSlug(slug)) throw new ArgumentException("Not a valid IGDB slug.");
        return $"fields {Lean}; where slug = \"{slug}\"; limit 1;";
    }

    public static string SteamSources => "fields id,name; limit 500;";

    public static string BySteam(long sourceId, string appId)
    {
        if (appId.Length == 0 || appId.Length > 20 || !appId.All(char.IsAsciiDigit)) throw new ArgumentException("A Steam app id is digits only.");
        return $"fields {LeanPrefixed("game.")}; where external_game_source = {sourceId.ToString(CultureInfo.InvariantCulture)} & uid = \"{appId}\"; limit 10;";
    }

    // ── the full record ───────────────────────────────────────────────────────

    /// <summary>
    /// The expansion groups of a full fetch. <c>fields *</c> (every field, which can never name a field that does not exist) is
    /// always sent; each group adds the expanded objects behind the references. Groups are separate so that when IGDB renames a
    /// field, only the group containing it is lost, not the record (section 9.7 of the design).
    /// </summary>
    public static readonly (string Name, string[] Fields)[] Groups =
    [
        ("core", ["cover.*", "genres.*", "game_type.*", "game_status.*", "platforms.*"]),
        ("media", ["artworks.*", "screenshots.*", "videos.*"]),
        ("companies", ["involved_companies.*", "involved_companies.company.*"]),
        ("company_logos", ["involved_companies.company.logo.*"]),
        ("names", ["alternative_names.*", "game_localizations.*", "game_localizations.cover.*"]),
        ("releases", ["release_dates.*", "release_dates.platform.*"]),
        ("release_extra", ["release_dates.release_region.*", "release_dates.status.*", "release_dates.date_format.*"]),
        ("commerce", ["external_games.*", "external_games.external_game_source.*", "websites.*", "websites.type.*"]),
        ("classification", ["themes.*", "keywords.*", "game_modes.*", "player_perspectives.*", "game_engines.*", "franchises.*", "franchise.*", "collections.*"]),
        ("ratings", ["age_ratings.*", "age_ratings.organization.*", "age_ratings.rating_category.*", "age_ratings.rating_content_descriptions.*"]),
        ("related", ["similar_games.name", "dlcs.name", "expansions.name", "standalone_expansions.name", "expanded_games.name", "remakes.name",
                     "remasters.name", "ports.name", "forks.name", "bundles.name", "parent_game.name", "version_parent.name"]),
        ("languages", ["language_supports.*", "language_supports.language.*", "language_supports.language_support_type.*"]),
        ("multiplayer", ["multiplayer_modes.*"]),
        // the characters sub-query's expansions: a rename there loses the characters' details, not the game
        ("characters", ["mug_shot.*", "character_gender.*", "character_species.*"]),
    ];

    public static string FullGameFields(ISet<string> disabledGroups) =>
        "*" + string.Concat(Groups.Where(g => g.Name != "characters" && !disabledGroups.Contains(g.Name)).SelectMany(g => g.Fields).Select(f => ", " + f));

    private static string CharacterFields(ISet<string> disabledGroups) =>
        "*" + string.Concat(Groups.Where(g => g.Name == "characters" && !disabledGroups.Contains(g.Name)).SelectMany(g => g.Fields).Select(f => ", " + f));

    /// <summary>One request carrying the game in full, its time to beat and its characters (measured: 200 in 0.16 s, 29 KB, three results).</summary>
    public static string Full(long id, ISet<string> disabledGroups, bool timeToBeat)
    {
        var n = id.ToString(CultureInfo.InvariantCulture);
        var body = $"query games \"game\" {{ fields {FullGameFields(disabledGroups)}; where id = {n}; }};\n";
        if (timeToBeat) body += $"query game_time_to_beats \"ttb\" {{ fields *; where game_id = {n}; }};\n";
        body += $"query characters \"characters\" {{ fields {CharacterFields(disabledGroups)}; where games = ({n}); limit 500; }};";
        return body;
    }

    // ── drift ─────────────────────────────────────────────────────────────────

    private static readonly Regex InvalidField = new(@"Invalid field name:\s*'(?<f>[^']+)'", RegexOptions.Compiled);

    /// <summary>The field IGDB says it does not know, from a 400 body such as <c>[{"title":"Invalid Field","cause":"Invalid field name: 'x'"}]</c>.</summary>
    public static string? FieldFromError(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = InvalidField.Match(body);
        return m.Success ? m.Groups["f"].Value : null;
    }

    /// <summary>The expansion group that contains a field the service rejected, or null if none does.</summary>
    public static string? GroupOf(string field)
    {
        // The most specific match wins: the longest expansion path that is the field itself or lies above it
        // ("involved_companies.company.logo" belongs to company_logos, not to companies); a rejected field that is a
        // parent of paths ("release_dates") takes the first group that uses it.
        string? best = null;
        var bestLen = -1;
        string? parentHit = null;
        foreach (var (name, fields) in Groups)
            foreach (var f in fields)
            {
                var path = f.EndsWith(".*", StringComparison.Ordinal) ? f[..^2] : f;
                if (string.Equals(path, field, StringComparison.OrdinalIgnoreCase) ||
                    field.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase))
                {
                    if (path.Length > bestLen) { best = name; bestLen = path.Length; }
                }
                else if (path.StartsWith(field + ".", StringComparison.OrdinalIgnoreCase))
                {
                    parentHit ??= name;
                }
            }
        return best ?? parentHit;
    }
}
