using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Controller.Tests.Entities;

public partial class BaseItemTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshMetadata_TechnicalOrImageOnly_DoesNotMaintainOwnedItems(bool technical)
    {
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
        var provider = new Mock<IProviderManager>(MockBehavior.Strict);
        provider.Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ItemUpdateType.None);
        BaseItem.LibraryManager = library.Object;
        BaseItem.ProviderManager = provider.Object;
        var directory = new Mock<IDirectoryService>(MockBehavior.Strict);
        var item = new OwnedItem { Id = Guid.NewGuid(), Path = "/media/movie.mkv" };
        var options = new MetadataRefreshOptions(directory.Object)
        {
            RefreshMediaInfoOnly = technical,
            MetadataRefreshMode = technical ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.None,
            ImageRefreshMode = technical ? MetadataRefreshMode.None : MetadataRefreshMode.FullRefresh
        };
        await item.RefreshMetadata(options, TestContext.Current.CancellationToken);
        Assert.False(item.OwnedItemsVisited);
        directory.VerifyNoOtherCalls();
        library.Verify(l => l.UpdateImagesAsync(It.IsAny<BaseItem>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OwnedItemInheritance_PreservesProtectedValues(bool empty, bool wholeItemLocked)
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
        BaseItem.LibraryManager = library.Object;
        var provider = new Mock<IProviderManager>();
        provider.Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ItemUpdateType.None);
        BaseItem.ProviderManager = provider.Object;
        var parent = new OwnedItem { Genres = ["Original"], ProductionLocations = ["Original"], Overview = "Original", OfficialRating = "Original", Studios = ["Original"] };
        var child = new Movie
        {
            Genres = empty ? [] : ["Edited"],
            ProductionLocations = empty ? [] : ["Edited"],
            Studios = empty ? [] : ["Edited"],
            Overview = empty ? string.Empty : "Edited",
            OfficialRating = empty ? string.Empty : "Edited",
            IsLocked = wholeItemLocked,
            LockedFields = wholeItemLocked ? [] : [MetadataField.Genres, MetadataField.ProductionLocations, MetadataField.Studios, MetadataField.Overview, MetadataField.OfficialRating]
        };
        await parent.RefreshChild(child);
        Assert.Equal(empty ? Array.Empty<string>() : ["Edited"], child.Genres);
        Assert.Equal(empty ? Array.Empty<string>() : ["Edited"], child.ProductionLocations);
        Assert.Equal(empty ? Array.Empty<string>() : ["Edited"], child.Studios);
        Assert.Equal(empty ? string.Empty : "Edited", child.Overview);
        Assert.Equal(empty ? string.Empty : "Edited", child.OfficialRating);
    }

    [Fact]
    public async Task OwnedItemInheritance_UsesLatestDestinationLocks()
    {
        var child = new Movie { Id = Guid.NewGuid(), Genres = ["Old"] };
        var current = new Movie { Id = child.Id, Genres = ["Edited"], LockedFields = [MetadataField.Genres] };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(child.Id)).Returns(current);
        library.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
        BaseItem.LibraryManager = library.Object;
        var provider = new Mock<IProviderManager>();
        provider.Setup(p => p.RefreshSingleItem(current, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ItemUpdateType.None);
        BaseItem.ProviderManager = provider.Object;
        await new OwnedItem { Genres = ["Original"] }.RefreshChild(child);
        Assert.Equal(["Edited"], current.Genres);
        provider.Verify(p => p.RefreshSingleItem(current, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SavePrimary_DoesNotCopyMetadataToVersions()
    {
        var library = new Mock<ILibraryManager>();
        var member = new Movie { Id = Guid.NewGuid(), Genres = ["Edited"], Overview = "Edited", LockedFields = [MetadataField.Genres] };
        library.Setup(l => l.GetLocalAlternateVersionIds(It.IsAny<Video>())).Returns([member.Id]);
        library.Setup(l => l.GetItemById(member.Id)).Returns(member);
        BaseItem.LibraryManager = library.Object;
        var primary = new Movie { Id = Guid.NewGuid(), Genres = ["Original"], Overview = "Original" };
        await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, TestContext.Current.CancellationToken);
        Assert.Equal(["Edited"], member.Genres);
        Assert.Equal("Edited", member.Overview);
        library.Verify(l => l.UpdateItemAsync(member, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class OwnedItem : Movie
    {
        public bool OwnedItemsVisited { get; private set; }

        protected override bool SupportsOwnedItems => true;

        public Task RefreshChild(BaseItem child)
            => RefreshMetadataForOwnedItem(child, true, new MetadataRefreshOptions(Mock.Of<IDirectoryService>()), TestContext.Current.CancellationToken);

        protected override Task<bool> RefreshedOwnedItems(MetadataRefreshOptions options, IReadOnlyList<FileSystemMetadata> children, CancellationToken cancellationToken)
        {
            OwnedItemsVisited = true;
            return Task.FromResult(false);
        }
    }
}
