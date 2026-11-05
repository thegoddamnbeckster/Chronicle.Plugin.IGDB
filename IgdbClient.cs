using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicle.Plugins;
using Serilog;

namespace Chronicle.Plugin.IGDB;

/// <summary>One answer from IGDB: the status, the parsed JSON (null unless 200) and, for a 400, the raw text (the field-drift logic reads it).</summary>
internal sealed record ApiReply(int Status, JsonDocument? Doc, string? ErrorText) : IDisposable
{
    public void Dispose() => Doc?.Dispose();
}

/// <summary>
/// HTTP wrapper for IGDB's v4 API and the Twitch token endpoint it authenticates through.
///
/// Measured against the live service (2026-11-05):
///   200 [..]                              found (an unknown id is 200 with [])
///   400 [{"title":"Invalid Field","cause":"Invalid field name: 'x'"}]   a field IGDB no longer has (names it)
///   401                                   a token IGDB does not accept
///   429 {"message":"Too Many Requests"}   no Retry-After, no rate-limit headers; 200 again about 0.6 s later
///   Twitch 403 {"message":"invalid client secret"}, 400 {"message":"invalid client"}
///
/// What each failure is told to Chronicle is decided in <see cref="Fail"/>. The client never retries on its own (the host's
/// ProviderCallGuard already retries pause-type failures twice); it opens a cool-down after a real failure so those retries cost
/// nothing; it keeps a credentials latch so a rejected secret costs one call, not one per queued item; the token is minted once
/// and shared. The client secret and the access token never appear in a log line, an exception message or a URL: the secret
/// travels in a form body, the token in a header, and every message passes <see cref="Redact"/>.
/// </summary>
internal sealed class IgdbClient : IDisposable
{
    public const string PluginId = "chronicle.plugin.igdb";
    public const string ApiBase = "https://api.igdb.com/v4/";
    public const string TokenUrl = "https://id.twitch.tv/oauth2/token";

    /// <summary>
    /// How long a rejection of the credentials is believed. After this one request is allowed again, because a 403 can come from a
    /// firewall and two 401s in a row can be a glitch at IGDB; saving settings clears the latch at once.
    /// </summary>
    public static readonly TimeSpan LatchLasts = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Limiter _limiter;
    private readonly Breaker _breaker;
    private readonly SemaphoreSlim _mintGate = new(1, 1);

