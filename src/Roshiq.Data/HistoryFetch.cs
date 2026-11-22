using System.Net;

namespace Roshiq.Data;

/// <summary>Чем закончилась загрузка истории из OpenDota — чтобы показать человеку понятную причину, а не текст исключения.</summary>
public enum HistoryFetchOutcome
{
    /// <summary>Матчи получены.</summary>
    Ok,
    /// <summary>OpenDota не знает такой аккаунт: неверный Steam ID или профиль ни разу не попадал в OpenDota.</summary>
    NotFound,
    /// <summary>Профиль есть, но матчей нет даже после обновления — почти всегда закрыта история матчей в Dota 2.</summary>
    Hidden,
    /// <summary>OpenDota ограничил число запросов (429).</summary>
    RateLimited,
    /// <summary>Нет связи с api.opendota.com (сеть, таймаут, блокировка).</summary>
    Offline,
    /// <summary>OpenDota ответил ошибкой сервера.</summary>
    ServiceDown,
}

public sealed record HistoryFetchResult(IReadOnlyList<RecentMatchRow> Rows, HistoryFetchOutcome Outcome, bool Refreshed);

/// <summary>
/// Загрузка последних матчей игрока: если OpenDota отдаёт пустой список, просим его обновить профиль
/// (POST /players/{id}/refresh) и несколько раз пробуем снова; если матчей так и нет — выясняем причину.
/// </summary>
public static class HistoryFetch
{
    /// <summary>Паузы между повторными попытками после запроса на обновление профиля.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays = new[] { TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(20) };

    public static Task<HistoryFetchResult> RunAsync(OpenDotaClient client, long accountId, IProgress<int>? waitedSec, CancellationToken ct) =>
        RunAsync(
            c => client.GetRecentMatchesAsync(accountId, c),
            c => client.RefreshPlayerAsync(accountId, c),
            async c => await client.GetPlayerProfileAsync(accountId, c).ConfigureAwait(false) != null,
            DefaultRetryDelays, waitedSec, ct);

    /// <param name="recent">Последние матчи.</param>
    /// <param name="refresh">Запрос на обновление профиля в OpenDota.</param>
    /// <param name="profileKnown">Знает ли OpenDota этот аккаунт.</param>
    /// <param name="retryDelays">Паузы перед повторами после обновления; пустой список — без повторов.</param>
    /// <param name="waitedSec">Сколько секунд уже ждём (для строки состояния).</param>
    public static async Task<HistoryFetchResult> RunAsync(
        Func<CancellationToken, Task<IReadOnlyList<RecentMatchRow>>> recent,
        Func<CancellationToken, Task<bool>> refresh,
        Func<CancellationToken, Task<bool>> profileKnown,
        IReadOnlyList<TimeSpan> retryDelays,
        IProgress<int>? waitedSec,
        CancellationToken ct)
    {
        IReadOnlyList<RecentMatchRow> rows;
        try { rows = await recent(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new HistoryFetchResult(Array.Empty<RecentMatchRow>(), Classify(ex), false);
        }
        if (rows.Count > 0) return new HistoryFetchResult(rows, HistoryFetchOutcome.Ok, false);

        // Пусто: просим OpenDota подтянуть историю из Steam и ждём.
        bool asked;
        try { asked = await refresh(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { asked = false; }
        if (asked)
        {
            int waited = 0;
            foreach (var delay in retryDelays)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                waited += (int)delay.TotalSeconds;
                waitedSec?.Report(waited);
                try { rows = await recent(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return new HistoryFetchResult(Array.Empty<RecentMatchRow>(), Classify(ex), true);
                }
                if (rows.Count > 0) return new HistoryFetchResult(rows, HistoryFetchOutcome.Ok, true);
            }
        }

        bool known;
        try { known = await profileKnown(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var o = Classify(ex);
            return new HistoryFetchResult(Array.Empty<RecentMatchRow>(), o == HistoryFetchOutcome.NotFound ? o : HistoryFetchOutcome.Hidden, asked);
        }
        return new HistoryFetchResult(Array.Empty<RecentMatchRow>(), known ? HistoryFetchOutcome.Hidden : HistoryFetchOutcome.NotFound, asked);
    }

    /// <summary>Ошибка запроса → понятная причина.</summary>
    public static HistoryFetchOutcome Classify(Exception ex)
    {
        if (ex is HttpRequestException h)
        {
            if (h.StatusCode is HttpStatusCode code)
            {
                int c = (int)code;
                if (c == 429) return HistoryFetchOutcome.RateLimited;
                if (c == 404) return HistoryFetchOutcome.NotFound;
                if (c >= 500) return HistoryFetchOutcome.ServiceDown;
                return HistoryFetchOutcome.ServiceDown;
            }
            return HistoryFetchOutcome.Offline;            // DNS, обрыв, TLS, блокировка
        }
        if (ex is TaskCanceledException or TimeoutException) return HistoryFetchOutcome.Offline;
        if (ex is System.Text.Json.JsonException) return HistoryFetchOutcome.ServiceDown;
        return HistoryFetchOutcome.ServiceDown;
    }

    /// <summary>Ключ строки локализации с объяснением причины.</summary>
    public static string MessageKey(HistoryFetchOutcome o) => o switch
    {
        HistoryFetchOutcome.NotFound => "hist_fail_notfound",
        HistoryFetchOutcome.Hidden => "hist_fail_hidden",
        HistoryFetchOutcome.RateLimited => "hist_fail_ratelimit",
        HistoryFetchOutcome.Offline => "hist_fail_offline",
        HistoryFetchOutcome.ServiceDown => "hist_fail_down",
        _ => "hist_import_empty",
    };
}
