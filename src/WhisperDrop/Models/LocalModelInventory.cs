using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WhisperDrop.Models;

public sealed record LocalModelInfo(
    string Id,
    string DisplayName,
    string FileName,
    string FilePath,
    long SizeBytes)
{
    public string SizeText => FormatBytes(SizeBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024d:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.#} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
    }
}

public interface ILocalModelInventory
{
    IReadOnlyList<LocalModelInfo> GetInstalled(string modelsFolder);

    void Delete(string modelsFolder, string modelId);

    void DeleteAll(string modelsFolder);
}

public sealed class LocalModelInventory : ILocalModelInventory
{
    private readonly IWhisperModelCatalog catalog;
    private readonly ISelectedModelAvailability availability;

    public LocalModelInventory(IWhisperModelCatalog catalog, ISelectedModelAvailability availability)
    {
        this.catalog = catalog;
        this.availability = availability;
    }

    public IReadOnlyList<LocalModelInfo> GetInstalled(string modelsFolder) =>
        catalog.Models
            .Select(model => CreateInstalledModel(modelsFolder, model))
            .Where(model => model is not null)
            .Cast<LocalModelInfo>()
            .ToArray();

    public void Delete(string modelsFolder, string modelId)
    {
        var model = catalog.Get(modelId);
        var path = availability.GetModelPath(modelsFolder, model.Id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteAll(string modelsFolder)
    {
        foreach (var model in catalog.Models)
        {
            Delete(modelsFolder, model.Id);
        }
    }

    private LocalModelInfo? CreateInstalledModel(string modelsFolder, RecognitionModel model)
    {
        var path = availability.GetModelPath(modelsFolder, model.Id);
        if (!File.Exists(path))
        {
            return null;
        }

        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            size = 0;
        }
        catch (UnauthorizedAccessException)
        {
            size = 0;
        }

        return new LocalModelInfo(model.Id, model.DisplayName, model.FileName, path, size);
    }
}
