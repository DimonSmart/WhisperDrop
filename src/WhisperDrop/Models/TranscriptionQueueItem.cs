using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace WhisperDrop.Models;

public sealed class TranscriptionQueueItem : INotifyPropertyChanged
{
    private string status = "Pending";
    private string progress = "—";
    private string language = "Auto";
    private string? transcript;
    private string? errorMessage;
    private bool canTranscribe;
    public TranscriptionQueueItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
    }

    public string FilePath { get; }
    public string FileName { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Status { get => status; internal set => Set(ref status, value); }
    public string Progress { get => progress; internal set => Set(ref progress, value); }
    public string Language { get => language; internal set => Set(ref language, value); }
    public string? Transcript { get => transcript; internal set { if (Set(ref transcript, value)) OnPropertyChanged(nameof(Preview)); } }
    public string? ErrorMessage { get => errorMessage; internal set => Set(ref errorMessage, value); }
    public string? Preview => Transcript is null ? null : CreatePreview(Transcript);
    public bool CanTranscribe { get => canTranscribe; internal set => Set(ref canTranscribe, value); }
    public bool CanCopy => Status == "Completed";

    internal void NotifyActionState() { OnPropertyChanged(nameof(CanCopy)); OnPropertyChanged(nameof(CanTranscribe)); }

    private static string CreatePreview(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return "No speech recognized.";
        var normalized = string.Join(" ", transcript.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 400 ? normalized : normalized[..399] + "…";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
