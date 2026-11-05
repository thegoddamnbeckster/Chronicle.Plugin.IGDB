"""Throwaway IGDB probe for the Chronicle IGDB plugin design (section 17, P1..P11).

Reads IGDB_CLIENT_ID and IGDB_CLIENT_SECRET from the environment. Never prints or stores the secret or the
access token. Responses are written to ./probe_out (next to this script) as JSON for use as test fixtures.
Paces itself to <= 3 requests/second except in the one deliberate burst test (P4), which is announced.
Standard library only.
"""
import json, os, sys, time, urllib.request, urllib.error, urllib.parse, statistics, struct, threading, concurrent.futures as cf

ID = os.environ.get("IGDB_CLIENT_ID", "").strip()
SECRET = os.environ.get("IGDB_CLIENT_SECRET", "").strip()
if not ID or not SECRET:
    sys.exit("Set IGDB_CLIENT_ID and IGDB_CLIENT_SECRET first.")

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "probe_out")
os.makedirs(OUT, exist_ok=True)
API = "https://api.igdb.com/v4/"
TOKEN = None
_last = [0.0]
_lock = threading.Lock()


def redact(s):
    if not isinstance(s, str):
        return s
    for secret in (SECRET, TOKEN):
        if secret:
            s = s.replace(secret, "<redacted>")
    return s


def save(name, obj):
    with open(os.path.join(OUT, name + ".json"), "w", encoding="utf-8") as f:
        f.write(redact(json.dumps(obj, indent=1, ensure_ascii=False)))


def pace(rps=3.0):
    with _lock:
        wait = _last[0] + 1.0 / rps - time.time()
        if wait > 0:
            time.sleep(wait)
        _last[0] = time.time()


def http(method, url, body=None, headers=None, timeout=20, do_pace=True):
    if do_pace:
        pace()
    req = urllib.request.Request(url, data=body, method=method, headers=headers or {})
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            data = r.read()
            return r.status, dict(r.headers), data, time.time() - t0
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read(), time.time() - t0
    except Exception as e:  # network
        return None, {}, repr(e).encode(), time.time() - t0


def mint(form=True, cid=None, secret=None):
    p = {"client_id": cid or ID, "client_secret": secret or SECRET, "grant_type": "client_credentials"}
    if form:
        return http("POST", "https://id.twitch.tv/oauth2/token", urllib.parse.urlencode(p).encode(),
                    {"Content-Type": "application/x-www-form-urlencoded"})
    return http("POST", "https://id.twitch.tv/oauth2/token?" + urllib.parse.urlencode(p))


def q(endpoint, body, do_pace=True, timeout=20):
    return http("POST", API + endpoint, body.encode("utf-8"),
                {"Client-ID": ID, "Authorization": "Bearer " + TOKEN, "Accept": "application/json"},
                timeout=timeout, do_pace=do_pace)


def jq(endpoint, body, **kw):
    s, h, d, t = q(endpoint, body, **kw)
    try:
        return s, json.loads(d), t, h
    except Exception:
        return s, d.decode("utf-8", "replace")[:500], t, h


summary = {}

# ---- P2: token endpoint ---------------------------------------------------------------------------------
s, h, d, t = mint(form=True)
j = json.loads(d) if s == 200 else {}
summary["P2_form_body_status"] = s
if s != 200:
    save("P2_token_form_failure", {"status": s, "body": d.decode("utf-8", "replace")[:400]})
    s2, h2, d2, _ = mint(form=False)
    summary["P2_query_status"] = s2
    j = json.loads(d2) if s2 == 200 else {}
TOKEN = j.get("access_token")
if not TOKEN:
    save("summary", summary)
    sys.exit("Could not get a token; see probe_out/P2_token_form_failure.json")
summary["P2_expires_in"] = j.get("expires_in")
bad_secret = mint(True, secret="x" * 30)
bad_id = mint(True, cid="x" * 30)
save("P2_bad_credentials", {"bad_secret": [bad_secret[0], bad_secret[2].decode("utf-8", "replace")[:300]],
                            "bad_id": [bad_id[0], bad_id[2].decode("utf-8", "replace")[:300]]})
summary["P2_bad_secret_status"] = bad_secret[0]
summary["P2_bad_id_status"] = bad_id[0]
s, _, d, _ = http("POST", API + "games", b"fields name; limit 1;", {"Client-ID": ID, "Authorization": "Bearer " + "x" * 30})
save("P2_bad_token_on_igdb", {"status": s, "body": d.decode("utf-8", "replace")[:300]})
summary["P2_bad_token_status"] = s
print("P2 done", {k: v for k, v in summary.items() if k.startswith("P2")})

