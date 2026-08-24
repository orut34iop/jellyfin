using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Libraries;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Item;
#pragma warning disable RS0030 // Do not use banned APIs
#pragma warning disable CA1304 // Specify CultureInfo
#pragma warning disable CA1311 // Specify a culture or use an invariant version
#pragma warning disable CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons

/// <summary>
/// Manager for handling people.
/// </summary>
/// <param name="dbProvider">Efcore Factory.</param>
/// <param name="itemTypeLookup">Items lookup service.</param>
/// <remarks>
/// Initializes a new instance of the <see cref="PeopleRepository"/> class.
/// </remarks>
public class PeopleRepository(IDbContextFactory<JellyfinDbContext> dbProvider, IItemTypeLookup itemTypeLookup) : IPeopleRepository
{
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider = dbProvider;

    /// <inheritdoc/>
    public IReadOnlyList<PersonInfo> GetPeople(InternalPeopleQuery filter)
    {
        using var context = _dbProvider.CreateDbContext();

        // Item-scoped lookups are used while building every item DTO. Start at the
        // mapping table so SQLite can use its ItemId index instead of scanning the
        // entire Peoples table and running correlated mapping subqueries per row.
        if (!filter.ItemId.IsEmpty())
        {
            IQueryable<PeopleBaseItemMap> itemPeopleQuery = TranslateItemQuery(
                    context.PeopleBaseItemMap.AsNoTracking().Where(e => e.ItemId == filter.ItemId),
                    context,
                    filter)
                .Include(e => e.People)
                .OrderBy(e => e.ListOrder)
                .ThenBy(e => e.People.PersonType)
                .ThenBy(e => e.People.Name);

            if (filter.Limit > 0)
            {
                itemPeopleQuery = itemPeopleQuery.Take(filter.Limit);
            }

            return itemPeopleQuery.AsEnumerable().Select(Map).ToArray();
        }

        var dbQuery = TranslateQuery(context.Peoples.AsNoTracking(), context, filter);

        dbQuery = dbQuery.OrderBy(e => e.Name);

        if (filter.Limit > 0)
        {
            dbQuery = dbQuery.Take(filter.Limit);
        }

        return dbQuery.AsEnumerable().Select(Map).ToArray();
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetPeopleNames(InternalPeopleQuery filter)
    {
        using var context = _dbProvider.CreateDbContext();
        IQueryable<string> dbQuery;
        if (filter.AncestorIds.Length > 0)
        {
            var mappingsBelowAncestors = context.AncestorIds.AsNoTracking()
                .Where(ancestor => filter.AncestorIds.Contains(ancestor.ParentItemId))
                .Join(
                    context.PeopleBaseItemMap.AsNoTracking(),
                    ancestor => ancestor.ItemId,
                    mapping => mapping.ItemId,
                    (_, mapping) => mapping);
            dbQuery = TranslateItemQuery(mappingsBelowAncestors, context, filter, applyAncestorFilter: false)
                .Select(mapping => mapping.People.Name)
                .Distinct();
        }
        else
        {
            dbQuery = TranslateQuery(context.Peoples.AsNoTracking(), context, filter)
                .Select(e => e.Name)
                .Distinct();
        }

        // dbQuery = dbQuery.OrderBy(e => e.ListOrder);
        if (filter.Limit > 0)
        {
            dbQuery = dbQuery.Take(filter.Limit);
        }

        return dbQuery.ToArray();
    }

    /// <inheritdoc />
    public void UpdatePeople(Guid itemId, IReadOnlyList<PersonInfo> people)
    {
        foreach (var item in people.Where(e => e.Role is null))
        {
            item.Role = string.Empty;
        }

        // multiple metadata providers can provide the _same_ person
        people = people.DistinctBy(GetPersonKey).ToArray();
        var personKeys = people.Select(GetPersonKey).ToHashSet(StringComparer.Ordinal);
        var personNames = people.Select(e => e.Name).Distinct(StringComparer.Ordinal).ToArray();

        using var context = _dbProvider.CreateDbContext();
        using var transaction = context.Database.BeginTransaction();
        var existingPersons = context.Peoples
            .Where(e => personNames.Contains(e.Name))
            .AsEnumerable()
            .Where(e => personKeys.Contains(GetPersonKey(e)))
            .ToArray();
        var existingPersonKeys = existingPersons.Select(GetPersonKey).ToHashSet(StringComparer.Ordinal);

        var toAdd = people
            .Where(e => e.Type is not PersonKind.Artist && e.Type is not PersonKind.AlbumArtist)
            .Where(e => !existingPersonKeys.Contains(GetPersonKey(e)))
            .Select(Map)
            .ToArray();
        context.Peoples.AddRange(toAdd);
        context.SaveChanges();

        var personsEntities = toAdd.Concat(existingPersons).ToDictionary(GetPersonKey, StringComparer.Ordinal);

        var existingMaps = context.PeopleBaseItemMap.Include(e => e.People).Where(e => e.ItemId == itemId).ToList();
        var existingMapsByKey = existingMaps
            .GroupBy(GetMapKey, StringComparer.Ordinal)
            .ToDictionary(e => e.Key, e => e.First(), StringComparer.Ordinal);
        var mapsToRemove = existingMaps.ToHashSet();

        var listOrder = 0;

        foreach (var person in people)
        {
            if (person.Type == PersonKind.Artist || person.Type == PersonKind.AlbumArtist)
            {
                continue;
            }

            var entityPerson = personsEntities[GetPersonKey(person)];
            var existingMap = existingMapsByKey.GetValueOrDefault(GetMapKey(person));
            if (existingMap is null)
            {
                context.PeopleBaseItemMap.Add(new PeopleBaseItemMap()
                {
                    Item = null!,
                    ItemId = itemId,
                    People = null!,
                    PeopleId = entityPerson.Id,
                    ListOrder = listOrder,
                    SortOrder = person.SortOrder,
                    Role = person.Role
                });
            }
            else
            {
                // Update the order for existing mappings
                existingMap.ListOrder = listOrder;
                existingMap.SortOrder = person.SortOrder;
                // person mapping already exists so remove from list
                mapsToRemove.Remove(existingMap);
            }

            listOrder++;
        }

        context.PeopleBaseItemMap.RemoveRange(mapsToRemove);

        context.SaveChanges();
        transaction.Commit();
    }

    private static string GetPersonKey(PersonInfo person)
        => person.Name + "-" + person.Type;

    private static string GetPersonKey(People person)
        => person.Name + "-" + person.PersonType;

    private static string GetMapKey(PersonInfo person)
        => person.Name + "-" + person.Type + "-" + person.Role;

    private static string GetMapKey(PeopleBaseItemMap map)
        => map.People.Name + "-" + map.People.PersonType + "-" + map.Role;

    private static PersonInfo Map(PeopleBaseItemMap mapping)
    {
        var personInfo = new PersonInfo()
        {
            Id = mapping.People.Id,
            ItemId = mapping.ItemId,
            Name = mapping.People.Name,
            Role = mapping.Role,
            SortOrder = mapping.SortOrder
        };
        if (Enum.TryParse<PersonKind>(mapping.People.PersonType, out var kind))
        {
            personInfo.Type = kind;
        }

        return personInfo;
    }

    private PersonInfo Map(People people)
    {
        var mapping = people.BaseItems?.FirstOrDefault();
        var personInfo = new PersonInfo()
        {
            Id = people.Id,
            Name = people.Name,
            Role = mapping?.Role,
            SortOrder = mapping?.SortOrder
        };
        if (Enum.TryParse<PersonKind>(people.PersonType, out var kind))
        {
            personInfo.Type = kind;
        }

        return personInfo;
    }

    private People Map(PersonInfo people)
    {
        var personInfo = new People()
        {
            Name = people.Name,
            PersonType = people.Type.ToString(),
            Id = people.Id,
        };

        return personInfo;
    }

    private IQueryable<People> TranslateQuery(IQueryable<People> query, JellyfinDbContext context, InternalPeopleQuery filter)
    {
        if (filter.User is not null && filter.IsFavorite.HasValue)
        {
            var personType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Person];
            var oldQuery = query;

            query = context.UserData
                .Where(u => u.Item!.Type == personType && u.IsFavorite == filter.IsFavorite && u.UserId.Equals(filter.User.Id))
                .Join(oldQuery, e => e.Item!.Name, e => e.Name, (item, person) => person)
                .Distinct()
                .AsNoTracking();
        }

        if (!filter.ItemId.IsEmpty())
        {
            query = query.Where(e => e.BaseItems!.Any(w => w.ItemId.Equals(filter.ItemId)));
        }

        if (!filter.AppearsInItemId.IsEmpty())
        {
            query = query.Where(e => e.BaseItems!.Any(w => w.ItemId.Equals(filter.AppearsInItemId)));
        }

        if (filter.AncestorIds.Length > 0)
        {
            query = query.Where(e => e.BaseItems!.Any(mapping => context.AncestorIds.Any(ancestor =>
                ancestor.ItemId == mapping.ItemId && filter.AncestorIds.Contains(ancestor.ParentItemId))));
        }

        var queryPersonTypes = filter.PersonTypes.Where(IsValidPersonType).ToList();
        if (queryPersonTypes.Count > 0)
        {
            query = query.Where(e => queryPersonTypes.Contains(e.PersonType));
        }

        var queryExcludePersonTypes = filter.ExcludePersonTypes.Where(IsValidPersonType).ToList();

        if (queryExcludePersonTypes.Count > 0)
        {
            query = query.Where(e => !queryExcludePersonTypes.Contains(e.PersonType));
        }

        if (filter.MaxListOrder.HasValue && !filter.ItemId.IsEmpty())
        {
            query = query.Where(e => e.BaseItems!.First(w => w.ItemId == filter.ItemId).ListOrder <= filter.MaxListOrder.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.NameContains))
        {
            var nameContainsUpper = filter.NameContains.ToUpper();
            query = query.Where(e => e.Name.ToUpper().Contains(nameContainsUpper));
        }

        return query;
    }

