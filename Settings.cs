namespace Chronicle.Plugin.IGDB;

/// <summary>Which of IGDB's scores becomes the item's single legacy rating.</summary>
public enum RatingSource { Total, Users, Critics }

/// <summary>
/// The plugin's settings, read once from Chronicle's saved values; every value has a default and an out-of-range or unparsable
/// one falls back to it, so a missing or mistyped setting never fails the plugin. Nothing here limits the DATA kept: every image,
/// name, tag and related record IGDB has is stored. These settings tune request pacing, timeouts, how the rating is chosen and the
/// word lists used for matching titles.
/// </summary>
internal sealed record Settings(
    string ClientId,
    string ClientSecret,
    double RequestsPerSecond,
    int MaxConcurrentRequests,
    TimeSpan RequestTimeout,
    TimeSpan CallDeadline,
    int SearchLimit,
    string PosterSize,
    RatingSource RatingSource,
    int MinimumRatingVotes,
    bool FetchTimeToBeat,
    bool TimeToBeatAsRuntime,
    IReadOnlyList<string> EditionWords,
    IReadOnlyList<string> NeverStripWords,
    IReadOnlyList<string> ExactTitleOnlyTypes,
    IReadOnlyDictionary<string, string> PlatformAliases)
{
    public const string KeyClientId = "client_id";
    public const string KeyClientSecret = "client_secret";
    public const string KeyRate = "requests_per_second";
    public const string KeyConcurrency = "max_concurrent_requests";
    public const string KeyRequestTimeout = "request_timeout_seconds";
    public const string KeyCallDeadline = "call_deadline_seconds";
    public const string KeySearchLimit = "search_limit";
    public const string KeyPosterSize = "poster_size";
    public const string KeyRatingSource = "rating_source";
    public const string KeyMinVotes = "minimum_rating_votes";
    public const string KeyFetchTtb = "fetch_time_to_beat";
    public const string KeyTtbRuntime = "use_time_to_beat_as_runtime";
    public const string KeyEditionWords = "edition_words";
    public const string KeyNeverStripWords = "never_strip_words";
    public const string KeyExactOnlyTypes = "exact_title_only_game_types";
    public const string KeyPlatformAliases = "platform_aliases";

    /// <summary>The cover sizes IGDB documents (a "_2x" form of each is also served).</summary>
    public static readonly string[] PosterSizes =
        ["cover_big_2x", "cover_big", "cover_small_2x", "cover_small", "720p", "1080p"];

    /// <summary>IGDB's documented limit is 4 requests a second; 3 leaves room for another client on the same Twitch application.</summary>
    public const double DefaultRate = 3;

    /// <summary>The interactive search (Add Media, file scans) gives a provider 4 seconds; the plugin stops at 3.5.</summary>
    public static readonly TimeSpan InteractiveDeadline = TimeSpan.FromSeconds(3.5);

    public bool HasCredentials => ClientId.Length > 0 && ClientSecret.Length > 0;

    public static readonly Settings Default = From(null);

    public static Settings From(IReadOnlyDictionary<string, string>? values)
    {
        string? Get(string key) => values is not null && values.TryGetValue(key, out var v) ? v?.Trim() : null;
        double Num(string key, double def, double lo, double hi) =>
            double.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d)
                ? Math.Clamp(d, lo, hi) : def;
        bool Flag(string key, bool def) => bool.TryParse(Get(key), out var b) ? b : def;
        List<string> ListOrDefault(string key, IReadOnlyList<string> def)
        {
            var l = TextRules.ParseList(Get(key));
            return l.Count > 0 ? l : def.ToList();
        }

        var poster = Get(KeyPosterSize)?.ToLowerInvariant();
        var rating = Get(KeyRatingSource)?.ToLowerInvariant() switch
        {
            "users" => RatingSource.Users,
            "critics" => RatingSource.Critics,
            _ => RatingSource.Total,
        };

        return new Settings(
            ClientId: Get(KeyClientId) ?? string.Empty,
            ClientSecret: Get(KeyClientSecret) ?? string.Empty,
            RequestsPerSecond: Num(KeyRate, DefaultRate, 1, 4),
            MaxConcurrentRequests: (int)Num(KeyConcurrency, 3, 1, 8),
            RequestTimeout: TimeSpan.FromSeconds(Num(KeyRequestTimeout, 8, 2, 20)),
            CallDeadline: TimeSpan.FromSeconds(Num(KeyCallDeadline, 20, 5, 24)),
            SearchLimit: (int)Num(KeySearchLimit, 10, 1, 50),
            PosterSize: poster is not null && PosterSizes.Contains(poster) ? poster : PosterSizes[0],
            RatingSource: rating,
            MinimumRatingVotes: (int)Num(KeyMinVotes, 0, 0, 100000),
            FetchTimeToBeat: Flag(KeyFetchTtb, true),
            TimeToBeatAsRuntime: Flag(KeyTtbRuntime, false),
            EditionWords: ListOrDefault(KeyEditionWords, TextRules.DefaultEditionWords),
            NeverStripWords: ListOrDefault(KeyNeverStripWords, TextRules.DefaultNeverStripWords),
            ExactTitleOnlyTypes: ListOrDefault(KeyExactOnlyTypes, TextRules.DefaultExactTitleOnlyTypes),
            PlatformAliases: TextRules.ParseAliases(ListOrDefault(KeyPlatformAliases, TextRules.DefaultPlatformAliases)));
    }
}
