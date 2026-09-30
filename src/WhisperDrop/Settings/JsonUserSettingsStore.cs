using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperDrop.Settings;

public interface IUserSettingsStore
{
    UserSettings Load();

    void Save(UserSettings settings);
}

public sealed class JsonUserSettingsStore : IUserSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
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

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new LenientEnumConverter<TranscriptionTask>());
        options.Converters.Add(new LenientEnumConverter<ProcessingDevice>());
        return options;
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
        int? cpuThreads = settings?.CpuThreads is int threads && threads >= 1 && threads <= Environment.ProcessorCount
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

    private sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            if (reader.TokenType == JsonTokenType.Number &&
                reader.TryGetInt32(out var numeric))
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), numeric);
            }

            if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number)
            {
                return default;
            }

            throw new JsonException($"Expected a string or number for {typeof(TEnum).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
