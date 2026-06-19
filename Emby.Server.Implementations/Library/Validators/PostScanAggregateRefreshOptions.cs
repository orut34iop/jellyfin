using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;

namespace Emby.Server.Implementations.Library.Validators;

internal static class PostScanAggregateRefreshOptions
{
    public static bool HasLocalMetadataOnlyImportLibrary(
        IEnumerable<BaseItem> libraries,
        Func<BaseItem, LibraryOptions> getLibraryOptions)
        => libraries.Any(library => LocalMetadataOnlyImportPolicy.IsEnabled(getLibraryOptions(library)));

    public static MetadataRefreshOptions CreateValidationOnly()
        => new(new DirectoryService(BaseItem.FileSystem))
        {
            ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
            MetadataRefreshMode = MetadataRefreshMode.ValidationOnly
        };
}
