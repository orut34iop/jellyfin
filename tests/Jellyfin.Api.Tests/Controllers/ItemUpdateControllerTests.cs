using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class ItemUpdateControllerTests
{
    private readonly ItemUpdateController _subject;

    public ItemUpdateControllerTests()
    {
        _subject = new ItemUpdateController(
            Mock.Of<IFileSystem>(),
            Mock.Of<ILibraryManager>(),
            Mock.Of<IProviderManager>(),
            Mock.Of<ILocalizationManager>(),
            Mock.Of<IServerConfigurationManager>());
    }

    [Theory]
    [InlineData(MetadataResetResult.Success, 204)]
    [InlineData(MetadataResetResult.NotFound, 404)]
    [InlineData(MetadataResetResult.InvalidRequest, 400)]
    [InlineData(MetadataResetResult.NoMetadata, 422)]
    [InlineData(MetadataResetResult.ProviderFailed, 502)]
    public async Task ResetMetadata_ReturnsSynchronousOutcome(MetadataResetResult outcome, int status)
    {
        var item = new Movie { Id = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById<BaseItem>(item.Id, It.IsAny<Guid>())).Returns(item);
        var providers = new Mock<IProviderManager>();
        var request = new MetadataResetRequest { Fields = [MetadataField.Genres], ProviderName = "Nfo" };
        providers.Setup(p => p.ResetMetadata(item, request, It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        var controller = new ItemUpdateController(Mock.Of<IFileSystem>(), library.Object, providers.Object, Mock.Of<ILocalizationManager>(), Mock.Of<IServerConfigurationManager>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = Assert.IsAssignableFrom<StatusCodeResult>(await controller.ResetMetadata(item.Id, request));
        Assert.Equal(status, result.StatusCode);
    }

    [Fact]
    public async Task UpdateItem_WhenOnlyTagsFieldSupplied_DoesNotThrowAndAppliesTags()
    {
        // Regression test for https://github.com/jellyfin/jellyfin/issues/17366
        // A partial update payload that only sets "Tags" leaves every other
        // BaseItemDto collection property null (they have no default
        // initializer). Genres and ProviderIds used to be fed straight into
        // Distinct()/ToList() without a null check, so this call used to throw
        // ArgumentNullException before the fix below was applied.
        var movie = new Movie();
        var request = new BaseItemDto
        {
            Tags = new[] { "new-tag-1", "new-tag-2" }
        };

        await InvokeUpdateItem(request, movie);

        Assert.Equal(new[] { "new-tag-1", "new-tag-2" }, movie.Tags);
        Assert.Empty(movie.Genres);
        Assert.Empty(movie.ProviderIds);
    }

    [Fact]
    public async Task UpdateItem_WhenGenresAndProviderIdsOmitted_LeavesExistingValuesUnchanged()
    {
        var movie = new Movie
        {
            Genres = new[] { "Action" }
        };
        movie.ProviderIds["Imdb"] = "tt1234567";

        var request = new BaseItemDto
        {
            Tags = Array.Empty<string>()
        };

        await InvokeUpdateItem(request, movie);

        Assert.Equal(new[] { "Action" }, movie.Genres);
        Assert.Equal("tt1234567", movie.ProviderIds["Imdb"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateItem_ProtectedFields_CanBeEditedAndClearedWithoutUnlocking(bool empty)
    {
        var locks = new[] { MetadataField.Genres, MetadataField.ProductionLocations, MetadataField.Overview };
        var movie = new Movie { Genres = ["Old"], ProductionLocations = ["Old"], LockedFields = locks };
        var request = new BaseItemDto
        {
            Genres = empty ? [] : ["Edited"],
            ProductionLocations = empty ? [] : ["Edited"],
            LockedFields = locks
        };
        await InvokeUpdateItem(request, movie);
        Assert.Equal(empty ? Array.Empty<string>() : ["Edited"], movie.Genres);
        Assert.Equal(empty ? Array.Empty<string>() : ["Edited"], movie.ProductionLocations);
        Assert.Equal(locks, movie.LockedFields);
    }

    private Task InvokeUpdateItem(BaseItemDto request, BaseItem item)
    {
        return _subject.UpdateItem(request, item);
    }
}
