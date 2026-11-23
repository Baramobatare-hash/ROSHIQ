using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Models;

namespace Roshiq.Recommendations;

/// <summary>
/// 0.26.0: ИИ-объяснение ошибок матча. Модель не ставится на компьютер: факты разбора уходят на сервер ROSHIQ
/// (Cloudflare Workers AI), ответ — короткий текст. Ключей в программе нет.
/// </summary>
public sealed class AiExplainClient
{
    public const string Endpoint = "https://roshiq-ai.baramobatare.workers.dev/explain";
    private readonly HttpClient _http;
    private readonly string _client;

    public AiExplainClient(string appVersion, HttpMessageHandler? handler = null)
    {
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(60);
        _client = "desktop/" + appVersion;
    }

    /// <summary>Текст объяснения или (null, причина) — причина уже готова для показа.</summary>
    public async Task<(string? Text, string? Error)> ExplainAsync(MatchReview review, IReadOnlyDictionary<string, object?> facts,
        Language lang, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            match = review.MatchId,
            slot = 0,
            lang = lang == Language.English ? "en" : "ru",
            facts,
        });
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Add("X-Roshiq-Client", _client);
                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                NetworkLog.Record("POST", Endpoint, ((int)res.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) + " (ИИ-объяснение матча)");
                string? text = null, error = null;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("text", out var t)) text = t.GetString();
                    if (doc.RootElement.TryGetProperty("error", out var e)) error = e.GetString();
                }
                catch (JsonException) { }
                if (res.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(text)) return (text!.Trim(), null);
                // сервер пишет ошибки по-русски — в английском интерфейсе показываем свои
                if (lang == Language.English) error = null;
                if (res.StatusCode == HttpStatusCode.TooManyRequests) return (null, error ?? Loc("ai_err_limit", lang));
                if ((int)res.StatusCode >= 500 && attempt == 0) { await Task.Delay(1500, ct).ConfigureAwait(false); continue; }
                return (null, error ?? Loc("ai_err_down", lang));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                NetworkLog.Record("POST", Endpoint, "ошибка: " + ex.Message);
                if (attempt == 0) { await Task.Delay(1500, ct).ConfigureAwait(false); continue; }
                return (null, Loc("ai_err_net", lang));
            }
        }
        return (null, Loc("ai_err_down", lang));
    }

    private static string Loc(string key, Language lang) => key switch
    {
        "ai_err_limit" => lang == Language.English ? "The daily AI limit is used up. Try tomorrow." : "Дневной лимит ИИ исчерпан. Попробуй завтра.",
        "ai_err_net" => lang == Language.English ? "Couldn't reach the AI. Check your internet connection and try again." : "Не удалось связаться с ИИ. Проверь интернет и попробуй ещё раз.",
        _ => lang == Language.English ? "The AI is unavailable right now. Try later." : "ИИ сейчас недоступен. Попробуй позже.",
    };
}
