using System.Net;
using Roshiq.Data;
using Xunit;

namespace Roshiq.Tests;

public class HistoryFetchTests
{
    private static readonly IReadOnlyList<TimeSpan> NoWait = new[] { TimeSpan.Zero, TimeSpan.Zero };
    private static IReadOnlyList<RecentMatchRow> Rows(int n) => Enumerable.Range(1, n).Select(i => new RecentMatchRow { MatchId = i }).ToList();

    [Fact]
    public async Task Matches_ReturnedRightAway_NoRefresh()
    {
        bool refreshed = false;
        var r = await HistoryFetch.RunAsync(_ => Task.FromResult(Rows(3)), _ => { refreshed = true; return Task.FromResult(true); },
            _ => Task.FromResult(true), NoWait, null, CancellationToken.None);
        Assert.Equal(HistoryFetchOutcome.Ok, r.Outcome);
        Assert.Equal(3, r.Rows.Count);
        Assert.False(refreshed);
    }

    [Fact]
    public async Task Empty_ThenRefresh_FindsMatchesOnRetry()
    {
        int calls = 0;
        var r = await HistoryFetch.RunAsync(_ => Task.FromResult(++calls >= 3 ? Rows(2) : Rows(0)), _ => Task.FromResult(true),
            _ => Task.FromResult(true), NoWait, null, CancellationToken.None);
        Assert.Equal(HistoryFetchOutcome.Ok, r.Outcome);
        Assert.True(r.Refreshed);
        Assert.Equal(2, r.Rows.Count);
    }

    [Fact]
    public async Task Empty_KnownProfile_MeansHiddenHistory()
    {
        var r = await HistoryFetch.RunAsync(_ => Task.FromResult(Rows(0)), _ => Task.FromResult(true),
            _ => Task.FromResult(true), NoWait, null, CancellationToken.None);
        Assert.Equal(HistoryFetchOutcome.Hidden, r.Outcome);
        Assert.Equal("hist_fail_hidden", HistoryFetch.MessageKey(r.Outcome));
    }

    [Fact]
    public async Task Empty_UnknownProfile_MeansNotFound()
    {
        var r = await HistoryFetch.RunAsync(_ => Task.FromResult(Rows(0)), _ => Task.FromResult(false),
            _ => Task.FromResult(false), NoWait, null, CancellationToken.None);
        Assert.Equal(HistoryFetchOutcome.NotFound, r.Outcome);
    }

    [Theory]
    [InlineData(429, HistoryFetchOutcome.RateLimited)]
    [InlineData(503, HistoryFetchOutcome.ServiceDown)]
    [InlineData(404, HistoryFetchOutcome.NotFound)]
    public async Task HttpErrors_GetReadableReason(int code, HistoryFetchOutcome expected)
    {
        var r = await HistoryFetch.RunAsync(
            _ => throw new HttpRequestException("x", null, (HttpStatusCode)code), _ => Task.FromResult(true),
            _ => Task.FromResult(true), NoWait, null, CancellationToken.None);
        Assert.Equal(expected, r.Outcome);
    }

    [Fact]
    public async Task NetworkFailureAndTimeout_AreOffline()
    {
        var a = await HistoryFetch.RunAsync(_ => throw new HttpRequestException("dns"), _ => Task.FromResult(true),
            _ => Task.FromResult(true), NoWait, null, CancellationToken.None);
        var b = await HistoryFetch.RunAsync(_ => throw new TaskCanceledException("timeout"), _ => Task.FromResult(true),
            _ => Task.FromResult(true), NoWait, null, CancellationToken.None);
        Assert.Equal(HistoryFetchOutcome.Offline, a.Outcome);
        Assert.Equal(HistoryFetchOutcome.Offline, b.Outcome);
    }

    [Fact]
    public void EveryOutcome_HasAMessage()
    {
        foreach (HistoryFetchOutcome o in Enum.GetValues(typeof(HistoryFetchOutcome)))
        {
            var key = HistoryFetch.MessageKey(o);
            Assert.False(string.IsNullOrEmpty(Roshiq.Core.Localization.Loc.Instance.Get(key)) || Roshiq.Core.Localization.Loc.Instance.Get(key) == key, key);
        }
    }
}
