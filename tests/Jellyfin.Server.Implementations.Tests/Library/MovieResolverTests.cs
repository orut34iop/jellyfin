using System;
using Emby.Naming.Common;
using Emby.Server.Implementations.Library.Resolvers.Movies;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class MovieResolverTests
{
    private static readonly NamingOptions _namingOptions = new();

    [Fact]
    public void Resolve_GivenLocalAlternateVersion_ResolvesToVideo()
    {
        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>());
        var itemResolveArgs = new ItemResolveArgs(
            Mock.Of<IServerApplicationPaths>(),
            null)
        {
            Parent = null,
            FileInfo = new FileSystemMetadata
            {
                FullName = "/movies/Black Panther (2018)/Black Panther (2018) - 1080p 3D.mk3d"
            }
        };

        Assert.NotNull(movieResolver.Resolve(itemResolveArgs));
    }

    [Fact]
    public void ResolveMultiple_GivenEpisodeUnderSeason_AssignsSeriesAndSeasonIds()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var seriesName = "Test Series";
        var seasonName = "Season 1";
        var season = new Season
        {
            Id = seasonId,
            Name = seasonName,
            IndexNumber = 1,
            SeriesId = seriesId,
            SeriesName = seriesName
        };

        var movieResolver = new MovieResolver(Mock.Of<IImageProcessor>(), Mock.Of<ILogger<MovieResolver>>(), _namingOptions, Mock.Of<IDirectoryService>());
        var result = movieResolver.ResolveMultiple(
            season,
            [
                new FileSystemMetadata
                {
                    FullName = "/tv/Test Series/Season 1/Test Series S01E01.mkv",
                    Name = "Test Series S01E01.mkv"
                }
            ],
            CollectionType.tvshows,
            Mock.Of<IDirectoryService>());

        var episode = Assert.IsType<Episode>(Assert.Single(result.Items));
        Assert.Equal(seriesId, episode.SeriesId);
        Assert.Equal(seriesName, episode.SeriesName);
        Assert.Equal(seasonId, episode.SeasonId);
        Assert.Equal(seasonName, episode.SeasonName);
        Assert.Equal(1, episode.ParentIndexNumber);
    }
}
