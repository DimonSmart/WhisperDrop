using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperDrop.Models;

public interface ISelectedModelAvailability
{
    string GetModelPath(string modelsFolder, string modelId);
    bool IsAvailable(string modelsFolder, string modelId);
}

public static class ModelIntegrity
{
    public static async Task<bool> VerifyAsync(string path, RecognitionModel model, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            return stream.Length == model.SizeBytes &&
                Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

public sealed class SelectedModelAvailability(IRecognitionModelCatalog catalog) : ISelectedModelAvailability
{
    public string GetModelPath(string modelsFolder, string modelId) => Path.Combine(modelsFolder, catalog.Get(modelId).FileName);
    // A downloaded model is checked against the pinned SHA-256 once, before installation.
    // Availability thereafter uses only file metadata; never hash 740 MB on startup.
    public bool IsAvailable(string modelsFolder, string modelId)
    {
        var model = catalog.Get(modelId);
        try
        {
            var file = new FileInfo(Path.Combine(modelsFolder, model.FileName));
            return file.Exists && file.Length == model.SizeBytes;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
