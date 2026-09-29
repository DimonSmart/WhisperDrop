using System.IO;

namespace WhisperDrop.Models;

public interface ISelectedModelAvailability
{
    string GetModelPath(string modelsFolder, string modelId);

    bool IsAvailable(string modelsFolder, string modelId);
}

public sealed class SelectedModelAvailability : ISelectedModelAvailability
{
    private readonly IWhisperModelCatalog catalog;

    public SelectedModelAvailability(IWhisperModelCatalog catalog)
    {
        this.catalog = catalog;
    }

    public string GetModelPath(string modelsFolder, string modelId) =>
        Path.Combine(modelsFolder, catalog.Get(modelId).FileName);

    public bool IsAvailable(string modelsFolder, string modelId) =>
        File.Exists(GetModelPath(modelsFolder, modelId));
}