# ---- P5: lookup tables ------------------------------------------------------------------------------------
for tbl, fields in [("game_types", "id,type"), ("game_statuses", "id,status"), ("external_game_sources", "id,name"),
                    ("website_types", "id,type"), ("release_date_regions", "id,region")]:
    s, j, t, _ = jq(tbl, f"fields {fields}; limit 500;")
    save("P5_" + tbl, {"status": s, "rows": j})
    summary["P5_" + tbl] = s if not isinstance(j, list) else len(j)
print("P5 done")

# ---- P1/P3: search ranking and clause order ------------------------------------------------------------
TITLES = ["Doom", "Prey", "Control", "Portal", "Portal 2", "Hades", "Hades II", "Journey", "Limbo", "Inside", "Fable", "Dune",
          "Final Fantasy VII", "Final Fantasy 7", "Mega Man X", "Mega Man 10", "Resident Evil 2", "The Elder Scrolls V: Skyrim",
          "The Witcher 3: Wild Hunt", "Dishonored", "Fallout", "Fallout: New Vegas", "Elden Ring", "Half-Life 2",
          "Stardew Valley", "Celeste", "Cuphead", "Return of the Obra Dinn", "I Am Alive", "Alien: Isolation",
          "Red Dead Redemption 2", "God of War", "Hollow Knight", "Baldur's Gate 3", "Hitman", "Tomb Raider",
          "Super Mario Odyssey", "Tetris", "Minecraft", "Rage", "Dark Souls III", "Civilization VI", "Sid Meier's Civilization VI"]
LEAN = "name,slug,first_release_date,game_type.type,version_parent,parent_game,total_rating_count,platforms.abbreviation"
res = []
for title in TITLES:
    clean = title.replace('"', "").replace("\\", "").replace(";", "")
    s, j, t, _ = jq("games", f'search "{clean}"; fields {LEAN}; limit 10;')
    s2, j2, t2, _ = jq("games", f'fields {LEAN}; where version_parent = null; search "{clean}"; limit 10;')
    s3, j3, t3, _ = jq("games", f'search "{clean}"; fields {LEAN}; where version_parent = null; limit 10;')
    def rank(rows):
        if not isinstance(rows, list):
            return None
        n = lambda x: "".join(c for c in x.lower() if c.isalnum())
        for i, r in enumerate(rows):
            if n(r.get("name", "")) == n(title):
                return i
        return -1
    res.append({"title": title, "plain": [s, rank(j), round(t, 2)], "where_after_fields": [s2, rank(j2) if s2 == 200 else str(j2)[:200]],
                "where_after_search": [s3, rank(j3) if s3 == 200 else str(j3)[:200]],
                "top3": [r.get("name") for r in j[:3]] if isinstance(j, list) else None})
    if title == "Doom":
        save("P1_fixture_doom", j)
save("P1_P3_ranking", res)
ranks = [r["plain"][1] for r in res if isinstance(r["plain"][1], int)]
summary["P1_exact_first"] = sum(1 for x in ranks if x == 0)
summary["P1_exact_in_top10"] = sum(1 for x in ranks if x >= 0)
summary["P1_total"] = len(TITLES)
summary["P3_where_after_fields_ok"] = sum(1 for r in res if r["where_after_fields"][0] == 200)
summary["P3_where_after_search_ok"] = sum(1 for r in res if r["where_after_search"][0] == 200)
print("P1/P3 done: exact first %s / in top10 %s of %s" % (summary["P1_exact_first"], summary["P1_exact_in_top10"], len(TITLES)))

# ---- P6: errors, unknown fields, missing id, deprecated field, hostile text -----------------------------------
tests = {
    "unknown_field": 'fields name,this_field_does_not_exist; limit 1;',
    "missing_id": "fields name; where id = 999999999;",
    "deprecated_category": "fields name,category,status; where id = 1942;",
    "quote_in_search": 'search "a\\"b"; fields name; limit 3;',
    "unterminated_quote": 'search "abc; fields name; limit 3;',
    "injection_attempt": 'search "x\\"; where id > 0; fields *; limit 500; \\""; fields name; limit 3;',
    "semicolon_in_search": 'search "a;b"; fields name; limit 3;',
    "empty_search": 'search ""; fields name; limit 3;',
    "garbage_body": "this is not apicalypse",
}
out6 = {}
for k, body in tests.items():
    s, j, t, _ = jq("games", body)
    out6[k] = {"status": s, "body": (j if not isinstance(j, list) else {"count": len(j), "first": j[:2]})}
s, _, d, _ = http("GET", API + "games", headers={"Client-ID": ID, "Authorization": "Bearer " + TOKEN})
out6["get_instead_of_post"] = {"status": s, "body": d.decode("utf-8", "replace")[:200]}
s, j, t, _ = jq("nonexistent_endpoint", "fields name;")
out6["bad_endpoint"] = {"status": s, "body": j if not isinstance(j, list) else "list"}
save("P6_errors", out6)
summary["P6"] = {k: v["status"] for k, v in out6.items()}
print("P6 done", summary["P6"])

