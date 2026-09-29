using System;
using System.IO;

namespace WhisperDrop.Settings;

public interface IApplicationPaths
{
    string SettingsFilePath { get; }

    string DefaultModelsFolder { get; }
}

public sealed class ApplicationPaths : IApplicationPaths
{
    private readonly string applicationDataFolder;

    public ApplicationPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperDrop"))
    {
    }

    public ApplicationPaths(string applicationDataFolder)
    {
        this.applicationDataFolder = applicationDataFolder;
    }

    public string SettingsFilePath => Path.Combine(applicationDataFolder, "settings.json");

    public string DefaultModelsFolder => Path.Combine(applicationDataFolder, "Models");
}
