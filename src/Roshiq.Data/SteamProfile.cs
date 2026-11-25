using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Models;

namespace Roshiq.Data;

/// <summary>Аккаунт, который когда-либо входил в Steam на этом компьютере (из config\loginusers.vdf).</summary>
public sealed record LocalSteamUser(string SteamId64, string? AccountName, string? PersonaName, bool MostRecent, long Timestamp)
{
    public long? AccountId => SteamIds.ToAccountId(SteamId64);
    public string Display => !string.IsNullOrWhiteSpace(PersonaName) ? PersonaName! : AccountName ?? SteamId64;
}

/// <summary>Разбор loginusers.vdf — обычного текстового файла Steam (формат KeyValues). Пароли в нём не хранятся.</summary>
public static class SteamLoginUsers
{
    public static IReadOnlyList<LocalSteamUser> Parse(string vdf)
    {
        var result = new List<LocalSteamUser>();
        var tokens = Tokenize(vdf);
        int i = 0;
        // "users" { "7656…" { "Key" "Value" … } … }
        while (i < tokens.Count && tokens[i] != "{") i++;
        i++;
        while (i < tokens.Count && tokens[i] != "}")
        {
            var id = tokens[i++];
            if (i >= tokens.Count || tokens[i] != "{") break;
            i++;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (i + 1 < tokens.Count && tokens[i] != "}")
            {
                var key = tokens[i++];
                if (tokens[i] == "{") { i = SkipBlock(tokens, i); continue; }
                fields[key] = tokens[i++];
            }
            i++; // }
            if (!Regex.IsMatch(id, @"^\d{17}$")) continue;
            fields.TryGetValue("AccountName", out var account);
            fields.TryGetValue("PersonaName", out var persona);
            bool recent = fields.TryGetValue("MostRecent", out var mr) && mr == "1";
            long ts = fields.TryGetValue("Timestamp", out var t) && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
            result.Add(new LocalSteamUser(id, account, persona, recent, ts));
        }
        return result.OrderByDescending(u => u.MostRecent).ThenByDescending(u => u.Timestamp).ToList();
    }

    private static int SkipBlock(List<string> tokens, int i)
    {
        int depth = 0;
        for (; i < tokens.Count; i++)
        {
            if (tokens[i] == "{") depth++;
            else if (tokens[i] == "}" && --depth == 0) return i + 1;
        }
        return i;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c is '{' or '}') { tokens.Add(c.ToString()); i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); i += 2; continue; }
                    sb.Append(text[i++]);
                }
                i++;
                tokens.Add(sb.ToString());
                continue;
            }
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
            tokens.Add(text[start..i]);
        }
        return tokens;
    }
}

/// <summary>
/// Вход через Steam (OpenID 2.0) — официальная страница steamcommunity.com. Программа получает только Steam ID:
/// ни пароля, ни доступа к инвентарю или покупкам.
/// </summary>
public static class SteamOpenId
{
    public const string Endpoint = "https://steamcommunity.com/openid/login";
    private const string Ns = "http://specs.openid.net/auth/2.0";
    private const string Select = "http://specs.openid.net/auth/2.0/identifier_select";
    private static readonly Regex ClaimedId = new(@"^https://steamcommunity\.com/openid/id/(\d{17})$", RegexOptions.CultureInvariant);

