namespace WhisperDrop.Models;

public sealed record RecognitionModelOption(
    string Id,
    string DisplayName,
    string ApproximateSize,
    bool IsInstalled,
    string? InstalledSizeText)
{
    public string InstalledSummary =>
        IsInstalled
            ? string.IsNullOrWhiteSpace(InstalledSizeText) ? "Installed" : $"Installed · {InstalledSizeText}"
            : string.Empty;
}