    private IQueryable<PeopleBaseItemMap> TranslateItemQuery(
        IQueryable<PeopleBaseItemMap> query,
        JellyfinDbContext context,
        InternalPeopleQuery filter,
        bool applyAncestorFilter = true)
    {
        if (filter.User is not null && filter.IsFavorite.HasValue)
        {
            var personType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Person];
            query = query.Where(mapping => context.UserData.Any(userData =>
                userData.Item!.Type == personType
                && userData.IsFavorite == filter.IsFavorite
                && userData.UserId.Equals(filter.User.Id)
                && userData.Item.Name == mapping.People.Name));
        }

        if (!filter.AppearsInItemId.IsEmpty())
        {
            query = query.Where(e => e.People.BaseItems!.Any(w => w.ItemId.Equals(filter.AppearsInItemId)));
        }

        if (applyAncestorFilter && filter.AncestorIds.Length > 0)
        {
            query = query.Where(mapping => context.AncestorIds.Any(ancestor =>
                ancestor.ItemId == mapping.ItemId && filter.AncestorIds.Contains(ancestor.ParentItemId)));
        }

        var queryPersonTypes = filter.PersonTypes.Where(IsValidPersonType).ToList();
        if (queryPersonTypes.Count > 0)
        {
            query = query.Where(e => queryPersonTypes.Contains(e.People.PersonType));
        }

        var queryExcludePersonTypes = filter.ExcludePersonTypes.Where(IsValidPersonType).ToList();
        if (queryExcludePersonTypes.Count > 0)
        {
            query = query.Where(e => !queryExcludePersonTypes.Contains(e.People.PersonType));
        }

        if (filter.MaxListOrder.HasValue)
        {
            query = query.Where(e => e.ListOrder <= filter.MaxListOrder.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.NameContains))
        {
            var nameContainsUpper = filter.NameContains.ToUpper();
            query = query.Where(e => e.People.Name.ToUpper().Contains(nameContainsUpper));
        }

        return query;
    }

    private bool IsAlphaNumeric(string str)
    {
        if (string.IsNullOrWhiteSpace(str))
        {
            return false;
        }

        for (int i = 0; i < str.Length; i++)
        {
            if (!char.IsLetter(str[i]) && !char.IsNumber(str[i]))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsValidPersonType(string value)
    {
        return IsAlphaNumeric(value);
    }
}