    public static string LoginUrl(string returnTo, string realm) =>
        Endpoint + "?" + string.Join("&", new[]
        {
            ("openid.ns", Ns), ("openid.mode", "checkid_setup"), ("openid.return_to", returnTo),
            ("openid.realm", realm), ("openid.identity", Select), ("openid.claimed_id", Select)
        }.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2)));

    /// <summary>Разобрать строку запроса ответа Steam.</summary>
    public static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            var k = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            var v = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            d[k] = v;
        }
        return d;
    }

    /// <summary>Steam ID64 из ответа (ещё не проверенного) или null.</summary>
    public static string? SteamIdFrom(IReadOnlyDictionary<string, string> response)
    {
        if (!response.TryGetValue("openid.mode", out var mode) || mode != "id_res") return null;
        if (!response.TryGetValue("openid.claimed_id", out var claimed)) return null;
        var m = ClaimedId.Match(claimed);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Проверить ответ у самого Steam (check_authentication): подделанный адрес возврата не пройдёт.
    /// Дополнительно сверяет, что ответ пришёл на наш адрес возврата.
    /// </summary>
    public static async Task<string?> VerifyAsync(IReadOnlyDictionary<string, string> response, string expectedReturnTo, HttpClient http, CancellationToken ct)
    {
        var id = SteamIdFrom(response);
        if (id == null) return null;
        if (!response.TryGetValue("openid.return_to", out var rt) || !rt.StartsWith(expectedReturnTo, StringComparison.Ordinal)) return null;
        if (!response.TryGetValue("openid.op_endpoint", out var op) || op != Endpoint) return null;
        var form = response.Where(kv => kv.Key.StartsWith("openid.", StringComparison.Ordinal))
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Key == "openid.mode" ? "check_authentication" : kv.Value)).ToList();
        using var content = new FormUrlEncodedContent(form);
        using var reply = await http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        var text = await reply.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        NetworkLog.Record("POST", Endpoint, "проверка входа Steam: " + (text.Contains("is_valid:true") ? "OK" : "отказ"));
        return text.Contains("is_valid:true", StringComparison.Ordinal) ? id : null;
    }
}

/// <summary>
/// Свежие ник и аватар прямо из открытого профиля Steam (steamcommunity.com/profiles/…?xml=1, без ключа).
/// OpenDota обновляет аватар только при своей переиндексации, поэтому после смены аватарки в Steam он долго старый.
/// </summary>
public static class SteamCommunity
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Regex Avatar = new(@"<avatarFull><!\[CDATA\[(https://[^\]]+)\]\]></avatarFull>", RegexOptions.CultureInvariant);
    private static readonly Regex Name = new(@"<steamID><!\[CDATA\[(.*?)\]\]></steamID>", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>(ник, ссылка на аватар) или null, если профиль закрыт или Steam не ответил.</summary>
    public static async Task<(string? Name, string? AvatarUrl)?> GetAsync(long accountId, CancellationToken ct)
    {
        long id64 = accountId + 76561197960265728L;
        var url = $"https://steamcommunity.com/profiles/{id64}/?xml=1";
        try
        {
            var xml = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            var avatar = Avatar.Match(xml) is { Success: true } a ? a.Groups[1].Value : null;
            var name = Name.Match(xml) is { Success: true } n ? n.Groups[1].Value.Trim() : null;
            NetworkLog.Record("GET", url, avatar != null ? "OK" : "нет аватара");
            return avatar == null && name == null ? null : (string.IsNullOrEmpty(name) ? null : name, avatar);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NetworkLog.Record("GET", url, "ошибка: " + ex.Message);
            return null;
        }
    }

    /// <summary>Разобрать ответ Steam (для тестов).</summary>
    public static (string? Name, string? AvatarUrl) Parse(string xml) =>
        (Name.Match(xml) is { Success: true } n ? n.Groups[1].Value.Trim() : null,
         Avatar.Match(xml) is { Success: true } a ? a.Groups[1].Value : null);
}

/// <summary>Профиль игрока в OpenDota.</summary>
public sealed record PlayerProfile(long AccountId, string? Name, string? AvatarUrl, int? RankTier, int? LeaderboardRank);

public static class Ranks
{
    private static readonly string[] Ru = { "", "Рекрут", "Страж", "Рыцарь", "Герой", "Легенда", "Властелин", "Божество", "Титан" };
    private static readonly string[] En = { "", "Herald", "Guardian", "Crusader", "Archon", "Legend", "Ancient", "Divine", "Immortal" };

    /// <summary>Медаль из rank_tier OpenDota (десятки — медаль, единицы — звёзды): 54 → «Легенда 4».</summary>
    public static string? Name(int? rankTier, int? leaderboard, Language lang)
    {
        if (rankTier is not int t || t < 10) return null;
        int medal = Math.Clamp(t / 10, 1, 8), stars = t % 10;
        var name = (lang == Language.English ? En : Ru)[medal];
        if (medal == 8) return leaderboard is int lb && lb > 0 ? $"{name} #{lb}" : name;
        return stars > 0 ? $"{name} {stars}" : name;
    }
}

