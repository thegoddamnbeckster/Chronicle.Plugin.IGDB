"""Follow-up IGDB probe (P12 name-equality matching strategies, P4b real rate-limit behaviour).
Same rules as igdb_probe.py: credentials only from env vars, never printed or stored."""
import json, os, sys, time, urllib.request, urllib.error, urllib.parse, threading, concurrent.futures as cf

ID = os.environ.get("IGDB_CLIENT_ID", "").strip()
SECRET = os.environ.get("IGDB_CLIENT_SECRET", "").strip()
if not ID or not SECRET:
    sys.exit("Set IGDB_CLIENT_ID and IGDB_CLIENT_SECRET first.")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "probe_out")
os.makedirs(OUT, exist_ok=True)
API = "https://api.igdb.com/v4/"
_last = [0.0]; _lock = threading.Lock()


def http(method, url, body=None, headers=None, timeout=20, pace=True):
    if pace:
        with _lock:
            w = _last[0] + 1 / 3 - time.time()
            if w > 0: time.sleep(w)
            _last[0] = time.time()
    req = urllib.request.Request(url, data=body, method=method, headers=headers or {})
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, dict(r.headers), r.read(), time.time() - t0
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read(), time.time() - t0
    except Exception as e:
        return None, {}, repr(e).encode(), time.time() - t0


s, h, d, t = http("POST", "https://id.twitch.tv/oauth2/token",
                  urllib.parse.urlencode({"client_id": ID, "client_secret": SECRET, "grant_type": "client_credentials"}).encode(),
                  {"Content-Type": "application/x-www-form-urlencoded"})
if s != 200:
    sys.exit(f"token failed: {s}")
TOKEN = json.loads(d)["access_token"]


def q(endpoint, body, pace=True):
    return http("POST", API + endpoint, body.encode(), {"Client-ID": ID, "Authorization": "Bearer " + TOKEN}, pace=pace)


def jq(endpoint, body, pace=True):
    s, h, d, t = q(endpoint, body, pace)
    try:
        return s, json.loads(d), h
    except Exception:
        return s, d.decode("utf-8", "replace")[:300], h


def norm(x):
    return "".join(c for c in x.lower() if c.isalnum())


LEAN = "name,slug,first_release_date,game_type.type,version_parent,total_rating_count,alternative_names.name"
CASES = [("Control", "Control"), ("Journey", "Journey"), ("Fallout", "Fallout"), ("Final Fantasy 7", "Final Fantasy VII"),
         ("Baldur's Gate 3", "Baldur's Gate III"), ("Civilization VI", "Sid Meier's Civilization VI"), ("Hitman", "Hitman"),
         ("Super Mario Odyssey", "Super Mario Odyssey"), ("Half-Life 2", "Half-Life 2"), ("Dune", "Dune"), ("Portal", "Portal"),
         ("Doom", "Doom"), ("Elden Ring", "Elden Ring"), ("Tomb Raider", "Tomb Raider"), ("Rage", "Rage"), ("Mega Man 10", "Mega Man 10"),
         ("Resident Evil 2", "Resident Evil 2"), ("The Witcher 3: Wild Hunt", "The Witcher 3: Wild Hunt")]
out = []
for query, expect in CASES:
    row = {"query": query, "expect": expect}
    strategies = {
        "search_limit10": f'search "{query}"; fields {LEAN}; limit 10;',
        "search_limit50": f'search "{query}"; fields {LEAN}; limit 50;',
        "name_equals_ci": f'fields {LEAN}; where name ~ "{query}"; sort total_rating_count desc; limit 10;',
        "name_equals_ci_main": f'fields {LEAN}; where name ~ "{query}" & game_type = 0; limit 10;',
        "name_prefix_ci": f'fields {LEAN}; where name ~ "{query}"*; sort total_rating_count desc; limit 10;',
        "alt_name_equals_ci": f'fields {LEAN}; where alternative_names.name ~ "{query}"; limit 10;',
        "name_equals_cs": f'fields {LEAN}; where name = "{query}"; limit 10;',
    }
    for k, body in strategies.items():
        s, j, _ = jq("games", body)
        if isinstance(j, list):
            names = [r.get("name", "") for r in j]
            hit = next((i for i, n in enumerate(names) if norm(n) == norm(expect)), -1)
            row[k] = {"status": s, "n": len(j), "rank_of_expected": hit, "first3": names[:3]}
        else:
            row[k] = {"status": s, "error": j}
    out.append(row)
with open(os.path.join(OUT, "P12_strategies.json"), "w", encoding="utf-8") as f:
    json.dump(out, f, indent=1, ensure_ascii=False)
print("P12 done")

# ---- P4b: real rate limit --------------------------------------------------------------------------------
time.sleep(3)
print("P4b: 150 requests as fast as possible, 16 parallel (429s are the point of this test)")
def one(_):
    t0 = time.time()
    s, h, d, t = q("games", "fields name; limit 1;", pace=False)
    keep = {k: v for k, v in h.items() if k.lower() in ("retry-after", "x-ratelimit-limit", "x-ratelimit-remaining", "x-ratelimit-reset", "content-type")}
    return {"t": round(t0, 3), "status": s, "headers": keep, "body": d[:200].decode("utf-8", "replace") if s != 200 else ""}
t_start = time.time()
with cf.ThreadPoolExecutor(16) as ex:
    res = list(ex.map(one, range(150)))
dur = time.time() - t_start
codes = {}
for r in res: codes[r["status"]] = codes.get(r["status"], 0) + 1
first429 = next((r for r in res if r["status"] == 429), None)
summary = {"seconds": round(dur, 2), "rps": round(150 / dur, 1), "status_counts": codes, "first_429": first429}
# recovery: how long until it answers 200 again after a 429
rec = None
if first429:
    t0 = time.time()
    for i in range(40):
        time.sleep(0.5)
        s, h, d, t = q("games", "fields name; limit 1;", pace=False)
        if s == 200:
            rec = round(time.time() - t0, 1); break
summary["seconds_until_200_again"] = rec
with open(os.path.join(OUT, "P4b_ratelimit.json"), "w", encoding="utf-8") as f:
    json.dump({"summary": summary, "sample": res[:5] + [r for r in res if r["status"] != 200][:5]}, f, indent=1)
print(json.dumps(summary, indent=1))
print("Done.")
