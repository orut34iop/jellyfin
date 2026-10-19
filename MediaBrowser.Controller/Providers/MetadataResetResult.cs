namespace MediaBrowser.Controller.Providers;

/// <summary>The outcome of a synchronous, scoped metadata reset.</summary>
public enum MetadataResetResult
{
    /// <summary>The selected fields were committed.</summary>
    Success,
    /// <summary>The item no longer exists.</summary>
    NotFound,
    /// <summary>The fields or provider selection are unsupported or ambiguous.</summary>
    InvalidRequest,
    /// <summary>The provider did not return usable values for every selected field.</summary>
    NoMetadata,
    /// <summary>The provider failed; no values were committed.</summary>
    ProviderFailed
}
