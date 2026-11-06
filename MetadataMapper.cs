using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.IGDB;

/// <summary>A game fetched in full: the record with every expansion, its time to beat and its characters (null when not fetched).</summary>
internal sealed record FullRecord(JsonElement Game, JsonElement? TimeToBeat, JsonElement? Characters);

/// <summary>
/// Turns IGDB records into Chronicle's <see cref="MediaMetadata"/>. NOTHING IGDB sends for a game is dropped: the complete record is
/// kept once, verbatim, under <c>extendedData.igdb</c>; the mapped fields and the compact summaries beside it are derived from
/// it. Where an entry would pollute a field (an executable file name among the alternative names) it is moved to its own field,
/// not discarded. Every reader is tolerant (see <see cref="J"/>).
/// </summary>
internal static class MetadataMapper
{
    public const string Source = "igdb";
    private const string ImageBase = "https://images.igdb.com/igdb/image/upload/";

    public static int? YearOf(long unixSeconds)
    {
        try
        {
            if (unixSeconds is < -2_000_000_000L or > 10_000_000_000L) return null;
            var y = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.Year;
            return y is >= 1900 and <= 2100 ? y : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static readonly Regex ImageId = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    /// <summary>The address of an IGDB image at a documented size; null for a missing or odd-looking id. The API's own protocol-relative addresses are never used.</summary>
    public static string? ImageUrl(string? imageId, string size) =>
        imageId is not null && ImageId.IsMatch(imageId) ? $"{ImageBase}t_{size}/{imageId}.jpg" : null;

    private static string? ImageIdOf(JsonElement e) => e.Str("image_id");

    public static string? YouTube(string? videoId) =>
        string.IsNullOrWhiteSpace(videoId) || !Regex.IsMatch(videoId, "^[A-Za-z0-9_-]{3,40}$") ? null : $"https://www.youtube.com/watch?v={videoId}";

    // ── the mapping ───────────────────────────────────────────────────────────

    public static MediaMetadata Map(JsonElement game, JsonElement? ttb, JsonElement? characters, bool full, Settings s,
        IReadOnlyCollection<string>? degraded = null)
    {
        var id = game.Long("id") ?? 0;
        var m = new MediaMetadata
        {
            ExternalId = id.ToString(CultureInfo.InvariantCulture),
            Source = Source,
            Title = game.Str("name")?.Trim() ?? string.Empty,
            Overview = TextRules.CleanText(game.Str("summary")),
            SourceUrl = game.Str("url"),
            Results = null,
        };
        if (game.Long("first_release_date") is { } ts) m.Year = YearOf(ts);

        // Pictures.
        var cover = game.Obj("cover");
        var shots = game.Objs("screenshots").ToList();
        var arts = game.Objs("artworks").ToList();
        m.PosterUrl = ImageUrl(cover is { } c ? ImageIdOf(c) : null, s.PosterSize);
        var firstShot = shots.Select(ImageIdOf).FirstOrDefault(i => i is not null);
        var firstArt = arts.Select(ImageIdOf).FirstOrDefault(i => i is not null);
        m.PosterUrl ??= ImageUrl(firstShot, "screenshot_big");
        m.BackdropUrl = ImageUrl(firstArt, "1080p") ?? ImageUrl(firstShot, "1080p");

        foreach (var shot in shots)
            if (ImageUrl(ImageIdOf(shot), "1080p") is { } u)
                m.AdditionalImages.Add(new AdditionalImage { Url = u, Type = "Screenshot", ThumbnailUrl = ImageUrl(ImageIdOf(shot), "screenshot_med") });
        foreach (var art in arts)
            if (ImageUrl(ImageIdOf(art), "1080p") is { } u)
                m.AdditionalImages.Add(new AdditionalImage { Url = u, Type = "Fanart", ThumbnailUrl = ImageUrl(ImageIdOf(art), "screenshot_med") });
        foreach (var loc in game.Objs("game_localizations"))
            if (loc.Obj("cover") is { } lc && ImageUrl(ImageIdOf(lc), "1080p") is { } u)
                m.AdditionalImages.Add(new AdditionalImage { Url = u, Type = "Front", ThumbnailUrl = ImageUrl(ImageIdOf(lc), "cover_big") });
        if (characters is { ValueKind: JsonValueKind.Array } chars)
            foreach (var ch in chars.EnumerateArray())
                if (ch.ValueKind == JsonValueKind.Object && ch.Obj("mug_shot") is { } mug && ImageUrl(ImageIdOf(mug), "1080p") is { } u)
                    m.AdditionalImages.Add(new AdditionalImage { Url = u, Type = "Character", ThumbnailUrl = ImageUrl(ImageIdOf(mug), "thumb") });

        // Words.
        m.Genres = game.Names("genres");
        var tags = new List<string>();
        foreach (var arr in new[] { "themes", "game_modes", "player_perspectives", "keywords" }) tags.AddRange(game.Names(arr));
        tags.AddRange(game.Names("collections").Select(n => "Series: " + n));
        tags.AddRange(game.Names("franchises").Select(n => "Franchise: " + n));
        if (game.Obj("franchise") is { } mf && mf.Str("name") is { Length: > 0 } fn) tags.Add("Franchise: " + fn);
        m.Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Companies are the game's credits: developers are the cast (the host's heading for this type is "Developers"); publishers, porting and
        // supporting studios are crew. A company can hold several roles, so it appears once per role.
        foreach (var ic in game.Objs("involved_companies"))
        {
            var company = ic.Obj("company");
            var cname = company?.Str("name");
            if (string.IsNullOrWhiteSpace(cname)) continue;
            // The company's logo, when IGDB has one (about four in five do), is the picture on its credit card; a company with none shows its initial.
            var logo = company?.Obj("logo") is { } lg ? ImageUrl(ImageIdOf(lg), "logo_med") : null;
            var pid = company?.Long("id") is { } cid ? $"igdb:company-{cid}" : null;   // lets the same company across games be one person with one logo
            if (ic.Bool("developer") == true) m.Cast.Add(new CastMember(cname, "Developer", pid, logo));
            if (ic.Bool("publisher") == true) m.Crew.Add(new CrewMember(cname, "Publisher", pid, logo));
            if (ic.Bool("porting") == true) m.Crew.Add(new CrewMember(cname, "Porting", pid, logo));
            if (ic.Bool("supporting") == true) m.Crew.Add(new CrewMember(cname, "Supporting", pid, logo));
        }

        m.AlternateNames = AlternateNames(game, m.Title);
        m.Rating = RatingOf(game, s, out _);

        if (s.TimeToBeatAsRuntime && ttb is { ValueKind: JsonValueKind.Array } tarr)
        {
            var first = tarr.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.Object);
            if (first.ValueKind == JsonValueKind.Object && (first.Long("normally") ?? first.Long("hastily") ?? first.Long("completely")) is { } secs and > 0)
                m.RuntimeMinutes = (int)Math.Round(secs / 60.0);
        }

        m.ExtendedData = Extended(game, ttb, characters, full, degraded);
        return m;
    }

