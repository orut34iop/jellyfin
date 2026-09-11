using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Helper methods for Moonfin local file playback.
/// </summary>
internal static class MoonfinLocalPlaybackHelper
{
    /// <summary>
    /// Tries to resolve the local file path that should be exposed to Moonfin.
    /// </summary>
    /// <param name="mediaSource">The media source.</param>
    /// <param name="item">The current item.</param>
    /// <param name="getItemById">Resolves alternate media source item ids.</param>
    /// <param name="isLocalRequest">Whether the request comes from the local host.</param>
    /// <param name="client">The authenticated client name.</param>
    /// <returns>The local file path, or <c>null</c> when the source should use normal Jellyfin streaming.</returns>
    internal static string? TryResolveLocalFilePath(
        MediaSourceInfo mediaSource,
        BaseItem item,
        Func<Guid, BaseItem?> getItemById,
        bool isLocalRequest,
        string? client)
    {
        if (!isLocalRequest
            || !IsMoonfinClient(client)
            || mediaSource.Protocol != MediaProtocol.File)
        {
            return null;
        }

        var sourceItem = item;
        if (Guid.TryParse(mediaSource.Id, out var mediaSourceItemId)
            && !mediaSourceItemId.Equals(item.Id))
        {
            sourceItem = getItemById(mediaSourceItemId) ?? item;
        }

        return string.IsNullOrWhiteSpace(sourceItem.Path) || !File.Exists(sourceItem.Path)
            ? null
            : sourceItem.Path;
    }

    /// <summary>
    /// Checks whether the request can use Moonfin local file playback without probing media streams.
    /// </summary>
    /// <param name="mediaSources">The media sources to inspect.</param>
    /// <param name="item">The current item.</param>
    /// <param name="getItemById">Resolves alternate media source item ids.</param>
    /// <param name="isLocalRequest">Whether the request comes from the local host.</param>
    /// <param name="client">The authenticated client name.</param>
    /// <param name="mediaSourceId">The requested media source id.</param>
    /// <returns><c>true</c> when media probing can be skipped.</returns>
    internal static bool ShouldSkipMediaProbe(
        IReadOnlyList<MediaSourceInfo> mediaSources,
        BaseItem item,
        Func<Guid, BaseItem?> getItemById,
        bool isLocalRequest,
        string? client,
        string? mediaSourceId)
    {
        var selectedMediaSources = string.IsNullOrWhiteSpace(mediaSourceId)
            ? mediaSources
            : mediaSources.Where(i => string.Equals(i.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));

        return selectedMediaSources.Any(mediaSource => TryResolveLocalFilePath(mediaSource, item, getItemById, isLocalRequest, client) is not null);
    }

    /// <summary>
    /// Applies local file paths to eligible Moonfin playback media sources.
    /// </summary>
    /// <param name="info">The playback info response.</param>
    /// <param name="item">The current item.</param>
    /// <param name="getItemById">Resolves alternate media source item ids.</param>
    /// <param name="isLocalRequest">Whether the request comes from the local host.</param>
    /// <param name="client">The authenticated client name.</param>
    /// <param name="onApplied">Called when a media source path is replaced.</param>
    internal static void ApplyLocalFilePaths(
        PlaybackInfoResponse info,
        BaseItem item,
        Func<Guid, BaseItem?> getItemById,
        bool isLocalRequest,
        string? client,
        Action<MediaSourceInfo, string>? onApplied = null)
    {
        foreach (var mediaSource in info.MediaSources)
        {
            var path = TryResolveLocalFilePath(mediaSource, item, getItemById, isLocalRequest, client);
            if (path is null)
            {
                continue;
            }

            mediaSource.Path = path;
            onApplied?.Invoke(mediaSource, path);
        }
    }

    private static bool IsMoonfinClient(string? client)
        => client?.Contains("Moonfin", StringComparison.OrdinalIgnoreCase) == true;
}
