using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Plugin.IGDB;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.IGDB.Tests;

public class ManifestTests
{
    private static JsonElement Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json"))).RootElement.Clone();

    [Fact]
    public void The_manifest_matches_the_code()
    {
        var m = Manifest();
        using var p = new IgdbMetadataProvider();
        Assert.Equal(p.PluginId, m.GetProperty("plugin_id").GetString());
        Assert.Equal("chronicle.plugin.igdb", p.PluginId);
        Assert.Equal(p.Name, m.GetProperty("name").GetString());
        Assert.Equal(p.Version, m.GetProperty("version").GetString());
        Assert.Equal(p.Author, m.GetProperty("author").GetString());
        var entry = typeof(IgdbMetadataProvider).FullName;
        Assert.Equal(entry, m.GetProperty("entry_type").GetString());
        Assert.NotNull(typeof(IgdbMetadataProvider).Assembly.GetType(m.GetProperty("entry_type").GetString()!));
        Assert.Equal("Chronicle.Plugin.IGDB", typeof(IgdbMetadataProvider).Assembly.GetName().Name);
        Assert.Equal("1.0.0", typeof(IgdbMetadataProvider).Assembly.GetName().Version!.ToString(3));
    }

    [Fact]
    public void The_manifest_declares_the_game_type_and_the_two_tasks()
    {
        var m = Manifest();
        Assert.Equal(["game"], m.GetProperty("supported_media_types").EnumerateArray().Select(x => x.GetString()));
        Assert.StartsWith("0.", m.GetProperty("min_chronicle_version").GetString());
        Assert.True(Version.Parse(m.GetProperty("min_chronicle_version").GetString()!) >= new Version(0, 7, 0));
        Assert.StartsWith("https://", m.GetProperty("iconUrl").GetString());
        Assert.Matches("^#[0-9A-Fa-f]{6}$", m.GetProperty("brandColorLight").GetString());
        Assert.Matches("^#[0-9A-Fa-f]{6}$", m.GetProperty("brandColorDark").GetString());
        Assert.Contains("steam:", m.GetProperty("fixMatchHint").GetString());
        Assert.Equal(0, m.GetProperty("settings").GetArrayLength());   // the schema comes from the plugin

        var tasks = m.GetProperty("background_tasks").EnumerateArray().ToDictionary(t => t.GetProperty("task_id").GetString()!);
        Assert.Equal(["fetch-missing-metadata", "resync-all-metadata"], tasks.Keys.OrderBy(k => k));
        Assert.True(tasks["fetch-missing-metadata"].GetProperty("default_enabled").GetBoolean());
        Assert.False(tasks["resync-all-metadata"].GetProperty("default_enabled").GetBoolean());   // it aborts at the first pause and starts over
        foreach (var t in tasks.Values)
            Assert.Equal(5, t.GetProperty("default_cron").GetString()!.Split(' ').Length);
        // offset from every other plugin's slot (they share the database's single writer)
        Assert.Equal("45 5 * * *", tasks["fetch-missing-metadata"].GetProperty("default_cron").GetString());
        Assert.Equal("45 3 * * 4", tasks["resync-all-metadata"].GetProperty("default_cron").GetString());
    }

    [Fact]
    public void The_brand_colours_are_readable_on_a_light_and_a_dark_page()
    {
        var m = Manifest();
        static double Lum(string hex)
        {
            double Ch(int i) { var c = Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16) / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(0) + 0.7152 * Ch(1) + 0.0722 * Ch(2);
        }
        static double Contrast(string a, string b) { var (hi, lo) = (Math.Max(Lum(a), Lum(b)), Math.Min(Lum(a), Lum(b))); return (hi + 0.05) / (lo + 0.05); }
        Assert.True(Contrast(m.GetProperty("brandColorLight").GetString()!, "#FFFFFF") >= 4.5);
        Assert.True(Contrast(m.GetProperty("brandColorDark").GetString()!, "#111111") >= 4.5);
    }

