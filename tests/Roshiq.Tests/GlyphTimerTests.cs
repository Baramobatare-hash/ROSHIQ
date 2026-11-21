using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.21.5: таймер глифа врагов по ручной отметке и волна с катапультой.</summary>
public class GlyphTimerTests
{
    [Fact]
    public void Glyph_ReadyAfterSixMinutes_AnnouncedOnce()
    {
        var g = new GlyphTracker();
        Assert.Null(g.ReadyIn(600));
        g.MarkUsed(600);
        Assert.Equal(360, g.ReadyIn(600));
        Assert.Equal(60, g.ReadyIn(900));
        Assert.False(g.DueReady(900));
        Assert.True(g.DueReady(960));
        Assert.False(g.DueReady(965));   // второй раз не напоминаем
        Assert.Equal(0, g.ReadyIn(965));
        Assert.Null(g.ReadyIn(1021));     // давно готов — отметка уходит из строки таймеров
    }

    [Fact]
    public void Glyph_TwoUsesKeepTheFirstRechargeAndQueueTheSecond()
    {
        var g = new GlyphTracker();
        g.MarkUsed(600);
        g.MarkUsed(607);
        Assert.Equal(353, g.ReadyIn(607));
        Assert.True(g.DueReady(960));
        Assert.Equal(329, g.ReadyIn(991));
        Assert.True(g.DueReady(1320));

        g.Clear();
        g.MarkUsed(600);
        g.MarkUsed(607);
        g.Undo();
        Assert.Equal(600, g.UsedAt);
        Assert.True(g.DueReady(960));
        Assert.Null(g.ReadyIn(1021));
    }

    [Fact]
    public void Glyph_ClearsWhenClockGoesBack()
    {
        var g = new GlyphTracker();
        g.MarkUsed(1500);
        Assert.Null(g.ReadyIn(100));     // новый матч
        Assert.Null(g.UsedAt);
    }

    [Fact]
    public void SiegeWave_EveryFiveMinutesFromFive()
    {
        var siege = TestData.Db.Timers.Single(t => t.Key == "siege");
        Assert.Equal(300, siege.FirstSec);
        Assert.Equal(300, siege.IntervalSec);
    }
}
