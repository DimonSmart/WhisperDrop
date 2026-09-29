using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperDrop.Models;

public sealed record RecognitionLanguage(string Code, string DisplayName);

public interface IRecognitionLanguageCatalog
{
    IReadOnlyList<RecognitionLanguage> Languages { get; }

    RecognitionLanguage Get(string code);
}

public sealed class RecognitionLanguageCatalog : IRecognitionLanguageCatalog
{
    private static readonly IReadOnlyList<RecognitionLanguage> LanguageList =
    [
        new("auto", "Auto-detect"),
        new("en", "English"),
        new("ru", "Russian"),
        new("es", "Spanish"),
        new("fr", "French"),
        new("de", "German"),
        new("it", "Italian"),
        new("pt", "Portuguese"),
        new("nl", "Dutch"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("zh", "Chinese"),
        new("uk", "Ukrainian")
    ];

    public IReadOnlyList<RecognitionLanguage> Languages => LanguageList;

    public RecognitionLanguage Get(string code) =>
        LanguageList.FirstOrDefault(language => string.Equals(language.Code, code, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(code), code, "The language is not supported by the recognition catalog.");
}
