using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Entities;

/// <summary>
/// Technical rows staged by a provider for the item's next database transaction.
/// Null collections retain existing rows; empty collections explicitly remove them.
/// </summary>
public sealed class MediaInfoSnapshot
{
    /// <summary>Gets or sets the replacement media streams.</summary>
    public IReadOnlyList<MediaStream>? Streams { get; set; }

    /// <summary>Gets or sets the replacement attachments.</summary>
    public IReadOnlyList<MediaAttachment>? Attachments { get; set; }

    /// <summary>Gets or sets the replacement chapters.</summary>
    public IReadOnlyList<ChapterInfo>? Chapters { get; set; }
}
