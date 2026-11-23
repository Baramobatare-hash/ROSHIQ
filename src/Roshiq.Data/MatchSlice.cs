using System.Text.Json;
using System.Text.Json.Nodes;

namespace Roshiq.Data;

/// <summary>
/// 0.26.1: урезанная копия матча OpenDota для сервера ИИ-объяснения — только поля, из которых сервер считает факты
/// (линия до 10:00 и соперники, время мёртвым, участие в драках, кто бил и убивал, предметы). Без ников и account_id.
/// Сервер не может сам брать матчи из OpenDota (общие адреса Cloudflare упираются в лимит), поэтому копию шлёт программа.
/// </summary>
public static class MatchSlice
{
    private static readonly string[] Common = { "player_slot", "hero_id", "net_worth", "last_hits", "kills", "deaths", "assists", "lane", "lane_role", "is_roaming",
        "item_0", "item_1", "item_2", "item_3", "item_4", "item_5" };
    private static readonly string[] Series = { "lh_t", "dn_t", "networth_t", "gold_t" };
    private static readonly string[] Mine = { "rank_tier", "life_state", "teamfight_participation", "damage_taken", "killed_by",
        "obs_placed", "sen_placed", "camps_stacked", "stuns", "tower_damage" };

    /// <summary>Копия матча и слот игрока accountId (null, если его нет в матче).</summary>
    public static (JsonObject Slice, int? Slot) Build(JsonElement root, long accountId)
    {
        var players = new JsonArray();
        int? slot = null;
        if (root.TryGetProperty("players", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ps.EnumerateArray())
            {
                bool me = p.TryGetProperty("account_id", out var acc) && acc.ValueKind == JsonValueKind.Number && acc.TryGetInt64(out var a) && a == accountId;
                var s = new JsonObject();
                foreach (var k in Common) Copy(p, s, k);
                foreach (var k in Series)
                    if (p.TryGetProperty(k, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        s[k] = new JsonArray(arr.EnumerateArray().Take(11).Select(x => (JsonNode?)JsonNode.Parse(x.GetRawText())).ToArray());
                s["kills_log"] = Log(p, "kills_log", e => Time(e) <= 600, withKey: true);
                if (me)
                {
                    if (p.TryGetProperty("player_slot", out var ps2) && ps2.TryGetInt32(out var sl)) slot = sl;
                    foreach (var k in Mine) Copy(p, s, k);
                    s["buyback_log"] = Log(p, "buyback_log", _ => true, withKey: false);
                    s["purchase_log"] = Log(p, "purchase_log", e => Time(e) > 0, withKey: true);
                }
                players.Add(s);
            }
        }
        var slice = new JsonObject { ["players"] = players };
        Copy(root, slice, "duration");
        Copy(root, slice, "avg_rank_tier");
        Copy(root, slice, "radiant_win");
        return (slice, slot);
    }

    private static void Copy(JsonElement from, JsonObject to, string key)
    {
        if (from.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null) to[key] = JsonNode.Parse(v.GetRawText());
    }

    private static int Time(JsonElement e) => e.TryGetProperty("time", out var t) && t.TryGetInt32(out var v) ? v : int.MinValue;

    private static JsonArray Log(JsonElement p, string key, Func<JsonElement, bool> keep, bool withKey)
    {
        var list = new JsonArray();
        if (!p.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !keep(e)) continue;
            var o = new JsonObject { ["time"] = Time(e) };
            if (withKey && e.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String) o["key"] = k.GetString();
            list.Add(o);
        }
        return list;
    }
}
