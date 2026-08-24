using System;
using System.Collections.Generic;
using System.Reflection;
using Emby.Server.Implementations.Dto;
using Jellyfin.Data.Enums;
using MediaBrowser.Common;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Dto;

public class DtoServiceTests
{
    [Fact]
    public void AttachPeople_LocalMetadataOnlyImportIncludesPeopleWithoutPersonEntity()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Local Movie" };
        var person = new PersonInfo
        {
            Name = "Local Actor",
            Role = "Lead",
            Type = PersonKind.Actor
        };

        var (dto, libraryManager) = AttachPeople(movie, [person], localMetadataOnlyImport: true);

        var attachedPerson = Assert.Single(dto.People);
        Assert.Equal("Local Actor", attachedPerson.Name);
        Assert.Equal("Lead", attachedPerson.Role);
        Assert.Equal(PersonKind.Actor, attachedPerson.Type);
        Assert.Equal(Guid.Empty, attachedPerson.Id);
        libraryManager.Verify(x => x.GetPerson(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void AttachPeople_DefaultModeKeepsExistingPersonEntityFiltering()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Default Movie" };
        var person = new PersonInfo
        {
            Name = "Missing Person Entity",
            Role = "Lead",
            Type = PersonKind.Actor
        };

        var (dto, libraryManager) = AttachPeople(movie, [person], localMetadataOnlyImport: false);

        Assert.Empty(dto.People);
        libraryManager.Verify(x => x.GetPerson("Missing Person Entity"), Times.Once);
    }

    [Fact]
    public void AttachPeople_LocalMetadataOnlyImportWithActorItemsIncludesActorId()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Local Movie" };
        var actor = new PersonInfo
        {
            Name = "Local Actor",
            Role = "Lead",
            Type = PersonKind.Actor
        };
        var director = new PersonInfo
        {
            Name = "Local Director",
            Type = PersonKind.Director
        };
        var actorItem = new Person { Id = Guid.NewGuid(), Name = actor.Name };

        var (dto, libraryManager) = AttachPeople(
            movie,
            [actor, director],
            localMetadataOnlyImport: true,
            createLocalActorItems: true,
            actorItem);

        Assert.Equal(2, dto.People.Length);
        Assert.Equal(actorItem.Id, dto.People[0].Id);
        Assert.Equal(Guid.Empty, dto.People[1].Id);
        libraryManager.Verify(x => x.GetPerson(actor.Name), Times.Once);
        libraryManager.Verify(x => x.GetPerson(director.Name), Times.Never);
    }

    private static (BaseItemDto Dto, Mock<ILibraryManager> LibraryManager) AttachPeople(
        Movie movie,
        IReadOnlyList<PersonInfo> people,
        bool localMetadataOnlyImport,
        bool createLocalActorItems = false,
        Person? actorItem = null)
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(x => x.GetPeople(movie)).Returns(people);
        libraryManager.Setup(x => x.GetPerson(It.IsAny<string>())).Returns((string name) =>
            string.Equals(name, actorItem?.Name, StringComparison.Ordinal) ? actorItem : null);
        libraryManager.Setup(x => x.GetLibraryOptions(movie)).Returns(new LibraryOptions
        {
            LocalMetadataOnlyImport = localMetadataOnlyImport,
            CreateLocalActorItems = createLocalActorItems
        });

        var dtoService = new DtoService(
            Mock.Of<ILogger<DtoService>>(),
            libraryManager.Object,
            Mock.Of<IUserDataManager>(),
            Mock.Of<IImageProcessor>(),
            Mock.Of<IProviderManager>(),
            Mock.Of<IRecordingsManager>(),
            Mock.Of<IApplicationHost>(),
            Mock.Of<IMediaSourceManager>(),
            new Lazy<ILiveTvManager>(() => Mock.Of<ILiveTvManager>()),
            Mock.Of<ITrickplayManager>(),
            Mock.Of<IChapterManager>());

        var dto = new BaseItemDto();
        typeof(DtoService)
            .GetMethod("AttachPeople", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dtoService, [dto, movie, null]);

        return (dto, libraryManager);
    }
}
