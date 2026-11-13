using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Dictation;
using WhisperDrop.Media;
using WhisperDrop.Models;
using WhisperDrop.Settings;

namespace WhisperDrop.State;

public sealed class InitialApplicationState : INotifyPropertyChanged
{
    public const int TranscribeTabIndex = 0;
    public const int ModelsTabIndex = 1;
    public const int AiTabIndex = 2;
    public const int SettingsTabIndex = ModelsTabIndex;

    private readonly IUserSettingsStore settingsStore;
    private readonly IRecognitionModelCatalog modelCatalog;
    private readonly ISelectedModelAvailability availability;
    private readonly ISelectedModelDownloadManager downloadManager;
    private readonly IRecognitionService recognitionService;
    private readonly IMediaPreparationService mediaPreparationService;
    private readonly IMicrophoneCaptureService microphoneCaptureService;
    private readonly ITranscriptEnhancementService transcriptEnhancementService;
    private readonly IRecognitionLanguageCatalog languageCatalog;
    private readonly ILocalModelInventory localModelInventory;
    private readonly IRecognitionRuntimeSelector runtimeSelector;
    private UserSettings settings;
    private int selectedTabIndex = TranscribeTabIndex;
    private string? unsupportedFormatMessage;
    private SelectedModelDownloadState selectedModelDownloadState;
    private string? selectedModelDownloadError;
    private double? selectedModelDownloadProgress;
    private bool isBatchActive;
    private string? transcriptionGuidance;
    private string aiApiKey = string.Empty;
    private string? aiConnectionStatus;
    private bool isAiConnectionTesting;
    private bool isMicrophoneRecording;
    private bool isDictationRecording;
    private string? dictationStatus;
    private readonly Queue<TranscriptionQueueItem> pendingDictations = new();
    private bool dictationPumpActive;
    public event Action<TranscriptionQueueItem>? DictationTranscribed;
    private bool isMicrophoneTransitioning;
    private string? microphoneRecordingStatus;
    private CancellationTokenSource? activeTranscription;

    public bool CanCancelTranscription => activeTranscription is not null && !activeTranscription.IsCancellationRequested;

    public void CancelTranscription()
    {
        activeTranscription?.Cancel();
        OnPropertyChanged(nameof(CanCancelTranscription));
    }

