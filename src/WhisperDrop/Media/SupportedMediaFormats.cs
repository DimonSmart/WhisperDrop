using System;
using System.Collections.Generic;
using System.IO;

namespace WhisperDrop.Media;

public static class SupportedMediaFormats
{
    private static readonly string[] extensions =
    [
        ".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus",
        ".mp4", ".m4v", ".mov", ".mkv", ".webm"
    ];

    private static readonly HashSet<string> extensionSet = new(extensions, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Extensions => extensions;

    public static bool IsSupported(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        return extension.Length > 0 && extensionSet.Contains(extension);
    }
}