/// <summary>Примерный рейтинг по соперникам: вилка медалей (rank_tier) и сколько матчей в основе.</summary>
public sealed record RankEstimate(int Low, int High, int Matches, bool SoloOnly, double Mean = double.NaN);

/// <summary>
/// Оценка рейтинга до калибровки (или в дополнение к медали): игра подбирает матчи по скрытому рейтингу,
/// поэтому средняя медаль игроков в рейтинговых матчах (average_rank OpenDota) показывает примерный уровень.
/// Пати сдвигают подбор, поэтому при достатке одиночных матчей считаем только их.
/// </summary>
public static class RankEstimator
{
    public const int MinMatches = 5;

    /// <summary>rank_tier → номер ступени: Рекрут 1 = 0 … Божество 5 = 34, Титан = 35.</summary>
    public static int ToStep(int rankTier)
    {
        int medal = Math.Clamp(rankTier / 10, 1, 8), stars = Math.Clamp(rankTier % 10, 1, 5);
        return medal == 8 ? 35 : (medal - 1) * 5 + (stars - 1);
    }

    public static int FromStep(int step)
    {
        step = Math.Clamp(step, 0, 35);
        return step == 35 ? 80 : (step / 5 + 1) * 10 + step % 5 + 1;
    }

    /// <param name="matches">average_rank и размер пати рейтинговых матчей, новые первыми.</param>
    public static RankEstimate? Estimate(IEnumerable<(int AverageRank, int PartySize)> matches, int take = 20, int skip = 0)
    {
        var all = matches.Where(m => m.AverageRank >= 10).Skip(skip).Take(take).ToList();
        var solo = all.Where(m => m.PartySize <= 1).ToList();
        bool soloOnly = solo.Count >= MinMatches;
        var used = soloOnly ? solo : all;
        if (used.Count < MinMatches) return null;
        var steps = used.Select(m => ToStep(m.AverageRank)).OrderBy(x => x).ToList();
        int Pct(double p) => steps[(int)Math.Round(p * (steps.Count - 1))];
        // средняя ступень (с дробной частью) — по ней видно, сколько осталось до следующего ранга
        double mean = steps.Average();
        return new RankEstimate(FromStep(Pct(0.25)), FromStep(Pct(0.75)), used.Count, soloOnly, mean);
    }

    /// <summary>
    /// История по матчам: оценка по окнам из <paramref name="window"/> рейтинговых матчей со сдвигом <paramref name="step"/>,
    /// от старых к новым. Хранить ничего не нужно — график виден сразу после первой загрузки.
    /// </summary>
    public static IReadOnlyList<RankEstimate> History(IReadOnlyList<(int AverageRank, int PartySize)> matches, int window = 20, int step = 10)
    {
        int n = matches.Count(m => m.AverageRank >= 10);
        var list = new List<RankEstimate>();
        for (int skip = 0; skip + window <= n; skip += step)
            if (Estimate(matches, window, skip) is { } e) list.Add(e);
        list.Reverse();
        return list;
    }

    /// <summary>Упаковка вилки в одно число для настроек: 25 и 31 → 2531.</summary>
    public static int Pack(RankEstimate e) => e.Low * 100 + e.High;
    public static (int Low, int High) Unpack(int v) => (v / 100, v % 100);

    /// <summary>
    /// Где вы внутри ранга: текущий ранг (rank_tier), следующий и сколько пройдено до него (0…1) по средней ступени.
    /// У Титана следующего нет — прогресс 1.
    /// </summary>
    public static (int Current, int? Next, double Progress) Progress(double meanStep)
    {
        meanStep = Math.Clamp(meanStep, 0, 35);
        int cur = (int)Math.Floor(meanStep);
        if (cur >= 35) return (80, null, 1);
        return (FromStep(cur), FromStep(cur + 1), meanStep - cur);
    }

