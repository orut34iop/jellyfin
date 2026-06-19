using Emby.Server.Implementations.Library.Validators;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class PostScanAggregateRefreshOptionsTests
{
    [Fact]
    public void HasLocalMetadataOnlyImportLibrary_DefaultOptions_ReturnsFalse()
    {
        var library = new CollectionFolder();

        Assert.False(PostScanAggregateRefreshOptions.HasLocalMetadataOnlyImportLibrary(
            new[] { library },
            _ => new LibraryOptions()));
    }

    [Fact]
    public void HasLocalMetadataOnlyImportLibrary_LocalMetadataOnlyImport_ReturnsTrue()
    {
        var library = new CollectionFolder();

        Assert.True(PostScanAggregateRefreshOptions.HasLocalMetadataOnlyImportLibrary(
            new[] { library },
            _ => new LibraryOptions { LocalMetadataOnlyImport = true }));
    }

    [Fact]
    public void CreateValidationOnly_DisablesRemoteMetadataAndImageRefresh()
    {
        var options = PostScanAggregateRefreshOptions.CreateValidationOnly();

        Assert.Equal(MetadataRefreshMode.ValidationOnly, options.MetadataRefreshMode);
        Assert.Equal(MetadataRefreshMode.ValidationOnly, options.ImageRefreshMode);
    }
}
