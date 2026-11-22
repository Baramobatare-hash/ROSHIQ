#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Мета и сборки по позициям из STRATZ (api.stratz.com/graphql): рейтинговые паблики текущей недели,
отдельно для каждой позиции 1–5, с процентом побед.

Нужен бесплатный токен STRATZ в переменной окружения STRATZ_TOKEN (в GitHub — секрет репозитория).
Без токена скрипт ничего не меняет и завершается успешно — работает старый источник (tools/update_builds.py).

Запуск: python tools/update_meta.py --probe   — проверить токен и схему API (печатает поля, ничего не пишет)
        python tools/update_meta.py           — обновить hero_meta.txt и hero_builds.txt
Только стандартная библиотека Python.
"""
import argparse
import subprocess
import json
import os
import sys
import time
import urllib.error
import urllib.request

ENDPOINT = "https://api.stratz.com/graphql"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RES = os.path.join(ROOT, "src", "Roshiq.Data", "Resources")
POSITIONS = ["POSITION_1", "POSITION_2", "POSITION_3", "POSITION_4", "POSITION_5"]


ip_waits = [0]


def gql(token, query, variables=None, retries=5):
    body = json.dumps({"query": query, "variables": variables or {}}).encode()
    last = None
    attempt = 0
    while attempt < retries:
        attempt += 1
        req = urllib.request.Request(ENDPOINT, data=body, method="POST", headers={
            "Authorization": "Bearer " + token, "Content-Type": "application/json",
            "User-Agent": "STRATZ_API", "Accept": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                d = json.load(r)
            if d.get("errors"):
                raise RuntimeError(json.dumps(d["errors"], ensure_ascii=False)[:800])
            return d.get("data") or {}
        except urllib.error.HTTPError as e:
            text = e.read()[:400].decode("utf-8", "replace")
            last = f"HTTP {e.code}: {text}"
            if e.code == 403 and "IP Address" in text and ip_waits[0] < 20:
                # бесплатный ключ: не больше двух IP за 15 минут; адрес раннера GitHub — новый, ждём, пока освободится место
                ip_waits[0] += 1
                print(f"  STRATZ: лимит IP-адресов, жду минуту ({ip_waits[0]}/20)")
                time.sleep(60)
                attempt -= 1               # ожидание IP не считается попыткой
                continue
            if e.code in (401, 403):
                raise RuntimeError("STRATZ отклонил токен: " + last)
            time.sleep(10 if e.code == 429 else 3 * (attempt + 1))
        except RuntimeError:
            raise
        except Exception as e:
            last = repr(e)
            time.sleep(3 * (attempt + 1))
    raise RuntimeError("STRATZ не ответил: " + str(last))


TYPE_REF = "name kind ofType { name kind ofType { name kind ofType { name kind } } }"


def type_name(t):
    parts = []
    while t:
        parts.append(t.get("name") or t.get("kind"))
        t = t.get("ofType")
    return "/".join(p for p in parts if p)


def probe(token):
    """Печатает поля heroStats, их аргументы и поля возвращаемых типов, затем пробные ответы для героя 1."""
    q = "query{ __type(name:\"HeroStatsQuery\"){ fields{ name args{ name type{ %s } } type{ %s } } } }" % (TYPE_REF, TYPE_REF)
    t = gql(token, q).get("__type") or {}
    wanted = {"winWeek", "winDay", "winMonth", "stats", "itemFullPurchase", "itemStartingPurchase", "itemBootPurchase",
              "itemNeutral", "heroVsHeroMatchup", "position", "laneOutcome", "guide"}
    leaf_types = set()
    for f in t.get("fields") or []:
        if f["name"] not in wanted:
            continue
        args = ", ".join(f"{a['name']}:{type_name(a['type'])}" for a in f["args"])
        print(f"heroStats.{f['name']}({args}) -> {type_name(f['type'])}")
        tt = f["type"]
        while tt.get("ofType"):
            tt = tt["ofType"]
        if tt.get("name"):
            leaf_types.add(tt["name"])
    print("все поля heroStats:", ", ".join(sorted(x["name"] for x in t.get("fields") or [])))
    for name in sorted(leaf_types):
        tt = gql(token, "query{ __type(name:\"%s\"){ fields{ name type{ %s } } } }" % (name, TYPE_REF)).get("__type") or {}
        print(f"  {name}: " + ", ".join(f"{x['name']}:{type_name(x['type'])}" for x in tt.get("fields") or []))
    for enum in ("RankBracketBasicEnum", "RankBracket", "MatchPlayerPositionType", "RankBracketHeroTimeDetail"):
        tt = gql(token, "query{ __type(name:\"%s\"){ enumValues{ name } } }" % enum).get("__type") or {}
        if tt:
            print(f"  enum {enum}: " + ", ".join(v["name"] for v in tt.get("enumValues") or []))
    print("--- пробные ответы (Anti-Mage, POSITION_1) ---")
    samples = {
        "itemFullPurchase": "heroStats{ itemFullPurchase(heroId:1, positionIds:[POSITION_1]){ __typename } }",
        "itemStartingPurchase": "heroStats{ itemStartingPurchase(heroId:1, positionIds:[POSITION_1]){ __typename } }",
        "winWeek": "heroStats{ winWeek(heroIds:[1], positionIds:[POSITION_1], take:2){ __typename } }",
    }
    for name, body in samples.items():
        try:
            print(name, json.dumps(gql(token, "query{ %s }" % body), ensure_ascii=False)[:400])
        except Exception as e:
            print(name, "ошибка:", str(e)[:400])


# ---------------------------------------------------------------- полный режим

BRACKETS = ["HERALD_GUARDIAN", "CRUSADER_ARCHON", "LEGEND_ANCIENT", "DIVINE_IMMORTAL"]
BRACKET_CODE = {"HERALD_GUARDIAN": "HG", "CRUSADER_ARCHON": "CA", "LEGEND_ANCIENT": "LA", "DIVINE_IMMORTAL": "DI"}
BUILD_BRACKETS = ["LEGEND_ANCIENT", "DIVINE_IMMORTAL"]   # сборки — по сильным рангам: так покупают те, кто выигрывает чаще
MIN_META_MATCHES = 40          # меньше матчей героя на позиции в группе рангов — строку в hero_meta не пишем
MIN_BUILD_MATCHES = 400        # меньше матчей на позиции (в сильных рангах) — сборку для позиции не делаем
MIN_POS_SHARE = 0.08           # позиция, где герой бывает реже 8% своих игр, — не его позиция
CORE_SHARE = 0.15              # «метовый» предмет: его покупают минимум в 15% игр героя на позиции
START_SHARE = 0.30             # стартовый предмет: берут минимум в 30% игр
PRIOR = 60                     # сглаживание процента побед редких предметов к среднему героя
EARLY_END, MID_END = 14, 26    # минуты: до 14 — ранняя игра, до 26 — средняя, дальше — поздняя
SIZES = {"start": 6, "early": 3, "mid": 3, "late": 3}
CONSUMABLES = {
    "item_tango", "item_tango_single", "item_flask", "item_clarity", "item_faerie_fire", "item_enchanted_mango",
    "item_ward_observer", "item_ward_sentry", "item_ward_dispenser", "item_tpscroll", "item_smoke_of_deceit", "item_dust",
    "item_blood_grenade", "item_tome_of_knowledge", "item_cheese", "item_aegis", "item_refresher_shard", "item_bottle",
    "item_famango", "item_great_famango", "item_greater_famango", "item_infused_raindrop", "item_aghanims_shard_roshan",
    "item_ultimate_scepter_roshan", "item_royal_jelly",
}
NOT_SLOT = {"item_aghanims_shard", "item_aghanims_shard_roshan", "item_moon_shard"}   # не занимают слот
SUPPORT_ONLY = {"item_ward_observer", "item_ward_sentry", "item_ward_dispenser", "item_smoke_of_deceit", "item_dust", "item_blood_grenade"}


def read_rows(name, min_fields):
    rows = []
    with open(os.path.join(RES, name), encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#"):
                parts = line.split("|")
                if len(parts) >= min_fields:
                    rows.append(parts)
    return rows


def stratz_items(token):
    """Запасной справочник предметов из STRATZ (если OpenDota недоступен): id → item_xxx, цена, компоненты."""
    for fields in ("id shortName stat{ cost } components{ componentId }", "id shortName stat{ cost }", "id shortName"):
        try:
            rows = gql(token, "query{ constants{ items{ %s } } }" % fields)["constants"]["items"] or []
            break
        except Exception as e:
            print(f"  справочник STRATZ ({fields}): {str(e)[:200]}")
            rows = []
    by_id, consts = {}, {}
    for r in rows:
        if r.get("id") is None or not r.get("shortName"):
            continue
        key = "item_" + r["shortName"]
        by_id[int(r["id"])] = key
        consts[key] = {"id": int(r["id"]), "cost": int(((r.get("stat") or {}).get("cost")) or 0) or None}
    for r in rows:
        comps = [c.get("componentId") for c in (r.get("components") or []) if c.get("componentId") is not None]
        key = by_id.get(int(r["id"])) if r.get("id") is not None else None
        if key and comps:
            consts[key]["components"] = [by_id[c][5:] for c in comps if c in by_id]
            consts[key]["created"] = True
    print(f"  справочник предметов из STRATZ: {len(by_id)}")
    return consts, by_id


def opendota_items():
    req = urllib.request.Request("https://api.opendota.com/api/constants/items", headers={"User-Agent": "ROSHIQ-data-update"})
    last = None
    for attempt in range(5):
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                items = json.load(r)
            consts = {"item_" + k: v for k, v in items.items()}
            by_id = {int(v["id"]): "item_" + k for k, v in items.items() if v.get("id") is not None}
            return consts, by_id
        except Exception as e:
            last = e
            time.sleep(5 * (attempt + 1))
    raise RuntimeError(f"нет справочника предметов OpenDota: {last}")


def all_components(key, consts, seen=None):
    seen = seen if seen is not None else set()
    for c in (consts.get(key, {}).get("components") or []):
        ck = c if c.startswith("item_") else "item_" + c
        if ck not in seen:
            seen.add(ck)
            all_components(ck, consts, seen)
    return seen


RANK_PAIRS = {"HG": "HERALD,GUARDIAN", "CA": "CRUSADER,ARCHON", "LA": "LEGEND,ANCIENT", "DI": "DIVINE,IMMORTAL"}


def fetch_meta(token):
    """{(hero, pos, bracket): (matches, wins)} за последнюю неделю — bracket: HG/CA/LA/DI/ALL; pos 1..5 (heroStats.winWeek)."""
    hero_ids = ",".join(str(i) for i in range(1, 160))
    meta = {}
    for code, ranks in RANK_PAIRS.items():
        for pos in range(1, 6):
            q = ("query{ heroStats{ winWeek(heroIds:[%s], positionIds:[POSITION_%d], bracketIds:[%s]){ week heroId matchCount winCount } } }"
                 % (hero_ids, pos, ranks))
            try:
                rows = gql(token, q)["heroStats"]["winWeek"] or []
            except Exception as e:
                print(f"  winWeek {code} поз.{pos}: ошибка {str(e)[:300]}")
                continue
            if not rows:
                print(f"  winWeek {code} поз.{pos}: пусто")
                continue
            last = max(int(r["week"]) for r in rows)
            got = 0
            for r in rows:
                if int(r["week"]) != last:
                    continue                                   # только последняя неделя — ближе к текущему патчу
                m, w = int(r.get("matchCount") or 0), int(r.get("winCount") or 0)
                for b in (code, "ALL"):
                    om, ow = meta.get((r["heroId"], pos, b), (0, 0))
                    meta[(r["heroId"], pos, b)] = (om + m, ow + w)
                got += 1
            if code == "HG" and pos == 1:
                print(f"  winWeek пример: неделя {last}, строк {got}; {json.dumps(rows[:2])[:200]}")
            time.sleep(0.4)
    return meta


def fetch_items(token, hero_id, pos):
    """Покупки и стартовые покупки одним запросом (так проверено 22.11 — сборки адекватные), сапоги — отдельно."""
    br = ",".join(BUILD_BRACKETS)
    q = ("query{ heroStats{ full: itemFullPurchase(heroId:%d, positionIds:[POSITION_%d], bracketBasicIds:[%s], matchLimit:0){ itemId instance time matchCount winCount }"
         " start: itemStartingPurchase(heroId:%d, positionIds:[POSITION_%d], bracketBasicIds:[%s]){ itemId instance wasGiven matchCount winCount } } }"
         % (hero_id, pos, br, hero_id, pos, br))
    d = gql(token, q)["heroStats"]
    boots = []
    if BOOTS_OK[0]:
        try:
            qb = ("query{ heroStats{ itemBootPurchase(heroId:%d, positionIds:[POSITION_%d], bracketBasicIds:[%s]){ itemId matchCount winCount } } }"
                  % (hero_id, pos, br))
            boots = gql(token, qb)["heroStats"]["itemBootPurchase"] or []
        except Exception as e:
            print(f"  сапоги STRATZ недоступны: {str(e)[:200]}")
            BOOTS_OK[0] = False
    return d.get("full") or [], d.get("start") or [], boots


BOOTS_OK = [True]


def make_build(pos, full, start, hero_matches, hero_wr, by_id, known, consts, boots_rows=()):
    """Сборка {phase: [keys]} по покупкам: популярные (метовые) предметы с лучшим процентом побед среди равных по времени."""
    agg = {}
    for r in full:
        if int(r.get("instance") or 0) != 0:
            continue                                   # только первая покупка предмета
        key = by_id.get(int(r["itemId"]))
        if not key or key not in known:
            continue
        n, w, t = int(r.get("matchCount") or 0), int(r.get("winCount") or 0), float(r.get("time") or 0)
        a = agg.setdefault(key, [0, 0, 0.0])
        a[0] += n; a[1] += w; a[2] += t * n
    # покупки STRATZ считает по своей выборке матчей, а не по всем матчам героя: число игр в выборке оцениваем по самому
    # частому предмету (сапоги, Magic Wand и т. п. есть почти в каждой игре)
    sample = max([a[0] for a in agg.values()] + [1])
    items = {}
    for k, (n, w, tn) in agg.items():
        if n <= 0:
            continue
        items[k] = {"n": n, "share": n / sample, "wr": (w + PRIOR * hero_wr) / (n + PRIOR), "t": tn / n}
    hero_matches = sample

    # компоненты не показываем, если собранный из них предмет покупают хотя бы в трети случаев
    dropped = set()
    for big, b in items.items():
        for comp in all_components(big, consts):
            if comp in items and b["n"] >= items[comp]["n"] / 3:
                dropped.add(comp)

    def phase_of(t):
        return "early" if t < EARLY_END else "mid" if t < MID_END else "late"

    pool = {k: v for k, v in items.items()
            if v["share"] >= CORE_SHARE and k not in CONSUMABLES and k not in dropped
            and not (known[k]["catalog"] and not consts.get(k, {}).get("created")) and k not in NOT_SLOT}
    pool = {k: v for k, v in pool.items() if (consts.get(k, {}).get("cost") is None or consts[k]["cost"] > 0) and not consts.get(k, {}).get("tier")}  # нейтралки не покупают

    # «лучше равных по времени»: процент побед предмета против среднего у популярных предметов той же стадии
    by_phase = {}
    for k, v in pool.items():
        by_phase.setdefault(phase_of(v["t"]), []).append(k)
    result = {}
    # сапоги: одна пара — самая частая «настоящая» (Boots of Speed — лишь ступень к ним). STRATZ отдаёт их и отдельно (itemBootPurchase)
    b_agg = {k: pool[k]["n"] for k in pool if known[k]["group"] == "boots"}
    for r in boots_rows or []:
        key = by_id.get(int(r.get("itemId") or 0))
        if key and key in known and known[key]["group"] == "boots":
            b_agg[key] = max(b_agg.get(key, 0), int(r.get("matchCount") or 0))
    if not b_agg:                                        # сапоги есть почти у всех — берём самые частые, даже если редкие в выборке
        b_agg = {k: v["n"] for k, v in items.items() if known[k]["group"] == "boots"}
    if len(b_agg) > 1:
        b_agg.pop("item_boots", None)
    main_boots = max(b_agg, key=b_agg.get) if b_agg else None
    for k in [k for k in pool if known[k]["group"] == "boots" and k != main_boots]:
        del pool[k]
    if main_boots:
        pool[main_boots] = {"n": b_agg[main_boots], "share": 1.0, "wr": pool.get(main_boots, {}).get("wr", hero_wr),
                            "t": min(pool.get(main_boots, {}).get("t", 6.0), EARLY_END - 1)}
    by_phase = {}
    for k, v in pool.items():
        by_phase.setdefault(phase_of(v["t"]), []).append(k)
    result = {}
    def cost(k):
        return consts.get(k, {}).get("cost") or 0
    floor = {"early": 0, "mid": 1000, "late": 2500}     # в средней и поздней игре — только серьёзные предметы
    for phase in ("early", "mid", "late"):
        keys = [k for k in by_phase.get(phase, []) if (known[k]["group"] != "boots" or k == main_boots)
                and (k == main_boots or not cost(k) or cost(k) >= floor[phase])]
        if not keys:
            continue
        mean = sum(pool[k]["wr"] * pool[k]["n"] for k in keys) / sum(pool[k]["n"] for k in keys)
        def score(k):
            v = pool[k]
            return (v["wr"] - mean) + 0.02 * min(v["share"] / CORE_SHARE, 4)    # небольшой бонус за популярность
        size = SIZES[phase]
        picks = sorted(keys, key=score, reverse=True)[:size]
        if main_boots in keys and main_boots not in picks:
            picks = picks[:size - 1] + [main_boots]          # сапоги в ранней игре — всегда
        result[phase] = sorted(picks, key=lambda k: pool[k]["t"])

    # стартовая закупка: что реально куплено до начала игры (минута ≤ 0) — по тем же данным, что и остальные стадии
    s_agg = {}
    for r in full:
        if float(r.get("time") or 0) > 1 or int(r.get("instance") or 0) != 0:
            continue
        key = by_id.get(int(r["itemId"]))
        if key and key in known:
            s_agg[key] = s_agg.get(key, 0) + int(r.get("matchCount") or 0)
    for r in ([] if s_agg else start):
        if r.get("wasGiven"):
            continue
        key = by_id.get(int(r["itemId"]))
        if key and key in known:
            s_agg[key] = s_agg.get(key, 0) + int(r.get("matchCount") or 0)
    if not s_agg:                                        # запасной путь: покупки на 0-й минуте
        for k, v in items.items():
            if v["t"] <= 0.5:
                s_agg[k] = v["n"]
    s_sample = max(list(s_agg.values()) + [1])           # тот же приём: самый частый стартовый предмет — почти в каждой игре
    starts = [k for k, n in sorted(s_agg.items(), key=lambda kv: -kv[1])
              if n / s_sample >= START_SHARE and (consts.get(k, {}).get("cost") or 0) <= 600
              and known[k]["phase"] == "start"                       # в базе ROSHIQ помечен стартовым (Tango, Branch, Circlet…)
              and not (pos <= 3 and k in SUPPORT_ONLY) and known[k]["group"] != "boots"]   # сапоги — одна пара, в ранней игре
    if pos == 5 and "item_ward_observer" in known and "item_ward_observer" not in starts:
        starts = ["item_ward_observer"] + starts                 # пятёрка всегда начинает с обсервера
    starts = starts[:SIZES["start"]]
    if starts:
        result["start"] = starts
    # один предмет — в одной стадии: купленное на старте не повторяем в ранней игре
    for phase in ("early", "mid", "late"):
        if phase in result:
            result[phase] = [k for k in result[phase] if k not in starts]
            if not result[phase]:
                del result[phase]
    return tidy(result, items, known, consts, main_boots, starts, cost, floor, phase_of)


ALTERNATIVES = [{"item_bfury", "item_desolator"}, {"item_orchid", "item_mage_slayer"}, {"item_radiance", "item_bfury"}]


def tidy(result, items, known, consts, main_boots, starts, cost, floor, phase_of):
    """Чистка сборки: без компонентов рядом с апгрейдом, без взаимоисключающих пар, 2–3 предмета в каждой стадии."""
    def picked():
        return [k for ph in ("early", "mid", "late") for k in result.get(ph, [])]
    def comps_of_picks():
        out = set()
        for k in picked():
            out |= all_components(k, consts)
        return out
    def drop_components():
        comps = comps_of_picks()
        for ph in ("early", "mid", "late"):
            if ph in result:
                result[ph] = [k for k in result[ph] if k not in comps or (k == main_boots and not any(
                    known.get(b, {}).get("group") == "boots" and b != k for b in picked()))]
    drop_components()
    for ph in ("early", "mid", "late"):             # из пары альтернатив оставляем более частую
        lst = result.get(ph, [])
        for alt in ALTERNATIVES:
            both = [k for k in picked() if k in alt]
            if len(both) > 1:
                loser = min(both, key=lambda k: items.get(k, {}).get("n", 0))
                for p2 in result:
                    if p2 != "start":
                        result[p2] = [k for k in result[p2] if k != loser]
    # добор: в каждой стадии минимум 2 предмета — самые частые покупки этой стадии (от 5% игр)
    used = set(picked()) | set(starts)
    blocked = comps_of_picks()
    for ph in ("early", "mid", "late"):
        lst = result.get(ph, [])
        if len(lst) >= 2:
            continue
        cands = sorted((k for k, v in items.items()
                        if phase_of(v["t"]) == ph and v["share"] >= 0.05 and k not in used and k not in blocked
                        and k not in CONSUMABLES and k not in NOT_SLOT and known[k]["group"] != "boots"
                        and not consts.get(k, {}).get("tier")
                        and not (known[k]["catalog"] and not consts.get(k, {}).get("created"))
                        and (not cost(k) or cost(k) >= floor[ph])),
                       key=lambda k: -items[k]["n"])
        for k in cands:
            if len(lst) >= 2:
                break
            if any(k in alt and any(o in alt for o in used if o != k) for alt in ALTERNATIVES):
                continue
            lst.append(k); used.add(k); blocked |= all_components(k, consts)
        if lst:
            result[ph] = sorted(lst, key=lambda k: items.get(k, {}).get("t", 0))
    drop_components()
    if main_boots and main_boots not in picked() and not any(known.get(k, {}).get("group") == "boots" for k in picked()):
        result.setdefault("early", []).insert(0, main_boots)
    for ph in list(result):
        if not result[ph]:
            del result[ph]
    return result


def keep_previous_stratz_builds():
    """STRATZ не дал сборок, а update_builds.py уже переписал файл сборками OpenDota (популярность у про).
    Если в репозитории лежат сборки STRATZ — возвращаем их: старые, но по позициям, лучше, чем общая популярность."""
    rel = "src/Roshiq.Data/Resources/hero_builds.txt"
    try:
        prev = subprocess.run(["git", "show", "HEAD:" + rel], capture_output=True, check=True).stdout.decode("utf-8-sig")
    except Exception as e:
        print(f"  прежний hero_builds.txt не прочитать: {e}")
        return
    if "STRATZ" not in "\n".join(prev.splitlines()[:12]):
        print("  в репозитории сборки не из STRATZ — оставляю OpenDota")
        return
    with open(os.path.join(RES, "hero_builds.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(prev)
    print("  вернул прежние сборки STRATZ из репозитория")


def full(token):
    heroes = {int(r[0]): r[1] for r in read_rows("heroes.txt", 6)}
    known = {}
    for r in read_rows("items.txt", 6):
        known[r[0]] = {"catalog": r[4] == "catalog", "group": r[4], "phase": r[3]}
    # id предметов — только из STRATZ: покупки тоже из STRATZ, и номера должны совпадать с их справочником.
    # Цены и компоненты — из OpenDota (точнее), где их нет — из STRATZ.
    s_consts, by_id = stratz_items(token)
    try:
        od_consts, od_by_id = opendota_items()
        diff = sum(1 for i, k in by_id.items() if i in od_by_id and od_by_id[i] != k)
        print(f"  id предметов: STRATZ {len(by_id)}, OpenDota {len(od_by_id)}, расходятся {diff}")
        consts = dict(s_consts)
        for k, v in od_consts.items():
            consts[k] = {**consts.get(k, {}), **v}
    except Exception as e:
        print(f"OpenDota недоступен ({str(e)[:150]}) — цены и компоненты берём из STRATZ")
        consts = s_consts
    if len(by_id) < 150:
        print("справочник предметов слишком мал — файлы не меняю")
        keep_previous_stratz_builds()
        return

    meta = fetch_meta(token)
    if len({h for (h, _, _) in meta}) < 100:
        print(f"STRATZ вернул мету только для {len({h for (h, _, _) in meta})} героев — файлы не меняю")
        keep_previous_stratz_builds()
        return
    total_all = sum(m for (h, p, b), (m, w) in meta.items() if b == "ALL")
    print(f"мета: {len(meta)} строк, матчей-героев (все ранги): {total_all}")

    # hero_meta.txt
    lines = []
    for (h, p, b), (m, w) in sorted(meta.items(), key=lambda kv: (heroes.get(kv[0][0], ""), kv[0][1], kv[0][2])):
        if h in heroes and m >= MIN_META_MATCHES:
            lines.append(f"{heroes[h]}|{p}|{b}|{m}|{w}")
    day = time.strftime("%Y-%m-%d", time.gmtime())
    header = [
        "# Мета по позициям: матчи и победы героя на каждой позиции по данным STRATZ (рейтинговые паблики последней недели).",
        "# Формат: hero|позиция|ранги|матчи|победы. Ранги: HG — Рекрут–Страж, CA — Рыцарь–Герой, LA — Легенда–Властелин, DI — Божество–Титан, ALL — все.",
        "# Файл создаётся автоматически: tools/update_meta.py, раз в неделю (.github/workflows/update-builds.yml).",
        f"# Обновлено {day}. Data provided by STRATZ.",
    ]
    with open(os.path.join(RES, "hero_meta.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(header + lines) + "\n")
    print(f"hero_meta.txt: {len(lines)} строк")
    bump_version(day)

    # сборки по позициям: только сильные ранги
    strong = {}
    for (h, p, b), (m, w) in meta.items():
        if b in ("LA", "DI"):
            sm, sw = strong.get((h, p), (0, 0))
            strong[(h, p)] = (sm + m, sw + w)
    hero_total = {}
    for (h, p), (m, w) in strong.items():
        hero_total[h] = hero_total.get(h, 0) + m
    built, calls = {}, 0
    for (h, p), (m, w) in sorted(strong.items()):
        if h not in heroes or m < MIN_BUILD_MATCHES or m < MIN_POS_SHARE * hero_total[h]:
            continue
        try:
            fullrows, startrows, bootrows = fetch_items(token, h, p)
        except Exception as e:
            print(f"  {heroes[h]} поз. {p}: ошибка {str(e)[:200]}")
            continue
        calls += 1
        time.sleep(0.35)                                  # лимит STRATZ — до 250 запросов в минуту
        try:
            b = make_build(p, fullrows, startrows, m, w / m, by_id, known, consts, bootrows)
        except Exception as e:
            print(f"  {heroes[h]} поз.{p}: сборка не получилась: {e!r}")
            continue
        if calls <= 3:
            print(f"  {heroes[h]} поз.{p}: строк покупок {len(fullrows)}, стартовых {len(startrows)}, сборка {b}")
        if "mid" in b or "late" in b:
            built[(heroes[h], p)] = b
    print(f"сборки STRATZ: {len(built)} (герой, позиция), запросов: {calls}")
    if len(built) < 150:
        print("слишком мало сборок — новые сборки STRATZ не записываю")
        keep_previous_stratz_builds()
        return
    # проверка здравого смысла: почти у каждого героя есть сапоги и Magic Wand / сапоги в ранней игре.
    # Если нет — данные перепутаны (например, id предметов не совпали), файл не трогаем.
    def sane(b):
        keys = [k for ph in ("early", "mid", "late") for k in b.get(ph, [])]
        return any(known.get(k, {}).get("group") == "boots" for k in keys)
    ok = sum(1 for b in built.values() if sane(b))
    print(f"проверка: сапоги есть в {ok} из {len(built)} сборок")
    if ok < 0.85 * len(built):
        print("сборки не прошли проверку здравого смысла — новые сборки STRATZ не записываю")
        keep_previous_stratz_builds()
        return

    # hero_builds.txt: сборки STRATZ заменяют строки для своих (герой, позиция); остальное — из прежнего файла (OpenDota)
    path = os.path.join(RES, "hero_builds.txt")
    old_lines = [l.rstrip("\n") for l in open(path, encoding="utf-8-sig")]
    keep = [l for l in old_lines if l and not l.startswith("#") and (l.split("|")[0], int(l.split("|")[1])) not in built]
    new = []
    for (hk, p), b in sorted(built.items()):
        for phase in ("start", "early", "mid", "late"):
            if b.get(phase):
                new.append(f"{hk}|{p}|{phase}|{','.join(b[phase])}")
    header = [
        "# Базовые сборки героев по ролям (справочные, без учёта противника).",
        "# Формат: hero|позиция|фаза|предметы через запятую.",
        "# Файл создаётся автоматически раз в неделю (.github/workflows/update-builds.yml).",
        "# Основной источник — STRATZ (tools/update_meta.py): рейтинговые паблики последней недели, ранги Легенда–Титан,",
        "# отдельно по каждой позиции: популярные предметы (от 15% игр) с лучшим процентом побед среди предметов той же стадии.",
        "# Где у STRATZ мало данных — сборка по самым частым покупкам из OpenDota (tools/update_builds.py).",
        f"# Обновлено {day}. Сборок STRATZ: {len(built)}. Data provided by STRATZ.",
    ]
    body = sorted(keep + new, key=lambda l: (l.split("|")[0], int(l.split("|")[1]), ["start", "early", "mid", "late"].index(l.split("|")[2])))
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(header + body) + "\n")
    print(f"hero_builds.txt: {len(new)} строк из STRATZ, {len(keep)} из прежнего файла")
    # примеры для журнала
    for hk, p in [("antimage", 1), ("juggernaut", 1), ("invoker", 2), ("axe", 3), ("rubick", 4), ("lion", 5), ("lion", 2), ("pudge", 4)]:
        if (hk, p) in built:
            print(f"  пример {hk} {p}: " + " | ".join(f"{ph}: {','.join(v)}" for ph, v in built[(hk, p)].items()))

    bump_version(day)


def bump_version(day):
    """Новая dataVersion: установленные программы заберут обновлённые файлы с GitHub (и git увидит изменение)."""
    vp = os.path.join(RES, "data_version.txt")
    v = open(vp, encoding="utf-8-sig").read().replace("\r\n", "\n").split("\n")
    stamp = day.replace("-", ".")
    cur = next((l.split("=", 1)[1] for l in v if l.startswith("dataVersion=")), "")
    if cur.startswith(stamp):
        tail = cur[len(stamp):].lstrip(".")
        stamp = f"{stamp}.{int(tail or 0) + 1}"
    v = [("dataVersion=" + stamp) if l.startswith("dataVersion=") else l for l in v]
    with open(vp, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(v))
    print("dataVersion=" + stamp)


def probe_items(token):
    """Диагностика покупок: один и тот же герой/позиция разными вариантами запроса — топ предметов по числу матчей."""
    _, by_id = stratz_items(token)
    br = ",".join(BUILD_BRACKETS)
    fields = "itemId instance time matchCount winCount"
    variants = {
        "full+start (как утром)": "full: itemFullPurchase(heroId:%d, positionIds:[POSITION_%d], bracketBasicIds:[%s], matchLimit:0){ %s } start: itemStartingPurchase(heroId:%d, positionIds:[POSITION_%d], bracketBasicIds:[%s]){ itemId matchCount }",
        "full один": "full: itemFullPurchase(heroId:%d, positionIds:[POSITION_%d], bracketBasicIds:[%s], matchLimit:0){ %s }",
        "full без matchLimit": "full: itemFullPurchase(heroId:%d, positionIds:[POSITION_%d], bracketBasicIds:[%s]){ %s }",
        "full без рангов и позиции": "full: itemFullPurchase(heroId:%d){ %s }",
    }
    for hero_id, pos, name in ((1, 1, "Anti-Mage"), (8, 1, "Juggernaut"), (31, 5, "Lich")):
        for label, body in variants.items():
            if label == "full+start (как утром)":
                q = body % (hero_id, pos, br, fields, hero_id, pos, br)
            elif label == "full без рангов и позиции":
                q = body % (hero_id, fields)
            else:
                q = body % (hero_id, pos, br, fields)
            try:
                rows = gql(token, "query{ heroStats{ %s } }" % q)["heroStats"]["full"] or []
            except Exception as e:
                print(f"{name} поз.{pos} [{label}]: ошибка {str(e)[:200]}")
                continue
            agg = {}
            for r in rows:
                if int(r.get("instance") or 0) != 0:
                    continue
                k = by_id.get(int(r["itemId"]), str(r["itemId"]))
                agg[k] = agg.get(k, 0) + int(r.get("matchCount") or 0)
            top = sorted(agg.items(), key=lambda x: -x[1])[:12]
            print(f"{name} поз.{pos} [{label}]: строк {len(rows)}; топ: " + ", ".join(f"{k.replace('item_', '')}:{n}" for k, n in top))
            time.sleep(0.5)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--probe", action="store_true")
    ap.add_argument("--probe-items", action="store_true")
    args = ap.parse_args()
    token = (os.environ.get("STRATZ_TOKEN") or "").strip()
    if not token:
        print("STRATZ_TOKEN не задан — данные STRATZ пропущены, работает старый источник")
        return
    if args.probe:
        probe(token)
        return
    if args.probe_items:
        probe_items(token)
        return
    full(token)


if __name__ == "__main__":
    main()
