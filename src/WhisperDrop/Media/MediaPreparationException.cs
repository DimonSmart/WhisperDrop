using System;

namespace WhisperDrop.Media;

public enum MediaPreparationErrorKind
{
    CannotOpenFile,
    NoAudioStream,
    UnsupportedAudioCodec,
    CorruptedOrIncomplete,
    DecoderFailure,
    RuntimeUnavailable,
    RuntimeIncompatible
}

public sealed class MediaPreparationException : Exception
{
    public MediaPreparationException(MediaPreparationErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public MediaPreparationErrorKind Kind { get; }

    public bool IsRuntimeFailure =>
        Kind is MediaPreparationErrorKind.RuntimeUnavailable or MediaPreparationErrorKind.RuntimeIncompatible;

    internal static MediaPreparationException Create(MediaPreparationErrorKind kind, Exception? innerException = null) =>
        new(kind, GetUserMessage(kind), innerException);

    public static string GetUserMessage(MediaPreparationErrorKind kind) => kind switch
    {
        MediaPreparationErrorKind.CannotOpenFile => "The media file could not be opened.",
        MediaPreparationErrorKind.NoAudioStream => "The file does not contain an audio track.",
        MediaPreparationErrorKind.UnsupportedAudioCodec => "The audio format is not supported.",
        MediaPreparationErrorKind.CorruptedOrIncomplete => "The media file appears to be damaged or incomplete.",
        MediaPreparationErrorKind.DecoderFailure => "The audio track could not be decoded.",
        MediaPreparationErrorKind.RuntimeUnavailable => "The bundled media decoder could not be loaded.",
        MediaPreparationErrorKind.RuntimeIncompatible => "The bundled media decoder is incompatible with this application build.",
        _ => "The media file could not be prepared."
    };
}
