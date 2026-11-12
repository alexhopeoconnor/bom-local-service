using BomLocalService.Models;
using BomLocalService.Utilities;
using Xunit;

namespace BomLocalService.Tests;

public class LocationValidationTests
{
    [Theory]
    [InlineData("Pomona", "QLD", "Pomona", "QLD")]
    [InlineData("  St. Lucia ", "qld", "St. Lucia", "QLD")]
    [InlineData("O'Connell", "nsw", "O'Connell", "NSW")]
    [InlineData("Wagga Wagga", "New South Wales", "Wagga Wagga", "NSW")]
    [InlineData("Bli Bli", "Queensland", "Bli Bli", "QLD")]
    [InlineData("Yéppoon", "QLD", "Yéppoon", "QLD")]
    [InlineData("Tewantin", "qld", "Tewantin", "QLD")]
    public void Accepts_ordinary_australian_place_names(
        string suburb,
        string state,
        string expectedSuburb,
        string expectedState)
    {
        Assert.True(LocationId.TryCreate(suburb, state, out var location, out var error), error);
        Assert.Equal(expectedSuburb, location!.Suburb);
        Assert.Equal(expectedState, location.State);
    }

    [Theory]
    [InlineData("", "QLD")]
    [InlineData("   ", "QLD")]
    [InlineData("*", "QLD")]
    [InlineData("Pomona*", "QLD")]
    [InlineData("Pomona?", "QLD")]
    [InlineData("../Pomona", "QLD")]
    [InlineData("Pomona/../Sydney", "QLD")]
    [InlineData("..\\Pomona", "QLD")]
    [InlineData("Pomona\u0000", "QLD")]
    [InlineData("Pomona\nSydney", "QLD")]
    [InlineData("Pomona", "XX")]
    [InlineData("Pomona", "")]
    public void Rejects_unsafe_or_invalid_locations(string suburb, string state)
    {
        Assert.False(LocationId.TryCreate(suburb, state, out _, out _));
    }

    [Fact]
    public void Rejects_unreasonably_long_names()
    {
        Assert.False(LocationId.TryCreate(new string('a', LocationId.MaxSuburbLength + 1), "QLD", out _, out _));
    }

    [Fact]
    public void Identity_is_case_and_whitespace_stable()
    {
        var first = LocationId.Create("St.  Lucia", "qld");
        var second = LocationId.Create("st. lucia", "QLD");

        Assert.Equal(first, second);
        Assert.Equal(first.Key, second.Key);
    }

    [Fact]
    public void Rejects_blank_state_and_wildcards_from_delete_route()
    {
        Assert.False(LocationId.TryCreate("Brisbane", "*", out _, out _));
        Assert.False(LocationId.TryCreate("*", "QLD", out _, out _));
    }
}
