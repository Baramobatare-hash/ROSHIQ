using System.Net;
using System.Net.Http;
using System.Text.Json;
using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.26.0: ИИ-объяснение — какие факты уходят на сервер и как программа читает ответ.</summary>
public class AiExplainTests
{
    private static MatchReview Review() => new()
    {
        MatchId = "7000000001",
        HeroId = 1,
        Position = Position.Carry,
        Parsed = true,
        Duration = 2410,
        RadiantWin = false,
        LastHits10 = 38,
        LastHits10Target = 60,
        LaneEfficiency = 41.6,
        Benchmarks = { new Benchmark("gold_per_min", 410, 0.12) },
        GoldAdv = { 0, -200, -400, -600, -800, -1000, -1100, -1200, -1300, -1400, -1500, -2000, -6200 },
        Items = { new ItemTimingRow("bfury", 1150, 960) },
        Killers = { new KillerRow(26, 4) },
        Timeline = { new TimelineEvent(760, "death", -1, "Смерть от Lion — без размена", "Killed by Lion — not traded") },
        Insights = { new Insight("lane", -2, "Линия: 38 добиваний к 10:00 при ориентире 60 для роли", "Lane: 38") },
        Players =
        {
            new ScoreRow(1, true, true, "Me", 22, 5, 9, 4, 12000, 190, 5, 410, 480, 15000, 1200, 0, new(), null),
            new ScoreRow(2, true, false, "Ally", 20, 3, 5, 10, 9000, 120, 3, 350, 400, 9000, 300, 0, new(), null),
            new ScoreRow(26, false, false, "Enemy", 18, 9, 2, 12, 8000, 40, 1, 300, 350, 11000, 0, 0, new(), null),
        },
    };

    private static Dictionary<string, object?> Facts(MatchReview r) =>
        MatchAiFacts.Build(r, id => id switch { 1 => "Anti-Mage", 2 => "Axe", 26 => "Lion", _ => "?" }, key => key == "bfury" ? "Battle Fury" : key);

    [Fact]
    public void Facts_HaveResultRoleAndGoldInWords()
    {
        var f = Facts(Review());
        Assert.Equal("Anti-Mage", f["hero"]);
        Assert.Equal("поражение", f["result"]);
        Assert.Equal("керри (позиция 1)", f["role"]);
        Assert.Equal("5/9/4", f["kda"]);
        var gold = Assert.IsType<Dictionary<string, string>>(f["team_gold_by_minute"]);
        Assert.StartsWith("твоя команда отстаёт на 1", gold["10 мин"]);
        Assert.Contains("6", gold["конец"]);
        Assert.Equal(new List<string> { "Axe" }, (List<string>)f["allies"]!);
        Assert.Equal(new List<string> { "Lion" }, (List<string>)f["enemies"]!);
        Assert.Contains("Battle Fury в 19:10 (обычно 16:00)", (List<string>)f["item_timings"]!);
    }

    [Fact]
    public void Facts_HaveNoNicknames()
    {
        var json = JsonSerializer.Serialize(Facts(Review()));
        Assert.DoesNotContain("Me\"", json);
        Assert.DoesNotContain("Ally", json);
        Assert.DoesNotContain("Enemy", json);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Client_ReturnsText_AndSendsClientHeader()
    {
        var h = new FakeHandler(_ => Json(HttpStatusCode.OK, "{\"text\":\"  Итог.\\n- ошибка  \"}"));
        var c = new AiExplainClient("0.26.0", h);
        var r = Review();
        var (text, error) = await c.ExplainAsync(r, Facts(r), Language.Russian);
        Assert.Equal("Итог.\n- ошибка", text);
        Assert.Null(error);
        Assert.Equal(1, h.Calls);
        Assert.Equal("desktop/0.26.0", h.Last!.Headers.GetValues("X-Roshiq-Client").Single());
    }

    [Fact]
    public async Task Client_RetriesOnce_ThenGivesReadableError()
    {
        var h = new FakeHandler(_ => Json(HttpStatusCode.BadGateway, "{\"error\":\"ИИ сейчас недоступен. Попробуй позже.\"}"));
        var c = new AiExplainClient("0.26.0", h);
        var r = Review();
        var (text, error) = await c.ExplainAsync(r, Facts(r), Language.Russian);
        Assert.Null(text);
        Assert.Equal("ИИ сейчас недоступен. Попробуй позже.", error);
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Client_Limit_NoRetry()
    {
        var h = new FakeHandler(_ => Json(HttpStatusCode.TooManyRequests, "{}"));
        var c = new AiExplainClient("0.26.0", h);
        var r = Review();
        var (text, error) = await c.ExplainAsync(r, Facts(r), Language.English);
        Assert.Null(text);
        Assert.Equal("The daily AI limit is used up. Try tomorrow.", error);
        Assert.Equal(1, h.Calls);
    }
}
