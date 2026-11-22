using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class TierListRoleTests
{
    private static readonly GameDatabase Db = GameDatabase.Load();

    private static Hero H(string key) => Db.FindHero(key)!;

    [Fact]
    public void RoleTier_UsesOnlyGamesOnThatPosition()
    {
        var meta = new List<HeroMetaRow>
        {
            // Lion: на пятой позиции выигрывает, на миде проигрывает — проценты не смешиваются
            new("lion", Position.HardSupport, "ALL", 20000, 10800),
            new("lion", Position.Mid, "ALL", 3000, 1300),
            new("crystal_maiden", Position.HardSupport, "ALL", 15000, 7300),
            new("witch_doctor", Position.HardSupport, "ALL", 12000, 6200),
            new("oracle", Position.HardSupport, "ALL", 9000, 4500),
            new("invoker", Position.Mid, "ALL", 30000, 15600),
        };
        var five = TierList.BuildFromMeta(meta, Db.Heroes, Position.HardSupport, RankBracket.All)!;
        var lion5 = five.Single(e => e.Hero.Key == "lion");
        Assert.True(lion5.WinRate > 0.53, "Lion на 5-й — 54%");
        Assert.Equal("S", lion5.Tier);
        var mid = TierList.BuildFromMeta(meta, Db.Heroes, Position.Mid, RankBracket.All)!;
        Assert.True(mid.Single(e => e.Hero.Key == "lion").WinRate < 0.45, "Lion на миде — свои 43%");
        Assert.DoesNotContain(five, e => e.Hero.Key == "invoker");
    }

    [Fact]
    public void RoleTier_RareHeroOnPosition_IsHidden()
    {
        var meta = new List<HeroMetaRow>
        {
            new("lion", Position.HardSupport, "ALL", 20000, 10000),
            new("crystal_maiden", Position.HardSupport, "ALL", 20000, 10000),
            new("antimage", Position.HardSupport, "ALL", 90, 60),   // мало игр — не показываем
        };
        var list = TierList.BuildFromMeta(meta, Db.Heroes, Position.HardSupport, RankBracket.All)!;
        Assert.DoesNotContain(list, e => e.Hero.Key == "antimage");
    }

    [Fact]
    public void RoleTier_BracketsAreSummedAndProHasNoMeta()
    {
        var meta = new List<HeroMetaRow>
        {
            new("lion", Position.HardSupport, "LA", 5000, 2400),
            new("lion", Position.HardSupport, "DI", 5000, 2800),
            new("crystal_maiden", Position.HardSupport, "LA", 5000, 2500),
            new("crystal_maiden", Position.HardSupport, "DI", 5000, 2500),
        };
        var high = TierList.BuildFromMeta(meta, Db.Heroes, Position.HardSupport, RankBracket.High)!;
        Assert.Equal(10000, high.Single(e => e.Hero.Key == "lion").Games);
        Assert.Null(TierList.BuildFromMeta(meta, Db.Heroes, Position.HardSupport, RankBracket.Pro));
        Assert.Null(TierList.BuildFromMeta(meta, Db.Heroes, Position.HardSupport, RankBracket.Low));
    }

    [Fact]
    public void EmbeddedMeta_LoadsWithoutWarnings()
    {
        Assert.DoesNotContain(Db.Warnings, w => w.StartsWith("hero_meta.txt", StringComparison.Ordinal));
    }
}
