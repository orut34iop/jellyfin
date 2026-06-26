using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Server.Implementations.IO;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.IO;

public class ManagedFileSystemTests
{
    private readonly IFixture _fixture;
    private readonly ManagedFileSystem _sut;

    public ManagedFileSystemTests()
    {
        _fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        _sut = _fixture.Create<ManagedFileSystem>();
    }

    [Fact]
    public void MoveDirectory_SameFileSystem_Correct()
        => MoveDirectoryInternal();

    [SkippableFact]
    public void MoveDirectory_DifferentFileSystem_Correct()
    {
        const string DestinationParent = "/dev/shm";

        Skip.IfNot(Directory.Exists(DestinationParent));

        MoveDirectoryInternal(DestinationParent);
    }

    internal void MoveDirectoryInternal(string? destinationParent = null)
    {
        const string TempFile0 = "tempfile0";
        const string TempFile1 = "tempfile1";

        destinationParent ??= Path.GetTempPath();

        var sourceDir = Directory.CreateTempSubdirectory();
        var destinationDir = Path.Join(destinationParent, Path.GetRandomFileName());
        FileHelper.CreateEmpty(Path.Join(sourceDir.FullName, TempFile0));
        FileHelper.CreateEmpty(Path.Join(sourceDir.FullName, TempFile1));

        _sut.MoveDirectory(sourceDir.FullName, destinationDir);

        Assert.True(Directory.Exists(destinationDir));
        Assert.True(File.Exists(Path.Join(destinationDir, TempFile0)));
        Assert.True(File.Exists(Path.Join(destinationDir, TempFile1)));
        Assert.False(Directory.Exists(sourceDir.FullName));

        Directory.Delete(destinationDir, true);
    }

