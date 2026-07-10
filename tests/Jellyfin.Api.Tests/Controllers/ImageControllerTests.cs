using System;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public static class ImageControllerTests
{
    [Fact]
    public static async Task GetItemImage_WithEmptyItemId_ReturnsNotFoundWithoutLibraryLookup()
    {
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        var controller = new ImageController(
            Mock.Of<IUserManager>(),
            libraryManager.Object,
            Mock.Of<IProviderManager>(),
            Mock.Of<IImageProcessor>(),
            Mock.Of<IFileSystem>(),
            Mock.Of<ILogger<ImageController>>(),
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<IApplicationPaths>());

        var result = await controller.GetItemImage(
            Guid.Empty,
            ImageType.Primary,
            maxWidth: null,
            maxHeight: null,
            width: null,
            height: null,
            quality: null,
            fillWidth: null,
            fillHeight: null,
            tag: null,
            format: null,
            percentPlayed: null,
            unplayedCount: null,
            blur: null,
            backgroundColor: null,
            foregroundLayer: null,
            imageIndex: null);

        Assert.IsType<NotFoundResult>(result);
        libraryManager.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("image/apng", ".apng")]
    [InlineData("image/avif", ".avif")]
    [InlineData("image/bmp", ".bmp")]
    [InlineData("image/gif", ".gif")]
    [InlineData("image/x-icon", ".ico")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/png", ".png")]
    [InlineData("image/png; charset=utf-8", ".png")]
    [InlineData("image/svg+xml", ".svg")]
    [InlineData("image/tiff", ".tiff")]
    [InlineData("image/webp", ".webp")]
    public static void TryGetImageExtensionFromContentType_Valid_True(string contentType, string extension)
    {
        Assert.True(ImageController.TryGetImageExtensionFromContentType(contentType, out var ex));
        Assert.Equal(extension, ex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("text/html")]
    public static void TryGetImageExtensionFromContentType_InValid_False(string? contentType)
    {
        Assert.False(ImageController.TryGetImageExtensionFromContentType(contentType, out var ex));
        Assert.Null(ex);
    }
}