    public InitialApplicationState(
        IUserSettingsStore settingsStore,
        IRecognitionModelCatalog modelCatalog,
        IRecognitionLanguageCatalog languageCatalog,
        ISelectedModelAvailability availability,
        ISelectedModelDownloadManager? downloadManager = null,
        IRecognitionService? recognitionService = null,
        ITranscriptEnhancementService? transcriptEnhancementService = null,
        ILocalModelInventory? localModelInventory = null,
        IRecognitionRuntimeSelector? runtimeSelector = null,
        IMediaPreparationService? mediaPreparationService = null,
        IMicrophoneCaptureService? microphoneCaptureService = null)
    {
        this.settingsStore = settingsStore;
        this.modelCatalog = modelCatalog;
        this.availability = availability;
        this.downloadManager = downloadManager ?? new SelectedModelDownloadManager(modelCatalog, availability, new SelectedModelDownloader());
        this.runtimeSelector = runtimeSelector ?? new ParakeetRuntimeSelector();
        this.recognitionService = recognitionService ?? new ParakeetRecognitionService(this.runtimeSelector);
        this.mediaPreparationService = mediaPreparationService ?? new DirectMediaPreparationService();
        this.microphoneCaptureService = microphoneCaptureService ?? new UnsupportedMicrophoneCaptureService();
        this.transcriptEnhancementService = transcriptEnhancementService ?? new UnavailableTranscriptEnhancementService();
        this.localModelInventory = localModelInventory ?? new LocalModelInventory(modelCatalog, availability);
        this.languageCatalog = languageCatalog;

        settings = settingsStore.Load();
        LanguageOptions = languageCatalog.Languages;
        ProcessingDeviceOptions =
        [
            new(ProcessingDevice.Auto, "Auto"),
            new(ProcessingDevice.Cpu, "CPU"),
            new(ProcessingDevice.Gpu, "GPU")
        ];
        CpuThreadOptions =
        [
            new CpuThreadsOption(null, "Auto"),
            .. Enumerable.Range(1, Math.Max(1, Environment.ProcessorCount))
                .Select(value => new CpuThreadsOption(value, value.ToString()))
        ];
        AiProviderOptions =
        [
            new(AiProviderPreset.Ollama, "Ollama"),
            new(AiProviderPreset.CustomOpenAiCompatible, "Custom OpenAI-compatible")
        ];
        AiContextSizeOptions =
        [
            new(null, "Auto"),
            new(4096, "4096"),
            new(8192, "8192"),
            new(16384, "16384"),
            new(32768, "32768")
        ];
        aiApiKey = settings.AiPostProcessing.Provider == AiProviderPreset.Ollama ? "ollama" : string.Empty;

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

    public IReadOnlyList<RecognitionModelOption> ModelOptions { get; private set; } = [];

    public IReadOnlyList<RecognitionLanguage> LanguageOptions { get; }

    public IReadOnlyList<ProcessingDeviceOption> ProcessingDeviceOptions { get; }

    public IReadOnlyList<CpuThreadsOption> CpuThreadOptions { get; }

    public IReadOnlyList<AiProviderOption> AiProviderOptions { get; }

    public IReadOnlyList<AiContextSizeOption> AiContextSizeOptions { get; }

    public ObservableCollection<TranscriptionQueueItem> QueueRows { get; } = [];

    public ObservableCollection<LocalModelInfo> InstalledModels { get; } = [];

    public int SelectedTabIndex
    {
        get => selectedTabIndex;
        set => SetField(ref selectedTabIndex, value);
    }

    public bool CanChangeModelStorage => !IsBatchActive && !QueueRows.Any(row => row.IsProcessing) && SelectedModelDownloadState != SelectedModelDownloadState.Downloading;

    public string ModelsFolder
    {
        get => settings.ModelsFolder!;
        set
        {
            if (string.Equals(settings.ModelsFolder, value, StringComparison.Ordinal))
            {
                return;
            }

            if (!CanChangeModelStorage)
            {
                TranscriptionGuidance = "Wait for recognition or the model download to finish before changing the models folder.";
                OnPropertyChanged(nameof(ModelsFolder));
                return;
            }

            recognitionService.UnloadModel();
            UpdateSettings(settings with { ModelsFolder = value }, nameof(ModelsFolder), reevaluateModels: true);
        }
    }

    public string SelectedModelId
    {
        get => settings.SelectedModelId;
        set
        {
            if (settings.SelectedModelId == value)
            {
                return;
            }

            recognitionService.UnloadModel();
            UpdateSettings(settings with { SelectedModelId = value }, nameof(SelectedModelId), reevaluateModels: true);
        }
    }

    public ProcessingDevice ProcessingDevice
    {
        get => settings.ProcessingDevice;
        set
        {
            UpdateSettings(settings with { ProcessingDevice = value }, nameof(ProcessingDevice));
            NotifyRuntimeStatusChanged();
        }
    }

    public int? CpuThreads
    {
        get => settings.CpuThreads;
        set => UpdateSettings(settings with { CpuThreads = value }, nameof(CpuThreads));
    }

    public bool AiPostProcessingEnabled
    {
        get => settings.AiPostProcessing.Enabled;
        set => UpdateAiSettings(settings.AiPostProcessing with { Enabled = value }, nameof(AiPostProcessingEnabled));
    }

    public AiProviderPreset AiProvider
    {
        get => settings.AiPostProcessing.Provider;
        set
        {
            var updated = settings.AiPostProcessing with { Provider = value };
            if (value == AiProviderPreset.Ollama)
            {
                updated = updated with { Endpoint = "http://localhost:11434/v1/" };
                if (string.IsNullOrWhiteSpace(aiApiKey))
                {
                    aiApiKey = "ollama";
                    OnPropertyChanged(nameof(AiApiKey));
                }
            }
            else if (aiApiKey == "ollama")
            {
                aiApiKey = string.Empty;
                OnPropertyChanged(nameof(AiApiKey));
            }

            UpdateAiSettings(updated, nameof(AiProvider));
            OnPropertyChanged(nameof(AiEndpoint));
        }
    }

    public string AiEndpoint
    {
        get => settings.AiPostProcessing.Endpoint;
        set => UpdateAiSettings(settings.AiPostProcessing with { Endpoint = value ?? string.Empty }, nameof(AiEndpoint));
    }

    public string AiModel
    {
        get => settings.AiPostProcessing.Model;
        set => UpdateAiSettings(settings.AiPostProcessing with { Model = value ?? string.Empty }, nameof(AiModel));
    }

    public string AiInstructions
    {
        get => settings.AiPostProcessing.Instructions;
        set => UpdateAiSettings(settings.AiPostProcessing with { Instructions = value ?? string.Empty }, nameof(AiInstructions));
    }

    public int? AiContextSize
    {
        get => settings.AiPostProcessing.ContextSize;
        set => UpdateAiSettings(settings.AiPostProcessing with { ContextSize = value }, nameof(AiContextSize));
    }

    public string AiApiKey
    {
        get => aiApiKey;
        set => SetField(ref aiApiKey, value ?? string.Empty);
    }

    public string? AiRemoteEndpointWarning =>
        AiPostProcessingOptions.IsLoopbackEndpoint(AiEndpoint)
            ? null
            : $"Transcript text will be sent to: {AiEndpoint}";

    public bool HasAiRemoteEndpointWarning => AiRemoteEndpointWarning is not null;

    public string? AiConnectionStatus
    {
        get => aiConnectionStatus;
        private set => SetField(ref aiConnectionStatus, value);
    }

    public bool IsAiConnectionTesting
    {
        get => isAiConnectionTesting;
        private set => SetField(ref isAiConnectionTesting, value);
    }

    public string? ProcessingDeviceReloadMessage =>
        runtimeSelector.RequiresModelReload(settings.ProcessingDevice)
            ? "The model will reload for the next transcription."
            : null;

    public string? ProcessingBackendStatus => runtimeSelector.Backend is { } backend ? $"Backend used: {backend}." : null;

    public bool HasProcessingDeviceReloadMessage => ProcessingDeviceReloadMessage is not null;

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

    public bool IsMicrophoneRecording => isMicrophoneRecording;

    public bool DictationEnabled
    {
        get => settings.Dictation.Enabled;
        set => UpdateSettings(settings with { Dictation = settings.Dictation with { Enabled = value } }, nameof(DictationEnabled));
    }

    public string DictationHotkey
    {
        get => settings.Dictation.Hotkey;
        set => UpdateSettings(settings with { Dictation = settings.Dictation with { Hotkey = value } }, nameof(DictationHotkey));
    }

    public bool DictationInsertAfterRecognition
    {
        get => settings.Dictation.InsertAfterRecognition;
        set => UpdateSettings(settings with { Dictation = settings.Dictation with { InsertAfterRecognition = value } }, nameof(DictationInsertAfterRecognition));
    }

    public string? DictationStatus => dictationStatus;

    public void SetDictationStatus(string? message) => SetField(ref dictationStatus, message, nameof(DictationStatus));

    public bool CanStartDictation => microphoneCaptureService.IsSupported &&
        !IsMicrophoneRecording && !isMicrophoneTransitioning && !isDictationRecording;

    public void SetDictationRecording(bool recording)
    {
        isDictationRecording = recording;
        OnPropertyChanged(nameof(CanUseMicrophone));
        RefreshQueueActionState();
    }

    public TranscriptionQueueItem AddDictationRecording(string path, DateTimeOffset started, DateTimeOffset finished)
    {
        var row = new TranscriptionQueueItem(Path.GetFullPath(path))
        {
            DictationId = Guid.NewGuid(),
            RecordingStartedAt = started,
            RecordingEndedAt = finished,
            InsertionStatus = DictationInsertAfterRecognition ? TextInsertionStatus.Pending : TextInsertionStatus.NotRequested
        };
        QueueRows.Add(row);
        return row;
    }

    public void QueueDictationRecognition(TranscriptionQueueItem row)
    {
        pendingDictations.Enqueue(row);
        StartPendingDictations();
    }

    private void StartPendingDictations()
    {
        if (dictationPumpActive || activeTranscription is not null || pendingDictations.Count == 0)
            return;
        dictationPumpActive = true;
        _ = ProcessPendingDictationsAsync();
    }

    private async Task ProcessPendingDictationsAsync()
    {
        try
        {
            while (activeTranscription is null && pendingDictations.Count > 0)
            {
                var row = pendingDictations.Dequeue();
                if (!QueueRows.Contains(row))
                    continue;

                if (!TryCreateRecognitionRequest(out var modelPath, out var options, out var guidance))
                {
                    row.ErrorMessage = guidance;
                    row.TranscriptionState = TranscriptionState.Error;
                }
                else
                {
                    try
                    {
                        await ExecuteTranscriptionAsync(
                            token => TranscribeItemAsync(row, modelPath, options, token), CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        row.TranscriptionState = TranscriptionState.Error;
                        row.ErrorMessage = exception.Message;
                    }
                }
                DictationTranscribed?.Invoke(row);
            }
        }
        finally
        {
            dictationPumpActive = false;
            if (activeTranscription is null && pendingDictations.Count > 0)
                StartPendingDictations();
        }
    }


    public string MicrophoneActionLabel => IsMicrophoneRecording ? "Stop recording" : "Record microphone";

    public string MicrophoneAvailabilityMessage => microphoneCaptureService.AvailabilityMessage;

    public string? MicrophoneRecordingStatus
    {
        get => microphoneRecordingStatus;
        private set
        {
            if (EqualityComparer<string?>.Default.Equals(microphoneRecordingStatus, value))
                return;
            SetField(ref microphoneRecordingStatus, value);
            OnPropertyChanged(nameof(MicrophoneStatusMessage));
        }
    }

    public string MicrophoneStatusMessage => microphoneCaptureService.IsSupported
        ? MicrophoneRecordingStatus ?? string.Empty
        : MicrophoneAvailabilityMessage;

    public bool CanUseMicrophone => !isDictationRecording && !isMicrophoneTransitioning && (IsMicrophoneRecording ||
        (microphoneCaptureService.IsSupported && activeTranscription is null && !IsBatchActive));

    public async Task ToggleMicrophoneRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUseMicrophone)
            return;
        isMicrophoneTransitioning = true;
        OnPropertyChanged(nameof(CanUseMicrophone));
        RefreshQueueActionState();
        try
        {
            await ToggleMicrophoneRecordingCoreAsync(cancellationToken);
        }
        finally
        {
            isMicrophoneTransitioning = false;
            OnPropertyChanged(nameof(CanUseMicrophone));
            RefreshQueueActionState();
        }
    }