# ---- P7: field fill rates ------------------------------------------------------------------------------
FULL = ("name,slug,url,summary,storyline,first_release_date,game_type.type,game_status.status,version_parent,version_title,parent_game,"
        "cover.image_id,artworks.image_id,screenshots.image_id,videos.video_id,genres.name,themes.name,game_modes.name,"
        "player_perspectives.name,keywords.name,platforms.name,platforms.abbreviation,involved_companies.developer,involved_companies.publisher,"
        "involved_companies.company.name,franchises.name,collections.name,alternative_names.name,alternative_names.comment,"
        "release_dates.human,release_dates.platform.name,age_ratings.organization.name,websites.url,websites.type.type,"
        "external_games.uid,external_games.external_game_source.name,rating,rating_count,aggregated_rating,aggregated_rating_count,"
        "total_rating,total_rating_count,similar_games.name,dlcs.name,expansions.name,remakes.name,remasters.name,ports.name,"
        "language_supports.language.name,game_engines.name,multiplayer_modes.id,updated_at")
famous = ["Hades", "Celeste", "Elden Ring", "Portal 2", "Stardew Valley", "Hollow Knight", "Cuphead", "Dishonored", "Fallout: New Vegas",
          "Half-Life 2", "God of War", "Baldur's Gate 3", "Return of the Obra Dinn", "Alien: Isolation", "Doom", "Control", "Journey",
          "Limbo", "Inside", "Resident Evil 2", "Minecraft", "Tetris", "Hitman", "Rage", "Prey"]
ids_famous = []
for title in famous:
    s, j, t, _ = jq("games", f'search "{title}"; fields name,total_rating_count; limit 5;')
    if isinstance(j, list) and j:
        ids_famous.append(sorted(j, key=lambda r: -(r.get("total_rating_count") or 0))[0]["id"])
s, obscure, t, _ = jq("games", 'fields id; where total_rating_count = null & game_type = 0; sort id desc; limit 25;')
ids_obscure = [r["id"] for r in obscure] if isinstance(obscure, list) else []
def fill(ids, label):
    rows = []
    for i in range(0, len(ids), 10):
        s, j, t, _ = jq("games", f"fields {FULL}; where id = ({','.join(map(str, ids[i:i+10]))}); limit 10;")
        if isinstance(j, list):
            rows += j
        else:
            save(f"P7_{label}_error", {"status": s, "body": j})
    keys = {}
    for r in rows:
        for k in FULL.split(","):
            base = k.split(".")[0]
            if r.get(base) not in (None, [], "", 0):
                keys[base] = keys.get(base, 0) + 1
    return rows, {k: f"{v}/{len(rows)}" for k, v in sorted(keys.items())}
rows_f, fill_f = fill(ids_famous, "famous")
rows_o, fill_o = fill(ids_obscure, "obscure")
save("P7_famous_records", rows_f)
save("P7_obscure_records", rows_o)
save("P7_fill", {"famous": fill_f, "obscure": fill_o})
summary["P7"] = {"famous_n": len(rows_f), "obscure_n": len(rows_o)}
print("P7 done", summary["P7"])

# ---- P9: platforms, slug characters -----------------------------------------------------------------------
plats = []
for off in (0, 500):
    s, j, t, _ = jq("platforms", f"fields name,abbreviation,alternative_name; limit 500; offset {off};")
    if isinstance(j, list):
        plats += j
save("P9_platforms", plats)
wanted = ["PS4", "PS5", "PC", "Switch", "Xbox One", "Xbox Series X", "PS3", "3DS", "Wii U", "Mac", "Linux", "iOS", "Android"]
low = {}
for p in plats:
    for key in (p.get("name"), p.get("abbreviation"), p.get("alternative_name")):
        if key:
            low.setdefault(key.lower(), p.get("name"))
summary["P9_tags"] = {w: low.get(w.lower()) for w in wanted}
slugs = []
for off in (0, 500, 1000, 5000, 20000):
    s, j, t, _ = jq("games", f"fields slug; limit 500; offset {off};")
    if isinstance(j, list):
        slugs += [r["slug"] for r in j if r.get("slug")]
chars = sorted(set("".join(slugs)))
save("P9_slug_chars", {"count": len(slugs), "characters": "".join(chars)})
summary["P9_slug_chars"] = "".join(chars)
print("P9 done", summary["P9_slug_chars"])

# ---- P11: latency and payload ------------------------------------------------------------------------
gid = ids_famous[0] if ids_famous else 1942
lat = {"lean_search": [], "full_fetch": [], "ttb": []}
sizes = {}
for title in famous[:12]:
    s, h, d, t = q("games", f'search "{title}"; fields {LEAN}; limit 10;')
    lat["lean_search"].append(t); sizes["lean_search"] = len(d)
