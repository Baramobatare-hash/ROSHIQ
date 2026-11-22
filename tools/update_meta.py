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


def opendota_items():
    req = urllib.request.Request("https://api.opendota.com/api/constants/items", headers={"User-Agent": "ROSHIQ-data-update"})
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


def fetch_meta(token):
    """{(hero, pos, bracket): (matches, wins)} — bracket: HG/CA/LA/DI/ALL; pos 1..5."""
    fields = "heroId position bracketBasicIds matchCount winCount"
    rows = []
    # по одной группе рангов за запрос: так bracketBasicIds в ответе может быть пустым — подставляем сами
    for br in BRACKETS:
        variants = [
            "query{ heroStats{ stats(groupByPosition:true, groupByBracket:true, bracketBasicIds:[%s]){ %s } } }" % (br, fields),
            "query{ heroStats{ stats(heroIds:[%s], groupByPosition:true, bracketBasicIds:[%s]){ %s } } }"
            % (",".join(str(i) for i in range(1, 160)), br, fields),
        ]
        got = []
        for q in variants:
            try:
                got = gql(token, q)["heroStats"]["stats"] or []
            except Exception as e:
                print(f"  stats {br}: ошибка {str(e)[:300]}")
                got = []
            print(f"  stats {br}: строк {len(got)}; пример: {json.dumps(got[:2], ensure_ascii=False)[:300]}")
            if len({r.get('heroId') for r in got}) >= 100:
                break
        for r in got:
            if not r.get("bracketBasicIds"):
                r["bracketBasicIds"] = br
        rows += got
        time.sleep(0.5)
    meta = {}
    for r in rows:
        pos = r.get("position") or ""
        if not pos.startswith("POSITION_") or r.get("bracketBasicIds") not in BRACKET_CODE:
            continue
        p = int(pos[-1])
        m, w = int(r.get("matchCount") or 0), int(r.get("winCount") or 0)
        for b in (BRACKET_CODE[r["bracketBasicIds"]], "ALL"):
            om, ow = meta.get((r["heroId"], p, b), (0, 0))
            meta[(r["heroId"], p, b)] = (om + m, ow + w)
    return meta


def fetch_items(token, hero_id, pos):
    br = ",".join(BUILD_BRACKETS)
    q = ("query{ heroStats{ full: itemFullPurchase(heroId:%d, positionIds:[POSITION_%d], bracketBasicIds:[%s]){ itemId instance time matchCount winCount }"
         " start: itemStartingPurchase(heroId:%d, positionIds:[POSITION_%d], bracketBasicIds:[%s]){ itemId instance wasGiven matchCount winCount } } }"
         % (hero_id, pos, br, hero_id, pos, br))
    d = gql(token, q)["heroStats"]
    return d.get("full") or [], d.get("start") or []


def make_build(pos, full, start, hero_matches, hero_wr, by_id, known, consts):
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
    items = {}
    for k, (n, w, tn) in agg.items():
        if n <= 0:
            continue
        items[k] = {"n": n, "share": n / hero_matches, "wr": (w + PRIOR * hero_wr) / (n + PRIOR), "t": tn / n}

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
            and not (known[k]["catalog"] and not consts.get(k, {}).get("created"))}
    pool = {k: v for k, v in pool.items() if consts.get(k, {}).get("cost", 1) and not consts.get(k, {}).get("tier")}  # нейтралки не покупают

    # «лучше равных по времени»: процент побед предмета против среднего у популярных предметов той же стадии
    by_phase = {}
    for k, v in pool.items():
        by_phase.setdefault(phase_of(v["t"]), []).append(k)
    result = {}
    boots = [k for k in pool if known[k]["group"] == "boots"]
    main_boots = max(boots, key=lambda k: pool[k]["n"]) if boots else None
    for phase in ("early", "mid", "late"):
        keys = [k for k in by_phase.get(phase, []) if known[k]["group"] != "boots" or k == main_boots]
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

    # стартовая закупка
    s_agg = {}
    for r in start:
        if r.get("wasGiven"):
            continue
        key = by_id.get(int(r["itemId"]))
        if key and key in known:
            s_agg[key] = s_agg.get(key, 0) + int(r.get("matchCount") or 0)
    if not s_agg:                                        # запасной путь: покупки на 0-й минуте
        for k, v in items.items():
            if v["t"] <= 0.5:
                s_agg[k] = v["n"]
    starts = [k for k, n in sorted(s_agg.items(), key=lambda kv: -kv[1])
              if n / hero_matches >= START_SHARE and consts.get(k, {}).get("cost", 0) <= 600
              and not (pos <= 3 and k in SUPPORT_ONLY)]
    if starts:
        result["start"] = starts[:SIZES["start"]]
    return result


def full(token):
    heroes = {int(r[0]): r[1] for r in read_rows("heroes.txt", 6)}
    known = {}
    for r in read_rows("items.txt", 6):
        known[r[0]] = {"catalog": r[4] == "catalog", "group": r[4], "phase": r[3]}
    consts, by_id = opendota_items()

    meta = fetch_meta(token)
    if len({h for (h, _, _) in meta}) < 100:
        print(f"STRATZ вернул мету только для {len({h for (h, _, _) in meta})} героев — файлы не меняю")
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
            fullrows, startrows = fetch_items(token, h, p)
        except Exception as e:
            print(f"  {heroes[h]} поз. {p}: ошибка {str(e)[:200]}")
            continue
        calls += 1
        time.sleep(0.35)                                  # лимит STRATZ — до 250 запросов в минуту
        b = make_build(p, fullrows, startrows, m, w / m, by_id, known, consts)
        if "mid" in b or "late" in b:
            built[(heroes[h], p)] = b
    print(f"сборки STRATZ: {len(built)} (герой, позиция), запросов: {calls}")
    if len(built) < 150:
        print("слишком мало сборок — hero_builds.txt не меняю")
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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--probe", action="store_true")
    args = ap.parse_args()
    token = (os.environ.get("STRATZ_TOKEN") or "").strip()
    if not token:
        print("STRATZ_TOKEN не задан — данные STRATZ пропущены, работает старый источник")
        return
    if args.probe:
        probe(token)
        return
    full(token)


if __name__ == "__main__":
    main()
