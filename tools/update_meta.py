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


def gql(token, query, variables=None, retries=5):
    body = json.dumps({"query": query, "variables": variables or {}}).encode()
    last = None
    for attempt in range(retries):
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
            last = f"HTTP {e.code}: {e.read()[:300]!r}"
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
    sys.exit("полный режим ещё не включён: сначала --probe")


if __name__ == "__main__":
    main()
