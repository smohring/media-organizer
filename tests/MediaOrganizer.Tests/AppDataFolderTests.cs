using MediaOrganizer.Core.Services;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>The data folder follows the override variable, so a run can be
/// pointed at a throwaway folder; without it, it is the per-user default.</summary>
public class AppDataFolderTests
{
    [Fact]
    public void Override_wins_over_the_default()
    {
        Assert.Equal(@"C:\Temp\demo-data", AppDataFolder.Resolve(@"C:\Temp\demo-data"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_an_override_the_per_user_folder_is_used(string? value)
    {
        var path = AppDataFolder.Resolve(value);

        Assert.EndsWith("MediaOrganizer", path);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), path);
    }
}