    [Fact]
    public void The_settings_schema_has_every_key_the_plugin_reads_and_Configure_tolerates_nothing()
    {
        using var p = new IgdbMetadataProvider();
        var keys = p.GetSettingsSchema().Settings.Select(s => s.Key).ToList();
        var read = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.Name.StartsWith("Key")).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.NotEmpty(read);
        Assert.All(read, k => Assert.Contains(k, keys));
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Contains(p.GetSettingsSchema().Settings, s => s.Type == SettingType.Password && s.Key == Settings.KeyClientSecret);
        Assert.All(p.GetSettingsSchema().Settings.Where(s => s.Type == SettingType.Dropdown), s => Assert.NotEmpty(s.Options));

        p.Configure(new Dictionary<string, string>());   // does not throw, does no network work
        p.Configure(new Dictionary<string, string> { ["requests_per_second"] = "banana", ["call_deadline_seconds"] = "-4", ["poster_size"] = "nope", ["rating_source"] = "" });
        Assert.False(p.Current.HasCredentials);
        Assert.Equal(3, p.Current.RequestsPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(5), p.Current.CallDeadline);   // clamped to its range
        Assert.Equal("cover_big_2x", p.Current.PosterSize);
        Assert.Equal(RatingSource.Total, p.Current.RatingSource);
    }

    [Fact]
    public void Settings_ranges_are_enforced_and_defaults_hide_nothing()
    {
        var s = Settings.From(new Dictionary<string, string>
        {
            ["requests_per_second"] = "99", ["max_concurrent_requests"] = "0", ["request_timeout_seconds"] = "1", ["call_deadline_seconds"] = "99",
            ["search_limit"] = "500", ["minimum_rating_votes"] = "-1",
        });
        Assert.Equal(4, s.RequestsPerSecond);
        Assert.Equal(1, s.MaxConcurrentRequests);
        Assert.Equal(TimeSpan.FromSeconds(2), s.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(24), s.CallDeadline);
        Assert.Equal(50, s.SearchLimit);
        Assert.Equal(0, s.MinimumRatingVotes);
        Assert.Equal(0, Settings.Default.MinimumRatingVotes);   // no vote threshold by default
        Assert.True(Settings.Default.FetchTimeToBeat);
        Assert.False(Settings.Default.TimeToBeatAsRuntime);
        // an empty word list falls back to the default, never to "nothing is an edition word"
        Assert.NotEmpty(Settings.From(new Dictionary<string, string> { ["edition_words"] = "  \n " }).EditionWords);
    }

    [Fact]
    public void The_game_type_is_declared_completely()
    {
        using var p = new IgdbMetadataProvider();
        var t = Assert.Single(p.GetSupportedMediaTypes());
        Assert.Equal("game", t.MediaTypeName);
        Assert.Equal("Video Games", t.DisplayName);   // as the IMDb plugin declares it
        Assert.Equal(1, t.HierarchyLevels);
        Assert.Equal(["Game"], t.HierarchyLabels);
        Assert.Equal("played", t.InteractionVerb);
        Assert.Equal("percent", t.ProgressUnit);   // as the IMDb plugin declares it, so the merged type never depends on load order
        Assert.True(t.IsTrackable);
        Assert.False(t.EnrichOnly);
        Assert.Equal(10, t.DefaultPriority);
        Assert.Null(t.ProviderFamily);
        Assert.Equal("Developers", t.CastHeading);
        Assert.False(t.CastAreActors);
        Assert.Equal(["games", "videogame", "video_game"], t.Aliases);
        Assert.False(t.IsPersonType);
        Assert.Null(t.ImportShape);
        Assert.Null(t.ScanStrategy);
    }

    /// <summary>The host's canonical field names (MetadataResolutionService.FieldMap), read from its source when this repo sits beside it.</summary>
    private static IReadOnlyList<string>? HostFields()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Chronicle", "src", "Chronicle.Services", "MetadataResolutionService.cs"));
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        var start = text.IndexOf("FieldMap =", StringComparison.Ordinal);
        var end = text.IndexOf("};", start, StringComparison.Ordinal);
        return Regex.Matches(text[start..end], "\\[\"([a-z_]+)\"\\]\\s*=").Select(m => m.Groups[1].Value).ToList();
    }

    [Fact]
    public void Every_declared_field_is_one_of_the_hosts_canonical_names()
    {
        // a misspelt name makes a field invisible on the Metadata Assignment page
        string[] known = ["title", "overview", "year", "poster_url", "backdrop_url", "runtime_minutes", "rating", "genres", "cast", "crew", "tags",
            "logo_url", "banner_url", "thumb_url", "clearart_url", "disc_url", "character_art_url", "collection", "composer", "label", "bpm", "mood",
            "language", "isrc", "birth_date", "death_date"];
        var declared = IgdbMetadataProvider.BuildType(Settings.Default).SupportedFields
            .Concat(IgdbMetadataProvider.BuildType(Settings.Default with { TimeToBeatAsRuntime = true }).SupportedFields).Distinct().ToList();
        Assert.All(declared, f => Assert.Contains(f, known));
        if (HostFields() is { } host) Assert.All(declared, f => Assert.Contains(f, host));
    }

    [Fact]
    public void A_field_is_declared_only_when_something_fills_it()
    {
        var plain = IgdbMetadataProvider.BuildType(Settings.Default).SupportedFields;
        Assert.DoesNotContain("runtime_minutes", plain);
        Assert.Contains("runtime_minutes", IgdbMetadataProvider.BuildType(Settings.Default with { TimeToBeatAsRuntime = true }).SupportedFields);
        foreach (var never in new[] { "collection", "label", "disc_url", "logo_url", "banner_url", "clearart_url", "character_art_url", "thumb_url" })
            Assert.DoesNotContain(never, plain);
        // companies fill cast (developers) and crew (publishers...). extended_data is not declared: the host resolver has no such field (the record shows in the plugin box regardless)
        Assert.All(new[] { "cast", "crew" }, f => Assert.Contains(f, plain));
        Assert.Equal(["igdb:", "steam:"], new IgdbMetadataProvider().GetAcceptedCrossRefPrefixes());
    }
}

