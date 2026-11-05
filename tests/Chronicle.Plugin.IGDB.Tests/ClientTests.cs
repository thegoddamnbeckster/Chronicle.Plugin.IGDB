using System.Net;
using System.Text.Json;
using Chronicle.Plugin.IGDB;
using Chronicle.Plugins;

namespace Chronicle.Plugin.IGDB.Tests;

public class ClientTests
{
    private static FakeIgdb Ok() => new FakeIgdb().Token().OnApiAny("games", "[]");

    private static Task<JsonElement> Q(IgdbClient c, CancellationToken ct = default) => c.QueryAsync("games", "fields id;", ct);

    // ── the token ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_token_is_minted_with_the_secret_in_a_form_body_never_in_a_url_and_sent_as_a_header()
    {
        var fake = Ok();
        var (client, _, _) = Harness.Client(fake);
        await Q(client);

        var mint = Assert.Single(fake.Tokens);
        Assert.Equal("POST", mint.Method);
        Assert.Equal("https://id.twitch.tv/oauth2/token", mint.Url);
        Assert.Contains("grant_type=client_credentials", mint.Body);
        Assert.Contains("client_id=" + Harness.Id, mint.Body);
        Assert.Contains("client_secret=" + Harness.Secret, mint.Body);
        var api = Assert.Single(fake.Api);
        Assert.Equal("Bearer " + Harness.TokenValue, api.Authorization);
        Assert.Equal(Harness.Id, api.ClientId);
        Assert.DoesNotContain(Harness.Secret, api.Url + api.Body);
        Assert.Equal("https://api.igdb.com/v4/games", api.Url);
    }

    [Fact]
    public async Task The_token_is_reused_and_minted_again_only_near_its_end()
    {
        var fake = Ok();
        var (client, _, clock) = Harness.Client(fake);
        for (var i = 0; i < 5; i++) await Q(client);
        Assert.Single(fake.Tokens);

        clock.Advance(TimeSpan.FromDays(40));
        await Q(client);
        Assert.Single(fake.Tokens);   // 62.5 days of life: still used at day 40

        clock.Advance(TimeSpan.FromDays(20));
        await Q(client);
        Assert.Single(fake.Tokens);   // day 60 of 62.5: still used (it is dropped 24 hours before it expires, at day 61.5)

        clock.Advance(TimeSpan.FromDays(3));
        await Q(client);
        Assert.Equal(2, fake.Tokens.Count());
    }

