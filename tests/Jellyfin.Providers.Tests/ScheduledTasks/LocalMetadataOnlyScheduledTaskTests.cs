using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Providers.MediaInfo;
using MediaBrowser.Providers.Trickplay;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.ScheduledTasks;

public class LocalMetadataOnlyScheduledTaskTests
{
    [Fact]
    public async Task TrickplayImagesTask_AllLibrariesLocalMetadataOnly_DoesNotQueryItems()
    {
        var libraryManager = CreateLibraryManager();
        var trickplayManager = new Mock<ITrickplayManager>(MockBehavior.Strict);
        var task = new TrickplayImagesTask(
            Mock.Of<ILogger<TrickplayImagesTask>>(),
            libraryManager.Object,
            Mock.Of<ILocalizationManager>(),
            trickplayManager.Object);

        await task.ExecuteAsync(Mock.Of<IProgress<double>>(), CancellationToken.None);

        libraryManager.VerifyGet(manager => manager.RootFolder, Times.Once);
        libraryManager.Verify(manager => manager.GetLibraryOptions(It.IsAny<BaseItem>()), Times.Once);
        libraryManager.VerifyNoOtherCalls();
        trickplayManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubtitleScheduledTask_AllLibrariesLocalMetadataOnly_DoesNotQueryItems()
    {
        var libraryManager = CreateLibraryManager();
        var subtitleManager = new Mock<ISubtitleManager>(MockBehavior.Strict);
        var task = new SubtitleScheduledTask(
            libraryManager.Object,
            Mock.Of<IServerConfigurationManager>(),
            subtitleManager.Object,
            Mock.Of<ILogger<SubtitleScheduledTask>>(),
            Mock.Of<ILocalizationManager>());

        await task.ExecuteAsync(Mock.Of<IProgress<double>>(), CancellationToken.None);

        libraryManager.VerifyGet(manager => manager.RootFolder, Times.Once);
        libraryManager.Verify(manager => manager.GetLibraryOptions(It.IsAny<BaseItem>()), Times.Once);
        libraryManager.VerifyNoOtherCalls();
        subtitleManager.VerifyNoOtherCalls();
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
