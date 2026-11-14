using System.Text;
using BomLocalService.Models;
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

    [Fact]
    public void Ordinary_names_keep_the_legacy_shape()
    {
        var location = LocationId.Create("Pomona", "QLD");
        var timestamp = new DateTime(2026, 11, 15, 1, 2, 3, DateTimeKind.Utc);

        Assert.True(LocationHelper.TryGetCacheFolderBaseName(location, timestamp, out var name, out var error), error);
        Assert.Equal("Pomona_QLD_20261115_010203", name);
    }

    [Theory]
    [InlineData("Wagga_Wagga", "NSW")]
    [InlineData("Wagga, Wagga", "NSW")]
    [InlineData("Winston, Salem", "QLD")]
    [InlineData("_Leading", "QLD")]
    [InlineData("Trailing_", "QLD")]
    [InlineData("A__B", "QLD")]
    [InlineData(".Foo", "QLD")]
    public void Lossy_names_use_v2_and_round_trip_exactly(string suburb, string state)
    {
        var location = LocationId.Create(suburb, state);
        var timestamp = new DateTime(2026, 11, 15, 1, 2, 3, DateTimeKind.Utc);

        Assert.True(LocationHelper.RequiresEncodedCacheFolderName(location));
        Assert.True(LocationHelper.TryGetCacheFolderBaseName(location, timestamp, out var name, out var error), error);
        Assert.StartsWith("v2~", name);

        Assert.True(LocationHelper.TryParseCacheFolderName(name, out var parsed), name);
        Assert.Equal(location.Key, parsed!.Location.Key);
        Assert.Equal(location.Suburb, parsed.Location.Suburb);
        Assert.Equal(timestamp, parsed.CacheTimestampUtc);

        Assert.True(LocationHelper.TryGetStagingFolderName(location, timestamp, "abcdef12", out var staging, out error), error);
        Assert.True(LocationHelper.TryParseCacheFolderName(staging, allowStaging: true, out var stagingParsed), staging);
        Assert.True(stagingParsed!.IsStaging);
        Assert.Equal("abcdef12", stagingParsed.UniqueSuffix);
        Assert.Equal(location.Key, stagingParsed.Location.Key);
        Assert.False(LocationHelper.TryParseCacheFolderName(staging, out _));
    }

    [Fact]
    public void V2_parser_rejects_noncanonical_state_date_base64_and_padding_aliases()
    {
        var location = LocationId.Create("Wagga_Wagga", "NSW");
        var timestamp = new DateTime(2026, 11, 15, 1, 2, 3, DateTimeKind.Utc);
        Assert.True(LocationHelper.TryGetCacheFolderBaseName(location, timestamp, out var name, out var error), error ?? string.Empty);
        Assert.True(LocationHelper.TryParseCacheFolderName(name, out _));
        var validName = name!;

        Assert.False(LocationHelper.TryParseCacheFolderName(validName.Replace("~NSW~", "~nsw~", StringComparison.Ordinal), out _));
        Assert.False(LocationHelper.TryParseCacheFolderName(validName.Replace("~NSW~", "~New South Wales~", StringComparison.Ordinal), out _));
        Assert.False(LocationHelper.TryParseCacheFolderName(validName.Replace("~20261115~", "~2026115~", StringComparison.Ordinal), out _));
        Assert.False(LocationHelper.TryParseCacheFolderName(validName.Replace("~NSW~", "=~NSW~", StringComparison.Ordinal), out _));
        Assert.False(LocationHelper.TryParseCacheFolderName(validName.Replace("~NSW~", "~~NSW~", StringComparison.Ordinal), out _));
    }

    [Fact]
    public void Accepted_locations_always_produce_final_and_staging_basenames()
    {
        var location = LocationId.Create(new string('a', LocationId.MaxSuburbLength), "QLD");
        var timestamp = new DateTime(2026, 11, 15, 1, 2, 3, DateTimeKind.Utc);

        Assert.True(LocationHelper.TryGetCacheFolderBaseName(location, timestamp, out var finalName, out var finalError), finalError);
        Assert.True(LocationHelper.TryGetStagingFolderName(location, timestamp, "abcdef12", out var stagingName, out var stagingError), stagingError);
        Assert.True(Encoding.UTF8.GetByteCount(finalName!) <= 255);
        Assert.True(Encoding.UTF8.GetByteCount(stagingName!) <= 255);
    }
}
