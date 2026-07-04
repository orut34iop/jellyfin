using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.Controller.Tests.Entities;

public class BaseItemTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("1", "0000000001")]
    [InlineData("t", "t")]
    [InlineData("test", "test")]
    [InlineData("test1", "test0000000001")]
    [InlineData("1test 2", "0000000001test 0000000002")]
    public void BaseItem_ModifySortChunks_Valid(string input, string expected)
        => Assert.Equal(expected, BaseItem.ModifySortChunks(input));

    [Theory]
    [InlineData("/Movies/Ted/Ted.mp4", "/Movies/Ted/Ted - Unrated Edition.mp4", "Ted", "Unrated Edition")]
    [InlineData("/Movies/Deadpool 2 (2018)/Deadpool 2 (2018).mkv", "/Movies/Deadpool 2 (2018)/Deadpool 2 (2018) - Super Duper Cut.mkv", "Deadpool 2 (2018)", "Super Duper Cut")]
    public void GetMediaSourceName_Valid(string primaryPath, string altPath, string name, string altName)
    {
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(x => x.GetPathProtocol(It.IsAny<string>()))
                .Returns((string x) => MediaProtocol.File);
        BaseItem.MediaSourceManager = mediaSourceManager.Object;

        var video = new Video()
        {
            Path = primaryPath
        };

        var videoAlt = new Video()
        {
            Path = altPath,
        };

        video.LocalAlternateVersions = [videoAlt.Path];

        Assert.Equal(name, video.GetMediaSourceName(video));
        Assert.Equal(altName, video.GetMediaSourceName(videoAlt));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RefreshMetadata_LocalMetadataOnlyImport_SkipsResolvingVideoSymlinksUnlessRemoteContentProbe(
        bool enableRemoteContentProbe,
        bool expectedSkipResolvingVideoSymlinks)
    {
        var video = new Video
        {
            Path = "/Movies/Ted/Ted.mp4"
        };

        BaseItem.FileSystem = Mock.Of<IFileSystem>();

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(i => i.GetLibraryOptions(video))
            .Returns(new LibraryOptions { LocalMetadataOnlyImport = true });
        BaseItem.LibraryManager = libraryManager.Object;

        var providerManager = new Mock<IProviderManager>();
        MetadataRefreshOptions? capturedOptions = null;
        providerManager.Setup(
                i => i.RefreshSingleItem(
                    video,
                    It.IsAny<MetadataRefreshOptions>(),
                    It.IsAny<CancellationToken>()))
            .Callback<BaseItem, MetadataRefreshOptions, CancellationToken>((_, options, _) => capturedOptions = options)
            .ReturnsAsync(ItemUpdateType.None);
        BaseItem.ProviderManager = providerManager.Object;

        var originalDirectoryService = new DirectoryService(Mock.Of<IFileSystem>());
        var refreshOptions = new MetadataRefreshOptions(originalDirectoryService)
        {
            EnableRemoteContentProbe = enableRemoteContentProbe
        };

        await video.RefreshMetadata(refreshOptions, CancellationToken.None);

        var directoryService = Assert.IsType<DirectoryService>(capturedOptions!.DirectoryService);
        Assert.Equal(expectedSkipResolvingVideoSymlinks, directoryService.SkipResolvingVideoSymlinks);
    }
}
