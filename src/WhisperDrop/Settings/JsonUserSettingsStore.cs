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
            return Defaults();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(paths.SettingsFilePath), SerializerOptions);
            return Normalize(settings);
        }
        catch (IOException)
        {
            return Defaults();
        }
        catch (JsonException)
        {
            return Defaults();
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

    private UserSettings Defaults() => new() { ModelsFolder = paths.DefaultModelsFolder };

    private UserSettings Normalize(UserSettings? settings)
    {
        var task = settings is not null && Enum.IsDefined(typeof(TranscriptionTask), settings.Task)
            ? settings.Task
            : TranscriptionTask.Transcribe;
        var processingDevice = settings is not null && Enum.IsDefined(typeof(ProcessingDevice), settings.ProcessingDevice)
            ? settings.ProcessingDevice
            : ProcessingDevice.Auto;
        var cpuThreads = settings?.CpuThreads is int threads && threads >= 1 && threads <= Environment.ProcessorCount
            ? threads
            : null;

        return new UserSettings
        {
            ModelsFolder = string.IsNullOrWhiteSpace(settings?.ModelsFolder) ? paths.DefaultModelsFolder : settings.ModelsFolder,
            SelectedModelId = string.IsNullOrWhiteSpace(settings?.SelectedModelId) ? "base" : settings.SelectedModelId,
            RecognitionLanguageCode = string.IsNullOrWhiteSpace(settings?.RecognitionLanguageCode) ? "auto" : settings.RecognitionLanguageCode,
            Task = task,
            VocabularyContext = settings?.VocabularyContext ?? string.Empty,
            SkipSilence = settings?.SkipSilence ?? false,
            ProcessingDevice = processingDevice,
            CpuThreads = cpuThreads
        };
    }
}