    private async Task ToggleMicrophoneRecordingCoreAsync(CancellationToken cancellationToken)
    {
        if (IsMicrophoneRecording)
        {
            try
            {
                var recordingPath = await microphoneCaptureService.StopAsync(cancellationToken);
                isMicrophoneRecording = false;
                OnPropertyChanged(nameof(IsMicrophoneRecording));
                OnPropertyChanged(nameof(MicrophoneActionLabel));
                OnPropertyChanged(nameof(CanUseMicrophone));
                AddFiles([recordingPath]);
                MicrophoneRecordingStatus = "Recording added to the queue.";
                RefreshQueueActionState();
                return;
            }
            catch
            {
                isMicrophoneRecording = false;
                OnPropertyChanged(nameof(IsMicrophoneRecording));
                OnPropertyChanged(nameof(MicrophoneActionLabel));
                OnPropertyChanged(nameof(CanUseMicrophone));
                MicrophoneRecordingStatus = null;
                RefreshQueueActionState();
                throw;
            }
        }

        if (!microphoneCaptureService.IsSupported)
            return;

        MicrophoneRecordingStatus = null;
        await microphoneCaptureService.StartAsync(cancellationToken);
        isMicrophoneRecording = true;
        OnPropertyChanged(nameof(IsMicrophoneRecording));
        OnPropertyChanged(nameof(MicrophoneActionLabel));
        OnPropertyChanged(nameof(CanUseMicrophone));
        MicrophoneRecordingStatus = "Recording from the selected microphone…";
        RefreshQueueActionState();
    }

