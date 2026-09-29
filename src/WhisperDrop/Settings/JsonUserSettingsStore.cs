using System;
using System.IO;
using System.Text.Json;

namespace WhisperDrop.Settings;

public interface IUserSettingsStore
{
    UserSettings Load();

    void Save(UserSettings settings);
}

public sealed class JsonUserSettingsStore : IUserSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly IApplicationPaths paths;

    public JsonUserSettingsStore(IApplicationPaths paths)
    {
        this.paths = paths;
    }

    public UserSettings Load()
    {
        if (!File.Exists(paths.SettingsFilePath))
        {
            return new UserSettings { ModelsFolder = paths.DefaultModelsFolder };
        }

        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(paths.SettingsFilePath), SerializerOptions);
            return Normalize(settings);
        }
        catch (IOException)
        {
            return new UserSettings { ModelsFolder = paths.DefaultModelsFolder };
        }
        catch (JsonException)
        {
            return new UserSettings { ModelsFolder = paths.DefaultModelsFolder };
        }
    }

    public void Save(UserSettings settings)
    {
        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(paths.SettingsFilePath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(paths.SettingsFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, SerializerOptions));
            File.Move(temporaryPath, paths.SettingsFilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private UserSettings Normalize(UserSettings? settings) => new()
    {
        ModelsFolder = string.IsNullOrWhiteSpace(settings?.ModelsFolder) ? paths.DefaultModelsFolder : settings.ModelsFolder,
        SelectedModelId = string.IsNullOrWhiteSpace(settings?.SelectedModelId) ? "base" : settings.SelectedModelId,
        RecognitionLanguageCode = string.IsNullOrWhiteSpace(settings?.RecognitionLanguageCode) ? "auto" : settings.RecognitionLanguageCode
    };
}
