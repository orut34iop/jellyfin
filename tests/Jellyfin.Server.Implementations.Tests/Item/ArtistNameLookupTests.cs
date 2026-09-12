using System;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Naming.Common;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using Moq;
using Xunit;
using ServerLibraryManager = Emby.Server.Implementations.Library.LibraryManager;

namespace Jellyfin.Server.Implementations.Tests.Item;

public sealed class ArtistNameLookupTests : SqliteDbTestFixture
{
    [Theory]
    [InlineData("Björk", "bjork")]
    [InlineData("AC/DC", "ac dc")]
    [InlineData("An Artist", " AN   ARTIST ")]
    [InlineData("Artist", "ARTIST")]
    public void GetArtist_UsesSameNormalizedNameAsFindArtists(string storedName, string requestedName)
    {
        var lookup = new ItemTypeLookup();
        var artistId = Guid.NewGuid();
        using (var context = CreateDbContext())
        {
            context.BaseItems.AddRange(
                new BaseItemEntity
                {
                    Id = artistId,
                    Name = storedName,
                    CleanName = storedName.GetCleanValue(),
                    Type = lookup.BaseItemKindNames[BaseItemKind.MusicArtist]
                },
                new BaseItemEntity
                {
                    Id = Guid.NewGuid(),
                    Name = storedName,
                    CleanName = storedName.GetCleanValue(),
                    Type = lookup.BaseItemKindNames[BaseItemKind.Person]
                });
            context.SaveChanges();
        }

        var manager = CreateLibraryManager(lookup);

        Assert.Equal(artistId, manager.GetArtist(requestedName).Id);
        Assert.Equal(artistId, Assert.Single(manager.GetArtists([requestedName])[requestedName]).Id);
    }

    [Fact]
    public void GetArtist_PrefersFilesystemArtistWhenNormalizedNamesMatch()
    {
        var lookup = new ItemTypeLookup();
        var parentId = Guid.NewGuid();
        var artistId = Guid.NewGuid();
        using (var context = CreateDbContext())
        {
            context.BaseItems.AddRange(
                new BaseItemEntity
                {
                    Id = parentId,
                    Name = "Music",
                    Type = lookup.BaseItemKindNames[BaseItemKind.Folder]
                },
                new BaseItemEntity
                {
                    Id = Guid.NewGuid(),
                    Name = "Bjork",
                    CleanName = "Bjork".GetCleanValue(),
                    Type = lookup.BaseItemKindNames[BaseItemKind.MusicArtist]
                },
                new BaseItemEntity
                {
                    Id = artistId,
                    ParentId = parentId,
                    Name = "Björk",
                    CleanName = "Björk".GetCleanValue(),
                    Type = lookup.BaseItemKindNames[BaseItemKind.MusicArtist]
                });
            context.SaveChanges();
        }

        Assert.Equal(artistId, CreateLibraryManager(lookup).GetArtist("Bjork").Id);
    }

    private ServerLibraryManager CreateLibraryManager(ItemTypeLookup lookup)
    {
        var repository = CreateBaseItemRepository(lookup);
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        fixture.Register(() => new NamingOptions());
        var configuration = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        configuration.Setup(c => c.ApplicationPaths.ProgramDataPath).Returns("/data");
        fixture.Inject<IItemRepository>(repository);
        fixture.Inject<ILinkedChildrenService>(new LinkedChildrenService(CreateDbContextFactory(), lookup, repository));
        return fixture.Create<ServerLibraryManager>();
    }
}
