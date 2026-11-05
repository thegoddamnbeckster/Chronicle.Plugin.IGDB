using System.Net;
using Chronicle.Plugins;

namespace Chronicle.Plugin.IGDB;

/// <summary>
/// How a failure is told to Chronicle. WHAT THE PLUGIN THROWS DECIDES WHAT HAPPENS TO THE ITEM (section 9 of the design):
///   HttpRequestException 429, or with NO status code  -> the item stays Pending, no retry is counted, the pass for this plugin stops
///   PluginAuthException                               -> AuthFailed (the pass goes on)
///   any other exception                               -> a retry is counted; three of them and the item is Exhausted for good
/// So an outage, a timeout, a 5xx, a rate limit or an unreadable answer is thrown as the first kind, and only a failure that is
/// really about one item ends as the last.
/// </summary>
internal static class Fail
{
    public const string KindKey = "igdb.kind";

    /// <summary>A failure that is not the item's fault: a pause. <paramref name="status"/> is 429 for a rate limit and null for everything else.</summary>
    public static HttpRequestException Pause(string message, HttpStatusCode? status, string kind, Exception? inner = null)
    {
        var ex = new HttpRequestException(message, inner, status);
        ex.Data[KindKey] = kind;
        return ex;
    }

    public static PluginAuthException Auth(string message, Exception? inner = null) =>
        inner is null ? new PluginAuthException(IgdbClient.PluginId, message) : new PluginAuthException(IgdbClient.PluginId, message, inner);

    /// <summary>"server" and "shape" failures can be about one record, so repeated ones are counted per item; the rest never are.</summary>
    public static bool IsPerItemKind(Exception ex) =>
        ex is HttpRequestException h && h.Data[KindKey] is "server" or "shape" or "badrequest";
}

/// <summary>
/// One limiter for every caller of one plugin instance: a minimum interval between request starts and a ceiling on requests in
/// flight. A caller waits only as long as its deadline allows; if the wait would pass the deadline it is refused with the
/// rate-limit form (a pause), never with a timeout (which would count against the item).
/// </summary>
internal sealed class Limiter
{
    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private TimeSpan _interval;
    private SemaphoreSlim _concurrency;

    public Limiter(double perSecond, int maxConcurrent, Func<DateTimeOffset> now, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _now = now;
        _delay = delay;
        _interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(perSecond, 0.1, 100));
        _concurrency = new SemaphoreSlim(Math.Clamp(maxConcurrent, 1, 64));
    }

    public void Configure(double perSecond, int maxConcurrent)
    {
        lock (_lock)
        {
            _interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(perSecond, 0.1, 100));
            var max = Math.Clamp(maxConcurrent, 1, 64);
            if (max != _concurrencyMax) { _concurrency = new SemaphoreSlim(max); _concurrencyMax = max; }
        }
    }

    private int _concurrencyMax = -1;

    /// <summary>Waits for a slot; returns a lease to dispose when the request has finished.</summary>
    public async Task<IDisposable> AcquireAsync(TimeSpan maxWait, CancellationToken ct)
    {
        SemaphoreSlim gate;
        TimeSpan wait;
        lock (_lock)
        {
            gate = _concurrency;
            var now = _now();
            var slot = _next > now ? _next : now;
            wait = slot - now;
            if (wait > maxWait)
                throw Fail.Pause("IGDB pacing queue is full: this request would wait longer than the call may take.", HttpStatusCode.TooManyRequests, "rate");
            _next = slot + _interval;
        }
        if (wait > TimeSpan.Zero) await _delay(wait, ct).ConfigureAwait(false);
        if (!await gate.WaitAsync(maxWait > TimeSpan.Zero ? maxWait : TimeSpan.FromMilliseconds(1), ct).ConfigureAwait(false))
            throw Fail.Pause("IGDB pacing queue is full: too many requests are open.", HttpStatusCode.TooManyRequests, "rate");
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }
}

/// <summary>
/// The cool-down after a REAL failure. Until it ends every call fails at once, without touching the network, with the same
/// exception, so the host's two retries cost two instant failures instead of two more requests into a struggling service. A call
/// that fails fast does not extend or escalate it; the first success resets it; authentication failures do not open it (the
/// credentials latch handles those). In memory only: a reload is a deliberate fresh start.
/// </summary>
internal sealed class Breaker(Func<DateTimeOffset> now, Func<double> random)
{
    private readonly object _lock = new();
    private DateTimeOffset _until = DateTimeOffset.MinValue;
    private HttpStatusCode? _status;
    private string _kind = "server";
    private string _message = string.Empty;
    private int _rateFailures, _serverFailures;

    // Technical limits of the plugin's own machinery. Measured 2026-11-05: IGDB answered 200 again 0.6 s after a burst that drew
    // 429s, so a rate limit starts at 2 s (also the host guard's own first retry delay); a server fault starts at 15 s.
    public static readonly TimeSpan RateStart = TimeSpan.FromSeconds(2), RateMax = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ServerStart = TimeSpan.FromSeconds(15), ServerMax = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RetryAfterMax = TimeSpan.FromMinutes(5);

    public bool IsOpen => Remaining > TimeSpan.Zero;

    public TimeSpan Remaining
    {
        get { lock (_lock) { var r = _until - now(); return r > TimeSpan.Zero ? r : TimeSpan.Zero; } }
    }

    public string Describe()
    {
        lock (_lock)
        {
            var r = _until - now();
            return r > TimeSpan.Zero ? $"cooling down for {FormatSpan(r)} after {_message}" : "ready";
        }
    }

    private static string FormatSpan(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))}s";

    /// <summary>Throws the failure that opened the cool-down, if one is open.</summary>
    public void ThrowIfOpen()
    {
        HttpStatusCode? status;
        string kind, message;
        TimeSpan remaining;
        lock (_lock)
        {
            remaining = _until - now();
            if (remaining <= TimeSpan.Zero) return;
            status = _status; kind = _kind == "server" ? "cooldown" : _kind; message = _message;   // a fail-fast call is not a new failure of any item
        }
        throw Fail.Pause($"IGDB is not being asked again yet: cooling down for {FormatSpan(remaining)} after {message}.", status, kind);
    }

    public void Trip(bool rateLimit, string message, TimeSpan? retryAfter = null)
    {
        lock (_lock)
        {
            TimeSpan baseLen;
            if (rateLimit)
            {
                _rateFailures++;
                baseLen = Scale(RateStart, _rateFailures, RateMax);
                _status = HttpStatusCode.TooManyRequests; _kind = "rate";
            }
            else
            {
                _serverFailures++;
                baseLen = Scale(ServerStart, _serverFailures, ServerMax);
                _status = null; _kind = "server";
            }
            if (retryAfter is { } ra && ra > TimeSpan.Zero) baseLen = ra > RetryAfterMax ? RetryAfterMax : ra;
            var jitter = 1.0 + (random() - 0.5) * 0.4;   // plus or minus 20%
            _until = now() + TimeSpan.FromSeconds(Math.Max(0.5, baseLen.TotalSeconds * jitter));
            _message = message;
        }
    }

    private static TimeSpan Scale(TimeSpan start, int failures, TimeSpan max)
    {
        var s = start.TotalSeconds * Math.Pow(2, Math.Clamp(failures - 1, 0, 20));
        return TimeSpan.FromSeconds(Math.Min(s, max.TotalSeconds));
    }

    public void Success()
    {
        lock (_lock) { _rateFailures = 0; _serverFailures = 0; _until = DateTimeOffset.MinValue; }
    }
}