    /// <summary>Середина вилки в ступенях — для сравнения «было / стало».</summary>
    public static double Mid(int low, int high) => (ToStep(low) + ToStep(high)) / 2.0;

    /// <summary>Сдвиг между прошлой и нынешней вилкой: +1 — выше, −1 — ниже, 0 — на месте (меньше ступени разницы).</summary>
    public static int Direction(int prevLow, int prevHigh, int low, int high)
    {
        double d = Mid(low, high) - Mid(prevLow, prevHigh);
        return d >= 1 ? 1 : d <= -1 ? -1 : 0;
    }

    /// <summary>
    /// Герои на калибровку: от 10 игр, сыграны за последний год, ранжируем по нижней границе доверительного
    /// интервала Уилсона — 60% на 50 играх надёжнее 80% на 10. Берём только тех, у кого граница от 45%.
    /// </summary>
    public static IReadOnlyList<PlayerHeroStat> CalibrationHeroes(IEnumerable<PlayerHeroStat> stats, DateTime nowUtc, int take = 3)
    {
        long since = new DateTimeOffset(nowUtc.AddDays(-365)).ToUnixTimeSeconds();
        return stats
            .Where(s => s.Games >= 10 && s.LastPlayed >= since)
            .Select(s => (Stat: s, Score: Wilson(s.Wins, s.Games)))
            .Where(x => x.Score >= 0.45)
            .OrderByDescending(x => x.Score)
            .Take(take)
            .Select(x => x.Stat)
            .ToList();
    }

    /// <summary>
    /// Кого лучше не брать на калибровку: от 10 игр за последний год, и даже верхняя граница интервала Уилсона ниже 45% —
    /// то есть проигрыши на герое не случайность.
    /// </summary>
    public static IReadOnlyList<PlayerHeroStat> AvoidHeroes(IEnumerable<PlayerHeroStat> stats, DateTime nowUtc, int take = 2)
    {
        long since = new DateTimeOffset(nowUtc.AddDays(-365)).ToUnixTimeSeconds();
        return stats
            .Where(s => s.Games >= 10 && s.LastPlayed >= since)
            .Select(s => (Stat: s, Upper: WilsonUpper(s.Wins, s.Games)))
            .Where(x => x.Upper < 0.45)
            .OrderBy(x => x.Upper)
            .Take(take)
            .Select(x => x.Stat)
            .ToList();
    }

    /// <summary>Группа роли по типичной позиции героя: 1, 2, 3 или 4 (саппорты 4 и 5 вместе).</summary>
    public static int RoleGroup(Position p) => p is Position.SoftSupport or Position.HardSupport ? 4 : (int)p;

    /// <summary>
    /// Лучшая роль по вашим героям: каждого героя относим к его самой типичной позиции, складываем игры и победы
    /// за последний год. Нужны хотя бы две роли от 20 игр; лучшая — по нижней границе Уилсона и не ниже 45%.
    /// </summary>
    public static (int Group, int Games, double WinRate)? BestRole(IEnumerable<PlayerHeroStat> stats, Func<int, Position?> mainPosition, DateTime nowUtc)
    {
        long since = new DateTimeOffset(nowUtc.AddDays(-365)).ToUnixTimeSeconds();
        var groups = stats
            .Where(s => s.Games > 0 && s.LastPlayed >= since && mainPosition(s.HeroId) is not null)
            .GroupBy(s => RoleGroup(mainPosition(s.HeroId)!.Value))
            .Select(g => (Group: g.Key, Games: g.Sum(x => x.Games), Wins: g.Sum(x => x.Wins)))
            .Where(g => g.Games >= 20)
            .ToList();
        if (groups.Count < 2) return null;
        var best = groups.OrderByDescending(g => Wilson(g.Wins, g.Games)).First();
        if (Wilson(best.Wins, best.Games) < 0.45) return null;
        return (best.Group, best.Games, (double)best.Wins / best.Games);
    }

