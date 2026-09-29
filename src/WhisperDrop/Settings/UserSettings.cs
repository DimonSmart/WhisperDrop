namespace WhisperDrop.Settings;

public sealed record UserSettings
{
    public string? ModelsFolder { get; init; }

    public string SelectedModelId { get; init; } = "base";

    public string RecognitionLanguageCode { get; init; } = "auto";
}
