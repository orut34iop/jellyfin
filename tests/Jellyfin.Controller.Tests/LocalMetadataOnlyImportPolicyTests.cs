using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Controller.Tests;

public class LocalMetadataOnlyImportPolicyTests
{
    [Fact]
    public void IsEnabled_DefaultLibraryOptions_ReturnsFalse()
        => Assert.False(LocalMetadataOnlyImportPolicy.IsEnabled(new LibraryOptions()));

    [Fact]
    public void IsEnabled_LocalMetadataOnlyImportLibraryOption_ReturnsTrue()
        => Assert.True(LocalMetadataOnlyImportPolicy.IsEnabled(new LibraryOptions { LocalMetadataOnlyImport = true }));

    [Fact]
    public void IsEnabled_NullLibraryOptions_ReturnsFalse()
        => Assert.False(LocalMetadataOnlyImportPolicy.IsEnabled(null));

    [Fact]
    public void IsEnabledForItem_NullItemOrManager_ReturnsFalse()
    {
        Assert.False(LocalMetadataOnlyImportPolicy.IsEnabledForItem(null, null));
    }

    [Theory]
    [InlineData("/media/movie.iso", true)]
    [InlineData("/media/movie.mkv", true)]
    [InlineData("/media/movie.MP4", true)]
    [InlineData("/media/poster.jpg", false)]
    [InlineData("/media/movie.nfo", false)]
    public void IsVideoLikePath_MatchesExpectedExtensions(string path, bool expected)
        => Assert.Equal(expected, LocalMetadataOnlyImportPolicy.IsVideoLikePath(path));

    [Theory]
    [InlineData("https://image.tmdb.org/t/p/original/person.jpg", false, true)]
    [InlineData("http://image.tmdb.org/t/p/original/person.jpg", true, false)]
    [InlineData("https://image.tmdb.org/t/p/original/person.jpg", true, false)]
    [InlineData("/metadata/People/A/Actor/poster.jpg", true, true)]
    [InlineData("", true, false)]
    public void CanImportImagePath_LocalMetadataOnlyImportSkipsRemoteUrls(
        string path,
        bool localMetadataOnlyImport,
        bool expected)
        => Assert.Equal(expected, LocalMetadataOnlyImportPolicy.CanImportImagePath(path, localMetadataOnlyImport));
}
