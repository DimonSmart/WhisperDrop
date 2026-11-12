using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using WhisperDrop.Models;
namespace WhisperDrop.Tests;
// Tiny pinned artifact for unit tests; production continues to use the immutable 740 MB pin.
internal sealed class TestModelCatalog : IRecognitionModelCatalog
{
    internal static readonly byte[] Bytes = System.Text.Encoding.UTF8.GetBytes("model");
    internal static RecognitionModel Model => new(ParakeetModelCatalog.ModelId, "Parakeet Ultra Q8_0", "5 bytes",
        "parakeet-ultra-Q8_0.gguf", "test", "https://example.invalid/model", Bytes.Length,
        Convert.ToHexString(SHA256.HashData(Bytes)));
    public IReadOnlyList<RecognitionModel> Models => [Model];
    public RecognitionModel Get(string id) => id == Model.Id ? Model : throw new ArgumentOutOfRangeException(nameof(id));
}
