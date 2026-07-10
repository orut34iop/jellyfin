using System;
using System.Collections.Generic;
using System.Data.Common;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public sealed class PeopleRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly CommandCaptureInterceptor _commandCaptureInterceptor = new();
    private readonly PeopleRepository _repository;
    private readonly Guid _itemId = Guid.NewGuid();

    public PeopleRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_commandCaptureInterceptor)
            .Options;

        using var context = CreateDbContext();
        context.Database.EnsureCreated();
        SeedPeople(context);

        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        _repository = new PeopleRepository(factory.Object, new Mock<IItemTypeLookup>().Object);
        _commandCaptureInterceptor.Commands.Clear();
    }

    [Fact]
    public void GetPeople_WithItemId_StartsAtMappingTableAndReturnsOrderedMappings()
    {
        var result = _repository.GetPeople(new InternalPeopleQuery { ItemId = _itemId });

        Assert.Collection(
            result,
            person => AssertPerson(person, "Bob Actor", PersonKind.Actor, "Supporting", 30),
            person => AssertPerson(person, "Alice Actor", PersonKind.Actor, "Lead", 20),
            person => AssertPerson(person, "Dana Director", PersonKind.Director, string.Empty, 10));
        var command = Assert.Single(_commandCaptureInterceptor.Commands);
        Assert.Contains("FROM \"PeopleBaseItemMap\" AS", command, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM \"Peoples\" AS", command, StringComparison.Ordinal);
    }

    [Fact]
    public void GetPeople_WithItemFilters_AppliesFiltersBeforeLimit()
    {
        var result = _repository.GetPeople(new InternalPeopleQuery(
            new[] { PersonKind.Actor.ToString() },
            new[] { PersonKind.Director.ToString() })
        {
            ItemId = _itemId,
            MaxListOrder = 1,
            NameContains = "Alice",
            Limit = 1
        });

        var person = Assert.Single(result);
        AssertPerson(person, "Alice Actor", PersonKind.Actor, "Lead", 20);
    }

    [Fact]
    public void GetPeople_WithExcludedPersonType_ExcludesMatchingPeople()
    {
        var result = _repository.GetPeople(new InternalPeopleQuery(
            Array.Empty<string>(),
            new[] { PersonKind.Director.ToString() }));

        Assert.NotEmpty(result);
        Assert.DoesNotContain(result, person => person.Type == PersonKind.Director);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private void AssertPerson(PersonInfo person, string name, PersonKind type, string? role, int sortOrder)
    {
        Assert.Equal(_itemId, person.ItemId);
        Assert.Equal(name, person.Name);
        Assert.Equal(type, person.Type);
        Assert.Equal(role, person.Role);
        Assert.Equal(sortOrder, person.SortOrder);
    }

    private void SeedPeople(JellyfinDbContext context)
    {
        var item = new BaseItemEntity { Id = _itemId, Type = "Movie" };
        var otherItem = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie" };
        var alice = new People { Id = Guid.NewGuid(), Name = "Alice Actor", PersonType = PersonKind.Actor.ToString() };
        var bob = new People { Id = Guid.NewGuid(), Name = "Bob Actor", PersonType = PersonKind.Actor.ToString() };
        var dana = new People { Id = Guid.NewGuid(), Name = "Dana Director", PersonType = PersonKind.Director.ToString() };
        var unrelated = new People { Id = Guid.NewGuid(), Name = "Unrelated Actor", PersonType = PersonKind.Actor.ToString() };

        context.PeopleBaseItemMap.AddRange(
            CreateMapping(item, alice, "Lead", 1, 20),
            CreateMapping(item, bob, "Supporting", 0, 30),
            CreateMapping(item, dana, string.Empty, 2, 10),
            CreateMapping(otherItem, unrelated, string.Empty, 0, 30));
        context.SaveChanges();
    }

    private static PeopleBaseItemMap CreateMapping(
        BaseItemEntity item,
        People person,
        string? role,
        int listOrder,
        int sortOrder)
        => new()
        {
            ItemId = item.Id,
            Item = item,
            PeopleId = person.Id,
            People = person,
            Role = role,
            ListOrder = listOrder,
            SortOrder = sortOrder
        };

    private JellyfinDbContext CreateDbContext()
        => new(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }
    }
}
