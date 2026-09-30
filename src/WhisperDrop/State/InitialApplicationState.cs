using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Models;
using WhisperDrop.Settings;

namespace WhisperDrop.State;

public sealed class InitialApplicationState : INotifyPropertyChanged
{
    public const int TranscribeTabIndex = 0;
    public const int ModelsTabIndex = 1;
    public const int SettingsTabIndex = ModelsTabIndex;

    private readonly IUserSettingsStore settingsStore;
    private readonly ISelectedModelAvailability availability;
    private readonly ISelectedModelDownloadManager downloadManager;
    private readonly IRecognitionService recognitionService;
    private readonly IRecognitionLanguageCatalog languageCatalog;
    private readonly ILocalModelInventory localModelInventory;
    private UserSettings settings;
    private int selectedTabIndex = TranscribeTabIndex;
    private string? unsupportedFormatMessage;
    private SelectedModelDownloadState selectedModelDownloadState;
    private string? selectedModelDownloadError;
    private double? selectedModelDownloadProgress;
    private bool isBatchActive;
    private string? transcriptionGuidance;

    public InitialApplicationState(
        IUserSettingsStore settingsStore,
        IWhisperModelCatalog modelCatalog,
        IRecognitionLanguageCatalog languageCatalog,
        ISelectedModelAvailability availability,
        ISelectedModelDownloadManager? downloadManager = null,
        IRecognitionService? recognitionService = null,
        ILocalModelInventory? localModelInventory = null)
    {
        this.settingsStore = settingsStore;
        this.availability = availability;
        this.downloadManager = downloadManager ?? new SelectedModelDownloadManager(modelCatalog, availability, new SelectedModelDownloader());
        this.recognitionService = recognitionService ?? new WhisperRecognitionService();
        this.localModelInventory = localModelInventory ?? new LocalModelInventory(modelCatalog, availability);
        settings = settingsStore.Load();
        ModelOptions = modelCatalog.Models;
        this.languageCatalog = languageCatalog;
        LanguageOptions = languageCatalog.Languages;
        RefreshInstalledModels();
        ReevaluateSelectedModel();
        SelectedTabIndex = TranscribeTabIndex;
        QueueRows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsTranscribeWorkspaceEmpty));
            RefreshQueueActionState();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<RecognitionModel> ModelOptions { get; }

    public IReadOnlyList<RecognitionLanguage> LanguageOptions { get; }

    public ObservableCollection<TranscriptionQueueItem> QueueRows { get; } = [];

    public ObservableCollection<LocalModelInfo> InstalledModels { get; } = [];

    public int SelectedTabIndex
    {
        get => selectedTabIndex;
        set => SetField(ref selectedTabIndex, value);
    }

    public string ModelsFolder
    {
        get => settings.ModelsFolder!;
        set => UpdateSettings(settings with { ModelsFolder = value });
    }

    public string SelectedModelId
    {
        get => settings.SelectedModelId;
        set => UpdateSettings(settings with { SelectedModelId = value });
    }

    public string RecognitionLanguageCode
    {
        get => settings.RecognitionLanguageCode;
        set => UpdateSettings(settings with { RecognitionLanguageCode = value });
    }

    public bool IsSelectedModelAvailable => SelectedModelDownloadState == SelectedModelDownloadState.Downloaded;

    public SelectedModelDownloadState SelectedModelDownloadState => selectedModelDownloadState;

    public double? SelectedModelDownloadProgress => selectedModelDownloadProgress;

    public bool CanDownloadSelectedModel => SelectedModelDownloadState is SelectedModelDownloadState.NotDownloaded or SelectedModelDownloadState.Error;

    public bool IsModelDownloadIndeterminate =>
        SelectedModelDownloadState == SelectedModelDownloadState.Downloading &&
        SelectedModelDownloadProgress is null;

    public string SelectedModelStatus => SelectedModelDownloadState switch
    {
        SelectedModelDownloadState.Downloaded => "Installed",
        SelectedModelDownloadState.Downloading when SelectedModelDownloadProgress is double progress => $"Downloading — {progress:P0}",
        SelectedModelDownloadState.Downloading => "Downloading…",
        SelectedModelDownloadState.Error => $"Download failed — {selectedModelDownloadError}",
        _ => "Not installed"
    };

    public bool HasInstalledModels => InstalledModels.Count > 0;

    public string InstalledModelsSummary
    {
        get
        {
            var total = InstalledModels.Sum(model => model.SizeBytes);
            var suffix = InstalledModels.Count == 1 ? "model" : "models";
            return $"{InstalledModels.Count} {suffix} · {LocalModelInfo.FormatBytes(total)}";
        }
    }

    public string? InstalledModelsEmptyMessage => HasInstalledModels ? null : "No models downloaded yet.";

    public bool IsTranscribeWorkspaceEmpty => QueueRows.Count == 0;

    public bool IsBatchActive
    {
        get => isBatchActive;
        private set => SetField(ref isBatchActive, value);
    }

    public bool CanReorderQueue => !IsBatchActive;
    public bool CanTranscribeAll => IsSelectedModelAvailable && !IsBatchActive && QueueRows.Any();
    public bool CanCopyAll => QueueRows.Any(row => row.CanCopy);

    public string? TranscriptionGuidance
    {
        get => transcriptionGuidance;
        private set
        {
            if (EqualityComparer<string?>.Default.Equals(transcriptionGuidance, value))
            {
                return;
            }

            transcriptionGuidance = value;
            OnPropertyChanged(nameof(TranscriptionGuidance));
            OnPropertyChanged(nameof(HasTranscriptionGuidance));
        }
    }

    public bool HasTranscriptionGuidance => !string.IsNullOrWhiteSpace(TranscriptionGuidance);

    public string? UnsupportedFormatMessage
    {
        get => unsupportedFormatMessage;
        private set => SetField(ref unsupportedFormatMessage, value);
    }

    public bool IsModelDownloaded(string modelId) => availability.IsAvailable(ModelsFolder, modelId);

    public async Task DownloadModelAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (availability.IsAvailable(ModelsFolder, modelId))
        {
            RefreshInstalledModels();
            if (string.Equals(modelId, SelectedModelId, StringComparison.Ordinal))
            {
                ReevaluateSelectedModel();
            }

            return;
        }

        var affectsSelectedModel = string.Equals(modelId, SelectedModelId, StringComparison.Ordinal);
        if (affectsSelectedModel)
        {
            selectedModelDownloadError = null;
            selectedModelDownloadProgress = null;
            SetSelectedModelDownloadState(SelectedModelDownloadState.Downloading);
        }

        var combinedProgress = new Progress<ModelDownloadProgress>(update =>
        {
            progress?.Report(update);
            if (!affectsSelectedModel)
            {
                return;
            }

            selectedModelDownloadProgress = update.TotalBytes is > 0
                ? (double)update.BytesReceived / update.TotalBytes.Value
                : null;
            OnPropertyChanged(nameof(SelectedModelDownloadProgress));
            OnPropertyChanged(nameof(SelectedModelStatus));
        });

        try
        {
            await downloadManager.DownloadAsync(ModelsFolder, modelId, combinedProgress, cancellationToken);
            RefreshInstalledModels();
            if (affectsSelectedModel)
            {
                ReevaluateSelectedModel();
            }
        }
        catch (OperationCanceledException)
        {
            if (affectsSelectedModel)
            {
                ReevaluateSelectedModel();
            }

            throw;
        }
        catch (Exception exception)
        {
            if (affectsSelectedModel)
            {
                selectedModelDownloadError = exception.Message;
                selectedModelDownloadProgress = null;
                SetSelectedModelDownloadState(SelectedModelDownloadState.Error);
            }

            throw;
        }
    }

    public async Task DownloadSelectedModelAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedModelDownloadState == SelectedModelDownloadState.Downloading || IsSelectedModelAvailable)
        {
            return;
        }

        try
        {
            await DownloadModelAsync(SelectedModelId, cancellationToken: cancellationToken);
        }
        catch
        {
            // The selected-model state already contains the actionable error.
        }
    }

    public void DeleteModel(string modelId)
    {
        EnsureModelStorageCanChange();
        recognitionService.UnloadModel();
        localModelInventory.Delete(ModelsFolder, modelId);
        RefreshInstalledModels();
        ReevaluateSelectedModel();
    }

    public void DeleteAllModels()
    {
        EnsureModelStorageCanChange();
        recognitionService.UnloadModel();
        localModelInventory.DeleteAll(ModelsFolder);
        RefreshInstalledModels();
        ReevaluateSelectedModel();
    }

    public FileQueueAddResult AddFiles(IEnumerable<string> filePaths)
    {
        var unsupportedFiles = new List<string>();
        var addedCount = 0;

        foreach (var filePath in filePaths)
        {
            if (!IsSupportedAudioFile(filePath))
            {
                unsupportedFiles.Add(filePath);
                continue;
            }

            var normalizedPath = NormalizeFilePath(filePath);
            if (QueueRows.Any(row => FilePathComparer.Equals(row.FilePath, normalizedPath)))
            {
                continue;
            }

            QueueRows.Add(new TranscriptionQueueItem(normalizedPath));
            addedCount++;
        }

        UnsupportedFormatMessage = unsupportedFiles.Count == 0
            ? null
            : "Only WAV files are supported. Unsupported files were not added.";
        return new FileQueueAddResult(addedCount, unsupportedFiles);
    }

    public bool MoveQueueItem(int sourceIndex, int destinationIndex)
    {
        if (IsBatchActive)
        {
            return false;
        }

        if (sourceIndex < 0 || sourceIndex >= QueueRows.Count ||
            destinationIndex < 0 || destinationIndex >= QueueRows.Count ||
            sourceIndex == destinationIndex)
        {
            return false;
        }

        QueueRows.Move(sourceIndex, destinationIndex);
        return true;
    }

    public bool RemoveQueueItem(TranscriptionQueueItem item) => QueueRows.Remove(item);

    public async Task TranscribeAsync(TranscriptionQueueItem item, CancellationToken cancellationToken = default)
    {
        if (!IsSelectedModelAvailable)
        {
            TranscriptionGuidance = "Download the selected model before transcription.";
            return;
        }

        TranscriptionGuidance = null;
        await TranscribeItemAsync(item, cancellationToken);
    }

    public async Task TranscribeAllAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSelectedModelAvailable)
        {
            TranscriptionGuidance = "Download the selected model before transcription.";
            return;
        }

        TranscriptionGuidance = null;
        IsBatchActive = true;
        RefreshQueueActionState();
        try
        {
            foreach (var item in QueueRows.ToArray())
            {
                try
                {
                    await TranscribeItemAsync(item, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // The item is already marked Error; continue the visual queue.
                }
            }
        }
        finally
        {
            IsBatchActive = false;
            RefreshQueueActionState();
        }
    }

    public string GetCompletedTranscriptsForCopy() =>
        string.Join(
            Environment.NewLine + Environment.NewLine,
            QueueRows.Where(row => row.CanCopy).Select(row => row.Transcript));

    public static bool IsSupportedAudioFile(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".wav", StringComparison.OrdinalIgnoreCase);

    private static StringComparer FilePathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizeFilePath(string filePath) => Path.GetFullPath(filePath);

    private void UpdateSettings(UserSettings updated)
    {
        settingsStore.Save(updated);
        settings = updated;
        OnPropertyChanged(nameof(ModelsFolder));
        OnPropertyChanged(nameof(SelectedModelId));
        OnPropertyChanged(nameof(RecognitionLanguageCode));
        RefreshInstalledModels();
        ReevaluateSelectedModel();
    }

    private void RefreshInstalledModels()
    {
        var installed = localModelInventory.GetInstalled(ModelsFolder);
        InstalledModels.Clear();
        foreach (var model in installed)
        {
            InstalledModels.Add(model);
        }

        OnPropertyChanged(nameof(HasInstalledModels));
        OnPropertyChanged(nameof(InstalledModelsSummary));
        OnPropertyChanged(nameof(InstalledModelsEmptyMessage));
    }

    private void ReevaluateSelectedModel()
    {
        selectedModelDownloadError = null;
        selectedModelDownloadProgress = null;
        SetSelectedModelDownloadState(
            availability.IsAvailable(ModelsFolder, SelectedModelId)
                ? SelectedModelDownloadState.Downloaded
                : SelectedModelDownloadState.NotDownloaded);

        if (IsSelectedModelAvailable)
        {
            TranscriptionGuidance = null;
        }
    }

    private void SetSelectedModelDownloadState(SelectedModelDownloadState value)
    {
        selectedModelDownloadState = value;
        OnPropertyChanged(nameof(SelectedModelDownloadState));
        OnPropertyChanged(nameof(SelectedModelDownloadProgress));
        OnPropertyChanged(nameof(CanDownloadSelectedModel));
        OnPropertyChanged(nameof(IsModelDownloadIndeterminate));
        OnPropertyChanged(nameof(IsSelectedModelAvailable));
        OnPropertyChanged(nameof(SelectedModelStatus));
        OnPropertyChanged(nameof(CanTranscribeAll));
        RefreshQueueActionState();
    }

    private async Task TranscribeItemAsync(TranscriptionQueueItem item, CancellationToken cancellationToken)
    {
        item.Status = "Transcribing";
        item.Progress = "0%";
        item.ErrorMessage = null;
        item.Transcript = null;
        RefreshQueueActionState();
        var progress = new Progress<double>(value => item.Progress = $"{value * 100:0}%");

        try
        {
            var result = await recognitionService.TranscribeAsync(
                availability.GetModelPath(ModelsFolder, SelectedModelId),
                RecognitionLanguageCode,
                item.FilePath,
                progress,
                cancellationToken);
            item.Transcript = result.Transcript;
            item.Language = RecognitionLanguageCode == "auto"
                ? result.DetectedLanguage is { } detected && TryGetLanguageName(detected, out var displayName)
                    ? displayName
                    : result.DetectedLanguage ?? "Auto"
                : languageCatalog.Get(RecognitionLanguageCode).DisplayName;
            item.Progress = "100%";
            item.Status = "Completed";
        }
        catch (OperationCanceledException)
        {
            item.Status = "Pending";
            item.Progress = "—";
            throw;
        }
        catch (Exception exception)
        {
            item.Status = "Error";
            item.Progress = "—";
            item.ErrorMessage = exception.Message;
        }
        finally
        {
            item.NotifyActionState();
            OnPropertyChanged(nameof(CanCopyAll));
            RefreshQueueActionState();
        }
    }

    private void EnsureModelStorageCanChange()
    {
        if (QueueRows.Any(row => row.Status == "Transcribing"))
        {
            throw new InvalidOperationException("Models cannot be deleted while transcription is running.");
        }
    }

    private void RefreshQueueActionState()
    {
        foreach (var row in QueueRows)
        {
            row.CanTranscribe = IsSelectedModelAvailable && !IsBatchActive && row.Status != "Transcribing";
            row.NotifyActionState();
        }

        OnPropertyChanged(nameof(CanReorderQueue));
        OnPropertyChanged(nameof(CanTranscribeAll));
        OnPropertyChanged(nameof(CanCopyAll));
    }

    private bool TryGetLanguageName(string code, out string displayName)
    {
        var language = LanguageOptions.FirstOrDefault(item => item.Code == code);
        displayName = language?.DisplayName ?? code;
        return language is not null;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
