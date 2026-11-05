using System.Net;
using System.Text;
using System.Text.Json;
using Chronicle.Plugin.IGDB;
using Serilog.Core;
using Serilog.Events;

namespace Chronicle.Plugin.IGDB.Tests;

/// <summary>A fake IGDB and Twitch: answers by URL and body, records every request, and fails the test on one nobody expected.</summary>
internal sealed class FakeIgdb : HttpMessageHandler
{
    public sealed record Reply(HttpStatusCode Status, string Body, Dictionary<string, string>? Headers = null);
    public sealed record Req(string Method, string Url, string Body, string? Authorization, string? ClientId);

    private readonly List<(Func<Req, bool> When, Func<Req, Reply> Answer)> _routes = [];
    public List<Req> All { get; } = [];
    public IEnumerable<Req> Api => All.Where(r => r.Url.StartsWith("https://api.igdb.com/", StringComparison.Ordinal));
    public IEnumerable<Req> Tokens => All.Where(r => r.Url.StartsWith("https://id.twitch.tv/", StringComparison.Ordinal));
    public int InFlight, MaxInFlight;
    public TimeSpan Latency = TimeSpan.FromMilliseconds(2);
    /// <summary>When true an IGDB API request (not the token) never answers: for deadline tests.</summary>
    public bool HangApi;

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), Encoding.UTF8);

    public static JsonElement FixtureJson(string name) => JsonDocument.Parse(Fixture(name)).RootElement.Clone();

    public FakeIgdb Token(string token = "token-AAAAAAAAAAAA", long expires = 5_400_000) =>
        On(r => r.Url.StartsWith("https://id.twitch.tv/oauth2/token", StringComparison.Ordinal),
           _ => new Reply(HttpStatusCode.OK, $"{{\"access_token\":\"{token}\",\"expires_in\":{expires},\"token_type\":\"bearer\"}}"));

    public FakeIgdb On(Func<Req, bool> when, Func<Req, Reply> answer) { _routes.Add((when, answer)); return this; }

    public FakeIgdb OnApi(string endpoint, Func<string, bool> body, Func<Reply> answer) =>
        On(r => r.Url == "https://api.igdb.com/v4/" + endpoint && body(r.Body), _ => answer());

    public FakeIgdb OnApi(string endpoint, string bodyContains, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        OnApi(endpoint, b => b.Contains(bodyContains, StringComparison.Ordinal), () => new Reply(status, json));

    public FakeIgdb OnApiAny(string endpoint, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        OnApi(endpoint, _ => true, () => new Reply(status, json));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var cur = Interlocked.Increment(ref InFlight);
        MaxInFlight = Math.Max(MaxInFlight, cur);
        try
        {
            if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct);
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var req = new Req(request.Method.Method, request.RequestUri!.ToString(), body,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Client-ID", out var ids) ? ids.FirstOrDefault() : null);
            lock (All) All.Add(req);
            if (HangApi && req.Url.StartsWith("https://api.igdb.com/", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, ct);

            foreach (var (when, answer) in _routes)
            {
                if (!when(req)) continue;
                var reply = answer(req);
                var resp = new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
                if (reply.Headers is not null)
                    foreach (var (k, v) in reply.Headers) resp.Headers.TryAddWithoutValidation(k, v);
                return resp;
            }
            throw new InvalidOperationException($"Unexpected request: {req.Method} {req.Url}\n{req.Body}");
        }
        finally { Interlocked.Decrement(ref InFlight); }
    }
}

/// <summary>Never answers (until cancelled): for timeout tests.</summary>
internal sealed class HangingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException();
    }
}

/// <summary>Records every log line, so a test can prove a secret never reached a log.</summary>
internal sealed class CaptureSink : ILogEventSink
{
    public List<string> Lines { get; } = [];
    public void Emit(LogEvent e) { lock (Lines) Lines.Add(e.RenderMessage() + (e.Exception is null ? "" : " " + e.Exception)); }
    public string All { get { lock (Lines) return string.Join("\n", Lines); } }
}

/// <summary>A clock and delay the tests control, so no test waits in real time.</summary>
internal sealed class FakeClock
{
    private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Now => _now;
    public readonly List<TimeSpan> Delays = [];
    public bool AdvanceOnDelay = true;
    public void Advance(TimeSpan t) => _now += t;
    public Task Delay(TimeSpan t, CancellationToken ct)
    {
        lock (Delays) Delays.Add(t);
        if (AdvanceOnDelay) _now += t;   // false lets a test make time stand still, as real time does while callers queue
        return Task.CompletedTask;
    }
}

internal static class Harness
{
    public const string Id = "client-id-1234567890";
    public const string Secret = "SECRET-VALUE-abcdef0123456789";
    public const string TokenValue = "token-AAAAAAAAAAAA";

    public static Settings Cfg(Func<Settings, Settings>? tweak = null)
    {
        var s = Settings.From(new Dictionary<string, string>
        {
            [Settings.KeyClientId] = Id,
            [Settings.KeyClientSecret] = Secret,
        }) with { RequestTimeout = TimeSpan.FromSeconds(5), CallDeadline = TimeSpan.FromSeconds(10), RequestsPerSecond = 4 };
        return tweak?.Invoke(s) ?? s;
    }

    public static (IgdbClient Client, CaptureSink Sink, FakeClock Clock) Client(HttpMessageHandler handler, Settings? settings = null, double random = 0.5)
    {
        var sink = new CaptureSink();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var clock = new FakeClock();
        var client = new IgdbClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, log, () => clock.Now, clock.Delay, () => random);
        client.Configure(settings ?? Cfg());
        return (client, sink, clock);
    }

    public static (IgdbMetadataProvider Provider, IgdbClient Client, CaptureSink Sink, FakeClock Clock) Provider(
        HttpMessageHandler handler, Settings? settings = null)
    {
        var s = settings ?? Cfg();
        var (client, sink, clock) = Client(handler, s);
        return (new IgdbMetadataProvider(client, s), client, sink, clock);
    }

    // ── canned IGDB answers built from the recorded fixtures ──────────────────

    /// <summary>Fallout: New Vegas with every expansion, as a full-fetch multiquery answer (game, time to beat, 36 characters).</summary>
    public static string FullAnswer(long id = 16)
    {
        var game = FakeIgdb.FixtureJson("P13_expanded_game.json");
        var mq = FakeIgdb.FixtureJson("P13_multiquery.json").GetProperty("result");
        var ttb = mq.EnumerateArray().First(p => p.GetProperty("name").GetString() == "ttb").GetProperty("result");
        var chars = mq.EnumerateArray().First(p => p.GetProperty("name").GetString() == "characters").GetProperty("result");
        return $"[{{\"name\":\"game\",\"result\":[{game.GetRawText()}]}},{{\"name\":\"ttb\",\"result\":{ttb.GetRawText()}}},{{\"name\":\"characters\",\"result\":{chars.GetRawText()}}}]";
    }

    /// <summary>The same game as a lean search result (the full record carries every lean field).</summary>
    public static string LeanFallout() => "[" + FakeIgdb.FixtureJson("P13_expanded_game.json").GetRawText() + "]";

    public static Chronicle.Plugins.Models.MediaSearchContext Ctx(string name, int? year = null, bool enrichment = true,
        IReadOnlyDictionary<string, string>? known = null, string? type = "game", IReadOnlyList<string>? alt = null) =>
        new(name, Year: year, MediaTypeName: type, KnownExternalIds: known, IsRealHierarchyPosition: enrichment, AltTitles: alt);
}
