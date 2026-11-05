using System.Globalization;

namespace Chronicle.Plugin.IGDB;

internal enum IdKind { Igdb, Steam, Slug }

/// <summary>What a Fix match, a stored id or a cross-reference id means: an IGDB game id, a Steam app id, or a game page slug.</summary>
internal sealed record ParsedId(IdKind Kind, long Number, string? Text)
{
    public const string Forms =
        "an IGDB game id (1942 or igdb:1942), an igdb.com game page address (https://www.igdb.com/games/the-witcher-3-wild-hunt), or steam: followed by a Steam app id (steam:22380).";

    /// <summary>
    /// The host passes Fix match input RAW, so every check is made here. A pasted address is only ever read for its slug: the plugin
    /// never fetches it, it asks IGDB's own API, so there is no request to a person-chosen host. The address must be https on
    /// igdb.com or www.igdb.com, with no credentials in it and no unusual port, and must be a game page.
    /// </summary>
    public static ParsedId Parse(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) throw new ArgumentException("Enter " + Forms);

        if (text.Contains("://", StringComparison.Ordinal) || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("igdb.com", StringComparison.OrdinalIgnoreCase))
            return FromUrl(text);

        var i = text.IndexOf(':');
        if (i > 0)
        {
            var prefix = text[..i].Trim().ToLowerInvariant();
            var rest = text[(i + 1)..].Trim();
            switch (prefix)
            {
                case "igdb" or "game":
                    return new ParsedId(IdKind.Igdb, PositiveInt(rest), null);
                case "steam":
                    if (rest.Length is 0 or > 20 || !rest.All(char.IsAsciiDigit) || !long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var app) || app <= 0)
                        throw new ArgumentException("A Steam app id is digits only (steam:22380).");
                    return new ParsedId(IdKind.Steam, app, rest);
            }
            throw new ArgumentException($"\"{Truncate(prefix)}:\" is not an id this plugin reads. Enter " + Forms);
        }
        return new ParsedId(IdKind.Igdb, PositiveInt(text), null);
    }

    private static long PositiveInt(string s)
    {
        if (s.Length is 0 or > 18 || !s.All(char.IsAsciiDigit) || !long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
            throw new ArgumentException("An IGDB game id is a positive whole number. Enter " + Forms);
        return n;
    }

    private static ParsedId FromUrl(string text)
    {
        var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            throw new ArgumentException("That is not an address this plugin can read. Enter " + Forms);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Only https addresses on igdb.com are read. Enter " + Forms);
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("An address with credentials in it is not read. Enter " + Forms);
        if (!uri.IsDefaultPort) throw new ArgumentException("Only igdb.com addresses on the standard port are read. Enter " + Forms);
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("igdb.com" or "www.igdb.com")) throw new ArgumentException("Only igdb.com addresses are read. Enter " + Forms);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new ArgumentException("That is the igdb.com home page, not a game. Enter " + Forms);
        if (segments[0] is "characters" or "companies" or "platforms" or "collections" or "franchises" or "genres" or "people" or "lists" or "users")
            throw new ArgumentException($"That is an igdb.com {segments[0]} page, not a game page. Enter " + Forms);
        if (segments[0] != "games" || segments.Length < 2)
            throw new ArgumentException("That is not an igdb.com game page. Enter " + Forms);

        var slug = Uri.UnescapeDataString(segments[1]).ToLowerInvariant();
        if (!Queries.IsSlug(slug)) throw new ArgumentException("That game page address has a name this plugin cannot read. Enter " + Forms);
        return new ParsedId(IdKind.Slug, 0, slug);
    }

    private static string Truncate(string s) => s.Length > 20 ? s[..20] : s;

    /// <summary>A positive IGDB id from what Chronicle holds for the item under "igdb" ("1942" or "igdb:1942"), else null. Never throws.</summary>
    public static long? KnownIgdb(IReadOnlyDictionary<string, string>? known)
    {
        if (known is null || !known.TryGetValue("igdb", out var v) || string.IsNullOrWhiteSpace(v)) return null;
        var t = v.Trim();
        if (t.StartsWith("igdb:", StringComparison.OrdinalIgnoreCase)) t = t[5..].Trim();
        return t.Length is > 0 and <= 18 && t.All(char.IsAsciiDigit) && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>A Steam app id (digits) from what Chronicle holds under "steam" ("22380" or "steam:22380"), else null. Never throws.</summary>
    public static string? KnownSteam(IReadOnlyDictionary<string, string>? known)
    {
        if (known is null || !known.TryGetValue("steam", out var v) || string.IsNullOrWhiteSpace(v)) return null;
        var t = v.Trim();
        if (t.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) t = t[6..].Trim();
        return t.Length is > 0 and <= 20 && t.All(char.IsAsciiDigit) && t != "0" ? t : null;
    }
}
