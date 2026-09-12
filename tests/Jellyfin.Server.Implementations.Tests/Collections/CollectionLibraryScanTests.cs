using System;
using System.IO;
using System.Threading.Tasks;
using Emby.Server.Implementations.Collections;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Collections;

public class CollectionLibraryScanTests
{
    [Fact]
    public async Task CreatingCollectionLibrary_RequestsRefreshThroughSharedGuard()
    {
        var path = Directory.CreateTempSubdirectory("jellyfin-collection-test-").FullName;
        try
        {
            var created = false;
            var folder = new Folder { Path = path };
            var root = new Mock<AggregateFolder>();
            root.SetupGet(f => f.Children).Returns(() => created ? [folder] : []);
            var library = new Mock<ILibraryManager>();
            library.SetupGet(m => m.RootFolder).Returns(root.Object);
            library.Setup(m => m.AddVirtualFolder("Collections", CollectionTypeOptions.boxsets, It.IsAny<LibraryOptions>(), true))
                .Callback(() => created = true)
                .Returns(Task.CompletedTask);
            var fileSystem = new Mock<IFileSystem>();
            fileSystem.Setup(f => f.AreEqual(path, path)).Returns(true);
            var localization = new Mock<ILocalizationManager>();
            localization.Setup(l => l.GetServerLocalizedString("Collections")).Returns("Collections");
            var manager = new CollectionManager(
                library.Object,
                Mock.Of<IApplicationPaths>(),
                localization.Object,
                fileSystem.Object,
                Mock.Of<ILibraryMonitor>(),
                NullLoggerFactory.Instance,
                Mock.Of<IProviderManager>(),
                Mock.Of<ILinkedChildrenService>());

            Assert.Same(folder, await manager.EnsureLibraryFolder(path, true).ConfigureAwait(true));
            Assert.Same(folder, await manager.EnsureLibraryFolder(path, true).ConfigureAwait(true));
            library.Verify(m => m.AddVirtualFolder("Collections", CollectionTypeOptions.boxsets, It.Is<LibraryOptions>(o => !o.EnableRealtimeMonitor && o.SaveLocalMetadata), true), Times.Once);
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }
}
