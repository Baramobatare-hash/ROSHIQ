using Roshiq.Core.Settings;
using Xunit;

namespace Roshiq.Tests;

public class RepoMigrationTests
{
    [Theory]
    [InlineData("baramobatare-rgb/ROSHIQ")]
    [InlineData("Baramobatare-RGB/roshiq/")]
    [InlineData("https://github.com/baramobatare-rgb/ROSHIQ")]
    public void OldRepo_MovesToNewAccount(string saved)
    {
        var s = new AppSettings();
        s.Data.UpdateRepo = saved;
        s.Normalize();
        Assert.Equal("Baramobatare-hash/ROSHIQ", s.Data.UpdateRepo);
    }

    [Fact]
    public void CustomRepo_IsKept()
    {
        var s = new AppSettings();
        s.Data.UpdateRepo = "someone/fork";
        s.Normalize();
        Assert.Equal("someone/fork", s.Data.UpdateRepo);
    }

    [Fact]
    public void EmptyRepo_GetsDefault()
    {
        var s = new AppSettings();
        s.Data.UpdateRepo = "";
        s.Normalize();
        Assert.Equal(DataSettings.DefaultUpdateRepo, s.Data.UpdateRepo);
    }
}