    [SkippableTheory]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "../Beethoven/Misc/Moonlight Sonata.mp3", "/Volumes/Library/Sample/Music/Beethoven/Misc/Moonlight Sonata.mp3")]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "../../Beethoven/Misc/Moonlight Sonata.mp3", "/Volumes/Library/Sample/Beethoven/Misc/Moonlight Sonata.mp3")]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "Beethoven/Misc/Moonlight Sonata.mp3", "/Volumes/Library/Sample/Music/Playlists/Beethoven/Misc/Moonlight Sonata.mp3")]
    [InlineData("/Volumes/Library/Sample/Music/Playlists/", "/mnt/Beethoven/Misc/Moonlight Sonata.mp3", "/mnt/Beethoven/Misc/Moonlight Sonata.mp3")]
    public void MakeAbsolutePathCorrectlyHandlesRelativeFilePathsOnUnixLike(
        string folderPath,
        string filePath,
        string expectedAbsolutePath)
    {
        Skip.If(OperatingSystem.IsWindows());

        var generatedPath = _sut.MakeAbsolutePath(folderPath, filePath);
        Assert.Equal(expectedAbsolutePath, generatedPath);
    }

    [SkippableTheory]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"..\Beethoven\Misc\Moonlight Sonata.mp3", @"C:\Volumes\Library\Sample\Music\Beethoven\Misc\Moonlight Sonata.mp3")]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"..\..\Beethoven\Misc\Moonlight Sonata.mp3", @"C:\Volumes\Library\Sample\Beethoven\Misc\Moonlight Sonata.mp3")]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"Beethoven\Misc\Moonlight Sonata.mp3", @"C:\Volumes\Library\Sample\Music\Playlists\Beethoven\Misc\Moonlight Sonata.mp3")]
    [InlineData(@"C:\\Volumes\Library\Sample\Music\Playlists\", @"D:\\Beethoven\Misc\Moonlight Sonata.mp3", @"D:\\Beethoven\Misc\Moonlight Sonata.mp3")]
    public void MakeAbsolutePathCorrectlyHandlesRelativeFilePathsOnWindows(
        string folderPath,
        string filePath,
        string expectedAbsolutePath)
    {
        Skip.IfNot(OperatingSystem.IsWindows());

        var generatedPath = _sut.MakeAbsolutePath(folderPath, filePath);

        Assert.Equal(expectedAbsolutePath, generatedPath);
    }

    [Theory]
    [InlineData("ValidFileName", "ValidFileName")]
    [InlineData("AC/DC", "AC DC")]
    [InlineData("Invalid\0", "Invalid ")]
    [InlineData("AC/DC\0KD/A", "AC DC KD A")]
    public void GetValidFilename_ReturnsValidFilename(string filename, string expectedFileName)
    {
        Assert.Equal(expectedFileName, _sut.GetValidFilename(filename));
    }

    [SkippableFact]
    public void GetFileInfo_DanglingSymlink_ExistsFalse()
    {
        Skip.If(OperatingSystem.IsWindows());

        string testFileDir = Path.Combine(Path.GetTempPath(), "jellyfin-test-data");
        string testFileName = Path.Combine(testFileDir, Path.GetRandomFileName() + "-danglingsym.link");

        try
        {
            Directory.CreateDirectory(testFileDir);
            Assert.Equal(0, symlink("thispathdoesntexist", testFileName));
            Assert.True(File.Exists(testFileName));

            var metadata = _sut.GetFileInfo(testFileName);
            Assert.False(metadata.Exists);
        }
        finally
        {
            File.Delete(testFileName);
        }
    }

    [SkippableFact]
    public void GetFileSystemInfo_LocalMetadataOnlyImportDanglingVideoSymlink_ExistsWithPlaceholderMetadata()
    {
        Skip.If(OperatingSystem.IsWindows());

        string testFileDir = Path.Combine(Path.GetTempPath(), "jellyfin-test-data");
        string testFileName = Path.Combine(testFileDir, Path.GetRandomFileName() + ".iso");

        try
        {
            Directory.CreateDirectory(testFileDir);
            Assert.Equal(0, symlink("thispathdoesntexist", testFileName));
            Assert.True(File.Exists(testFileName));

            var metadata = _sut.GetFileSystemInfo(testFileName, skipResolvingVideoSymlinks: true);

            Assert.True(metadata.Exists);
            Assert.False(metadata.IsDirectory);
            Assert.Equal(LocalMetadataOnlyImportPolicy.PlaceholderVideoLength, metadata.Length);
            Assert.Equal(LocalMetadataOnlyImportPolicy.StableFileTimestampUtc, metadata.CreationTimeUtc);
            Assert.Equal(LocalMetadataOnlyImportPolicy.StableFileTimestampUtc, metadata.LastWriteTimeUtc);
        }
        finally
        {
            File.Delete(testFileName);
        }
    }

    [Fact]
    public void GetFileInfo_LocalMetadataOnlyImportVideoFile_ReturnsPlaceholderWithoutChangingDefault()
    {
        string testFileDir = Path.Combine(Path.GetTempPath(), "jellyfin-test-data", Path.GetRandomFileName());
        string videoPath = Path.Combine(testFileDir, "movie.mkv");

        try
        {
            Directory.CreateDirectory(testFileDir);
            File.WriteAllBytes(videoPath, [0, 1, 2, 3]);

            var defaultMetadata = _sut.GetFileSystemInfo(videoPath);
            Assert.True(defaultMetadata.Exists);
            Assert.Equal(4, defaultMetadata.Length);

            var localMetadataOnlyMetadata = _sut.GetFileSystemInfo(videoPath, true);
            Assert.True(localMetadataOnlyMetadata.Exists);
            Assert.False(localMetadataOnlyMetadata.IsDirectory);
            Assert.Equal(LocalMetadataOnlyImportPolicy.PlaceholderVideoLength, localMetadataOnlyMetadata.Length);
            Assert.Equal(LocalMetadataOnlyImportPolicy.StableFileTimestampUtc, localMetadataOnlyMetadata.CreationTimeUtc);
            Assert.Equal(LocalMetadataOnlyImportPolicy.StableFileTimestampUtc, localMetadataOnlyMetadata.LastWriteTimeUtc);
        }
        finally
        {
            if (Directory.Exists(testFileDir))
            {
                Directory.Delete(testFileDir, true);
            }
        }
    }

    [SkippableFact]
    public void GetFileSystemEntries_LocalMetadataOnlyImportDanglingVideoSymlink_ReturnsPlaceholderWithLocalMetadataFiles()
    {
        Skip.If(OperatingSystem.IsWindows());

        string testFileDir = Path.Combine(Path.GetTempPath(), "jellyfin-test-data", Path.GetRandomFileName());
        string videoPath = Path.Combine(testFileDir, "movie.iso");
        string nfoPath = Path.Combine(testFileDir, "movie.nfo");
        string posterPath = Path.Combine(testFileDir, "movie-poster.jpg");

        try
        {
            Directory.CreateDirectory(testFileDir);
            Assert.Equal(0, symlink("thispathdoesntexist", videoPath));
            File.WriteAllText(nfoPath, "<movie><title>Local Movie</title></movie>");
            FileHelper.CreateEmpty(posterPath);

            var metadata = _sut.GetFileSystemEntries(testFileDir, false, true).ToArray();

            var video = Assert.Single(metadata, entry => string.Equals(entry.FullName, videoPath, StringComparison.Ordinal));
            Assert.True(video.Exists);
            Assert.False(video.IsDirectory);
            Assert.Equal(LocalMetadataOnlyImportPolicy.PlaceholderVideoLength, video.Length);
            Assert.Equal(LocalMetadataOnlyImportPolicy.StableFileTimestampUtc, video.CreationTimeUtc);
            Assert.Equal(LocalMetadataOnlyImportPolicy.StableFileTimestampUtc, video.LastWriteTimeUtc);

            var directVideo = _sut.GetFileSystemInfo(videoPath, true);
            Assert.True(directVideo.Exists);
            Assert.False(directVideo.IsDirectory);
            Assert.Equal(LocalMetadataOnlyImportPolicy.PlaceholderVideoLength, directVideo.Length);

            Assert.Contains(metadata, entry => string.Equals(entry.FullName, nfoPath, StringComparison.Ordinal) && entry.Exists);
            Assert.Contains(metadata, entry => string.Equals(entry.FullName, posterPath, StringComparison.Ordinal) && entry.Exists);
        }
        finally
        {
            if (Directory.Exists(testFileDir))
            {
                Directory.Delete(testFileDir, true);
            }
        }
    }

    [SuppressMessage("Naming Rules", "SA1300:ElementMustBeginWithUpperCaseLetter", Justification = "Have to")]
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.UserDirectories)]
    private static extern int symlink(string target, string linkpath);
}