    public bool IsBatchActive
    {
        get => isBatchActive;
        private set
        {
            if (isBatchActive == value)
                return;
            SetField(ref isBatchActive, value);
            OnPropertyChanged(nameof(CanUseMicrophone));
        }
    }

    public bool CanReorderQueue => activeTranscription is null && !IsBatchActive;
    public bool CanTranscribeAll => activeTranscription is null && IsSelectedModelAvailable && !IsBatchActive && !IsMicrophoneRecording && !isMicrophoneTransitioning && !isDictationRecording && QueueRows.Any();
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
            if (!IsSupportedMediaFile(filePath))
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
            : "Unsupported file format. Unsupported files were not added.";
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

    public Task TranscribeAsync(TranscriptionQueueItem item, CancellationToken cancellationToken = default) =>
        ExecuteTranscriptionAsync(token => TranscribeCoreAsync(item, token), cancellationToken);

    public Task TranscribeAllAsync(CancellationToken cancellationToken = default) =>
        ExecuteTranscriptionAsync(TranscribeAllCoreAsync, cancellationToken);

    private async Task ExecuteTranscriptionAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        if (activeTranscription is not null)
            return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        activeTranscription = cancellation;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        OnPropertyChanged(nameof(CanCancelTranscription));
        OnPropertyChanged(nameof(CanUseMicrophone));
        RefreshQueueActionState();
        try { await operation(cancellation.Token); }
        finally
        {
            ApplicationAcceptanceProbe.RecordRun(this, timer.Elapsed);
            activeTranscription = null;
            StartPendingDictations();
            OnPropertyChanged(nameof(CanCancelTranscription));
            OnPropertyChanged(nameof(CanUseMicrophone));
            RefreshQueueActionState();
        }
    }

    private async Task TranscribeCoreAsync(TranscriptionQueueItem item, CancellationToken cancellationToken)
    {
        if (!TryCreateRecognitionRequest(out var modelPath, out var options, out var guidance))
        {
            TranscriptionGuidance = guidance;
            return;
        }

        var aiOptions = CreateAiOptionsSnapshot();
        TranscriptionGuidance = null;
        try
        {
            var completed = await TranscribeItemAsync(item, modelPath, options, cancellationToken);
            if (completed && aiOptions.Enabled)
            {
                item.AiEnhancementState = AiEnhancementState.Pending;
                await EnhanceItemAsync(item, aiOptions, cancellationToken);
            }
        }
        catch (RecognitionConfigurationException exception)
        {
            TranscriptionGuidance = exception.Message;
        }
        catch (MediaPreparationException exception) when (exception.IsRuntimeFailure)
        {
            TranscriptionGuidance = exception.Message;
        }
    }

    private async Task TranscribeAllCoreAsync(CancellationToken cancellationToken)
    {
        if (!TryCreateRecognitionRequest(out var modelPath, out var options, out var guidance))
        {
            TranscriptionGuidance = guidance;
            return;
        }

        var aiOptions = CreateAiOptionsSnapshot();
        var batchItems = QueueRows.ToArray();
        TranscriptionGuidance = null;
        IsBatchActive = true;
        RefreshQueueActionState();

        try
        {
            foreach (var item in batchItems)
            {
                try
                {
                    var completed = await TranscribeItemAsync(item, modelPath, options, cancellationToken);
                    if (completed && aiOptions.Enabled)
                    {
                        item.AiEnhancementState = AiEnhancementState.Pending;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (RecognitionConfigurationException exception)
                {
                    TranscriptionGuidance = exception.Message;
                    break;
                }
                catch (MediaPreparationException exception) when (exception.IsRuntimeFailure)
                {
                    TranscriptionGuidance = exception.Message;
                    break;
                }
            }

            if (aiOptions.Enabled)
            {
                recognitionService.UnloadModel();
                NotifyRuntimeStatusChanged();

                foreach (var item in batchItems.Where(item =>
                             item.TranscriptionState == TranscriptionState.Completed &&
                             item.RecognitionResult is not null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await EnhanceItemAsync(item, aiOptions, cancellationToken);
                }
            }
        }
        finally
        {
            IsBatchActive = false;
            RefreshQueueActionState();
        }
    }

    public async Task RunAiPostProcessingAsync(
        TranscriptionQueueItem item,
        CancellationToken cancellationToken = default)
    {
        if (item.RecognitionResult is null ||
            item.TranscriptionState != TranscriptionState.Completed)
        {
            return;
        }

        await EnhanceItemAsync(item, CreateAiOptionsSnapshot() with { Enabled = true }, cancellationToken);
    }

    public async Task TestAiConnectionAsync(CancellationToken cancellationToken = default)
    {
        IsAiConnectionTesting = true;
        AiConnectionStatus = "Testing…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await transcriptEnhancementService.TestConnectionAsync(
                CreateAiOptionsSnapshot() with { Enabled = true },
                timeout.Token);
            AiConnectionStatus = "Connected.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AiConnectionStatus = "AI server is not available.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TranscriptEnhancementException exception)
        {
            AiConnectionStatus = exception.Message;
        }
        catch
        {
            AiConnectionStatus = "The configured AI endpoint or model could not be used.";
        }
        finally
        {
            IsAiConnectionTesting = false;
        }
    }

    public string GetCompletedTranscriptsForCopy() =>
        string.Join(
            Environment.NewLine + Environment.NewLine,
            QueueRows
                .Where(row => row.CanCopy)
                .Select(row => row.EffectiveTranscript!));

    public string GetAiInputForCopy(TranscriptionQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.RecognitionResult is { } recognition
            ? TranscriptEnhancementPromptBuilder.BuildDebugInput(
                recognition,
                CreateAiOptionsSnapshot() with { Enabled = true })
            : string.Empty;
    }

    public static bool IsSupportedMediaFile(string filePath) =>
        SupportedMediaFormats.IsSupported(filePath);

    private static StringComparer FilePathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizeFilePath(string filePath) => Path.GetFullPath(filePath);

    private void UpdateSettings(UserSettings updated, string propertyName, bool reevaluateModels = false)
    {
        if (updated == settings)
        {
            return;
        }

        settingsStore.Save(updated);
        settings = updated;
        OnPropertyChanged(propertyName);

        if (reevaluateModels)
        {
            RefreshInstalledModels();
            ReevaluateSelectedModel();
        }
    }

    private void UpdateAiSettings(AiPostProcessingSettings updated, string propertyName)
    {
        UpdateSettings(settings with { AiPostProcessing = updated }, propertyName);
        AiConnectionStatus = null;
        OnPropertyChanged(nameof(AiRemoteEndpointWarning));
        OnPropertyChanged(nameof(HasAiRemoteEndpointWarning));
    }

    private void RefreshInstalledModels()
    {
        var installed = localModelInventory.GetInstalled(ModelsFolder);
        InstalledModels.Clear();
        foreach (var model in installed)
        {
            InstalledModels.Add(model);
        }

        var installedById = installed.ToDictionary(model => model.Id, StringComparer.Ordinal);
        ModelOptions = modelCatalog.Models
            .Select(model => installedById.TryGetValue(model.Id, out var localModel)
                ? new RecognitionModelOption(
                    model.Id,
                    model.DisplayName,
                    model.ApproximateSize,
                    true,
                    localModel.SizeText)
                : new RecognitionModelOption(
                    model.Id,
                    model.DisplayName,
                    model.ApproximateSize,
                    false,
                    null))
            .OrderByDescending(model => model.IsInstalled)
            .ToArray();

        OnPropertyChanged(nameof(ModelOptions));
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

    private bool TryCreateRecognitionRequest(
        out string modelPath,
        out RecognitionOptions options,
        out string? guidance)
    {
        modelPath = string.Empty;
        options = CreateRecognitionOptionsSnapshot();
        guidance = null;

        if (!IsSelectedModelAvailable)
        {
            guidance = "Download the selected model before transcription.";
            return false;
        }

        modelPath = availability.GetModelPath(ModelsFolder, SelectedModelId);
        return true;
    }

    private RecognitionOptions CreateRecognitionOptionsSnapshot()
    {
        return new RecognitionOptions
        {
            ProcessingDevice = settings.ProcessingDevice,
            CpuThreads = settings.CpuThreads
        };
    }

    private AiPostProcessingOptions CreateAiOptionsSnapshot() => new()
    {
        Enabled = settings.AiPostProcessing.Enabled,
        Endpoint = settings.AiPostProcessing.Endpoint,
        Model = settings.AiPostProcessing.Model,
        Instructions = settings.AiPostProcessing.Instructions,
        ContextSize = settings.AiPostProcessing.ContextSize,
        ApiKey = aiApiKey
    };

    private async Task<bool> TranscribeItemAsync(
        TranscriptionQueueItem item,
        string modelPath,
        RecognitionOptions options,
        CancellationToken cancellationToken)
    {
        item.TranscriptionState = TranscriptionState.Preparing;
        item.AiEnhancementState = AiEnhancementState.NotRequested;
        item.Progress = "—";
        item.IsProgressIndeterminate = true;
        item.ErrorMessage = null;
        item.AiEnhancementError = null;
        item.LatestCorrection = null;
        item.RecognitionResult = null;
        item.ProcessedTranscript = null;
        item.ResetPartialTranscript();
        RefreshQueueActionState();

        var preparationProgress = new Progress<double>(value =>
        {
            item.IsProgressIndeterminate = false;
            item.Progress = $"{Math.Clamp(value, 0, 1) * 100:0}%";
        });

        try
        {
            await using var prepared = await mediaPreparationService.PrepareAsync(
                item.FilePath,
                preparationProgress,
                cancellationToken);
            ApplicationAcceptanceProbe.Record("prepared", new { durationSeconds = prepared.Duration?.TotalSeconds });

            item.TranscriptionState = TranscriptionState.Transcribing;
            item.Progress = "—";
            item.IsProgressIndeterminate = true;
            RefreshQueueActionState();

            var recognitionProgress = new Progress<double>(value =>
                item.Progress = $"{Math.Clamp(value, 0, 1) * 100:0}%");
            var recognizedSegments = new Progress<string>(item.AppendRecognizedSegment);

            var result = await recognitionService.TranscribeAsync(
                modelPath,
                options,
                prepared.AudioPath,
                recognitionProgress,
                cancellationToken,
                recognizedSegments);

            item.RecognitionResult = result;
            item.Language = result.DetectedLanguage is { } detected && TryGetLanguageName(detected, out var displayName)
                ? displayName : result.DetectedLanguage ?? "—";
            item.Progress = result.IsComplete ? "100%" : "—";
            item.IsProgressIndeterminate = false;
            item.TranscriptionState = result.IsComplete ? TranscriptionState.Completed : TranscriptionState.Incomplete;
            return result.IsComplete;
        }
        catch (OperationCanceledException)
        {
            item.TranscriptionState = TranscriptionState.Cancelled;
            item.Progress = "—";
            item.IsProgressIndeterminate = false;
            item.RecognitionResult = null;
            item.ProcessedTranscript = null;
            item.ErrorMessage = null;
            throw;
        }
        catch (MediaPreparationException exception)
        {
            item.TranscriptionState = TranscriptionState.Error;
            item.Progress = "—";
            item.IsProgressIndeterminate = false;
            item.ErrorMessage = exception.Message;
            if (exception.IsRuntimeFailure)
                throw;

            return false;
        }
        catch (RecognitionConfigurationException exception)
        {
            item.TranscriptionState = TranscriptionState.Error;
            item.Progress = "—";
            item.IsProgressIndeterminate = false;
            item.ErrorMessage = exception.Message;
            throw;
        }
        catch (Exception exception)
        {
            item.TranscriptionState = TranscriptionState.Error;
            item.Progress = "—";
            item.IsProgressIndeterminate = false;
            item.ErrorMessage = exception.Message;
            return false;
        }
        finally
        {
            item.NotifyActionState();
            OnPropertyChanged(nameof(CanCopyAll));
            RefreshQueueActionState();
            NotifyRuntimeStatusChanged();
        }
    }

    private async Task EnhanceItemAsync(
        TranscriptionQueueItem item,
        AiPostProcessingOptions options,
        CancellationToken cancellationToken)
    {
        if (item.RecognitionResult is not { } recognition)
        {
            return;
        }

        item.AiEnhancementState = AiEnhancementState.Improving;
        item.AiEnhancementError = null;
        item.LatestCorrection = null;
        item.IsProgressIndeterminate = false;
        item.Progress = "0%";
        RefreshQueueActionState();

        var progress = new AiCorrectionProgress(
            new Progress<double>(value => item.Progress = $"{Math.Clamp(value, 0, 1) * 100:0}%"),
            new Progress<TranscriptCorrectionPreview>(value => item.LatestCorrection = value));

        try
        {
            var result = await transcriptEnhancementService.EnhanceAsync(
                recognition,
                options,
                progress,
                cancellationToken);

            item.ProcessedTranscript = result.Transcript;
            item.Progress = "100%";
            item.AiEnhancementState = AiEnhancementState.Completed;
        }
        catch (OperationCanceledException)
        {
            item.Progress = "—";
            item.AiEnhancementError = "AI processing was cancelled.";
            item.AiEnhancementState = AiEnhancementState.Cancelled;
            throw;
        }
        catch (TranscriptEnhancementException exception)
        {
            item.Progress = "—";
            item.AiEnhancementError = exception.Message;
            item.AiEnhancementState = AiEnhancementState.Failed;
        }
        catch
        {
            item.Progress = "—";
            item.AiEnhancementError = "The transcript is available, but AI post-processing failed.";
            item.AiEnhancementState = AiEnhancementState.Failed;
        }
        finally
        {
            item.NotifyActionState();
            OnPropertyChanged(nameof(CanCopyAll));
            RefreshQueueActionState();
        }
    }

    private sealed class AiCorrectionProgress(
        IProgress<double> percent,
        IProgress<TranscriptCorrectionPreview> corrections) : ITranscriptCorrectionProgress
    {
        public void Report(double value) => percent.Report(value);

        public void ReportCorrection(TranscriptCorrectionPreview correction) =>
            corrections.Report(correction);
    }

    private void EnsureModelStorageCanChange()
    {
        if (!CanChangeModelStorage)
        {
            throw new InvalidOperationException("Wait for recognition or the model download to finish before deleting models.");
        }
    }

    private void RefreshQueueActionState()
    {
        OnPropertyChanged(nameof(CanChangeModelStorage));
        foreach (var row in QueueRows)
        {
            row.CanTranscribe =
                IsSelectedModelAvailable &&
                activeTranscription is null &&
                !IsBatchActive &&
                !IsMicrophoneRecording &&
                !isMicrophoneTransitioning &&
                !isDictationRecording &&
                !row.IsProcessing &&
                row.AiEnhancementState != AiEnhancementState.Improving;
            row.CanEnhance =
                !IsBatchActive &&
                row.TranscriptionState == TranscriptionState.Completed &&
                row.RecognitionResult is not null &&
                row.AiEnhancementState != AiEnhancementState.Improving;
            row.NotifyActionState();
        }

        OnPropertyChanged(nameof(CanReorderQueue));
        OnPropertyChanged(nameof(CanTranscribeAll));
        OnPropertyChanged(nameof(CanCopyAll));
    }

    private void NotifyRuntimeStatusChanged()
    {
        OnPropertyChanged(nameof(ProcessingBackendStatus));
        OnPropertyChanged(nameof(ProcessingDeviceReloadMessage));
        OnPropertyChanged(nameof(HasProcessingDeviceReloadMessage));
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
