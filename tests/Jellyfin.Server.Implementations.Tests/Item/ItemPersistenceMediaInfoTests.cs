using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public class ItemPersistenceMediaInfoTests : SqliteDbTestFixture
{
    private readonly ItemPersistenceService _service;

    public ItemPersistenceMediaInfoTests()
    {
        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.ReverseVirtualPath(It.IsAny<string>())).Returns((string path) => path);
        _service = new ItemPersistenceService(CreateDbContextFactory(), host.Object, NullLogger<ItemPersistenceService>.Instance);
    }

    [Fact]
    public async Task SaveMediaInfo_StaleDescription_DoesNotOverwriteMetadataOrRelationships()
    {
        var id = Guid.NewGuid();
        var primary = Guid.NewGuid();
        var stamp = DateTime.UtcNow.AddDays(-10);
        var key = JsonDefaults.Options.PropertyNamingPolicy?.ConvertName(nameof(Video.PrimaryVersionId)) ?? nameof(Video.PrimaryVersionId);
        var stored = new JsonObject { [key] = primary.ToString(), ["unrecognizedPluginData"] = "keep" };
        using (var context = CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity
            {
                Id = id, Type = typeof(Movie).FullName!, Path = "/media/movie.mkv", Name = "Edited", Genres = "Edited",
                ProductionLocations = string.Empty, IsLocked = true, DateLastRefreshed = stamp, Data = stored.ToJsonString()
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var stale = new Movie
        {
            Id = id, Path = "/media/movie.mkv", Name = "Old", Genres = ["Old"], ProductionLocations = ["Old"],
            RunTimeTicks = TimeSpan.TicksPerHour, Container = "mkv", Width = 1920, Height = 1080
        };
        await _service.SaveMediaInfoAsync(stale, TestContext.Current.CancellationToken);
        using (var context = CreateDbContext())
        {
            var saved = context.BaseItems.Single(e => e.Id.Equals(id));
            Assert.Equal("Edited", saved.Name);
            Assert.Equal("Edited", saved.Genres);
            Assert.Equal(string.Empty, saved.ProductionLocations);
            Assert.True(saved.IsLocked);
            Assert.Equal(stamp, saved.DateLastRefreshed);
            Assert.Equal(TimeSpan.TicksPerHour, saved.RunTimeTicks);
            Assert.Equal(1920, saved.Width);
            var data = JsonNode.Parse(saved.Data!)!;
            Assert.Equal(primary.ToString(), data[key]!.GetValue<string>());
            Assert.Equal("keep", data["unrecognizedPluginData"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveMediaInfo_EntireSnapshotCommitsOrRollsBack(bool fail)
    {
        var id = Guid.NewGuid();
        using (var context = CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity { Id = id, Type = typeof(Movie).FullName!, Path = "/media/movie.mkv", RunTimeTicks = 1 });
            context.MediaStreamInfos.Add(new MediaStreamInfo { Item = null!, ItemId = id, StreamIndex = 0, StreamType = MediaStreamTypeEntity.Video, Codec = "old" });
            context.AttachmentStreamInfos.Add(new AttachmentStreamInfo { Item = null!, ItemId = id, Index = 0, Filename = "old" });
            context.Chapters.Add(new Chapter { Item = null!, ItemId = id, ChapterIndex = 0, StartPositionTicks = 0, Name = "old" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            if (fail)
            {
                await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_attachment BEFORE INSERT ON AttachmentStreamInfos BEGIN SELECT RAISE(ABORT, 'injected failure'); END", TestContext.Current.CancellationToken);
            }
        }

        var item = new Movie
        {
            Id = id, Path = "/media/movie.mkv", RunTimeTicks = 2,
            PendingMediaInfo = new MediaInfoSnapshot
            {
                Streams = [new MediaStream { Index = 0, Codec = "new", Type = MediaStreamType.Video }],
                Attachments = [new MediaAttachment { Index = 0, FileName = "new" }],
                Chapters = [new ChapterInfo { Name = "new" }]
            }
        };
        if (fail)
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => _service.SaveMediaInfoAsync(item, TestContext.Current.CancellationToken));
        }
        else
        {
            await _service.SaveMediaInfoAsync(item, TestContext.Current.CancellationToken);
            Assert.Null(item.PendingMediaInfo);
        }

        using var check = CreateDbContext();
        var expected = fail ? "old" : "new";
        Assert.Equal(fail ? 1 : 2, check.BaseItems.Single(e => e.Id.Equals(id)).RunTimeTicks);
        Assert.Equal(expected, check.MediaStreamInfos.Single().Codec);
        Assert.Equal(expected, check.AttachmentStreamInfos.Single().Filename);
        Assert.Equal(expected, check.Chapters.Single().Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetMetadata_ValuesAndIndexesCommitTogetherWithoutChangingLocks(bool fail)
    {
        var id = Guid.NewGuid();
        var primary = Guid.NewGuid();
        using (var context = CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity
            {
                Id = id, Type = typeof(Movie).FullName!, Path = "/media/movie.mkv", Genres = "Old", ProductionLocations = "Old",
                Overview = "Keep", IsLocked = true, Data = "{\"primaryVersionId\":\"" + primary + "\",\"pluginData\":7}"
            });
            context.BaseItemMetadataFields.Add(new BaseItemMetadataField { Item = null!, ItemId = id, Id = (int)MetadataField.Genres });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            if (fail)
            {
                await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_reset BEFORE INSERT ON ItemValuesMap BEGIN SELECT RAISE(ABORT, 'injected failure'); END", TestContext.Current.CancellationToken);
            }
        }

        var replacement = new Movie { Id = id, Path = "/media/movie.mkv", Genres = ["New"], ProductionLocations = [], Overview = "Stale", IsLocked = false };
        var save = () => _service.SaveMetadataFieldsAsync(replacement, [MetadataField.Genres, MetadataField.ProductionLocations], [], TestContext.Current.CancellationToken);
        if (fail)
        {
            await Assert.ThrowsAsync<DbUpdateException>(save);
        }
        else
        {
            Assert.True(await save());
        }

        using var check = CreateDbContext();
        var item = check.BaseItems.Single(e => e.Id.Equals(id));
        Assert.Equal(fail ? "Old" : "New", item.Genres);
        Assert.Equal(fail ? "Old" : string.Empty, item.ProductionLocations);
        Assert.True(item.IsLocked);
        Assert.Equal("Keep", item.Overview);
        Assert.Equal((int)MetadataField.Genres, check.BaseItemMetadataFields.Single().Id);
        Assert.Equal(primary.ToString(), JsonNode.Parse(item.Data!)!["primaryVersionId"]!.GetValue<string>());
        Assert.Equal(7, JsonNode.Parse(item.Data!)!["pluginData"]!.GetValue<int>());
        Assert.Equal(fail ? 0 : 1, check.ItemValuesMap.Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetMetadata_CastAndScalarUpdatesShareTransaction(bool fail)
    {
        var id = Guid.NewGuid();
        using (var context = CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity { Id = id, Type = typeof(Movie).FullName!, Path = "/media/movie.mkv", Overview = "Old" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            PeopleRepository.UpdatePeople(context, id, [new PersonInfo { Name = "Old actor", Type = Jellyfin.Data.Enums.PersonKind.Actor }]);
            if (fail)
            {
                await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_cast BEFORE INSERT ON PeopleBaseItemMap BEGIN SELECT RAISE(ABORT, 'injected failure'); END", TestContext.Current.CancellationToken);
            }
        }

        var item = new Movie { Id = id, Path = "/media/movie.mkv", Overview = "New" };
        var save = () => _service.SaveMetadataFieldsAsync(
            item,
            [MetadataField.Overview, MetadataField.Cast],
            [new PersonInfo { Name = "New actor", Type = Jellyfin.Data.Enums.PersonKind.Actor }],
            TestContext.Current.CancellationToken);
        if (fail)
        {
            await Assert.ThrowsAsync<DbUpdateException>(save);
        }
        else
        {
            Assert.True(await save());
        }

        using var check = CreateDbContext();
        Assert.Equal(fail ? "Old" : "New", check.BaseItems.Single(e => e.Id.Equals(id)).Overview);
        Assert.Equal(fail ? "Old actor" : "New actor", check.PeopleBaseItemMap.Select(m => m.People.Name).Single());
    }

    [Fact]
    public async Task SaveMediaInfo_NullCollectionsRetainRowsAndEmptyCollectionsClearRows()
    {
        var id = Guid.NewGuid();
        using (var context = CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity { Id = id, Type = typeof(Movie).FullName!, Path = "/media/movie.mkv" });
            context.MediaStreamInfos.Add(new MediaStreamInfo { Item = null!, ItemId = id, StreamIndex = 0, StreamType = MediaStreamTypeEntity.Video, Codec = "keep" });
            context.AttachmentStreamInfos.Add(new AttachmentStreamInfo { Item = null!, ItemId = id, Index = 0, Filename = "keep" });
            context.Chapters.Add(new Chapter { Item = null!, ItemId = id, ChapterIndex = 0, StartPositionTicks = 0, Name = "keep" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _service.SaveMediaInfoAsync(new Movie { Id = id, Path = "/media/movie.mkv", PendingMediaInfo = new MediaInfoSnapshot { Streams = [] } }, TestContext.Current.CancellationToken);
        using var check = CreateDbContext();
        Assert.Empty(check.MediaStreamInfos);
        Assert.Single(check.AttachmentStreamInfos);
        Assert.Single(check.Chapters);
    }

    [Fact]
    public async Task SaveMediaInfo_CancellationDoesNotDeleteExistingRows()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.SaveMediaInfoAsync(new Movie { Id = Guid.NewGuid() }, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveMediaInfo_DeletedOrMovedItem_DoesNotRestoreOldSource(bool moved)
    {
        var id = Guid.NewGuid();
        if (moved)
        {
            using var context = CreateDbContext();
            context.BaseItems.Add(new BaseItemEntity { Id = id, Type = typeof(Movie).FullName!, Path = "/media/new.mkv", RunTimeTicks = 1 });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await _service.SaveMediaInfoAsync(new Movie { Id = id, Path = "/media/old.mkv", RunTimeTicks = 99 }, TestContext.Current.CancellationToken);
        using var check = CreateDbContext();
        if (moved)
        {
            Assert.Equal(1, check.BaseItems.Single(e => e.Id.Equals(id)).RunTimeTicks);
        }
        else
        {
            Assert.Empty(check.BaseItems.Where(e => e.Id.Equals(id)));
        }
    }
}
