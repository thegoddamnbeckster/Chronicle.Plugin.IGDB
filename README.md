# Chronicle.Plugin.IGDB

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.IGDB?label=Chronicle.Plugin.IGDB&color=6441a5)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.IGDB/releases/latest)

Video game metadata plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Fetches **everything [IGDB](https://www.igdb.com/) has for a game**: the cover, every screenshot and artwork, the summary and storyline, genres, themes, game modes, player perspectives and keywords, developers and publishers (with their logos), platforms and release dates, the IGDB score and the critics' score, age ratings, related games (DLC, expansions, remakes, ports, similar games), characters, websites, store ids (Steam, GOG, Epic and the rest), videos, and how long players take to finish it. It is the first source for Chronicle's **Video Games** library and **needs no other plugin**.

**Plugin ID:** `chronicle.plugin.igdb`
**Version:** 1.0.0
**Auth:** a free Twitch developer client id and client secret (see Setup). IGDB is free for non-commercial use.

---

## Supported Media Types

| Media Type | Hierarchy | Fields |
|------------|-----------|--------|
| `game` | Game (one level) | title, overview, year, poster_url, backdrop_url, rating, genres, tags (and runtime_minutes when "use time to beat as the runtime" is on) |

The type is named **`game`** ("Video Games", verb "played"), the name the IMDb, Wikipedia and the other game plugins already use, so every games plugin shares one library. IGDB declares it exactly as IMDb does (display name, verb and unit), because Chronicle merges the declarations of every plugin that names a type and they must agree. If no game plugin has created the type yet, **Chronicle creates a plugin's types when it starts, so restart Chronicle once after installing**. The names `games`, `videogame` and `video_game` are accepted as aliases from clients.

The cover is the poster; the first artwork (or screenshot) is the backdrop. **Every** screenshot, artwork, localized cover and character picture is also kept as an additional image (`Screenshot`, `Fanart`, `Front`, `Character`); nothing is limited.

Priority is 10: IGDB is the primary source for games. **It takes part in Add Media and the scan search** (one quick request), so a game can be added by name.

**For its data to show, check Metadata Assignment** (Settings, Metadata Assignment, Video Games). A type with no saved lists uses the first plugin that has a value, so IGDB is used as soon as it is the only one; when you add another games source, put IGDB where you want it.

---

## What is kept

**Everything IGDB sends for a game is stored**, with no limits and no filtering. The complete record, exactly as IGDB returned it with every reference expanded, is kept once under `extendedData.igdb`, so a field IGDB adds later is stored without a plugin change. Beside it, compact summaries make the common things easy to read: `developers`, `publishers`, `porting`, `supporting`, `company_logos`, `platforms`, `release_dates`, `series`, `franchises`, `websites`, `videos` (links, never embedded), `external_ids` (every store, grouped by store), `ratings` (IGDB users, critics and the total, on IGDB's 0 to 100 scale with the vote count), `time_to_beat` (seconds), `characters`, and `executable_names`.

`executable_names` holds the file names IGDB mixes into a game's alternative names ("game.exe"). They are kept, but they are not offered as alternative names, because they would pollute matching.

The single rating Chronicle shows is on a 0 to 10 scale, from IGDB's total score by default (setting **Rating to use**). A score is never hidden because few people voted unless you set a floor.

---

## External ID Format

Stored under source `igdb`, as the bare IGDB game id (`16`, `1942`).

**Fix match** accepts:

* an IGDB id: `1942` or `igdb:1942`;
* an igdb.com game page address: `https://www.igdb.com/games/fallout-new-vegas` (only https, only `igdb.com` or `www.igdb.com`, only a `/games/` page; the plugin reads the address for its name and asks IGDB's own API, it never fetches the address);
* `steam:` and a Steam app id: `steam:22380`.

Nothing else is accepted, and the message says which forms are.

This plugin publishes **no cross-reference ids** to other plugins: nothing under `ids` is written, because no other games plugin yet accepts a Steam, GOG or Epic id in a known form. Every store id IGDB lists is in `extendedData.external_ids`. It does accept `igdb:` and `steam:` ids that another plugin finds.

---

## How a game is matched

1. **An id Chronicle already holds** (`igdb`, then `steam`) is an exact lookup.
2. **By name.** IGDB's own fuzzy search ranked the exact title first for only 22 of 43 titles in testing, so the plugin asks for an **exact title or alternative name** first (one query, most-rated first), and uses the fuzzy search only if nothing fits. Nothing similar is ever accepted: a game is chosen only if its title (or an alternative name) equals the library's, once edition words and Roman numerals are allowed for.
   * **Edition words** ("Game of the Year Edition", "Deluxe Edition", "Definitive Edition", "GOTY") are ignored on one side. A bare word that is often part of a game's own name ("Pokemon Gold", "Persona 5 Royal", "Super Smash Bros. Ultimate") is **not** an edition word unless "Edition" or "Version" follows it, and an exact title always beats its base game. **Words that make a different release are never ignored**: Remastered, Remake, Redux, Director's Cut, Part 2, a sequel number. "Resident Evil 2" and its 2019 remake are different games; "Hades" is not "Hades II".
   * **A store's "Remake" or "Remastered" label** ("Resident Evil 4 Remake") is read as the game's type: IGDB names the 2023 remake "Resident Evil 4" and records that it is a Remake, so the label matches only a record of that type.
   * **Roman and Arabic sequel numbers** are treated as equal ("Final Fantasy 7" finds "Final Fantasy VII"), but a lone I, V or X is never a numeral: "Mega Man X" is not "Mega Man 10".
   * **A year** in the title ("Doom (2016)") or known to Chronicle tells the two Dooms apart; a year far from every release date caps the score below Chronicle's confidence gate. **A platform tag** ("[PS4]", "(PC)") is a small bonus, never a penalty.
   * **DLC, expansions, bundles, mods** and other types that are part of, or wrap, another game are matched only on an exact title and carry a penalty. Nothing is excluded.
3. **If nothing fits yet, the fuzzy search**, on the whole title first (a store's "Assassin's Creed: Odyssey" is IGDB's "Assassin's Creed Odyssey"), then on the main title alone. Store typography (curly quotes, "(R)", "(TM)") is made plain first.
4. **The winner is fetched in full inside the search**, with its characters and time to beat, in one request, so the record Chronicle receives is complete.

**Add Media** (a person typing) takes at most two quick requests and also lists **similar titles** ("witcher 3" offers "The Witcher 3: Wild Hunt") for you to choose from; they are scored under Chronicle's confidence gate, so nothing is ever accepted automatically on one. Enrichment accepts an exact title or alternative name only.

A game IGDB does not have is **not found**, and Chronicle treats not found as final until you reset it from the Enrichment page; Fix match is the remedy for a wrong or missing match.

---

## Speed, limits and failures

IGDB allows **4 requests a second** (and 8 open at once). The plugin keeps to `requests_per_second` (3 by default) with one limiter shared by everything. One enrichment is typically two requests, so about 1,000 games take roughly 12 minutes and 10,000 about 2 hours, in the background.

What a failure does is deliberate:

| What happened | What Chronicle sees |
|---|---|
| IGDB rate limit (HTTP 429) | The item stays Pending, no retry is used up, the pass stops and the scheduler retries 15 minutes later. The plugin then refuses to ask again for a few seconds (2, then doubling to 5 minutes), so Chronicle's own two retries cost nothing. |
| IGDB down, a timeout, a network error, an answer that is not JSON | The same: a pause, never a failure against the item. |
| Twitch or IGDB rejects the client id or secret | "Needs attention": authentication failed. After the first rejection **no request is sent at all** for 15 minutes, or until you save the settings (saving always clears it). **Chronicle does not requeue the failed items by itself: after fixing the credentials, reset the Authentication Failed items on the Enrichment page.** |
| A game IGDB does not have | Not found. |
| One game's record fails to load three times in a row, **while IGDB answers other requests** | That item is marked failed, so one bad record cannot block the queue. An outage never does this: nothing counts while IGDB is down. |

An interactive search (Add Media) never shows an error: it returns what it has within 3.5 seconds. Every request has its own timeout (8 seconds) and every lookup a deadline (20 seconds, under the 25 Chronicle allows). If IGDB removes or renames a field, **only the part of the record that used it is lost**: the plugin reads IGDB's error, switches that part off for an hour, retries once, and reports "degraded" on the health check. A record fetched meanwhile says so (`extendedData.degraded` lists the parts that were off, and `partial` is true), so a re-sync replaces it.

The client secret is never logged, never placed in a URL, and is removed from every message. **It is visible to administrators on the plugin's settings page** (it is encrypted at rest).

---

## Setup

1. Sign in at <https://dev.twitch.tv/console> with a Twitch account that has **two-factor authentication on**.
2. **Register Your Application**: any name, OAuth redirect `https://localhost`, category **Application Integration**, client type **Confidential**.
3. Open the application, click **New Secret**, and copy the **Client ID** and the **Client Secret** (the secret is shown once). **Use a separate application for each Chronicle instance**: an application can have only 25 active tokens, and every plugin reload takes one.
4. In Chronicle, **Plugins**, install IGDB from the catalog and approve its file hash. **Restart Chronicle once** so the plugin is loaded (and the Video Games type is created if no other plugin has).
5. Open the plugin's settings, enter the client id and secret, save, and run **Health check**.
6. Add games with **Add Media**. **Do not create a scan folder of type Video Games**: the file scanner recognises no game files.
7. Check **Metadata Assignment** for Video Games.

---

## Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `client_id` | | Your Twitch application's client id. |
| `client_secret` | | Your Twitch application's client secret. Stored encrypted. |
| `requests_per_second` | `3` | 1 to 4 (IGDB allows 4). |
| `max_concurrent_requests` | `3` | 1 to 8 (IGDB allows 8). |
| `request_timeout_seconds` | `8` | One request. 2 to 20. |
| `call_deadline_seconds` | `20` | One lookup. 5 to 24. |
| `search_limit` | `10` | Candidates per query. 1 to 50. |
| `poster_size` | `cover_big_2x` | Cover size. |
| `rating_source` | total | Total, IGDB users, or critics. |
| `minimum_rating_votes` | `0` | 0 hides nothing. |
| `fetch_time_to_beat` | on | Part of the same request. |
| `use_time_to_beat_as_runtime` | off | Fills the runtime; Chronicle derives time spent from runtime. |
| `edition_words` | a list | Words that mark an edition of the same game. |
| `never_strip_words` | a list | Qualifiers that make a different release. |
| `exact_title_only_game_types` | a list | Types matched only on an exact title. None is excluded. |
| `platform_aliases` | a list | `tag = platform name as IGDB spells it`, for `(PS4)`-style tags. |

---

## Background tasks

| Task | Schedule | Default |
|------|----------|---------|
| Fetch Missing Game Metadata (`fetch-missing-metadata`) | daily 05:45 | on |
| Re-sync All Game Metadata (`resync-all-metadata`) | Thursday 03:45 | **off** |

A re-sync stops at the first IGDB outage or rate limit and starts again from the beginning next time, so it suits small libraries; turn it on if you want new artwork and corrected data picked up. Images IGDB removes stay available for 30 days, so a re-sync from time to time refreshes the addresses.

---

## Structure

```
IgdbMetadataProvider.cs   the plugin: search, fetch by id, images, health, settings
IgdbClient.cs             HTTP, the Twitch token, the credentials latch, failure mapping
Resilience.cs             the failures Chronicle is told, the rate limiter, the cool-down
Queries.cs                the query bodies and the expansion groups
Matchers.cs               scoring a candidate against a library title
TextRules.cs              titles: folding, numerals, edition words, platform tags, safe query text
MetadataMapper.cs         IGDB records to Chronicle metadata (nothing dropped)
ParsedId.cs               Fix match and id forms
Settings.cs, J.cs, TtlCache.cs
tests/                    xUnit tests on real recorded IGDB responses (tests/fixtures/live-2026-11-05)
tools/                    the probe scripts used to measure the live service
```

---

## Building

```
dotnet build -c Release
dotnet test tests/Chronicle.Plugin.IGDB.Tests
```

The live tests run only with `IGDB_LIVE=1`, `IGDB_CLIENT_ID` and `IGDB_CLIENT_SECRET` set.

Design and measurements: `docs/plans/2026-11-05-igdb-plugin-design.md` in the Chronicle repository.

## License

MIT
