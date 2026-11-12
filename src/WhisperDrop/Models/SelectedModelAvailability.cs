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
    public static bool Verify(string path, RecognitionModel model)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length == model.SizeBytes &&
                Convert.ToHexString(SHA256.HashData(stream)).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

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
    public bool IsAvailable(string modelsFolder, string modelId) => ModelIntegrity.Verify(GetModelPath(modelsFolder, modelId), catalog.Get(modelId));
}