public class StandaloneTests
{
    private static readonly string[] AllowedPrefixes = ["System", "Microsoft", "netstandard", "mscorlib", "Serilog", "Chronicle.Plugins", "Chronicle.Plugin.IGDB"];

    [Fact]
    public void The_assembly_references_nothing_but_the_framework_serilog_and_the_plugin_interfaces()
    {
        var refs = typeof(IgdbMetadataProvider).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.All(refs, r => Assert.Contains(AllowedPrefixes, p => r.StartsWith(p, StringComparison.Ordinal)));
        Assert.DoesNotContain(refs, r => r.StartsWith("Chronicle.Plugin.", StringComparison.Ordinal) && r != "Chronicle.Plugin.IGDB");
        Assert.DoesNotContain(refs, r => r is "Chronicle.Core" or "Chronicle.Services" or "Chronicle.Data");
    }

    [Fact]
    public void No_other_plugin_is_named_anywhere_in_the_assembly()
    {
        var bytes = File.ReadAllBytes(typeof(IgdbMetadataProvider).Assembly.Location);
        var text = Encoding.Unicode.GetString(bytes) + "\n" + Encoding.Unicode.GetString(bytes[1..]) + "\n" + Encoding.ASCII.GetString(bytes);
        // the name of a plugin is the segment after "chronicle.plugin." (its own namespace and file name also contain the prefix)
        var names = Regex.Matches(text, @"chronicle\.plugin\.([a-z0-9_]+)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.Equal("igdb", n));
        // the manifest declares no dependency either
        var manifest = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")).ToLowerInvariant();
        Assert.DoesNotContain("depend", manifest);
        Assert.DoesNotContain("requires", manifest);
        // every plugin name the manifest mentions is its own (the plugin id and the entry type both do)
        Assert.All(Regex.Matches(manifest, @"chronicle\.plugin\.([a-z0-9_]+)").Select(m => m.Groups[1].Value), n => Assert.Equal("igdb", n));
    }

    [Fact]
    public async Task Nothing_else_installed_means_a_full_enrichment_still_ends_with_data()
    {
        var fake = new FakeIgdb().Token()
            .OnApi("games", _ => true, () => new FakeIgdb.Reply(System.Net.HttpStatusCode.OK, Harness.LeanFallout()))
            .OnApi("multiquery", _ => true, () => new FakeIgdb.Reply(System.Net.HttpStatusCode.OK, Harness.FullAnswer()));
        var (p, _, _, _) = Harness.Provider(fake);
        var found = await p.SearchAsync(new MediaSearchContext("Fallout: New Vegas", IsRealHierarchyPosition: true));   // no type, no ids, no year
        Assert.Equal("Fallout: New Vegas", found[0].Metadata.Title);
        Assert.NotNull(found[0].Metadata.PosterUrl);
        Assert.NotEmpty(found[0].Metadata.Genres);
        Assert.False(found[0].Metadata.ExtendedData!.Value.TryGetProperty("ids", out _));   // nothing published for another plugin to misread
    }
}

public class LiveTests
{
    private static IgdbMetadataProvider Live()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("IGDB_LIVE") == "1", "set IGDB_LIVE=1 with IGDB_CLIENT_ID and IGDB_CLIENT_SECRET to run the live tests");
        var id = Environment.GetEnvironmentVariable("IGDB_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable("IGDB_CLIENT_SECRET");
        Skip.If(string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret), "IGDB_CLIENT_ID and IGDB_CLIENT_SECRET are needed");
        var p = new IgdbMetadataProvider();
        p.Configure(new Dictionary<string, string> { ["client_id"] = id!, ["client_secret"] = secret! });
        return p;
    }

    [SkippableTheory]
    [InlineData("Fallout: New Vegas", 2010, "Fallout: New Vegas")]
    [InlineData("Final Fantasy 7", 1997, "Final Fantasy VII")]
    [InlineData("Baldur's Gate 3", 2023, "Baldur's Gate III")]
    [InlineData("Control", 2019, "Control")]
    [InlineData("Journey", 2012, "Journey")]
    [InlineData("Doom", 2016, "Doom")]
    [InlineData("Doom", 1993, "Doom")]
    [InlineData("Hades", 2020, "Hades")]
    [InlineData("Hades II", 2025, "Hades II")]
    [InlineData("The Witcher 3: Wild Hunt - Game of the Year Edition", 2015, "The Witcher 3: Wild Hunt")]
    public async Task Real_titles_find_the_right_game(string title, int year, string expected)
    {
        using var p = Live();
        var found = await p.SearchAsync(Harness.Ctx(title, year));
        Assert.NotEmpty(found);
        Assert.Equal(expected, found[0].Metadata.Title);
        Assert.True(found[0].Score >= Matchers.FetchGate, $"score {found[0].Score}: {found[0].ScoreReason}");
        Assert.False(found[0].Metadata.ExtendedData!.Value.GetProperty("partial").GetBoolean());
    }

    [SkippableFact]
    public async Task The_two_Dooms_are_told_apart_by_year()
    {
        using var p = Live();
        var d16 = (await p.SearchAsync(Harness.Ctx("Doom", 2016)))[0].Metadata;
        var d93 = (await p.SearchAsync(Harness.Ctx("Doom", 1993)))[0].Metadata;
        Assert.NotEqual(d16.ExternalId, d93.ExternalId);
        Assert.Equal(2016, d16.Year);
        Assert.Equal(1993, d93.Year);
    }

    [SkippableFact]
    public async Task Fix_match_forms_and_a_missing_game()
    {
        using var p = Live();
        var byId = await p.GetByIdAsync("16");
        Assert.Equal("Fallout: New Vegas", byId.Title);
        Assert.Equal("16", (await p.GetByIdAsync("https://www.igdb.com/games/fallout-new-vegas")).ExternalId);
        Assert.Equal("16", (await p.GetByIdAsync("steam:22380")).ExternalId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => p.GetByIdAsync("999999999"));
        Assert.True(await p.HealthCheckAsync());
    }

    [SkippableFact]
    public async Task An_interactive_search_is_one_quick_request()
    {
        using var p = Live();
        await p.SearchAsync(Harness.Ctx("Hades", enrichment: false));   // warms the token
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = await p.SearchAsync(Harness.Ctx("Hades", enrichment: false));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.5), sw.Elapsed.ToString());
        Assert.NotEmpty(found);
    }
}
