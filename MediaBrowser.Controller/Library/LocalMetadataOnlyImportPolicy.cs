#nullable enable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Central policy for the local metadata only import mode.
/// </summary>
public static class LocalMetadataOnlyImportPolicy
{
    public const string EnvironmentVariableName = "JELLYFIN_LOCAL_METADATA_ONLY_IMPORT";

    public const long PlaceholderVideoLength = 1;

    private static readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso",
        ".img",
        ".mkv",
        ".mp4",
        ".m4v",
        ".mov",
        ".avi",
        ".wmv",
        ".webm",
        ".m2ts",
        ".ts",
        ".mpeg",
        ".mpg",
        ".flv"
    };

    public static DateTime StableFileTimestampUtc { get; } = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static bool IsEnabled(LibraryOptions? libraryOptions)
        => IsEnvironmentEnabled() || libraryOptions?.LocalMetadataOnlyImport == true;

    public static bool IsEnabledForItem(BaseItem? item, ILibraryManager? libraryManager)
    {
        if (IsEnvironmentEnabled())
        {
            return true;
        }

        return item is not null
            && libraryManager is not null
            && libraryManager.GetLibraryOptions(item).LocalMetadataOnlyImport;
    }

    public static bool IsEnvironmentEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        return bool.TryParse(value, out var enabled)
            ? enabled
            : string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
              || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVideoLikePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return _videoExtensions.Contains(Path.GetExtension(path));
    }

    public static bool IsRemoteHttpPath(string? path)
        => path is not null
           && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    public static bool CanImportImagePath(string? path, bool localMetadataOnlyImport)
        => !string.IsNullOrWhiteSpace(path)
           && (!localMetadataOnlyImport || !IsRemoteHttpPath(path));
}
