using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

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
            var document = JsonNode.Parse(File.ReadAllText(paths.SettingsFilePath)) as JsonObject;
            if (document is null) return Defaults();
            if (document["AiPostProcessing"] is not null and not JsonObject) document["AiPostProcessing"] = null;
            var settings = document.Deserialize<UserSettings>(SerializerOptions);
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
        catch (UnauthorizedAccessException)
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
        options.Converters.Add(new LenientEnumConverter<ProcessingDevice>());
        options.Converters.Add(new NullableIntegerConverter());
        options.Converters.Add(new LenientStringConverter());
        options.Converters.Add(new LenientBooleanConverter());
        options.Converters.Add(new LenientEnumConverter<AiProviderPreset>());
        return options;
    }

    private UserSettings Defaults() => new() { ModelsFolder = paths.DefaultModelsFolder };

    private UserSettings Normalize(UserSettings? settings)
    {
        var processingDevice = settings is not null && Enum.IsDefined(typeof(ProcessingDevice), settings.ProcessingDevice)
            ? settings.ProcessingDevice
            : ProcessingDevice.Auto;
        int? cpuThreads = settings?.CpuThreads is int threads && threads >= 1 && threads <= Environment.ProcessorCount
            ? threads
            : null;
        var aiPostProcessing = NormalizeAiPostProcessing(settings?.AiPostProcessing);

        return new UserSettings
        {
            ModelsFolder = string.IsNullOrWhiteSpace(settings?.ModelsFolder) ? paths.DefaultModelsFolder : settings.ModelsFolder,
            SelectedModelId = Models.ParakeetModelCatalog.ModelId,
            ProcessingDevice = processingDevice,
            CpuThreads = cpuThreads,
            AiPostProcessing = aiPostProcessing
        };
    }

    private static AiPostProcessingSettings NormalizeAiPostProcessing(AiPostProcessingSettings? settings)
    {
        var provider = settings is not null && Enum.IsDefined(typeof(AiProviderPreset), settings.Provider)
            ? settings.Provider
            : AiProviderPreset.Ollama;
        var endpoint = NormalizeEndpoint(settings?.Endpoint);
        var model = string.IsNullOrWhiteSpace(settings?.Model) ? "gpt-oss:20b" : settings.Model.Trim();
        var contextSize = settings?.ContextSize is > 0 ? settings.ContextSize : null;

        return new AiPostProcessingSettings
        {
            Enabled = settings?.Enabled ?? false,
            Provider = provider,
            Endpoint = endpoint,
            Model = model,
            Instructions = settings?.Instructions ?? string.Empty,
            ContextSize = contextSize
        };
    }

    private static string NormalizeEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "http://localhost:11434/v1/";
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.AbsoluteUri.TrimEnd('/') + "/";
        }

        return trimmed;
    }

    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString()!;
            reader.Skip();
            return string.Empty;
        }
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
    private sealed class LenientBooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return reader.GetBoolean();
            reader.Skip();
            return false;
        }
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    private sealed class NullableIntegerConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)) return value;
            reader.Skip();
            return null;
        }
        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value is int number) writer.WriteNumberValue(number); else writer.WriteNullValue();
        }
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

            reader.Skip();
            return default;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
