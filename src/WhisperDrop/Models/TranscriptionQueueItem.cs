using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace WhisperDrop.Models;

public enum TranscriptionState
{
    Pending,
    Transcribing,
    Completed,
    Error
}

public enum AiEnhancementState
{
    NotRequested,
    Pending,
    Improving,
    Completed,
    Failed,
    Cancelled
}

public sealed class TranscriptionQueueItem : INotifyPropertyChanged
{
    private TranscriptionState transcriptionState = TranscriptionState.Pending;
    private AiEnhancementState aiEnhancementState = AiEnhancementState.NotRequested;
    private string progress = "—";
    private string language = "Auto";
    private RecognitionResult? recognitionResult;
    private string? processedTranscript;
    private string? errorMessage;
    private string? aiEnhancementError;
    private bool canTranscribe;
    private bool canEnhance;

    public TranscriptionQueueItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
    }

    public string FilePath { get; }

    public string FileName { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TranscriptionState TranscriptionState
    {
        get => transcriptionState;
        internal set
        {
            if (Set(ref transcriptionState, value))
            {
                NotifyDerivedState();
            }
        }
    }

    public AiEnhancementState AiEnhancementState
    {
        get => aiEnhancementState;
        internal set
        {
            if (Set(ref aiEnhancementState, value))
            {
                NotifyDerivedState();
            }
        }
    }

    public string Status => TranscriptionState switch
    {
        TranscriptionState.Transcribing => "Transcribing",
        TranscriptionState.Error => "Error",
        TranscriptionState.Completed when AiEnhancementState == AiEnhancementState.Pending => "Waiting for AI",
        TranscriptionState.Completed when AiEnhancementState == AiEnhancementState.Improving => "Improving",
        TranscriptionState.Completed when AiEnhancementState == AiEnhancementState.Failed => "AI failed",
        TranscriptionState.Completed when AiEnhancementState == AiEnhancementState.Cancelled => "AI cancelled",
        TranscriptionState.Completed => "Completed",
        _ => "Pending"
    };

    public string Progress
    {
        get => progress;
        internal set => Set(ref progress, value);
    }

    public string Language
    {
        get => language;
        internal set => Set(ref language, value);
    }

    public RecognitionResult? RecognitionResult
    {
        get => recognitionResult;
        internal set
        {
            if (Set(ref recognitionResult, value))
            {
                NotifyTranscriptState();
            }
        }
    }

    public string? ProcessedTranscript
    {
        get => processedTranscript;
        internal set
        {
            if (Set(ref processedTranscript, value))
            {
                NotifyTranscriptState();
            }
        }
    }

    public string? RawTranscript => RecognitionResult?.Transcript;

    public string? EffectiveTranscript => ProcessedTranscript ?? RawTranscript;

    // Retained as a compatibility/readability alias for bindings and callers.
    public string? Transcript => EffectiveTranscript;

    public string? ErrorMessage
    {
        get => errorMessage;
        internal set => Set(ref errorMessage, value);
    }

    public string? AiEnhancementError
    {
        get => aiEnhancementError;
        internal set => Set(ref aiEnhancementError, value);
    }

    public string? Preview => EffectiveTranscript is null ? null : CreatePreview(EffectiveTranscript);

    public bool CanTranscribe
    {
        get => canTranscribe;
        internal set => Set(ref canTranscribe, value);
    }

    public bool CanEnhance
    {
        get => canEnhance;
        internal set => Set(ref canEnhance, value);
    }

    public bool CanCopy => EffectiveTranscript is not null;

    public bool CanCopyRaw => RawTranscript is not null && ProcessedTranscript is not null;

    public bool HasProcessedTranscript => ProcessedTranscript is not null;

    public string AiActionLabel => ProcessedTranscript is null ? "Run AI" : "Improve again";

    internal void NotifyActionState()
    {
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanCopyRaw));
        OnPropertyChanged(nameof(CanEnhance));
        OnPropertyChanged(nameof(CanTranscribe));
        OnPropertyChanged(nameof(AiActionLabel));
    }

    private void NotifyDerivedState()
    {
        OnPropertyChanged(nameof(Status));
        NotifyActionState();
    }

    private void NotifyTranscriptState()
    {
        OnPropertyChanged(nameof(RawTranscript));
        OnPropertyChanged(nameof(EffectiveTranscript));
        OnPropertyChanged(nameof(Transcript));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(HasProcessedTranscript));
        NotifyActionState();
    }

    private static string CreatePreview(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return "No speech recognized.";
        }

        var normalized = string.Join(" ", transcript.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 400 ? normalized : normalized[..399] + "…";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
