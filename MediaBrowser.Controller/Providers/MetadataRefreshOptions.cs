#nullable disable

#pragma warning disable CA1819, CS1591

using System;
using System.Linq;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Providers;

namespace MediaBrowser.Controller.Providers
{
    public class MetadataRefreshOptions : ImageRefreshOptions
    {
        public MetadataRefreshOptions(IDirectoryService directoryService)
            : base(directoryService)
        {
            MetadataRefreshMode = MetadataRefreshMode.Default;
        }

        public MetadataRefreshOptions(MetadataRefreshOptions copy)
            : base(copy.DirectoryService)
        {
            CopyFrom(copy);
        }

        public MetadataRefreshOptions(MetadataRefreshOptions copy, IDirectoryService directoryService)
            : base(directoryService)
        {
            CopyFrom(copy);
        }

        /// <summary>
        /// Gets or sets a value indicating whether all existing data should be overwritten with new data from providers
        /// when paired with MetadataRefreshMode=FullRefresh.
        /// </summary>
        public bool ReplaceAllMetadata { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether all existing trickplay images should be overwritten
        /// when paired with MetadataRefreshMode=FullRefresh.
        /// </summary>
        public bool RegenerateTrickplay { get; set; }

        public MetadataRefreshMode MetadataRefreshMode { get; set; }

        public RemoteSearchResult SearchResult { get; set; }

        public string[] RefreshPaths { get; set; }

        public bool ForceSave { get; set; }

        public bool EnableRemoteContentProbe { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to update only technical media information for playback,
        /// without importing descriptive metadata or marking a full metadata refresh as complete.
        /// </summary>
        public bool RefreshMediaInfoOnly { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether only external streams should be rediscovered,
        /// without probing the media file or replacing embedded streams, attachments or chapters.
        /// </summary>
        public bool RefreshExternalStreamsOnly { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a playback probe should be skipped if another
        /// request has already persisted the required primary stream.
        /// </summary>
        public bool ProbeMissingStreamsOnly { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the shortcut target was confirmed to have changed.
        /// Technical information belonging to its previous target must not be reused.
        /// </summary>
        public bool MediaSourceChanged { get; set; }

        /// <summary>
        /// Creates a refresh limited to external audio and subtitle discovery.
        /// </summary>
        /// <param name="directoryService">A directory service with current filesystem contents.</param>
        /// <returns>The refresh options.</returns>
        public static MetadataRefreshOptions ForExternalStreams(IDirectoryService directoryService)
            => new(directoryService)
            {
                RefreshMediaInfoOnly = true,
                RefreshExternalStreamsOnly = true,
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.None
            };

        public bool RefreshItem(BaseItem item)
        {
            if (RefreshPaths is not null && RefreshPaths.Length > 0)
            {
                return RefreshPaths.Contains(item.Path ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private void CopyFrom(MetadataRefreshOptions copy)
        {
            MetadataRefreshMode = copy.MetadataRefreshMode;
            ForceSave = copy.ForceSave;
            ReplaceAllMetadata = copy.ReplaceAllMetadata;
            EnableRemoteContentProbe = copy.EnableRemoteContentProbe;
            RefreshMediaInfoOnly = copy.RefreshMediaInfoOnly;
            RefreshExternalStreamsOnly = copy.RefreshExternalStreamsOnly;
            ProbeMissingStreamsOnly = copy.ProbeMissingStreamsOnly;
            MediaSourceChanged = copy.MediaSourceChanged;

            IsAutomated = copy.IsAutomated;
            ImageRefreshMode = copy.ImageRefreshMode;
            ReplaceAllImages = copy.ReplaceAllImages;
            RegenerateTrickplay = copy.RegenerateTrickplay;
            ReplaceImages = copy.ReplaceImages;
            SearchResult = copy.SearchResult;
            RemoveOldMetadata = copy.RemoveOldMetadata;

            if (copy.RefreshPaths is not null && copy.RefreshPaths.Length > 0)
            {
                RefreshPaths ??= Array.Empty<string>();

                RefreshPaths = copy.RefreshPaths.ToArray();
            }
        }
    }
}
