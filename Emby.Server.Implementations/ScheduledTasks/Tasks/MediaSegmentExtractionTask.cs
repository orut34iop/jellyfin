using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Task to obtain media segments.
/// </summary>
public class MediaSegmentExtractionTask : IScheduledTask
{
    /// <summary>
    /// The library manager.
    /// </summary>
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private static readonly BaseItemKind[] _itemTypes = [BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Audio, BaseItemKind.AudioBook];

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentExtractionTask" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="localization">The localization manager.</param>
    /// <param name="mediaSegmentManager">The segment manager.</param>
    public MediaSegmentExtractionTask(ILibraryManager libraryManager, ILocalizationManager localization, IMediaSegmentManager mediaSegmentManager)
    {
        _libraryManager = libraryManager;
        _localization = localization;
        _mediaSegmentManager = mediaSegmentManager;
    }

    /// <inheritdoc/>
    public string Name => _localization.GetLocalizedString("TaskExtractMediaSegments");

    /// <inheritdoc/>
    public string Description => _localization.GetLocalizedString("TaskExtractMediaSegmentsDescription");

    /// <inheritdoc/>
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc/>
    public string Key => "TaskExtractMediaSegments";

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        progress.Report(0);

        var pagesize = 100;
        var libraries = _libraryManager.RootFolder.Children
            .Where(library => !LocalMetadataOnlyImportPolicy.IsEnabled(_libraryManager.GetLibraryOptions(library)))
            .ToArray();

        if (libraries.Length == 0)
        {
            progress.Report(100);
            return;
        }

        var query = new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video, MediaType.Audio],
            IsVirtualItem = false,
            IncludeItemTypes = _itemTypes,
            DtoOptions = new DtoOptions(true),
            SourceTypes = [SourceType.Library],
            Recursive = true,
            Limit = pagesize
        };

        var numberOfVideos = libraries.Sum(library =>
        {
            query.Parent = library;
            return _libraryManager.GetCount(query);
        });

        if (numberOfVideos == 0)
        {
            progress.Report(100);
            return;
        }

        var numComplete = 0;
        foreach (var library in libraries)
        {
            query.Parent = library;
            query.StartIndex = 0;

            while (true)
            {
                var baseItems = _libraryManager.GetItemList(query);
                var currentPageCount = baseItems.Count;
                if (currentPageCount == 0)
                {
                    break;
                }

                // TODO parallelize with Parallel.ForEach?
                for (var i = 0; i < currentPageCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var item = baseItems[i];
                    var libraryOptions = _libraryManager.GetLibraryOptions(item);

                    // Only local files supported
                    if (item.IsFileProtocol && File.Exists(item.Path))
                    {
                        await _mediaSegmentManager.RunSegmentPluginProviders(item, libraryOptions, false, cancellationToken).ConfigureAwait(false);
                    }

                    // Update progress
                    numComplete++;
                    double percent = (double)numComplete / numberOfVideos;
                    progress.Report(100 * percent);
                }

                if (currentPageCount < pagesize)
                {
                    break;
                }

                query.StartIndex += pagesize;
            }
        }

        progress.Report(100);
    }

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        };
    }
}
