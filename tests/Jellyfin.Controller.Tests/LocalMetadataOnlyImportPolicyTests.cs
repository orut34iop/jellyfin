using System;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Controller.Tests;

public class LocalMetadataOnlyImportPolicyTests
{
    [Fact]
    public void IsEnabled_DefaultLibraryOptions_ReturnsFalse()
    {
        var previous = Environment.GetEnvironmentVariable(LocalMetadataOnlyImportPolicy.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(LocalMetadataOnlyImportPolicy.EnvironmentVariableName, null);

        try
        {
            Assert.False(LocalMetadataOnlyImportPolicy.IsEnabled(new LibraryOptions()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalMetadataOnlyImportPolicy.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public void IsEnabled_LocalMetadataOnlyImportLibraryOption_ReturnsTrue()
    {
        var previous = Environment.GetEnvironmentVariable(LocalMetadataOnlyImportPolicy.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(LocalMetadataOnlyImportPolicy.EnvironmentVariableName, null);

        try
        {
            Assert.True(LocalMetadataOnlyImportPolicy.IsEnabled(new LibraryOptions { LocalMetadataOnlyImport = true }));
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalMetadataOnlyImportPolicy.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public void IsEnabled_EnvironmentVariableTrue_ReturnsTrue()
    {
        var previous = Environment.GetEnvironmentVariable(LocalMetadataOnlyImportPolicy.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(LocalMetadataOnlyImportPolicy.EnvironmentVariableName, "true");

        try
        {
            Assert.True(LocalMetadataOnlyImportPolicy.IsEnabled(new LibraryOptions()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalMetadataOnlyImportPolicy.EnvironmentVariableName, previous);
        }
    }

    [Theory]
    [InlineData("/media/movie.iso", true)]
    [InlineData("/media/movie.mkv", true)]
    [InlineData("/media/movie.MP4", true)]
    [InlineData("/media/poster.jpg", false)]
    [InlineData("/media/movie.nfo", false)]
    public void IsVideoLikePath_MatchesExpectedExtensions(string path, bool expected)
        => Assert.Equal(expected, LocalMetadataOnlyImportPolicy.IsVideoLikePath(path));
}
