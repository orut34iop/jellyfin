using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.ScheduledTasks;

public class LocalMetadataOnlyScheduledTaskTests
{
    [Fact]
    public async Task MediaSegmentExtractionTask_AllLibrariesLocalMetadataOnly_DoesNotQueryItems()
    {
        var libraryManager = CreateLibraryManager();
        var task = new MediaSegmentExtractionTask(
            libraryManager.Object,
            Mock.Of<ILocalizationManager>(),
            Mock.Of<IMediaSegmentManager>());

        await task.ExecuteAsync(Mock.Of<IProgress<double>>(), CancellationToken.None);

        libraryManager.VerifyGet(manager => manager.RootFolder, Times.Once);
        libraryManager.Verify(manager => manager.GetLibraryOptions(It.IsAny<BaseItem>()), Times.Once);
        libraryManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChapterImagesTask_AllLibrariesLocalMetadataOnly_DoesNotQueryItems()
    {
        var libraryManager = CreateLibraryManager();
        var task = new ChapterImagesTask(
            Mock.Of<Microsoft.Extensions.Logging.ILogger<ChapterImagesTask>>(),
            libraryManager.Object,
            Mock.Of<IApplicationPaths>(),
            Mock.Of<IChapterManager>(),
            Mock.Of<IFileSystem>(),
            Mock.Of<ILocalizationManager>());

        await task.ExecuteAsync(Mock.Of<IProgress<double>>(), CancellationToken.None);

        libraryManager.VerifyGet(manager => manager.RootFolder, Times.Once);
        libraryManager.Verify(manager => manager.GetLibraryOptions(It.IsAny<BaseItem>()), Times.Once);
        libraryManager.VerifyNoOtherCalls();
    }

    private static Mock<ILibraryManager> CreateLibraryManager()
    {
        var library = new Folder();
        var root = new AggregateFolder
        {
            Children = [library]
        };
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        libraryManager.SetupGet(manager => manager.RootFolder).Returns(root);
        libraryManager.Setup(manager => manager.GetLibraryOptions(library))
            .Returns(new LibraryOptions { LocalMetadataOnlyImport = true });
        return libraryManager;
    }
}
