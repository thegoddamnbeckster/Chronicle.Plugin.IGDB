"""Third IGDB probe: P12b (one-request OR query) and P13 (fields * with expansions, multiquery of game + extras,
characters endpoint). Credentials only from env vars; never printed or stored."""
import json, os, sys, time, urllib.request, urllib.error, urllib.parse

ID = os.environ.get("IGDB_CLIENT_ID", "").strip()
SECRET = os.environ.get("IGDB_CLIENT_SECRET", "").strip()
if not ID or not SECRET:
    sys.exit("Set IGDB_CLIENT_ID and IGDB_CLIENT_SECRET first.")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "probe_out")
os.makedirs(OUT, exist_ok=True)
API = "https://api.igdb.com/v4/"


def http(url, body, headers):
    time.sleep(0.4)
    req = urllib.request.Request(url, data=body, method="POST", headers=headers)
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return r.status, r.read(), time.time() - t0
    except urllib.error.HTTPError as e:
        return e.code, e.read(), time.time() - t0


s, d, _ = http("https://id.twitch.tv/oauth2/token",
               urllib.parse.urlencode({"client_id": ID, "client_secret": SECRET, "grant_type": "client_credentials"}).encode(),
               {"Content-Type": "application/x-www-form-urlencoded"})
if s != 200:
    sys.exit(f"token failed: {s}")
TOKEN = json.loads(d)["access_token"]
H = {"Client-ID": ID, "Authorization": "Bearer " + TOKEN}


def q(ep, body):
    s, d, t = http(API + ep, body.encode(), H)
    try:
        return s, json.loads(d), t, len(d)
    except Exception:
        return s, d.decode("utf-8", "replace")[:400], t, len(d)


