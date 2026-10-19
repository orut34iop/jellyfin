using System;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class ItemRefreshControllerTests
{
    [Fact]
    public void RefreshItem_TechnicalOnly_QueuesExplicitProbeWithoutReplacingMetadata()
    {
        var item = new Video { Id = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById<BaseItem>(item.Id, Guid.Empty)).Returns(item);
        var providers = new Mock<IProviderManager>();
        var controller = Create(library, providers);
        Assert.IsType<NoContentResult>(controller.RefreshItem(item.Id, refreshMediaInfoOnly: true));
        providers.Verify(
            p => p.QueueRefresh(
                item.Id,
                It.Is<MetadataRefreshOptions>(o => o.RefreshMediaInfoOnly && o.EnableRemoteContentProbe && !o.ProbeMissingStreamsOnly
                    && !o.ReplaceAllMetadata && o.MetadataRefreshMode == MetadataRefreshMode.FullRefresh
                    && o.ImageRefreshMode == MetadataRefreshMode.None),
                RefreshPriority.High),
            Times.Once);
    }

    [Fact]
    public void RefreshItem_TechnicalOnlyWithReplacement_RejectsConflictingIntent()
    {
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        var providers = new Mock<IProviderManager>(MockBehavior.Strict);
        var controller = Create(library, providers);
        Assert.IsType<BadRequestObjectResult>(controller.RefreshItem(Guid.NewGuid(), replaceAllMetadata: true, refreshMediaInfoOnly: true));
        library.VerifyNoOtherCalls();
        providers.VerifyNoOtherCalls();
    }

    [Fact]
    public void RefreshItem_TechnicalOnlyFolder_DoesNotQueueRecursiveScan()
    {
        var item = new Folder { Id = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById<BaseItem>(item.Id, Guid.Empty)).Returns(item);
        var providers = new Mock<IProviderManager>(MockBehavior.Strict);
        Assert.IsType<BadRequestObjectResult>(Create(library, providers).RefreshItem(item.Id, refreshMediaInfoOnly: true));
        providers.VerifyNoOtherCalls();
    }

    private static ItemRefreshController Create(Mock<ILibraryManager> library, Mock<IProviderManager> providers)
        => new(library.Object, providers.Object, Mock.Of<IFileSystem>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
}