    // ── names ─────────────────────────────────────────────────────────────────

    /// <summary>Every alternative and localized name, except executable file names (which go to <c>extendedData.executable_names</c>).</summary>
    public static List<string> AlternateNames(JsonElement game, string title)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { title };
        foreach (var a in game.Objs("alternative_names"))
        {
            var n = a.Str("name")?.Trim();
            if (string.IsNullOrEmpty(n) || IsExecutableEntry(a)) continue;
            if (seen.Add(n)) list.Add(n);
        }
        foreach (var l in game.Objs("game_localizations"))
        {
            var n = l.Str("name")?.Trim();
            if (!string.IsNullOrEmpty(n) && seen.Add(n)) list.Add(n);
        }
        return list;
    }

    public static bool IsExecutableEntry(JsonElement alt) =>
        alt.Str("comment") is { } c && c.Contains("executable", StringComparison.OrdinalIgnoreCase);

    // ── rating ────────────────────────────────────────────────────────────────

    /// <summary>The single legacy rating on a 0 to 10 scale, from the chosen score; null when there is none. A score is never hidden by its vote count unless the person set a floor.</summary>
    public static double? RatingOf(JsonElement game, Settings s, out int? votes)
    {
        (string value, string count) = s.RatingSource switch
        {
            RatingSource.Users => ("rating", "rating_count"),
            RatingSource.Critics => ("aggregated_rating", "aggregated_rating_count"),
            _ => ("total_rating", "total_rating_count"),
        };
        votes = (int?)game.Long(count);
        if (game.Dbl(value) is not { } v || v <= 0) return null;
        if (s.MinimumRatingVotes > 0 && (votes ?? 0) < s.MinimumRatingVotes) return null;
        return Math.Round(v / 10.0, 1);
    }

    // ── extended data ─────────────────────────────────────────────────────────

    private static JsonElement Extended(JsonElement game, JsonElement? ttb, JsonElement? characters, bool full, IReadOnlyCollection<string>? degraded)
    {
        // "partial" is true for a lean search record AND for a full one fetched while some expansion group was switched off (IGDB
        // had dropped a field), so a re-sync knows to replace it; "degraded" names the groups that were off.
        var o = new JsonObject
        {
            ["partial"] = !full || degraded is { Count: > 0 },
            ["extras"] = full,
        };
        if (degraded is { Count: > 0 })
        {
            var d = new JsonArray();
            foreach (var g in degraded.OrderBy(x => x, StringComparer.Ordinal)) d.Add(g);
            o["degraded"] = d;
        }

        // Scores in the shape of the planned ratings contract: native scale, with the vote count.
        var ratings = new JsonArray();
        void Score(string variant, string v, string c)
        {
            if (game.Dbl(v) is { } val && val > 0)
                ratings.Add(new JsonObject { ["key"] = "igdb", ["variant"] = variant, ["value"] = val, ["scale"] = 100, ["votes"] = game.Long(c) });
        }
        Score("total", "total_rating", "total_rating_count");
        Score("users", "rating", "rating_count");
        Score("critics", "aggregated_rating", "aggregated_rating_count");
        if (ratings.Count > 0) o["ratings"] = ratings;

        var developers = new JsonArray(); var publishers = new JsonArray(); var porting = new JsonArray(); var supporting = new JsonArray();
        var logos = new JsonArray();
        foreach (var ic in game.Objs("involved_companies"))
        {
            var company = ic.Obj("company");
            var name = company?.Str("name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (ic.Bool("developer") == true) developers.Add(name);
            if (ic.Bool("publisher") == true) publishers.Add(name);
            if (ic.Bool("porting") == true) porting.Add(name);
            if (ic.Bool("supporting") == true) supporting.Add(name);
            if (company?.Obj("logo") is { } logo && ImageUrl(ImageIdOf(logo), "logo_med") is { } lu)
                logos.Add(new JsonObject { ["company"] = name, ["url"] = lu, ["width"] = logo.Long("width"), ["height"] = logo.Long("height") });
        }
        if (developers.Count > 0) o["developers"] = developers;
        if (publishers.Count > 0) o["publishers"] = publishers;
        if (porting.Count > 0) o["porting"] = porting;
        if (supporting.Count > 0) o["supporting"] = supporting;
        if (logos.Count > 0) o["company_logos"] = logos;

        if (game.Obj("game_type")?.Str("type") is { } gt) o["game_type"] = gt;
        if (game.Obj("game_status")?.Str("status") is { } gs) o["game_status"] = gs;
        AddNames(o, "platforms", game.Names("platforms"));
        AddNames(o, "series", game.Names("collections"));
        AddNames(o, "franchises", game.Names("franchises"));

        var dates = new JsonArray();
        foreach (var rd in game.Objs("release_dates"))
            dates.Add(new JsonObject
            {
                ["platform"] = rd.Obj("platform")?.Str("name"),
                ["human"] = rd.Str("human"),
                ["region"] = rd.Obj("release_region")?.Str("region"),
                ["date"] = rd.Long("date"),
            });
        if (dates.Count > 0) o["release_dates"] = dates;

        var websites = new JsonArray();
        foreach (var w in game.Objs("websites"))
            if (w.Str("url") is { Length: > 0 } wu)
                websites.Add(new JsonObject { ["type"] = w.Obj("type")?.Str("type"), ["url"] = wu, ["trusted"] = w.Bool("trusted") });
        if (websites.Count > 0) o["websites"] = websites;

        var videos = new JsonArray();
        foreach (var v in game.Objs("videos"))
            if (v.Str("video_id") is { } vid)
                videos.Add(new JsonObject { ["name"] = v.Str("name"), ["video_id"] = vid, ["url"] = YouTube(vid) });
        if (videos.Count > 0) o["videos"] = videos;

        // Store ids, grouped by the store (every entry kept).
        var ext = new JsonObject();
        foreach (var eg in game.Objs("external_games"))
        {
            var source = eg.Obj("external_game_source")?.Str("name") ?? "unknown";
            if (ext[source] is not JsonArray list) ext[source] = list = new JsonArray();
            list.Add(new JsonObject
            {
                ["uid"] = eg.Str("uid"), ["url"] = eg.Str("url"), ["name"] = eg.Str("name"), ["year"] = eg.Long("year"),
            });
        }
        if (ext.Count > 0) o["external_ids"] = ext;

        var exe = new JsonArray();
        foreach (var a in game.Objs("alternative_names"))
            if (IsExecutableEntry(a) && a.Str("name") is { } en) exe.Add(en);
        if (exe.Count > 0) o["executable_names"] = exe;

        if (ttb is { ValueKind: JsonValueKind.Array } t)
        {
            var first = t.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.Object);
            if (first.ValueKind == JsonValueKind.Object) o["time_to_beat"] = JsonNode.Parse(first.GetRawText());
        }
        if (characters is { ValueKind: JsonValueKind.Array } c)
        {
            var list = new JsonArray();
            foreach (var ch in c.EnumerateArray())
                if (ch.ValueKind == JsonValueKind.Object)
                {
                    var node = JsonNode.Parse(ch.GetRawText())!.AsObject();
                    if (ch.Obj("mug_shot") is { } mug && ImageUrl(ImageIdOf(mug), "1080p") is { } mu) node["mug_shot_url"] = mu;
                    list.Add(node);
                }
            o["characters"] = list;
        }

        // The series this game belongs to, in the shape Chronicle's collection grouping reads (an id and a name). A game in several series is
        // grouped under the first IGDB lists; every series stays in the complete record below and in the "Series:" tags. A game with no series
        // gets no such field and stays at the top level.
        foreach (var col in game.Objs("collections"))
            if (col.Long("id") is { } cid && col.Str("name") is { Length: > 0 } cname)
            {
                o["belongsToCollection"] = new JsonObject { ["id"] = cid, ["name"] = cname, ["url"] = col.Str("url") };
                break;
            }

        // The complete record, exactly as IGDB sent it, stored once.
        o["igdb"] = JsonNode.Parse(game.GetRawText());
        return JsonSerializer.SerializeToElement(o);
    }

    private static void AddNames(JsonObject o, string key, List<string> names)
    {
        if (names.Count == 0) return;
        var a = new JsonArray();
        foreach (var n in names) a.Add(n);
        o[key] = a;
    }
}