def save(name, obj):
    with open(os.path.join(OUT, name + ".json"), "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=1, ensure_ascii=False)


summary = {}
LEAN = "name,game_type.type,first_release_date,total_rating_count"

# ---- P12b: one-request OR -------------------------------------------------------------------------------
or_res = {}
for title in ["Final Fantasy 7", "Baldur's Gate 3", "Civilization VI", "Control", "Fallout", "Hades"]:
    body = f'fields {LEAN}; where (name ~ "{title}" | alternative_names.name ~ "{title}"); sort total_rating_count desc; limit 10;'
    s, j, t, n = q("games", body)
    or_res[title] = {"status": s, "n": len(j) if isinstance(j, list) else None,
                     "names": [r.get("name") for r in j][:5] if isinstance(j, list) else j}
save("P12b_or_query", or_res)
summary["P12b"] = {k: [v["status"], v["n"]] for k, v in or_res.items()}
print("P12b", summary["P12b"])

# ---- P13: fields * plus expansions ----------------------------------------------------------------------
s, j, t, n = q("games", 'fields id; where name ~ "Fallout: New Vegas"; limit 1;')
gid = j[0]["id"] if isinstance(j, list) and j else 1942
EXPAND = ("cover.*, artworks.*, screenshots.*, videos.*, genres.*, themes.*, keywords.*, game_modes.*, player_perspectives.*, platforms.*, "
          "game_type.*, game_status.*, involved_companies.*, involved_companies.company.*, involved_companies.company.logo.*, "
          "alternative_names.*, game_localizations.*, game_localizations.cover.*, release_dates.*, release_dates.platform.*, "
          "external_games.*, external_games.external_game_source.*, websites.*, websites.type.*, age_ratings.*, "
          "age_ratings.organization.*, age_ratings.rating_category.*, age_ratings.rating_content_descriptions.*, "
          "language_supports.*, language_supports.language.*, language_supports.language_support_type.*, multiplayer_modes.*, "
          "game_engines.*, franchises.*, collections.*, similar_games.name, dlcs.name, expansions.name, standalone_expansions.name, "
          "expanded_games.name, remakes.name, remasters.name, ports.name, forks.name, bundles.name, parent_game.name, version_parent.name")
s_star, star, t_star, n_star = q("games", f"fields *; where id = {gid};")
s_exp, exp, t_exp, n_exp = q("games", f"fields *, {EXPAND}; where id = {gid};")
star_keys = sorted(star[0].keys()) if isinstance(star, list) and star else []
exp_keys = sorted(exp[0].keys()) if isinstance(exp, list) and exp else []
missing = [k for k in star_keys if k not in exp_keys]
summary["P13_star_vs_expanded"] = {"star_status": s_star, "star_keys": len(star_keys), "star_bytes": n_star, "star_s": round(t_star, 3),
                                   "expanded_status": s_exp, "expanded_keys": len(exp_keys), "expanded_bytes": n_exp,
                                   "expanded_s": round(t_exp, 3), "keys_lost_by_expanding": missing,
                                   "error_if_any": exp if s_exp != 200 else None}
if isinstance(exp, list) and exp:
    save("P13_expanded_game", exp[0])
    save("P13_star_game", star[0])
    # which expanded fields turned into objects vs stayed ids
    shapes = {}
    for k, v in exp[0].items():
        if isinstance(v, list):
            shapes[k] = "list of " + (type(v[0]).__name__ if v else "empty")
        else:
            shapes[k] = type(v).__name__
    summary["P13_shapes"] = shapes
    ic = (exp[0].get("involved_companies") or [{}])[0]
    summary["P13_company_logo_expanded"] = bool(((ic.get("company") or {}).get("logo")))
    gl = exp[0].get("game_localizations")
    summary["P13_localizations"] = (gl[:1] if gl else "none")
print("P13 expansion:", json.dumps({k: v for k, v in summary["P13_star_vs_expanded"].items() if k != "star_keys"})[:600])

# 10-result search with the full expansion
s, j, t, n = q("games", f'search "Fallout"; fields *, {EXPAND}; limit 10;')
summary["P13_search10_full"] = {"status": s, "bytes": n, "seconds": round(t, 3), "n": len(j) if isinstance(j, list) else j}
s, j, t, n = q("games", f'fields *, {EXPAND}; where name ~ "Hades"; sort total_rating_count desc; limit 10;')
summary["P13_exactname10_full"] = {"status": s, "bytes": n, "seconds": round(t, 3), "n": len(j) if isinstance(j, list) else j}

# ---- characters endpoint --------------------------------------------------------------------------------
s, j, t, n = q("characters", f"fields *, mug_shot.*, character_gender.*, character_species.*; where games = ({gid}); limit 50;")
save("P13_characters", {"status": s, "result": j})
summary["P13_characters"] = {"status": s, "n": len(j) if isinstance(j, list) else j, "bytes": n, "seconds": round(t, 3)}

# ---- multiquery: game + time to beat + characters -----------------------------------------------------------
mq = (f'query games "game" {{ fields *, cover.*, genres.*; where id = {gid}; }};\n'
      f'query game_time_to_beats "ttb" {{ fields *; where game_id = {gid}; }};\n'
      f'query characters "characters" {{ fields *, mug_shot.*; where games = ({gid}); limit 50; }};')
s, d, t = http(API + "multiquery", mq.encode(), H)
try:
    mj = json.loads(d)
except Exception:
    mj = d.decode("utf-8", "replace")[:500]
save("P13_multiquery", {"status": s, "result": mj})
parts = {}
if isinstance(mj, list):
    for part in mj:
        r = part.get("result")
        parts[part.get("name")] = len(r) if isinstance(r, list) else (r if r is not None else part)
summary["P13_multiquery"] = {"status": s, "seconds": round(t, 3), "bytes": len(d), "parts": parts}
print("P13 multiquery:", summary["P13_multiquery"])

with open(os.path.join(OUT, "summary3.json"), "w", encoding="utf-8") as f:
    json.dump(summary, f, indent=1, ensure_ascii=False)
print("Done.")