    public static double WilsonUpper(int wins, int games, double z = 1.64)
    {
        if (games <= 0) return 1;
        double p = (double)wins / games, z2 = z * z;
        return (p + z2 / (2 * games) + z * Math.Sqrt((p * (1 - p) + z2 / (4 * games)) / games)) / (1 + z2 / games);
    }

    public static double Wilson(int wins, int games, double z = 1.64)
    {
        if (games <= 0) return 0;
        double p = (double)wins / games, z2 = z * z;
        return (p + z2 / (2 * games) - z * Math.Sqrt((p * (1 - p) + z2 / (4 * games)) / games)) / (1 + z2 / games);
    }

    /// <summary>«Страж 5 – Рыцарь 1» или одна медаль, если границы совпали.</summary>
    public static string Range(RankEstimate e, Language lang)
    {
        var lo = Ranks.Name(e.Low, null, lang) ?? "?";
        var hi = Ranks.Name(e.High, null, lang) ?? "?";
        return e.Low == e.High ? lo : lo + " – " + hi;
    }
}

/// <summary>Ваша статистика на герое (все матчи из OpenDota).</summary>
public sealed record PlayerHeroStat(int HeroId, int Games, int Wins, long LastPlayed, int AgainstGames = 0, int AgainstWins = 0)
{
    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
    /// <summary>Ваш винрейт, когда этот герой был у противника.</summary>
    public double AgainstWinRate => AgainstGames == 0 ? 0 : (double)AgainstWins / AgainstGames;
}

/// <summary>Кэш вашей статистики по героям (cache\player_heroes.json).</summary>
public sealed class PlayerHeroStatsCache
{
    private readonly string _path;
    private Dictionary<int, PlayerHeroStat> _stats = new();

    public PlayerHeroStatsCache(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize<StatsFile>(File.ReadAllText(path));
                if (file?.Heroes != null) { _stats = file.Heroes.ToDictionary(h => h.HeroId); AccountId = file.AccountId; UpdatedUtc = file.UpdatedUtc; }
            }
        }
        catch (Exception ex) { Log.Warn("Кэш статистики героев повреждён: " + ex.Message); }
    }

    public long? AccountId { get; private set; }
    public DateTime? UpdatedUtc { get; private set; }
    public int Count => _stats.Count;
    public IReadOnlyDictionary<int, PlayerHeroStat> All => _stats;

    public PlayerHeroStat? Get(int heroId) => _stats.TryGetValue(heroId, out var s) ? s : null;

    public void Replace(long accountId, IEnumerable<PlayerHeroStat> stats, DateTime utc)
    {
        _stats = stats.Where(s => s.Games > 0 || s.AgainstGames > 0).ToDictionary(s => s.HeroId);
        AccountId = accountId;
        UpdatedUtc = utc;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new StatsFile { AccountId = accountId, UpdatedUtc = utc, Heroes = _stats.Values.ToList() }));
        }
        catch (Exception ex) { Log.Warn("Не удалось сохранить статистику героев: " + ex.Message); }
    }

    public void Clear()
    {
        _stats = new();
        AccountId = null;
        UpdatedUtc = null;
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    /// <summary>Герои противника, против которых вы чаще проигрываете (не меньше minGames игр), худшие первыми.</summary>
    public IReadOnlyList<PlayerHeroStat> WorstMatchups(int count = 6, int minGames = 10, double below = 0.45) =>
        _stats.Values.Where(s => s.AgainstGames >= minGames && s.AgainstWinRate < below)
            .OrderBy(s => s.AgainstWinRate).ThenByDescending(s => s.AgainstGames).Take(count).ToList();

    /// <summary>Герои для пула: сыграно не меньше minGames, отсортированы по «играю часто и выигрываю».</summary>
    public IReadOnlyList<int> SuggestPool(int count = 12, int minGames = 10) =>
        _stats.Values.Where(s => s.Games >= minGames)
            .OrderByDescending(s => s.Games * (0.5 + s.WinRate))
            .Take(count).Select(s => s.HeroId).ToList();

    private sealed class StatsFile
    {
        public long? AccountId { get; set; }
        public DateTime? UpdatedUtc { get; set; }
        public List<PlayerHeroStat>? Heroes { get; set; }
    }
}