    [Fact]
    public async Task Twenty_callers_at_once_cause_one_mint()
    {
        var fake = Ok();
        var (client, _, _) = Harness.Client(fake);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Q(client)));
        Assert.Single(fake.Tokens);
        Assert.Equal(20, fake.Api.Count());
    }

    [Fact]
    public async Task A_401_gets_one_fresh_token_and_one_more_try()
    {
        var calls = 0;
        var fake = new FakeIgdb().Token()
            .OnApi("games", _ => true, () => ++calls == 1 ? new FakeIgdb.Reply(HttpStatusCode.Unauthorized, "{}") : new FakeIgdb.Reply(HttpStatusCode.OK, "[]"));
        var (client, _, _) = Harness.Client(fake);
        await Q(client);
        Assert.Equal(2, fake.Tokens.Count());
        Assert.Equal(2, fake.Api.Count());
        Assert.False(client.CredentialsRejected);
    }

    [Fact]
    public async Task A_second_401_is_an_authentication_failure_and_latches_the_credentials()
    {
        var fake = new FakeIgdb().Token().OnApiAny("games", "{}", HttpStatusCode.Unauthorized);
        var (client, _, _) = Harness.Client(fake);
        await Assert.ThrowsAsync<PluginAuthException>(() => Q(client));
        Assert.True(client.CredentialsRejected);
        var before = fake.All.Count;
        // every later call is refused at once, with no request at all
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<PluginAuthException>(() => Q(client));
        Assert.Equal(before, fake.All.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Twitch_refusing_the_credentials_is_an_authentication_failure_with_no_api_request(HttpStatusCode status)
    {
        var fake = new FakeIgdb().On(r => r.Url.Contains("oauth2/token"), _ => new FakeIgdb.Reply(status, "{\"status\":403,\"message\":\"invalid client secret\"}"));
        var (client, _, _) = Harness.Client(fake);
        var ex = await Assert.ThrowsAsync<PluginAuthException>(() => Q(client));
        Assert.Equal(IgdbClient.PluginId, ex.PluginId);
        Assert.Contains("dev.twitch.tv", ex.Message);
        Assert.Empty(fake.Api);
        Assert.True(client.CredentialsRejected);
        await Assert.ThrowsAsync<PluginAuthException>(() => Q(client));
        Assert.Single(fake.Tokens);   // the second call never reached Twitch
    }

    [Fact]
    public async Task New_credentials_clear_the_latch_and_the_token()
    {
        var fake = new FakeIgdb().On(r => r.Url.Contains("oauth2/token") && r.Body.Contains("client_secret=OLD"), _ => new FakeIgdb.Reply(HttpStatusCode.Forbidden, "{}"))
            .Token().OnApiAny("games", "[]");
        var (client, _, _) = Harness.Client(fake, Harness.Cfg(s => s with { ClientSecret = "OLD-SECRET-VALUE" }));
        await Assert.ThrowsAsync<PluginAuthException>(() => Q(client));
        Assert.True(client.CredentialsRejected);

        client.Configure(Harness.Cfg());   // the person saved a new secret
        Assert.False(client.CredentialsRejected);
        await Q(client);
        Assert.Equal(2, fake.Tokens.Count());
    }

    [Fact]
    public async Task The_same_credentials_keep_the_token_when_settings_are_saved_again()
    {
        var fake = Ok();
        var (client, _, _) = Harness.Client(fake);
        await Q(client);
        client.Configure(Harness.Cfg(s => s with { SearchLimit = 20 }));
        await Q(client);
        Assert.Single(fake.Tokens);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("id", "")]
    [InlineData("", "secret")]
    public async Task Missing_credentials_are_an_authentication_failure_with_no_request(string id, string secret)
    {
        var fake = Ok();
        var (client, _, _) = Harness.Client(fake, Harness.Cfg(s => s with { ClientId = id, ClientSecret = secret }));
        var ex = await Assert.ThrowsAsync<PluginAuthException>(() => Q(client));
        Assert.Contains("client id and client secret", ex.Message);
        Assert.Empty(fake.All);
    }

    // ── failures and what they are told to Chronicle ──────────────────────────

    [Fact]
    public async Task A_429_is_thrown_as_429_not_waited_out_and_opens_a_cool_down()
    {
        var fake = new FakeIgdb().Token().OnApiAny("games", "{\"message\":\"Too Many Requests\"}", HttpStatusCode.TooManyRequests);
        var (client, _, clock) = Harness.Client(fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.True(client.CoolingDown);
        Assert.Equal(TimeSpan.FromSeconds(2), client.CoolDownRemaining);   // measured: IGDB answers again within a second
        Assert.Empty(clock.Delays.Where(d => d > TimeSpan.FromSeconds(1)));  // nothing was slept out
    }

    [Fact]
    public async Task During_the_cool_down_every_call_fails_at_once_with_no_request_and_the_cool_down_does_not_grow()
    {
        var fake = new FakeIgdb().Token().OnApiAny("games", "{}", HttpStatusCode.TooManyRequests);
        var (client, _, clock) = Harness.Client(fake);
        await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
        var requests = fake.All.Count;
        for (var i = 0; i < 10; i++)
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
            Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
            Assert.Equal("rate", ex.Data[Fail.KindKey]);
        }
        Assert.Equal(requests, fake.All.Count);
        Assert.True(client.CoolDownRemaining <= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task The_cool_down_doubles_on_repeated_real_failures_to_a_ceiling_and_resets_on_success()
    {
        var fail = true;
        var fake = new FakeIgdb().Token().OnApi("games", _ => true, () => fail ? new FakeIgdb.Reply(HttpStatusCode.TooManyRequests, "{}") : new FakeIgdb.Reply(HttpStatusCode.OK, "[]"));
        var (client, _, clock) = Harness.Client(fake);
        var seen = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
            seen.Add(Math.Round(client.CoolDownRemaining.TotalSeconds));
            clock.Advance(client.CoolDownRemaining + TimeSpan.FromMilliseconds(10));
        }
        Assert.Equal([2d, 4, 8, 16, 32, 64, 128, 256, 300, 300, 300, 300], seen);

        fail = false;
        await Q(client);
        Assert.False(client.CoolingDown);
        fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
        Assert.Equal(2, Math.Round(client.CoolDownRemaining.TotalSeconds));   // the sequence started again
    }

    [Fact]
    public async Task The_cool_down_has_plus_or_minus_twenty_percent_jitter()
    {
        var lows = new List<double>();
        foreach (var r in new[] { 0.0, 1.0 })
        {
            var fake = new FakeIgdb().Token().OnApiAny("games", "{}", HttpStatusCode.TooManyRequests);
            var (client, _, _) = Harness.Client(fake, random: r);
            await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
            lows.Add(client.CoolDownRemaining.TotalSeconds);
        }
        Assert.Equal(1.6, lows[0], 2);
        Assert.Equal(2.4, lows[1], 2);
    }

    [Fact]
    public async Task A_cool_down_ends_and_the_next_call_goes_through()
    {
        var fail = true;
        var fake = new FakeIgdb().Token().OnApi("games", _ => true, () => fail ? new FakeIgdb.Reply(HttpStatusCode.TooManyRequests, "{}") : new FakeIgdb.Reply(HttpStatusCode.OK, "[]"));
        var (client, _, clock) = Harness.Client(fake);
        await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
        fail = false;
        clock.Advance(TimeSpan.FromSeconds(3));
        await Q(client);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_server_fault_is_a_pause_with_no_status_so_it_never_counts_against_an_item(HttpStatusCode status)
    {
        var fake = new FakeIgdb().Token().OnApiAny("games", "<html>oops</html>", status);
        var (client, _, _) = Harness.Client(fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
        Assert.Null(ex.StatusCode);
        Assert.Equal("server", ex.Data[Fail.KindKey]);
        Assert.Equal(TimeSpan.FromSeconds(15), client.CoolDownRemaining);
    }

    [Fact]
    public async Task An_answer_that_is_not_json_is_a_pause_not_a_not_found()
    {
        var fake = new FakeIgdb().Token().OnApiAny("games", "<html>gateway</html>");
        var (client, _, _) = Harness.Client(fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
        Assert.Null(ex.StatusCode);
        Assert.Equal("shape", ex.Data[Fail.KindKey]);
    }

    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public async Task An_answer_that_is_not_a_list_is_a_pause(string json)
    {
        var fake = new FakeIgdb().Token().OnApiAny("games", json);
        var (client, _, _) = Harness.Client(fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
        Assert.Null(ex.StatusCode);
        Assert.Equal("shape", ex.Data[Fail.KindKey]);
    }

    [Fact]
    public async Task A_network_error_is_a_pause_and_never_shows_the_secret_or_the_token()
    {
        var fake = new FakeIgdb().Token()
            .OnApi("games", _ => true, () => throw new HttpRequestException($"failed with {Harness.Secret} and {Harness.TokenValue} and {Harness.Id}"));
        var (client, sink, _) = Harness.Client(fake);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
        Assert.Null(ex.StatusCode);
        Assert.Equal("transport", ex.Data[Fail.KindKey]);
        foreach (var secret in new[] { Harness.Secret, Harness.TokenValue, Harness.Id })
        {
            Assert.DoesNotContain(secret, ex.Message);
            Assert.DoesNotContain(secret, sink.All);
        }
    }

    [Fact]
    public async Task A_request_that_never_answers_ends_at_the_request_timeout_as_a_pause()
    {
        var fake = new FakeIgdb { HangApi = true }.Token();
        var (client, _, _) = Harness.Client(fake, Harness.Cfg(s => s with { RequestTimeout = TimeSpan.FromMilliseconds(150) }));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());
        Assert.Null(ex.StatusCode);
        Assert.Equal("transport", ex.Data[Fail.KindKey]);
    }

    [Fact]
    public async Task A_cancelled_caller_is_cancelled_not_paused()
    {
        var fake = new FakeIgdb { HangApi = true }.Token();
        var (client, _, _) = Harness.Client(fake);
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Q(client, cts.Token));
        Assert.False(client.CoolingDown);
    }

    [Fact]
    public async Task A_400_is_returned_for_the_caller_to_read_and_never_opens_a_cool_down()
    {
        var body = """[{"title":"Invalid Field","status":400,"cause":"Invalid field name: 'x'"}]""";
        var fake = new FakeIgdb().Token().OnApiAny("games", body, HttpStatusCode.BadRequest);
        var (client, _, _) = Harness.Client(fake);
        using var reply = await client.PostAsync("games", "fields x;", CancellationToken.None);
        Assert.Equal(400, reply.Status);
        Assert.Equal("x", Queries.FieldFromError(reply.ErrorText));
        Assert.False(client.CoolingDown);
        // QueryAsync (which needs an answer) turns the same 400 into a pause
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Q(client));
        Assert.Null(ex.StatusCode);
        Assert.Equal("badrequest", ex.Data[Fail.KindKey]);
    }

    [Fact]
    public async Task The_secret_and_the_token_never_reach_a_log_or_a_message_in_any_scenario()
    {
        var scenarios = new Func<FakeIgdb>[]
        {
            () => Ok(),
            () => new FakeIgdb().Token().OnApiAny("games", "{}", HttpStatusCode.TooManyRequests),
            () => new FakeIgdb().Token().OnApiAny("games", "{}", HttpStatusCode.InternalServerError),
            () => new FakeIgdb().Token().OnApiAny("games", "{}", HttpStatusCode.Unauthorized),
            () => new FakeIgdb().Token().OnApiAny("games", "not json"),
            () => new FakeIgdb().On(r => r.Url.Contains("oauth2"), _ => new FakeIgdb.Reply(HttpStatusCode.Forbidden, "{\"message\":\"invalid client secret " + Harness.Secret + "\"}")),
        };
        foreach (var make in scenarios)
        {
            var (client, sink, _) = Harness.Client(make());
            string message = "";
            try { await Q(client); } catch (Exception ex) { message = ex.Message + ex.InnerException?.Message; }
            foreach (var secret in new[] { Harness.Secret, Harness.TokenValue })
            {
                Assert.DoesNotContain(secret, message);
                Assert.DoesNotContain(secret, sink.All);
            }
        }
    }

    [Fact]
    public void Redact_removes_the_secret_the_token_and_the_client_id()
    {
        var (client, _, _) = Harness.Client(Ok());
        var text = $"x {Harness.Secret} y {Harness.Id} z";
        var red = client.Redact(text);
        Assert.DoesNotContain(Harness.Secret, red);
        Assert.DoesNotContain(Harness.Id, red);
        Assert.Equal(string.Empty, client.Redact(null));
    }

    // ── pacing ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Requests_are_paced_by_one_limiter()
    {
        var fake = Ok();
        var (client, _, clock) = Harness.Client(fake, Harness.Cfg(s => s with { RequestsPerSecond = 4 }));
        for (var i = 0; i < 5; i++) await Q(client);
        var waits = clock.Delays.Where(d => d > TimeSpan.Zero).ToList();
        Assert.Equal(4, waits.Count);
        Assert.All(waits, w => Assert.Equal(250, w.TotalMilliseconds, 1));
    }

    [Fact]
    public async Task The_limiter_is_shared_by_concurrent_callers_and_caps_requests_in_flight()
    {
        var fake = Ok();
        fake.Latency = TimeSpan.FromMilliseconds(25);
        var (client, _, _) = Harness.Client(fake, Harness.Cfg(s => s with { MaxConcurrentRequests = 2, RequestsPerSecond = 4 }));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Q(client)));
        Assert.True(fake.MaxInFlight <= 2, $"max in flight was {fake.MaxInFlight}");
        Assert.Equal(12, fake.Api.Count());
    }

    [Fact]
    public async Task A_queue_that_would_take_too_long_is_refused_as_a_rate_limit_not_a_timeout()
    {
        var fake = Ok();
        var (client, _, clock) = Harness.Client(fake, Harness.Cfg(s => s with { RequestsPerSecond = 1 }));
        clock.AdvanceOnDelay = false;   // time stands still while twenty callers queue
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            try { await Q(client); return (HttpRequestException?)null; }
            catch (HttpRequestException ex) { return ex; }
        }));
        var refused = results.Where(r => r is not null).ToList();
        Assert.NotEmpty(refused);
        Assert.All(refused, r => { Assert.Equal(HttpStatusCode.TooManyRequests, r!.StatusCode); Assert.Equal("rate", r.Data[Fail.KindKey]); });
        Assert.InRange(fake.Api.Count(), 8, 12);   // about ten seconds of queue at one a second
        Assert.False(client.CoolingDown);   // a full local queue is not IGDB's fault
    }

    [Fact]
    public async Task Warming_the_token_never_throws()
    {
        var fake = new FakeIgdb().On(r => r.Url.Contains("oauth2"), _ => new FakeIgdb.Reply(HttpStatusCode.Forbidden, "{}"));
        var (client, _, _) = Harness.Client(fake);
        await client.WarmAsync(CancellationToken.None);
        var good = Ok();
        var (client2, _, _) = Harness.Client(good);
        await client2.WarmAsync(CancellationToken.None);
        Assert.Single(good.Tokens);
        Assert.Empty(good.Api);
    }
}
