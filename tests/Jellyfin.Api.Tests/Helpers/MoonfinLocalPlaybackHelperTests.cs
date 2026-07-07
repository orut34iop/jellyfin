using System;
using System.IO;
using Jellyfin.Api.Helpers;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers
{
    public static class MoonfinLocalPlaybackHelperTests
    {
        [Fact]
        public static void ApplyLocalFilePaths_LocalMoonfinFileSource_UsesItemPath()
        {
            var path = Path.GetTempFileName();
            try
            {
                var item = CreateItem(path);
                var mediaSource = CreateMediaSource(item.Id, "/substituted/path.mkv");
                var info = CreatePlaybackInfo(mediaSource);
                var appliedCount = 0;

                MoonfinLocalPlaybackHelper.ApplyLocalFilePaths(
                    info,
                    item,
                    _ => null,
                    true,
                    "Moonfin for macOS",
                    (_, _) => appliedCount++);

                Assert.Equal(path, mediaSource.Path);
                Assert.Equal(1, appliedCount);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public static void ApplyLocalFilePaths_AlternateMediaSourceItem_UsesResolvedItemPath()
        {
            var currentPath = Path.GetTempFileName();
            var alternatePath = Path.GetTempFileName();
            try
            {
                var currentItem = CreateItem(currentPath);
                var alternateItem = CreateItem(alternatePath);
                var mediaSource = CreateMediaSource(alternateItem.Id, "/substituted/alternate.mkv");
                var info = CreatePlaybackInfo(mediaSource);

                MoonfinLocalPlaybackHelper.ApplyLocalFilePaths(
                    info,
                    currentItem,
                    id => id.Equals(alternateItem.Id) ? alternateItem : null,
                    true,
                    "Moonfin",
                    null);

                Assert.Equal(alternatePath, mediaSource.Path);
            }
            finally
            {
                File.Delete(currentPath);
                File.Delete(alternatePath);
            }
        }

        [Theory]
        [InlineData(false, "Moonfin for macOS", MediaProtocol.File)]
        [InlineData(true, "Jellyfin Web", MediaProtocol.File)]
        [InlineData(true, null, MediaProtocol.File)]
        [InlineData(true, "Moonfin for macOS", MediaProtocol.Http)]
        public static void ApplyLocalFilePaths_IneligibleRequest_DoesNotChangePath(
            bool isLocalRequest,
            string? client,
            MediaProtocol protocol)
        {
            var path = Path.GetTempFileName();
            const string OriginalPath = "/substituted/path.mkv";
            try
            {
                var item = CreateItem(path);
                var mediaSource = CreateMediaSource(item.Id, OriginalPath);
                mediaSource.Protocol = protocol;
                var info = CreatePlaybackInfo(mediaSource);

                MoonfinLocalPlaybackHelper.ApplyLocalFilePaths(
                    info,
                    item,
                    _ => null,
                    isLocalRequest,
                    client,
                    (_, _) => throw new InvalidOperationException("The local path should not be applied."));

                Assert.Equal(OriginalPath, mediaSource.Path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public static void ApplyLocalFilePaths_MissingFile_DoesNotChangePath()
        {
            var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            const string OriginalPath = "/substituted/path.mkv";
            var item = CreateItem(missingPath);
            var mediaSource = CreateMediaSource(item.Id, OriginalPath);
            var info = CreatePlaybackInfo(mediaSource);

            MoonfinLocalPlaybackHelper.ApplyLocalFilePaths(
                info,
                item,
                _ => null,
                true,
                "Moonfin for macOS",
                (_, _) => throw new InvalidOperationException("The local path should not be applied."));

            Assert.Equal(OriginalPath, mediaSource.Path);
        }

        private static BaseItem CreateItem(string path)
            => new Movie
            {
                Id = Guid.NewGuid(),
                Path = path
            };

        private static MediaSourceInfo CreateMediaSource(Guid id, string path)
            => new()
            {
                Id = id.ToString("N"),
                Path = path,
                Protocol = MediaProtocol.File
            };

        private static PlaybackInfoResponse CreatePlaybackInfo(MediaSourceInfo mediaSource)
            => new()
            {
                MediaSources = new[] { mediaSource }
            };
    }
}