for i in ids_famous[:12]:
    s, h, d, t = q("games", f"fields {FULL}; where id = {i};")
    lat["full_fetch"].append(t); sizes["full_fetch"] = len(d)
    s, h, d, t = q("game_time_to_beats", f"fields hastily,normally,completely,count; where game_id = {i};")
    lat["ttb"].append(t); sizes["ttb"] = len(d)
s, h, d, t = q("games", f'search "Hades"; fields {FULL}; limit 10;')
sizes["full_search_10"] = len(d)
lat["full_search_10"] = [t]
summary["P11_latency_s"] = {k: {"median": round(statistics.median(v), 3), "max": round(max(v), 3)} for k, v in lat.items() if v}
summary["P11_payload_bytes"] = sizes
def conc(n):
    t0 = time.time()
    def one(title):
        return q("games", f'search "{title}"; fields {LEAN}; limit 10;', do_pace=False)[0]
    with cf.ThreadPoolExecutor(n) as ex:
        codes = list(ex.map(one, famous[:12]))
    return {"seconds_for_12": round(time.time() - t0, 2), "statuses": sorted(set(codes), key=str)}
summary["P11_conc2"] = conc(2)
time.sleep(2)
summary["P11_conc3"] = conc(3)
time.sleep(2)
print("P11 done", summary["P11_latency_s"])

# ---- P4: multiquery counting, burst, Retry-After ---------------------------------------------------------
mq = "\n".join(f'query games "g{i}" {{ fields name; limit 1; offset {i}; }};' for i in range(10))
t0 = time.time()
codes = []
for _ in range(5):
    s, h, d, t = http("POST", API + "multiquery", mq.encode(), {"Client-ID": ID, "Authorization": "Bearer " + TOKEN}, do_pace=False)
    codes.append(s)
summary["P4_multiquery_5_requests_x10_queries"] = {"statuses": codes, "seconds": round(time.time() - t0, 2)}
time.sleep(3)
print("P4: now a deliberate burst of 12 parallel requests (a few 429s are expected)")
def burst_one(_):
    s, h, d, t = q("games", "fields name; limit 1;", do_pace=False)
    return s, {k: v for k, v in h.items() if k.lower() in ("retry-after", "x-ratelimit-limit", "x-ratelimit-remaining", "x-ratelimit-reset")}, d[:150].decode("utf-8", "replace")
with cf.ThreadPoolExecutor(12) as ex:
    burst = list(ex.map(burst_one, range(12)))
save("P4_burst", burst)
summary["P4_burst_statuses"] = sorted(b[0] for b in burst)
summary["P4_429_headers"] = next((b[1] for b in burst if b[0] == 429), None)
summary["P4_429_body"] = next((b[2] for b in burst if b[0] == 429), None)
time.sleep(5)

# ---- P8: image CDN ---------------------------------------------------------------------------------------
def jpeg_size(b):
    i = 2
    while i < len(b) - 9:
        if b[i] != 0xFF:
            i += 1; continue
        m = b[i + 1]
        if 0xC0 <= m <= 0xCF and m not in (0xC4, 0xC8, 0xCC):
            return struct.unpack(">HH", b[i + 5:i + 9])[::-1]
        i += 2 + struct.unpack(">H", b[i + 2:i + 4])[0]
    return None
imgs = []
for r in rows_f[:15]:
    c = (r.get("cover") or {}).get("image_id")
    if c:
        imgs += [("cover_big_2x", c), ("1080p", c), ("cover_small", c)]
    shots = r.get("screenshots") or []
    if shots:
        imgs.append(("screenshot_big", shots[0]["image_id"]))
def fetch_img(a):
    size, iid = a
    s, h, d, t = http("GET", f"https://images.igdb.com/igdb/image/upload/t_{size}/{iid}.jpg", do_pace=False)
    return {"size": size, "status": s, "bytes": len(d) if isinstance(d, bytes) else None, "seconds": round(t, 2),
            "dims": jpeg_size(d) if s == 200 and isinstance(d, bytes) else None, "ctype": h.get("Content-Type")}
t0 = time.time()
with cf.ThreadPoolExecutor(8) as ex:
    img_res = list(ex.map(fetch_img, imgs))
save("P8_images", img_res)
summary["P8"] = {"count": len(img_res), "seconds_total": round(time.time() - t0, 2),
                 "statuses": sorted({str(x["status"]) for x in img_res}),
                 "sample_dims": {k: [x["dims"] for x in img_res if x["size"] == k][:3] for k in ("cover_big_2x", "1080p", "cover_small", "screenshot_big")}}
print("P8 done", summary["P8"]["statuses"])

save("summary", summary)
print("\nDone. Summary written to", os.path.join(OUT, "summary.json"))
