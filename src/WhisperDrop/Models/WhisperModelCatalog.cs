using System;
using System.Collections.Generic;
using System.Linq;
using Whisper.net.Ggml;

namespace WhisperDrop.Models;

public sealed record RecognitionModel(
    string Id,
    string DisplayName,
    GgmlType GgmlType,
    bool IsEnglishOnly,
    string ApproximateSize,
    string FileName);

public interface IWhisperModelCatalog
{
    IReadOnlyList<RecognitionModel> Models { get; }

    RecognitionModel Get(string id);
}

public sealed class WhisperModelCatalog : IWhisperModelCatalog
{
    private static readonly IReadOnlyList<RecognitionModel> ModelList =
    [
        Model("tiny", "Tiny", GgmlType.Tiny, false, "75 MB"),
        Model("base", "Base", GgmlType.Base, false, "142 MB"),
        Model("small", "Small", GgmlType.Small, false, "466 MB"),
        Model("medium", "Medium", GgmlType.Medium, false, "1.5 GB"),
        Model("large-v3-turbo", "Large V3 Turbo", GgmlType.LargeV3Turbo, false, "1.6 GB"),
        Model("large-v3", "Large V3", GgmlType.LargeV3, false, "3.1 GB"),
        Model("tiny-en", "Tiny English", GgmlType.TinyEn, true, "75 MB"),
        Model("base-en", "Base English", GgmlType.BaseEn, true, "142 MB"),
        Model("small-en", "Small English", GgmlType.SmallEn, true, "466 MB"),
        Model("medium-en", "Medium English", GgmlType.MediumEn, true, "1.5 GB"),
        Model("large-v2", "Large V2", GgmlType.LargeV2, false, "3.1 GB"),
        Model("large-v1", "Large V1", GgmlType.LargeV1, false, "3.1 GB")
    ];

    public IReadOnlyList<RecognitionModel> Models => ModelList;

    public RecognitionModel Get(string id) =>
        ModelList.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(id), id, "The model is not in the Whisper catalog.");

    private static RecognitionModel Model(string id, string displayName, GgmlType type, bool englishOnly, string size) =>
        new(id, displayName, type, englishOnly, size, $"ggml-{id}.bin");
}
