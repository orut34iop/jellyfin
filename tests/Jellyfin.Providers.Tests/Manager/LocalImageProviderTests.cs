using System.IO;
using System.Linq;
using Emby.Naming.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.LocalMetadata.Images;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.Manager;

public class LocalImageProviderTests
{
    public LocalImageProviderTests()
    {
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>();
    }

    [Theory]
    [InlineData("CD1", "CD2")]
    [InlineData("cd1", "cd2")]
    [InlineData("dvd1", "dvd2")]
    [InlineData("part1", "part2")]
    [InlineData("pt1", "pt2")]
    [InlineData("disc1", "disc2")]
    [InlineData("disk1", "disk2")]
    [InlineData("part-a", "part-b")]
    [InlineData("(disc 1)", "(disc 2)")]
    public void GetImages_StackParts_FindSharedArtwork(string firstPart, string secondPart)
    {
        var names = new[] { $"SW-622-{firstPart}.mp4", $"SW-622-{secondPart}.mp4", "SW-622-poster.jpg", "SW-622-fanart.jpg" };
        foreach (var part in names.Take(2))
        {
            var images = GetImages(part, names);

            Assert.Equal("SW-622-poster.jpg", Assert.Single(images, image => image.Type == ImageType.Primary).FileInfo.Name);
            Assert.Equal("SW-622-fanart.jpg", Assert.Single(images, image => image.Type == ImageType.Backdrop).FileInfo.Name);
        }
    }

    [Theory]
    [InlineData("SW-622-CD1-poster.jpg", "SW-622-CD1-fanart.jpg", true)]
    [InlineData("poster.jpg", "fanart.jpg", false)]
    public void GetImages_ExistingArtwork_KeepsPriority(string poster, string backdrop, bool mixed)
    {
        var images = GetImages("SW-622-CD1.mp4", ["SW-622-CD1.mp4", "SW-622-CD2.mp4", "SW-622-poster.jpg", "SW-622-fanart.jpg", poster, backdrop], mixed);

        Assert.Equal(poster, Assert.Single(images, image => image.Type == ImageType.Primary).FileInfo.Name);
        Assert.Equal(backdrop, Assert.Single(images, image => image.Type == ImageType.Backdrop).FileInfo.Name);
    }

    [Theory]
    [InlineData("SW-622-CD1-poster.jpg", ImageType.Backdrop, "SW-622-fanart.jpg")]
    [InlineData("SW-622-CD1-fanart.jpg", ImageType.Primary, "SW-622-poster.jpg")]
    public void GetImages_OneImageTypeMissing_FillsOnlyMissingType(string existing, ImageType missingType, string expected)
    {
        var images = GetImages("SW-622-CD1.mp4", ["SW-622-CD1.mp4", "SW-622-CD2.mp4", "SW-622-poster.jpg", "SW-622-fanart.jpg", existing]);

        Assert.Equal(2, images.Length);
        Assert.Contains(images, image => image.FileInfo.Name == existing);
        Assert.Equal(expected, Assert.Single(images, image => image.Type == missingType).FileInfo.Name);
    }

    [Theory]
    [InlineData("SW-622-CD1.mp4", "Other-CD2.mp4")]
    [InlineData("SW-622-CD1.mp4", "SW-622-part2.mp4")]
    [InlineData("SW-622-CD1.mp4", "SW-622-CD1.mkv")]
    public void GetImages_NoMatchingStack_DoesNotUseSharedArtwork(string first, string second)
    {
        var images = GetImages(first, [first, second, "SW-622-poster.jpg", "SW-622-fanart.jpg"]);

        Assert.Empty(images);
    }

    [Fact]
    public void GetImages_StackInMixedFolder_DoesNotUseOtherMoviesOrGenericArtwork()
    {
        var images = GetImages("SW-622-CD1.mp4", ["SW-622-CD1.mp4", "SW-622-CD2.mp4", "Other-poster.jpg", "Other-fanart.jpg", "poster.jpg", "fanart.jpg"]);

        Assert.Empty(images);
    }

    [Fact]
    public void GetImages_Stack_PreservesFullTitleAndNumberedBackdrops()
    {
        var images = GetImages("Movie-Part1-1080p-CD1.mp4", ["Movie-Part1-1080p-CD1.mp4", "Movie-Part1-1080p-CD2.mp4", "Movie-Part1-1080p-poster.jpg", "Movie-Part1-1080p-fanart.jpg", "Movie-Part1-1080p-fanart-1.jpg", "Movie-poster.jpg"]);

        Assert.Equal("Movie-Part1-1080p-poster.jpg", Assert.Single(images, image => image.Type == ImageType.Primary).FileInfo.Name);
        Assert.Equal(2, images.Count(image => image.Type == ImageType.Backdrop));
    }

    [Fact]
    public void GetImages_EmptySharedArtwork_IsIgnored()
    {
        var images = GetImages("SW-622-CD1.mp4", ["SW-622-CD1.mp4", "SW-622-CD2.mp4", "SW-622-poster.jpg", "SW-622-fanart.jpg"], imageLength: 0);

        Assert.Empty(images);
    }

    private static LocalImageInfo[] GetImages(string videoName, string[] names, bool mixed = true, long imageLength = 1)
    {
        var directory = Path.GetTempPath();
        var files = names.Select(name => new FileSystemMetadata
        {
            Name = name,
            FullName = Path.Combine(directory, name),
            Extension = Path.GetExtension(name),
            Length = imageLength
        }).ToArray();
        var directoryService = new Mock<IDirectoryService>();
        directoryService.Setup(service => service.GetFileSystemEntries(It.IsAny<string>())).Returns(files);
        var item = new Movie
        {
            Path = Path.Combine(directory, videoName),
            VideoType = VideoType.VideoFile,
            IsInMixedFolder = mixed
        };
        var provider = new LocalImageProvider(Mock.Of<IFileSystem>(), new NamingOptions());

        return provider.GetImages(item, directoryService.Object).ToArray();
    }
}
