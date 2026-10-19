using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

[Collection("Video version mutations")]
public sealed class VideoVersionMutationTests : IDisposable
{
    private readonly ILibraryManager _previousLibrary = BaseItem.LibraryManager;
    private readonly Dictionary<Guid, TestMovie> _items = [];
    private readonly VideosController _controller;

    public VideoVersionMutationTests()
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        library.Setup(l => l.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<Guid>())).Returns((Guid id, Guid _) => _items.GetValueOrDefault(id));
        library.Setup(l => l.GetItemById<Video>(It.IsAny<Guid>(), It.IsAny<Guid>())).Returns((Guid id, Guid _) => _items.GetValueOrDefault(id));
        library.Setup(l => l.GetItemById<Video>(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        library.Setup(l => l.GetLinkedAlternateVersions(It.IsAny<Video>())).Returns((Video v) =>
            v.LinkedAlternateVersions.Select(l => (Video)_items[l.ItemId!.Value]).ToArray());
        library.Setup(l => l.GetLocalAlternateVersionIds(It.IsAny<Video>())).Returns((Video v) => v.LocalAlternateVersions.Select(Guid.Parse).ToArray());
        BaseItem.LibraryManager = library.Object;
        _controller = new VideosController(library.Object, null!, null!, null!, null!, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    public void Dispose() => BaseItem.LibraryManager = _previousLibrary;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutation_WaitsForRefreshAndRereadsProtectedMetadata(bool split)
    {
        var primary = AddMovie();
        var secondary = AddMovie();
        Link(primary, secondary);
        var lease = await MetadataUpdateLock.AcquireAsync(secondary.Id, TestContext.Current.CancellationToken);
        Task<ActionResult> operation;
        try
        {
            operation = split ? _controller.DeleteAlternateSources(primary.Id) : _controller.MergeVersions([primary.Id, secondary.Id]);
            Assert.False(operation.IsCompleted);
            // A refresh/edit persisted a new instance while the mutation was waiting.
            _items[secondary.Id] = new TestMovie
            {
                Id = secondary.Id,
                PrimaryVersionId = primary.Id,
                Genres = ["最新流派"],
                ProductionLocations = [],
                LockedFields = [MetadataField.Genres, MetadataField.ProductionLocations]
            };
        }
        finally
        {
            lease.Dispose();
        }

        Assert.IsType<NoContentResult>(await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var current = _items[secondary.Id];
        Assert.Equal(["最新流派"], current.Genres);
        Assert.Empty(current.ProductionLocations);
        Assert.Equal(2, current.LockedFields.Length);
        Assert.Equal(1, current.Saves);
        Assert.Equal(split ? null : primary.Id, current.PrimaryVersionId);
        Assert.Equal(0, secondary.Saves);
    }

    [Fact]
    public async Task Merge_ExistingGroupsSelectedBySecondary_FlattensEveryMember()
    {
        var a = AddMovie();
        var b = AddMovie();
        var c = AddMovie();
        var d = AddMovie();
        Link(a, b);
        Link(c, d);
        Assert.IsType<NoContentResult>(await _controller.MergeVersions([b.Id, d.Id]));
        var primary = Assert.Single(_items.Values, v => v.PrimaryVersionId is null);
        Assert.Equal(3, primary.LinkedAlternateVersions.Length);
        foreach (var member in _items.Values.Where(v => v != primary))
        {
            Assert.Equal(primary.Id, member.PrimaryVersionId);
            Assert.Empty(member.LinkedAlternateVersions);
            Assert.Equal([member.Id.ToString()], member.Genres);
        }

        Assert.IsType<NoContentResult>(await _controller.DeleteAlternateSources(d.Id));
        Assert.All(_items.Values, v =>
        {
            Assert.Null(v.PrimaryVersionId);
            Assert.Empty(v.LinkedAlternateVersions);
            Assert.Equal([v.Id.ToString()], v.Genres);
        });
    }

    [Fact]
    public async Task MergeThenSplit_OverlappingRequestsDoNotReattachSplitMembers()
    {
        var primary = AddMovie();
        var secondary = AddMovie();
        Link(primary, secondary);
        Task<ActionResult> merge;
        Task<ActionResult> split;
        using (await MetadataUpdateLock.AcquireAsync(secondary.Id, TestContext.Current.CancellationToken))
        {
            merge = _controller.MergeVersions([primary.Id, secondary.Id]);
            split = _controller.DeleteAlternateSources(primary.Id);
            Assert.False(merge.IsCompleted);
            Assert.False(split.IsCompleted);
        }

        await Task.WhenAll(merge, split).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.All(_items.Values, v =>
        {
            Assert.Null(v.PrimaryVersionId);
            Assert.Empty(v.LinkedAlternateVersions);
        });
    }

    [Fact]
    public async Task Merge_DuplicateIdIsNotASecondVideo()
    {
        var item = AddMovie();
        Assert.IsType<BadRequestObjectResult>(await _controller.MergeVersions([item.Id, item.Id]));
        Assert.Equal(0, item.Saves);
    }

    [Fact]
    public async Task MergeAndSplit_FileBasedMembersRetainTheirOwnerAndSeeEntireManualGroup()
    {
        var a = AddMovie();
        var b = AddMovie();
        var c = AddMovie();
        var d = AddMovie();
        a.LocalAlternateVersions = [b.Id.ToString()];
        b.PrimaryVersionId = a.Id;
        c.LocalAlternateVersions = [d.Id.ToString()];
        d.PrimaryVersionId = c.Id;
        Assert.IsType<NoContentResult>(await _controller.MergeVersions([b.Id, d.Id]));
        Assert.Equal(a.Id, b.PrimaryVersionId);
        Assert.Equal(c.Id, d.PrimaryVersionId);
        foreach (var video in _items.Values)
        {
            Assert.Equal(4, video.MediaSourceCount);
            Assert.Equal(_items.Keys.Order(), video.Sources().Order());
        }

        // Splitting through a local member resolves the root of the manual group.
        Assert.IsType<NoContentResult>(await _controller.DeleteAlternateSources(b.Id));
        Assert.Null(a.PrimaryVersionId);
        Assert.Null(c.PrimaryVersionId);
        Assert.Equal(a.Id, b.PrimaryVersionId);
        Assert.Equal(c.Id, d.PrimaryVersionId);
        Assert.All(_items.Values, v => Assert.Equal(2, v.MediaSourceCount));
        Assert.All(_items.Values, v => Assert.Equal([v.Id.ToString()], v.Genres));
    }

    private TestMovie AddMovie()
    {
        var item = new TestMovie { Id = Guid.NewGuid() };
        item.Genres = [item.Id.ToString()];
        _items.Add(item.Id, item);
        return item;
    }

    private static void Link(Video primary, Video secondary)
    {
        primary.LinkedAlternateVersions = [new LinkedChild { ItemId = secondary.Id, Type = LinkedChildType.LinkedAlternateVersion }];
        secondary.PrimaryVersionId = primary.Id;
    }

    private sealed class TestMovie : Movie
    {
        public int Saves { get; private set; }

        public Guid[] Sources() => GetAllItemsForMediaSources().Select(i => i.Item.Id).ToArray();

        public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }
}
