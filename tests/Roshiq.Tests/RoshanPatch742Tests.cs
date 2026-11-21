using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.24.2: патч 7.42 — Рошан быстрее после каждого убийства, катапульты после 30:00 каждые 2:30.</summary>
public class RoshanPatch742Tests
{
    [Fact]
    public void Roshan_RespawnShrinksPerKill_DownToFiveMinutes()
    {
        var r = new RoshanTracker();
        r.MarkKilled(900);
        Assert.Equal(new[] { 900 + 420, 900 + 600 }, new[] { r.State(900).RespawnMinAt, r.State(900).RespawnMaxAt });
        r.MarkKilled(2000);
        Assert.Equal(2000 + 360, r.State(2000).RespawnMinAt);
        Assert.Equal(2000 + 540, r.State(2000).RespawnMaxAt);
        r.MarkKilled(3000);
        Assert.Equal(3000 + 300, r.State(3000).RespawnMinAt);
        Assert.Equal(3000 + 480, r.State(3000).RespawnMaxAt);
        r.MarkKilled(6000);
        Assert.Equal(6000 + 300, r.State(6000).RespawnMinAt);   // ниже 5 минут не уходит
        Assert.Equal(6000 + 480, r.State(6000).RespawnMaxAt);
    }

    [Fact]
    public void Roshan_UndoDoesNotCount_NewMatchResets()
    {
        var r = new RoshanTracker();
        r.MarkKilled(900);
        r.MarkKilled(2000);
        r.Undo();                                   // ошибочная вторая отметка
        Assert.Equal(1, r.Kills);
        r.MarkKilled(2100);
        Assert.Equal(2100 + 360, r.State(2100).RespawnMinAt);
        r.State(100);                               // часы ушли назад — новый матч
        Assert.Equal(0, r.Kills);
    }

    [Fact]
    public void Siege_EveryTwoAndHalfMinutesAfterThirty()
    {
        var early = TestData.Db.Timers.Single(t => t.Key == "siege");
        Assert.Equal(1500, early.LastSec);
        var late = TestData.Db.Timers.Single(t => t.Key == "siege_late");
        Assert.Equal(1800, late.FirstSec);
        Assert.Equal(150, late.IntervalSec);
    }
}
