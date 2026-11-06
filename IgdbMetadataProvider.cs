using System.Net;
using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Chronicle.Plugin.IGDB;

/// <summary>
/// Chronicle metadata provider for IGDB (video games). Independent of every other plugin: it needs only the item's name (and its
/// year or platform when they are in the title). A Steam or IGDB id the item already holds is used as an exact shortcut when it
/// is well formed; without one the plugin searches by name. Nothing it writes is required by another plugin, and it publishes no
/// cross-reference ids.
///
/// Matching order (design section 6.3, from the live probe): an exact-name-or-alternative-name query first, because IGDB's fuzzy
/// search ranked the exact title first for only 22 of 43 titles, then the fuzzy search as a fallback. Nothing similar is ever
/// accepted: a candidate must have the library's title (allowing edition words and Roman numerals) or an alternative name equal
/// to it. The winner is fetched IN FULL inside <see cref="SearchAsync"/>, with its time to beat and characters in one multiquery, so
/// the record Chronicle receives is complete even though the host swallows a failed follow-up fetch.
///
/// What a failure is told to Chronicle is decided in <see cref="Fail"/> and section 9 of the design: an outage, a timeout, a 5xx and
/// a rate limit are pauses (the item stays Pending, no retry is counted), a rejected key is an authentication failure, and only
/// the failure of one item's own record counts against that item. An interactive search (Add Media, scan search) never throws: it
/// returns what it has.
/// See docs/plans/2026-11-05-igdb-plugin-design.md in the Chronicle repo.
/// </summary>
public sealed class IgdbMetadataProvider : IMetadataProvider, IDisposable
{
    public string PluginId => IgdbClient.PluginId;
    public string Name     => "IGDB";
    public string Version  => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(IgdbMetadataProvider).Assembly)?.InformationalVersion.Split('+')[0] ?? "1.1.0";   // the project's <Version>: one place to bump
    public string Author   => "Chronicle Contributors";

    private static readonly ILogger Log = Serilog.Log.ForContext<IgdbMetadataProvider>();

    // Technical limits of the plugin's own machinery.
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SourceIdCacheFor = TimeSpan.FromHours(24);
    private static readonly TimeSpan EmptyCacheFor = TimeSpan.FromMinutes(1);
    /// <summary>How long a switched-off expansion group stays off before IGDB is asked for it again (a transient 400 must not degrade the plugin for good).</summary>
    private static readonly TimeSpan GroupOffFor = TimeSpan.FromHours(1);
    private const int MaxCandidates = 5;
    private const int PoisonLimit = 3;

    private IgdbClient? _client;
    private HttpClient? _ownedHttp;
    private HttpClient? _imageHttp;
    private bool _clientSupplied;
    private Settings _settings = Settings.Default;
    private CancellationTokenSource _warm = new();

    private readonly TtlCache<JsonElement> _queries = new(CacheFor, 500);
    private readonly TtlCache<JsonElement> _queriesEmpty = new(EmptyCacheFor, 200);   // "nothing found" is remembered for a minute only: a game may be added to IGDB
    private readonly TtlCache<FullRecord> _full = new(CacheFor, 200);
    private readonly TtlCache<long> _steamSource = new(SourceIdCacheFor, 4);
    private readonly Dictionary<string, DateTimeOffset> _disabledGroups = new(StringComparer.Ordinal);
    private readonly object _groupLock = new();
    private readonly Dictionary<string, (int Count, int SuccessMark)> _poison = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unknownTypesLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _imageLock = new();

    internal Settings Current => _settings;
    internal IReadOnlyCollection<string> DisabledGroups => ActiveDisabledGroups();

    /// <summary>The expansion groups that are switched off right now (a group comes back after <see cref="GroupOffFor"/>).</summary>
    private HashSet<string> ActiveDisabledGroups()
    {
        var now = _client?.Now ?? DateTimeOffset.UtcNow;
        lock (_groupLock)
        {
            foreach (var g in _disabledGroups.Where(kv => now - kv.Value >= GroupOffFor).Select(kv => kv.Key).ToList()) _disabledGroups.Remove(g);
            return new HashSet<string>(_disabledGroups.Keys, StringComparer.Ordinal);
        }
    }
    internal string? ConfiguredUserAgent { get; private set; }

    public IgdbMetadataProvider() { }

    /// <summary>For tests: a client over a fake HTTP handler, and the settings to use.</summary>
    internal IgdbMetadataProvider(IgdbClient client, Settings? settings = null)
    {
        _client = client;
        _clientSupplied = true;
        _settings = settings ?? Settings.Default;
        client.Configure(_settings);
    }

    // ── capabilities ──────────────────────────────────────────────────────────

    public MediaTypeSupport[] GetSupportedMediaTypes() => [BuildType(_settings)];

    internal static MediaTypeSupport BuildType(Settings s)
    {
        // The names must be the host's canonical field names (MetadataResolutionService.FieldMap): they are what puts this plugin on
        // the Metadata Assignment page for each field. runtime_minutes is declared only when the setting that fills it is on, so
        // the host never queues a field that can only fail.
        var fields = new List<string> { "title", "overview", "year", "poster_url", "backdrop_url", "rating", "genres", "tags", "cast", "crew", "collection" };
        if (s.TimeToBeatAsRuntime) fields.Add("runtime_minutes");
        return new MediaTypeSupport
        {
            MediaTypeName = TypeName,
            // The same declaration the IMDb plugin makes for this type ("Video Games", played, percent): Chronicle merges every plugin's
            // declaration of one type by name, so the two must agree or the type's unit would depend on which plugin loaded first.
            DisplayName = "Video Games",
            HierarchyLevels = 1,
            HierarchyLabels = ["Game"],
            InteractionVerb = "played",
            ProgressUnit = "percent",
            IsTrackable = true,
            CastHeading = "Developers",
            CastAreActors = false,
            // IGDB names the series a game belongs to; Chronicle groups a game under its series when it is told one, and leaves it at the top level when not.
            SupportsCollections = true,
            // Client wording only. The type is named "game", as the IMDb plugin, the Wikipedia plugin and the other game plugins already name it.
            Aliases = ["games", "videogame", "video_game"],
            EnrichOnly = false,
            DefaultPriority = 10,
            SupportedFields = fields,
        };
    }

    public const string TypeName = "game";

    private static bool TypeFits(string? type) =>
        type is null or TypeName or "games" or "videogame" or "video_game";

    public IReadOnlyList<string> GetAcceptedCrossRefPrefixes() => ["igdb:", "steam:"];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key = "notice", Label = "Getting a client id and secret", Type = SettingType.Notice,
                Description = "IGDB is free for non-commercial use but needs a Twitch developer application. Sign in to dev.twitch.tv/console (Twitch requires " +
                              "two-factor authentication on the account), click Register Your Application, enter any name, the redirect address https://localhost, " +
                              "category Application Integration and client type Confidential, then open the application, click New Secret and copy the Client ID and " +
                              "the secret (shown once). Use a separate application for each Chronicle instance: an application can have only 25 active tokens.",
            },
            new SettingDefinition { Key = Settings.KeyClientId, Label = "Twitch client id", Type = SettingType.Text, Required = true,
                Description = "The Client ID of your Twitch application." },
            new SettingDefinition { Key = Settings.KeyClientSecret, Label = "Twitch client secret", Type = SettingType.Password, Required = true,
                Description = "The Client Secret of your Twitch application. Saved encrypted; administrators can see saved values on this page. Never logged or sent anywhere but Twitch." },
            new SettingDefinition { Key = Settings.KeyRate, Label = "Requests per second", Type = SettingType.Number, DefaultValue = "3",
                Description = "IGDB allows 4 a second. 3 leaves room for another client on the same application. 1 to 4." },
            new SettingDefinition { Key = Settings.KeyConcurrency, Label = "Requests in flight", Type = SettingType.Number, DefaultValue = "3",
                Description = "The most requests open at once (IGDB allows 8). 1 to 8." },
            new SettingDefinition { Key = Settings.KeyRequestTimeout, Label = "Request timeout (seconds)", Type = SettingType.Number, DefaultValue = "8",
                Description = "How long one request may take before it is treated as an outage and paused. 2 to 20." },
            new SettingDefinition { Key = Settings.KeyCallDeadline, Label = "Call deadline (seconds)", Type = SettingType.Number, DefaultValue = "20",
                Description = "How long one lookup may take altogether. Chronicle gives a lookup 25 seconds, so keep this under that. 5 to 24." },
            new SettingDefinition { Key = Settings.KeySearchLimit, Label = "Candidates per search", Type = SettingType.Number, DefaultValue = "10",
                Description = "How many games IGDB returns for a title query before they are scored. 1 to 50." },
            new SettingDefinition
            {
                Key = Settings.KeyPosterSize, Label = "Cover size", Type = SettingType.Dropdown, DefaultValue = Settings.PosterSizes[0],
                Description = "The size of the cover used as the poster. Every other image is kept at its largest useful size.",
                Options = Settings.PosterSizes.Select(v => new SelectOption { Value = v, Label = v }).ToList(),
            },
            new SettingDefinition
            {
                Key = Settings.KeyRatingSource, Label = "Rating to use", Type = SettingType.Dropdown, DefaultValue = "total",
                Description = "Which IGDB score becomes the item's rating (on a 0 to 10 scale). All three scores are kept in the extended data either way.",
                Options =
                [
                    new SelectOption { Value = "total", Label = "Total (users and critics)" },
                    new SelectOption { Value = "users", Label = "IGDB users" },
                    new SelectOption { Value = "critics", Label = "Critics" },
                ],
            },
            new SettingDefinition { Key = Settings.KeyMinVotes, Label = "Minimum votes for the rating", Type = SettingType.Number, DefaultValue = "0",
                Description = "0 hides nothing. A higher number keeps a score from fewer votes in the extended data only." },
            new SettingDefinition { Key = Settings.KeyFetchTtb, Label = "Fetch time to beat", Type = SettingType.Boolean, DefaultValue = "true",
                Description = "Includes how long IGDB's players take to finish the game (part of the same request)." },
            new SettingDefinition { Key = Settings.KeyTtbRuntime, Label = "Use time to beat as the runtime", Type = SettingType.Boolean, DefaultValue = "false",
                Description = "Fills the item's runtime with the \"normally\" time to beat. Off by default: Chronicle derives time spent from runtime." },
            new SettingDefinition { Key = Settings.KeyEditionWords, Label = "Edition words", Type = SettingType.TextArea,
                DefaultValue = string.Join("\n", TextRules.DefaultEditionWords),
                Description = "One per line. Words that mark an EDITION of the same game (\"Deluxe Edition\") and are ignored when comparing titles. Never add Remastered or Remake: those are different releases." },
            new SettingDefinition { Key = Settings.KeyNeverStripWords, Label = "Words that make a different release", Type = SettingType.TextArea,
                DefaultValue = string.Join("\n", TextRules.DefaultNeverStripWords),
                Description = "One per line. A title qualifier containing one of these is never ignored, whatever the edition words say." },
            new SettingDefinition { Key = Settings.KeyExactOnlyTypes, Label = "Types matched only on an exact title", Type = SettingType.TextArea,
                DefaultValue = string.Join("\n", TextRules.DefaultExactTitleOnlyTypes),
                Description = "One per line, as IGDB names the game type. These (DLC, bundles, mods...) are matched only when the title is exact and carry a penalty. No type is excluded." },
            new SettingDefinition { Key = Settings.KeyPlatformAliases, Label = "Platform tags", Type = SettingType.TextArea,
                DefaultValue = string.Join("\n", TextRules.DefaultPlatformAliases),
                Description = "One per line, tag = platform name as IGDB spells it. A trailing (PS4) or [PC] on a title is read as a platform hint when the tag is listed here." },
        ],
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        _settings = Settings.From(settings);
        if (_clientSupplied) { _client!.Configure(_settings); return; }

        if (_client is null)
        {
            _ownedHttp = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.All,
            })
            { Timeout = Timeout.InfiniteTimeSpan };   // each request has its own timeout and the call has a deadline
            ConfiguredUserAgent = IgdbClient.UserAgent(Version);
            _ownedHttp.DefaultRequestHeaders.UserAgent.TryParseAdd(ConfiguredUserAgent);
            _client = new IgdbClient(_ownedHttp, Log);
        }
        _client.Configure(_settings);

        Log.Information("IGDB configured ({Rate} requests a second, timeout {Timeout}s, deadline {Deadline}s, credentials {Creds})",
            _settings.RequestsPerSecond, _settings.RequestTimeout.TotalSeconds, _settings.CallDeadline.TotalSeconds,
            _settings.HasCredentials ? "present" : "missing");

        // Start the token in the background so the first interactive search does not pay for it. Best effort, never throws.
        var previous = _warm;
        _warm = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        if (_settings.HasCredentials)
        {
            var client = _client; var token = _warm.Token;
            _ = Task.Run(() => client.WarmAsync(token));
        }
    }

    private IgdbClient Client => _client ?? throw Fail.Auth("IGDB is not configured. Enter a Twitch client id and secret in this plugin's settings.");

    // ── guarded calls ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one plugin call under its deadline. Enrichment and by-id calls THROW their mapped failures; an interactive search never
    /// throws (the host's search catches everything and shows nothing anyway, and its guard would sleep six seconds retrying a
    /// pause) and returns <paramref name="whenInteractiveFails"/>.
    /// </summary>
    private async Task<T> GuardedAsync<T>(bool interactive, T whenInteractiveFails, string what, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var deadline = interactive ? Settings.InteractiveDeadline : _settings.CallDeadline;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);
        try
        {
            return await work(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The plugin's own deadline, not Chronicle's: a pause, never the host's 25-second fallback (which reads as "not found").
            Log.Warning("IGDB {What} took longer than {Seconds:0.#} seconds", what, deadline.TotalSeconds);
            if (interactive) return whenInteractiveFails;
            // IGDB is answering, slowly: a short cool-down, so the host's own two retries fail at once instead of each waiting out another deadline
            // (three deadlines and the retry sleeps would be longer than the 40 seconds the host holds an item).
            _client?.NoteSlow();
            throw Fail.Pause($"IGDB did not finish within {deadline.TotalSeconds:0.#} seconds.", null, "transport");
        }
        catch (Exception ex) when (interactive && ex is not OperationCanceledException)
        {
            Log.Information("IGDB interactive {What} gave no result: {Reason}", what, Safe(ex.Message));
            return whenInteractiveFails;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or PluginAuthException or KeyNotFoundException or ArgumentException
                                              or InvalidDataException or HttpRequestException))
        {
            // Anything this plugin did not expect would reach the host as an ordinary failure, or as a permanent "Skipped" (an
            // InvalidOperationException). It is a pause instead: the item is not at fault and nothing is lost.
            Log.Error(ex, "IGDB {What}: unexpected {Type}", what, ex.GetType().Name);
            throw Fail.Pause($"The IGDB plugin hit an unexpected error ({ex.GetType().Name}: {Safe(ex.Message)}).", null, "shape", ex);
        }
    }

    private string Safe(string? s) => _client?.Redact(s) ?? s ?? string.Empty;

    // ── search ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context, CancellationToken ct = default)
    {
        if (!TypeFits(context.MediaTypeName)) return [];
        var interactive = !context.IsRealHierarchyPosition;
        var raw = FirstText(context);
        if (string.IsNullOrWhiteSpace(raw) && ParsedId.KnownIgdb(context.KnownExternalIds) is null && ParsedId.KnownSteam(context.KnownExternalIds) is null)
            return [];

        if (!interactive) Client.ThrowIfCannotAuthenticate();
        else if (_client is null || !_settings.HasCredentials || _client.CredentialsRejected)
        {
            Log.Information("IGDB interactive search skipped: no accepted credentials");
            return [];
        }

        return await GuardedAsync<IReadOnlyList<ScoredCandidate>>(interactive, [], "search", t => SearchCoreAsync(context, interactive, t), ct).ConfigureAwait(false);
    }

    private static string? FirstText(MediaSearchContext c) =>
        !string.IsNullOrWhiteSpace(c.PreciseName) ? c.PreciseName :
        c.AltTitles?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) is { } alt ? alt : c.Name;

    private async Task<IReadOnlyList<ScoredCandidate>> SearchCoreAsync(MediaSearchContext context, bool interactive, CancellationToken ct)
    {
        var s = _settings;

        // 1. an id Chronicle already holds: an exact lookup
        if (ParsedId.KnownIgdb(context.KnownExternalIds) is { } known)
        {
            var rec = await FetchFullAsync(known, ct).ConfigureAwait(false);
            if (rec is not null) return [Identified(rec, 100, "known IGDB id")];
        }
        if (ParsedId.KnownSteam(context.KnownExternalIds) is { } steam)
        {
            var id = await SteamToGameAsync(steam, ct).ConfigureAwait(false);
            if (id is { } gid && await FetchFullAsync(gid, ct).ConfigureAwait(false) is { } rec)
                return [Identified(rec, 100, "known Steam id")];
        }

        // 2. by name
        var want = WantedFrom(context, s);
        if (want is null) return [];
        var variants = NameVariants(want, context, s);
        if (variants.Count == 0) return [];

        var found = new Dictionary<long, (GameBrief Brief, JsonElement Game, Match Match)>();
        bool Settled() => found.Values.Any(f => f.Match.Score >= Matchers.FetchGate);

        // An interactive caller (a person typing into Add Media) also gets SIMILAR titles to choose from, scored under Chronicle's gate so
        // that nothing is accepted automatically on them. The enrichment pass accepts an exact title or alternative name only.
        await CollectAsync(found, Queries.ExactName(variants, s.SearchLimit), want, interactive, ct).ConfigureAwait(false);

        // The fuzzy search: the whole title first (a store's "Assassin's Creed: Odyssey" is IGDB's "Assassin's Creed Odyssey"), then, for the
        // enrichment pass, the main title alone. The interactive caller has 3.5 seconds, enough for two quick requests (0.1 to 0.25 s each).
        if (!Settled())
        {
            var whole = TextRules.CleanQueryText(want.Stripped);
            if (whole.Length > 0)
                await CollectAsync(found, Queries.Fuzzy(whole, s.SearchLimit), want, interactive, ct).ConfigureAwait(false);
            if (!interactive && !Settled() && want.Main is { Length: > 0 } main && !string.Equals(TextRules.CleanQueryText(main), whole, StringComparison.OrdinalIgnoreCase)
                && TextRules.CleanQueryText(main).Length > 0)
                await CollectAsync(found, Queries.Fuzzy(main, s.SearchLimit), want, interactive, ct).ConfigureAwait(false);
        }

        var ranked = found.Values.OrderByDescending(f => f.Match.Score).ThenByDescending(f => f.Brief.RatingCount).Take(MaxCandidates).ToList();
        if (ranked.Count == 0) return [];

        var result = new List<ScoredCandidate>();
        for (var i = 0; i < ranked.Count; i++)
        {
            var (brief, game, match) = ranked[i];
            MediaMetadata meta;
            if (i == 0 && !interactive && match.Score >= Matchers.FetchGate && await FetchFullAsync(brief.Id, ct).ConfigureAwait(false) is { } full)
                meta = MetadataMapper.Map(full.Game, full.TimeToBeat, full.Characters, true, s, ActiveDisabledGroups());
            else
                meta = MetadataMapper.Map(game, null, null, false, s);
            result.Add(new ScoredCandidate(meta, match.Score, match.Reason));
        }
        return result;
    }

    private ScoredCandidate Identified(FullRecord rec, int score, string reason) =>
        new(MetadataMapper.Map(rec.Game, rec.TimeToBeat, rec.Characters, true, _settings, ActiveDisabledGroups()), score, reason) { IdentifierMatch = true };

    /// <summary>"Doom (2016) [PS4]" and "Doom [PS4] (2016)" are both read: platform tag, year, platform tag again.</summary>
    private static (string Title, int? Year, string? Platform) ReadTitle(string raw, Settings s)
    {
        var (t1, p1) = TextRules.SplitPlatform(raw.Trim(), s.PlatformAliases);
        var (t2, year) = TextRules.SplitYear(t1);
        var (t3, p2) = TextRules.SplitPlatform(t2, s.PlatformAliases);
        return (t3.Trim(), year, p1 ?? p2);
    }

    /// <summary>Reads the library title: its year, its platform tag, its edition words. Null when nothing usable is left.</summary>
    private static Wanted? WantedFrom(MediaSearchContext context, Settings s)
    {
        var raw = FirstText(context);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var (title, suffixYear, platform) = ReadTitle(raw, s);
        if (TextRules.CleanQueryText(title).Length == 0) return null;
        return Wanted.From(title, context.Year ?? suffixYear, platform, s);
    }

    /// <summary>The titles one exact-name query asks for: the title, without its edition words, and with its sequel numbers written the other way.</summary>
    private static List<string> NameVariants(Wanted want, MediaSearchContext context, Settings s)
    {
        var list = new List<string> { want.Title };
        if (!string.Equals(want.Stripped, want.Title, StringComparison.Ordinal)) list.Add(want.Stripped);
        if (!string.IsNullOrWhiteSpace(want.ReworkBase)) list.Add(want.ReworkBase);   // "Resident Evil 4 Remake": IGDB names the remake "Resident Evil 4"
        foreach (var v in list.ToList())
            if (TextRules.SwapNumerals(v) is { } swapped) list.Add(swapped);
        // The raw item name, when the host's cleaned alternative title differs from it.
        if (!string.IsNullOrWhiteSpace(context.Name) && !string.Equals(context.Name, want.Title, StringComparison.OrdinalIgnoreCase))
        {
            var (n2, _, _) = ReadTitle(context.Name, s);
            if (TextRules.CleanQueryText(n2).Length > 0 && TextRules.Normalize(n2) != TextRules.Normalize(want.Title)) list.Add(n2);
        }
        return list.Select(TextRules.CleanQueryText).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
    }

    private async Task CollectAsync(Dictionary<long, (GameBrief, JsonElement, Match)> found, string body, Wanted want, bool similarToo, CancellationToken ct)
    {
        var rows = await QueryCachedAsync("games", body, ct).ConfigureAwait(false);
        foreach (var game in rows.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object) continue;
            var brief = GameBrief.From(game);
            if (brief is null) continue;
            if (brief.TypeName is { } tn && !Matchers.KnownMatchableTypes.Contains(tn) &&
                !_settings.ExactTitleOnlyTypes.Contains(tn, StringComparer.OrdinalIgnoreCase))
            {
                bool first;
                lock (_unknownTypesLogged) first = _unknownTypesLogged.Add(tn);
                if (first) Log.Warning("IGDB game type \"{Type}\" is not one this plugin knows; it is matched on an exact title only", tn);
            }
            var match = Matchers.Score(want, brief, _settings) ?? (similarToo ? Matchers.ScoreSimilar(want, brief) : null);
            if (match is null) continue;
            if (!found.TryGetValue(brief.Id, out var old) || match.Score > old.Item3.Score)
                found[brief.Id] = (brief, game.Clone(), match);
        }
    }

    private async Task<JsonElement> QueryCachedAsync(string endpoint, string body, CancellationToken ct)
    {
        var key = endpoint + "\n" + body;
        if (_queries.TryGet(key, out var hit) || _queriesEmpty.TryGet(key, out hit)) return hit;
        var rows = await Client.QueryAsync(endpoint, body, ct).ConfigureAwait(false);
        if (rows.GetArrayLength() == 0) _queriesEmpty.Set(key, rows); else _queries.Set(key, rows);
        return rows;
    }

    // ── steam, slug ───────────────────────────────────────────────────────────

    private async Task<long?> SteamSourceIdAsync(CancellationToken ct)
    {
        if (_steamSource.TryGet("steam", out var cached)) return cached;
        var rows = await Client.QueryAsync("external_game_sources", Queries.SteamSources, ct).ConfigureAwait(false);
        foreach (var r in rows.EnumerateArray())
            if (r.ValueKind == JsonValueKind.Object && string.Equals(r.Str("name"), "Steam", StringComparison.OrdinalIgnoreCase) && r.Long("id") is { } id)
            {
                _steamSource.Set("steam", id);
                return id;
            }
        return null;   // the table could not be read as expected: the Steam route is skipped (the deprecated numeric category is never sent)
    }

    /// <summary>The IGDB game a Steam app id belongs to. Several games can share one uid (bundles, DLC): the main game wins, then the earliest release.</summary>
    private async Task<long?> SteamToGameAsync(string appId, CancellationToken ct)
    {
        if (await SteamSourceIdAsync(ct).ConfigureAwait(false) is not { } source) return null;
        var rows = await Client.QueryAsync("external_games", Queries.BySteam(source, appId), ct).ConfigureAwait(false);
        var games = new List<(long Id, bool Main, long Date)>();
        foreach (var eg in rows.EnumerateArray())
        {
            if (eg.ValueKind != JsonValueKind.Object || eg.Obj("game") is not { } g || g.Long("id") is not { } id) continue;
            games.Add((id, string.Equals(g.Obj("game_type")?.Str("type"), "Main Game", StringComparison.OrdinalIgnoreCase), g.Long("first_release_date") ?? long.MaxValue));
        }
        return games.Count == 0 ? null : games.OrderByDescending(g => g.Main).ThenBy(g => g.Date).First().Id;
    }

    // ── the full record ───────────────────────────────────────────────────────

    /// <summary>
    /// The game with every expansion, its time to beat and its characters, in ONE multiquery (measured: 200 in 0.16 s). Null when IGDB
    /// has no such game. A 400 naming a field IGDB no longer has costs only the group containing it; a second failure is a pause.
    /// Repeated failures of one record count against the item (section 9.4 of the design) so it cannot stall the queue.
    /// </summary>
    private async Task<FullRecord?> FetchFullAsync(long id, CancellationToken ct)
    {
        var key = "game:" + id;
        if (_full.TryGet(key, out var cached)) return cached;
        try
        {
            var rec = await FetchFullCoreAsync(id, ct).ConfigureAwait(false);
            lock (_poison) _poison.Remove(key);
            if (rec is not null) _full.Set(key, rec);
            return rec;
        }
        catch (Exception ex) when (Fail.IsPerItemKind(ex))
        {
            // A failure counts against the item only when IGDB has answered other requests since this record last failed: during an
            // outage nothing succeeds, and an item must not be exhausted by IGDB being down for a few hours.
            var successes = Client.Successes;
            int n;
            lock (_poison)
            {
                if (_poison.Count > 500) _poison.Clear();
                n = !_poison.TryGetValue(key, out var e) ? 1 : successes > e.SuccessMark ? e.Count + 1 : e.Count;
                _poison[key] = (n, !_poison.ContainsKey(key) || successes > _poison[key].SuccessMark ? successes : _poison[key].SuccessMark);
            }
            if (n >= PoisonLimit)
            {
                lock (_poison) _poison.Remove(key);
                throw new InvalidDataException($"IGDB's record for game {id} failed {n} times in a row ({Safe(ex.Message)}); it is counted against this item so it cannot block the others.", ex);
            }
            throw;
        }
    }

    private async Task<FullRecord?> FetchFullCoreAsync(long id, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var disabled = ActiveDisabledGroups();
            using var reply = await Client.PostAsync("multiquery", Queries.Full(id, disabled, _settings.FetchTimeToBeat), ct).ConfigureAwait(false);

            if (reply.Status == 400)
            {
                var field = Queries.FieldFromError(reply.ErrorText);
                var group = field is null ? null : Queries.GroupOf(field);
                if (attempt >= 1)
                    throw Fail.Pause($"IGDB rejected the full-record query even without the field that failed ({Safe(IgdbClient.Trim(reply.ErrorText))}).", null, "badrequest");
                var at = Client.Now;
                lock (_groupLock)
                {
                    if (group is not null)
                    {
                        if (_disabledGroups.TryAdd(group, at))
                            Log.Warning("IGDB no longer has the field {Field}; the {Group} group is switched off (tried again in an hour)", field, group);
                    }
                    else
                    {
                        foreach (var g in Queries.Groups) _disabledGroups[g.Name] = at;
                        Log.Warning("IGDB rejected a query ({Reason}); every expansion group is switched off (tried again in an hour)", Safe(IgdbClient.Trim(reply.ErrorText)));
                    }
                }
                continue;
            }

            var root = reply.Doc!.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                throw Fail.Pause("IGDB answered a multiquery with a shape this plugin does not know.", null, "shape");

            JsonElement? game = null, ttb = null, chars = null;
            foreach (var part in root.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;
                if (!part.TryGet("result", out var result) || result.ValueKind != JsonValueKind.Array) continue;
                switch (part.Str("name"))
                {
                    case "game": game = result.Clone(); break;
                    case "ttb": ttb = result.Clone(); break;
                    case "characters": chars = result.Clone(); break;
                }
            }
            if (game is null) throw Fail.Pause("IGDB's multiquery answer had no game part.", null, "shape");
            var first = game.Value.EnumerateArray().FirstOrDefault(g => g.ValueKind == JsonValueKind.Object);
            if (first.ValueKind != JsonValueKind.Object) return null;   // a genuine, well-formed empty answer: IGDB has no such game
            return new FullRecord(first.Clone(), ttb, chars);
        }
    }

    // ── fetch by id ───────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        var parsed = ParsedId.Parse(externalId);   // ArgumentException: Chronicle clears a stored id that fails here and searches again
        Client.ThrowIfCannotAuthenticate();
        return await GuardedAsync<MediaMetadata>(false, null!, "fetch", async t =>
        {
            long? gameId = parsed.Kind switch
            {
                IdKind.Igdb => parsed.Number,
                IdKind.Steam => await SteamToGameAsync(parsed.Text!, t).ConfigureAwait(false),
                _ => await SlugToGameAsync(parsed.Text!, t).ConfigureAwait(false),
            };
            if (gameId is null) throw new KeyNotFoundException($"IGDB has no game for {externalId.Trim()}.");
            var rec = await FetchFullAsync(gameId.Value, t).ConfigureAwait(false)
                      ?? throw new KeyNotFoundException($"IGDB has no game {gameId}.");
            return MetadataMapper.Map(rec.Game, rec.TimeToBeat, rec.Characters, true, _settings, ActiveDisabledGroups());
        }, ct).ConfigureAwait(false);
    }

    private async Task<long?> SlugToGameAsync(string slug, CancellationToken ct)
    {
        var rows = await Client.QueryAsync("games", Queries.BySlug(slug), ct).ConfigureAwait(false);
        foreach (var g in rows.EnumerateArray())
            if (g.ValueKind == JsonValueKind.Object && g.Long("id") is { } id) return id;
        return null;
    }

    // ── images, health ────────────────────────────────────────────────────────

    private const long MaxImageBytes = 25L * 1024 * 1024;

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort || !string.Equals(uri.Host, "images.igdb.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This plugin fetches images only from https://images.igdb.com.");

        HttpClient http;
        lock (_imageLock)
            http = _imageHttp ??= new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            { Timeout = TimeSpan.FromSeconds(30) };
        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is > MaxImageBytes) throw new InvalidDataException("The image is larger than 25 MB.");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxImageBytes) throw new InvalidDataException("The image is larger than 25 MB.");
        }
        return ms.ToArray();
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null || !_settings.HasCredentials) return false;
        if (_client.CredentialsRejected) { Log.Warning("IGDB health check: the credentials were rejected"); return false; }
        if (_client.CoolingDown) { Log.Warning("IGDB health check: {State}", _client.BreakerState); return false; }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_settings.CallDeadline);
            var rows = await _client.QueryAsync("games", "fields id; limit 1;", cts.Token).ConfigureAwait(false);
            if (DisabledGroups.Count > 0) Log.Warning("IGDB is working but degraded: groups switched off: {Groups}", string.Join(", ", DisabledGroups));
            return rows.ValueKind == JsonValueKind.Array;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warning("IGDB health check failed: {Reason}", Safe(ex.Message));
            return false;
        }
    }

    public void Dispose()
    {
        _warm.Cancel();
        _warm.Dispose();
        if (!_clientSupplied) _client?.Dispose();
        _ownedHttp?.Dispose();
        _imageHttp?.Dispose();
    }
}
