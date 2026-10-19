using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Providers.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.TV;

[Collection("LibraryManagerTests")]
public sealed class SeriesRefreshCoordinationTests : IDisposable
{
    private readonly ILibraryManager _previousLibrary = BaseItem.LibraryManager;
    private readonly Mock<ILibraryManager> _library = new();
    private readonly SeriesMetadataService _service;

    public SeriesRefreshCoordinationTests()
    {
        _library.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
        BaseItem.LibraryManager = _library.Object;
        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.GetConfiguration("metadata")).Returns(new MetadataConfiguration());
        _service = new SeriesMetadataService(
            config.Object,
            NullLogger<SeriesMetadataService>.Instance,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            _library.Object,
            Mock.Of<ILocalizationManager>(),
            Mock.Of<IExternalDataManager>(),
            Mock.Of<IItemRepository>());
    }

    public void Dispose() => BaseItem.LibraryManager = _previousLibrary;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileChildren_WaitsForEditAndUsesCurrentSeasonOrEpisode(bool episode)
    {
        BaseItem snapshot = episode ? new Episode() : new Season { Children = [] };
        snapshot.Id = Guid.NewGuid();
        BaseItem current = episode ? new Episode() : new Season { Children = [] };
        current.Id = snapshot.Id;
        current.Genres = ["已编辑"];
        current.ProductionLocations = [];
        current.LockedFields = [MetadataField.Genres, MetadataField.ProductionLocations];
        _library.Setup(l => l.GetItemById(snapshot.Id)).Returns(() => current);
        var series = new FixtureSeries { Id = Guid.NewGuid(), PresentationUniqueKey = "new-series-key", Entries = [snapshot] };
        var method = typeof(SeriesMetadataService).GetMethod("UpdateSeriesChildrenInfoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task operation;
        using (await MetadataUpdateLock.AcquireAsync(snapshot.Id, TestContext.Current.CancellationToken))
        {
            operation = (Task)method.Invoke(_service, [series, TestContext.Current.CancellationToken])!;
            Assert.False(operation.IsCompleted);
        }

        await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("new-series-key", ((IHasSeries)current).SeriesPresentationUniqueKey);
        Assert.Equal(["已编辑"], current.Genres);
        Assert.Empty(current.ProductionLocations);
        Assert.Equal(2, current.LockedFields.Length);
        _library.Verify(l => l.UpdateItemAsync(It.Is<BaseItem>(i => ReferenceEquals(i, current)), It.IsAny<BaseItem>(), It.Is<ItemUpdateType>(t => t.HasFlag(ItemUpdateType.MetadataImport)), It.IsAny<CancellationToken>()), Times.Once);
        _library.Verify(l => l.UpdateItemAsync(It.Is<BaseItem>(i => ReferenceEquals(i, snapshot)), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestrictedRefresh_DoesNotPrepareOrSaveSeasons(bool technical)
    {
        var season = new Season { Id = Guid.NewGuid(), Path = "/fixture/Season 2" };
        var series = new FixtureSeries { Id = Guid.NewGuid(), Entries = [season], DateLastRefreshed = DateTime.UtcNow };
        _library.Setup(l => l.GetItemById(season.Id)).Returns(season);
        _library.Setup(l => l.GetItemById(series.Id)).Returns(series);
        _library.Setup(l => l.GetSeasonNumberFromPath(season.Path, season.ParentId)).Returns(2);
        await _service.RefreshMetadata(
            series,
            new MetadataRefreshOptions(Mock.Of<IDirectoryService>())
        {
            RefreshMediaInfoOnly = technical,
            MetadataRefreshMode = technical ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.None,
            ImageRefreshMode = MetadataRefreshMode.None
        },
            TestContext.Current.CancellationToken);
        Assert.Null(season.IndexNumber);
        _library.Verify(l => l.UpdateItemAsync(season, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class FixtureSeries : Series
    {
        public BaseItem[] Entries { get; init; } = [];

        public override IEnumerable<BaseItem> Children
        {
            get => Entries;
            set { }
        }
    }
}
