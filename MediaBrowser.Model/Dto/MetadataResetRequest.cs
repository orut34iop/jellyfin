using MediaBrowser.Model.Entities;

namespace MediaBrowser.Model.Dto;

/// <summary>Explicitly resets selected fields from one enabled provider, retaining existing locks.</summary>
public sealed class MetadataResetRequest
{
    /// <summary>Gets or sets the fields to replace.</summary>
    public MetadataField[] Fields { get; set; } = [];

    /// <summary>Gets or sets the exact enabled provider name.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether to use a remote rather than local provider.</summary>
    public bool Remote { get; set; }

    /// <summary>Gets or sets a value indicating whether empty or omitted source values may clear selected fields.</summary>
    public bool AllowEmptyValues { get; set; }
}
