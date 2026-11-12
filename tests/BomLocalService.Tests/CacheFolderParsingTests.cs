using BomLocalService.Utilities;
using Xunit;

namespace BomLocalService.Tests;

public class CacheFolderParsingTests
{
    [Theory]
    [InlineData("Pomona_QLD_20261112_071633", "Pomona", "QLD", "2026-11-12T07:16:33Z")]
    [InlineData("Gold_Coast_QLD_20251216_130831", "Gold Coast", "QLD", "2025-12-16T13:08:31Z")]
    [InlineData("St._Lucia_QLD_20261112_071633", "St. Lucia", "QLD", "2026-11-12T07:16:33Z")]
    [InlineData("O'Connell_NSW_20261112_071633", "O'Connell", "NSW", "2026-11-12T07:16:33Z")]
    [InlineData("Bli_Bli_QLD_20261112_071633", "Bli Bli", "QLD", "2026-11-12T07:16:33Z")]
    [InlineData("Port_Macquarie_NSW_20261112_071633", "Port Macquarie", "NSW", "2026-11-12T07:16:33Z")]
    public void Parses_complete_basename_and_preserves_periods(
        string folderName,
        string suburb,
        string state,
        string timestamp)
    {
        Assert.True(LocationHelper.TryParseCacheFolderName(folderName, out var parsed));
        Assert.Equal(suburb, parsed!.Location.Suburb);
        Assert.Equal(state, parsed.Location.State);
        Assert.Equal(DateTime.Parse(timestamp).ToUniversalTime(), parsed.CacheTimestampUtc);
    }

    [Theory]
    [InlineData("Pomona_QLD_20261112_071633.png")]
    [InlineData("Pomona_QLD_20261112")]
    [InlineData("Pomona_QLD_20261112_071633_ab12cd34")]
    [InlineData("Pomona_XX_20261112_071633")]
    [InlineData("../Pomona_QLD_20261112_071633")]
    public void Rejects_non_snapshot_names(string folderName)
    {
        Assert.False(LocationHelper.TryParseCacheFolderName(folderName, out _));
    }

    [Fact]
    public void Sanitizer_matches_historical_folder_convention()
    {
        Assert.Equal("St._Lucia_QLD", LocationHelper.SanitizeFileName("St. Lucia_QLD"));
        Assert.Equal("O'Connell_NSW", LocationHelper.SanitizeFileName("O'Connell_NSW"));
        Assert.Equal("Wagga_Wagga_NSW", LocationHelper.SanitizeFileName("Wagga Wagga_NSW"));
    }
}