    /// <summary>The credentials in force; replaced as one value so a request always uses a matching pair.</summary>
    private sealed record Creds(string Id, string Secret)
    {
        public string Hash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Id + "\n" + Secret)));
    }

    /// <summary>A token, until when it is used, and the credentials it was minted with, held together and replaced as one.</summary>
    private sealed record TokenState(string Token, DateTimeOffset Until, Creds For);

    private sealed record Latched(string CredsHash, DateTimeOffset At);

    private volatile Creds _creds = new(string.Empty, string.Empty);
    private volatile TokenState? _state;
    private volatile Latched? _latched;
    private TimeSpan _requestTimeout = TimeSpan.FromSeconds(8);

    private int _requests, _successes;

    /// <summary>How many requests have been sent to IGDB (the token endpoint is not counted); tests check each call's budget against it.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>How many requests IGDB has answered with a result. A rise tells the poison-item counter that IGDB works for other records.</summary>
    public int Successes => Volatile.Read(ref _successes);

    public DateTimeOffset Now => _now();

    public IgdbClient(HttpClient http, ILogger? log = null, Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? random = null)
    {
        _http = http;
        _log = log ?? Serilog.Core.Logger.None;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        var rnd = random ?? Random.Shared.NextDouble;
        _limiter = new Limiter(Settings.DefaultRate, 3, _now, _delay);
        _breaker = new Breaker(_now, rnd);
    }

    public bool CoolingDown => _breaker.IsOpen;
    public TimeSpan CoolDownRemaining => _breaker.Remaining;
    public string BreakerState => _breaker.Describe();

    /// <summary>True while the current credentials are the ones that were rejected, and not for longer than <see cref="LatchLasts"/>.</summary>
    public bool CredentialsRejected =>
        _latched is { } l && l.CredsHash == _creds.Hash() && _now() - l.At < LatchLasts;

    /// <summary>IGDB is answering, but slowly: opens a short cool-down so the host's own retries do not each wait out another deadline.</summary>
    public void NoteSlow() => _breaker.Trip(false, "IGDB answering too slowly");

    public static string UserAgent(string version) => $"Chronicle-IGDB/{version}";

    /// <summary>Applies the settings. Saving settings always clears the latch (the person may have fixed the Twitch side); different credentials also discard the token.</summary>
    public void Configure(Settings s)
    {
        var old = _creds;
        var next = new Creds(s.ClientId, s.ClientSecret);
        _creds = next;
        _requestTimeout = s.RequestTimeout;
        _limiter.Configure(s.RequestsPerSecond, s.MaxConcurrentRequests);
        _latched = null;
        if (old != next) _state = null;
    }

    // ── redaction ─────────────────────────────────────────────────────────────

    /// <summary>Removes the secret, the access token and the client id from anything that is about to leave the plugin.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var c = _creds;
        foreach (var secret in new[] { c.Secret, _state?.Token, c.Id })
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 6)
                text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text;
    }

    // ── credentials ───────────────────────────────────────────────────────────

    private static bool Printable(string s) => s.All(c => c is > ' ' and < '\u007f');

    /// <summary>Throws at once, with no request, if the credentials are missing, cannot be real, or were already rejected.</summary>
    public void ThrowIfCannotAuthenticate()
    {
        var c = _creds;
        if (string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(c.Secret))
            throw Fail.Auth("IGDB needs a Twitch client id and client secret. Create a free application at dev.twitch.tv/console (Twitch needs two-factor authentication on), then enter both in this plugin's settings.");
        if (!Printable(c.Id) || !Printable(c.Secret))
            throw Fail.Auth("The Twitch client id or secret contains a space or a character a Twitch credential never has. Copy them again from dev.twitch.tv/console.");
        if (CredentialsRejected)
            throw Fail.Auth("Twitch or IGDB did not accept the client id and secret. Create a new secret at dev.twitch.tv/console, enter it in this plugin's settings and save, then reset the Authentication Failed items on the Enrichment page.");
    }

    /// <summary>Latches the credentials that were used, but only if they are still the ones in force (a stale answer must not latch a corrected pair).</summary>
    private PluginAuthException Latch(Creds used, string message)
    {
        if (used == _creds)
        {
            _latched = new Latched(used.Hash(), _now());
            _state = null;
        }
        _log.Warning("IGDB credentials rejected: {Message}", message);
        return Fail.Auth(message);
    }

    // ── the token ─────────────────────────────────────────────────────────────

    private TokenState? Valid() => _state is { } s && s.For == _creds && _now() < s.Until ? s : null;

    private async Task<TokenState> TokenAsync(CancellationToken ct)
    {
        if (Valid() is { } fast) return fast;
        await _mintGate.WaitAsync(ct).ConfigureAwait(false);   // single-flight: concurrent callers wait for one mint
        try
        {
            if (Valid() is { } again) return again;
            ThrowIfCannotAuthenticate();
            _breaker.ThrowIfOpen();

            var used = _creds;   // the pair this mint is for; if settings change meanwhile the result is not stored
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = used.Id,
                    ["client_secret"] = used.Secret,
                    ["grant_type"] = "client_credentials",
                }),
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_requestTimeout);
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _breaker.Trip(false, "a timeout reaching Twitch");
                throw Fail.Pause($"Twitch did not answer within {_requestTimeout.TotalSeconds:0} seconds.", null, "transport");
            }
            catch (HttpRequestException ex)
            {
                _breaker.Trip(false, "a network error reaching Twitch");
                throw Fail.Pause($"Could not reach Twitch: {Redact(ex.Message)}", null, "transport");
            }

            using (resp)
            {
                var text = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
                var code = (int)resp.StatusCode;
                if (code == 200)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var token = doc.RootElement.Str("access_token");
                        var life = doc.RootElement.Long("expires_in") ?? 3600;
                        if (string.IsNullOrEmpty(token)) throw new JsonException("no access_token");
                        // Use the token until 24 hours before it expires; a short-lived one (a day or less, or no expires_in) for 90% of its life; never under a minute.
                        var usable = TimeSpan.FromSeconds(Math.Max(60, life > 172800 ? life - 86400.0 : life * 0.9));
                        var state = new TokenState(token, _now() + usable, used);
                        if (used == _creds) _state = state;   // settings changed while this mint was in flight: the token is for a pair no longer in force
                        _breaker.Success();
                        return state;
                    }
                    catch (JsonException)
                    {
                        _breaker.Trip(false, "an unreadable answer from Twitch");
                        throw Fail.Pause("Twitch answered with something that is not a token.", null, "twitch");
                    }
                }
                if (code is 400 or 403)
                    throw Latch(used, "Twitch did not accept the client id and secret (" + code + "). Create a new secret at dev.twitch.tv/console, enter it in this plugin's settings and save, then reset the Authentication Failed items on the Enrichment page.");
                if (code == 429)
                {
                    _breaker.Trip(true, "HTTP 429 from Twitch", RetryAfter(resp));
                    throw Fail.Pause("Twitch is rate limiting token requests (HTTP 429).", HttpStatusCode.TooManyRequests, "rate");
                }
                _breaker.Trip(false, $"HTTP {code} from Twitch");
                throw Fail.Pause($"Twitch answered HTTP {code} to the token request.", null, "twitch");   // not about any one record
            }
        }
        finally
        {
            _mintGate.Release();
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage r) =>
        r.Headers.RetryAfter?.Delta ?? (r.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);

    /// <summary>Warms the token in the background so the first interactive search does not pay for it. Never throws.</summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        try { await TokenAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Debug("IGDB token warm-up did not complete: {Reason}", Redact(ex.Message)); }
        catch (OperationCanceledException) { }
    }

    // ── queries ───────────────────────────────────────────────────────────────

    /// <summary>POSTs an Apicalypse body to an endpoint and returns the answer; 400 is returned (not thrown), everything else that is not 200 is thrown in its mapped form.</summary>
    public async Task<ApiReply> PostAsync(string endpoint, string body, CancellationToken ct)
    {
        ThrowIfCannotAuthenticate();
        _breaker.ThrowIfOpen();

        for (var attempt = 0; ; attempt++)
        {
            var state = await TokenAsync(ct).ConfigureAwait(false);
            using var lease = await _limiter.AcquireAsync(QueueLimit, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _requests);

            using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            req.Headers.TryAddWithoutValidation("Client-ID", state.For.Id);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.Token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_requestTimeout);
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _breaker.Trip(false, "a timeout reaching IGDB");
                throw Fail.Pause($"IGDB did not answer within {_requestTimeout.TotalSeconds:0} seconds.", null, "transport");
            }
            catch (HttpRequestException ex)
            {
                _breaker.Trip(false, "a network error reaching IGDB");
                throw Fail.Pause($"Could not reach IGDB: {Redact(ex.Message)}", null, "transport", ex);
            }

            using (resp)
            {
                var code = (int)resp.StatusCode;
                string text;
                try { text = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)); }   // bytes, not ReadAsString: a bad charset header must not throw
                catch (HttpRequestException ex)
                {
                    _breaker.Trip(false, "a network error reading IGDB's answer");
                    throw Fail.Pause($"Could not read IGDB's answer: {Redact(ex.Message)}", null, "transport", ex);
                }

                switch (code)
                {
                    case 200:
                        try
                        {
                            var doc = JsonDocument.Parse(text);
                            _breaker.Success();
                            Interlocked.Increment(ref _successes);
                            return new ApiReply(200, doc, null);
                        }
                        catch (JsonException)
                        {
                            _breaker.Trip(false, "an unreadable answer from IGDB");
                            throw Fail.Pause("IGDB answered with something that is not JSON.", null, "shape");
                        }
                    case 400:
                        return new ApiReply(400, null, text);
                    case 401:
                        if (attempt == 0)
                        {
                            // one fresh token and one more try; only the token that was rejected is discarded (another caller may have minted a new one)
                            if (_state is { } cur && cur.Token == state.Token) _state = null;
                            continue;
                        }
                        throw Latch(state.For, "IGDB did not accept the access token Twitch issued. Check that the client id belongs to the secret.");
                    case 403:
                        throw Latch(state.For, "IGDB refused this client id (HTTP 403).");
                    case 429:
                        _breaker.Trip(true, "HTTP 429 from IGDB", RetryAfter(resp));
                        throw Fail.Pause("IGDB is rate limiting this client (HTTP 429).", HttpStatusCode.TooManyRequests, "rate");
                    default:
                        _breaker.Trip(false, $"HTTP {code} from IGDB");
                        throw Fail.Pause($"IGDB answered HTTP {code}.", null, code >= 500 || code == 404 ? "server" : "badrequest");
                }
            }
        }
    }

    /// <summary>The longest a request may queue behind the limiter before it is refused as rate limited (the caller's own deadline also cancels the wait).</summary>
    private static readonly TimeSpan QueueLimit = TimeSpan.FromSeconds(10);

    /// <summary>A query that must succeed: returns the root array (copied, so the document can be disposed). A 400 is thrown as a pause.</summary>
    public async Task<JsonElement> QueryAsync(string endpoint, string body, CancellationToken ct)
    {
        using var reply = await PostAsync(endpoint, body, ct).ConfigureAwait(false);
        if (reply.Status == 400)
            throw Fail.Pause($"IGDB rejected a query ({Redact(Trim(reply.ErrorText))}).", null, "badrequest");
        var root = reply.Doc!.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw Fail.Pause("IGDB answered with a shape this plugin does not know (not a list).", null, "shape");
        return root.Clone();
    }

    internal static string Trim(string? s) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length > 300 ? s[..300] : s).ReplaceLineEndings(" ");

    public void Dispose()
    {
        _mintGate.Dispose();
    }
}
