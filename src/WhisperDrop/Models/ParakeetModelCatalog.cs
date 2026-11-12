using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperDrop.Models;

public sealed record RecognitionModel(string Id, string DisplayName, string ApproximateSize,
    string FileName, string Revision, string DownloadUrl, long SizeBytes, string Sha256);

public interface IRecognitionModelCatalog
{
    IReadOnlyList<RecognitionModel> Models { get; }
    RecognitionModel Get(string id);
}

public sealed class ParakeetModelCatalog : IRecognitionModelCatalog
{
    public const string ModelId = "parakeet-ultra-q8_0";
    private static readonly IReadOnlyList<RecognitionModel> ModelList =
    [
        new(ModelId, "Parakeet Ultra Q8_0", "740,363,168 bytes", "parakeet-ultra-Q8_0.gguf",
            "a1539be1b849ea7463b88343e5277b373d0c4ea9",
            "https://huggingface.co/handy-computer/parakeet-ultra-gguf/resolve/a1539be1b849ea7463b88343e5277b373d0c4ea9/parakeet-ultra-Q8_0.gguf",
            740363168, "007a59761e9258779f189df396b50d12b83bb5b79e66df4b955c230d2f2a0a59")
    ];
    public IReadOnlyList<RecognitionModel> Models => ModelList;
    public RecognitionModel Get(string id) => ModelList.FirstOrDefault(m => m.Id == id)
        ?? throw new ArgumentOutOfRangeException(nameof(id), "The model is not in the Parakeet catalog.");
}
