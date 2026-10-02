using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly IWhisperModelCatalog modelCatalog;
    private readonly ISelectedModelAvailability availability;
    private readonly ISelectedModelDownloadManager downloadManager;
    private readonly IRecognitionService recognitionService;
    private readonly IMediaPreparationService mediaPreparationService;
    private readonly ITranscriptEnhancementService transcriptEnhancementService;
    private readonly IRecognitionLanguageCatalog languageCatalog;
    private readonly ILocalModelInventory localModelInventory;
    private readonly IVadModelManager vadModelManager;
    private readonly IWhisperRuntimeSelector runtimeSelector;
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

    public InitialApplicationState(
        IUserSettingsStore settingsStore,
        IWhisperModelCatalog modelCatalog,
        IRecognitionLanguageCatalog languageCatalog,
        ISelectedModelAvailability availability,
        ISelectedModelDownloadManager? downloadManager = null,
        IRecognitionService? recognitionService = null,
        ITranscriptEnhancementService? transcriptEnhancementService = null,
        ILocalModelInventory? localModelInventory = null,
        IVadModelManager? vadModelManager = null,
        IWhisperRuntimeSelector? runtimeSelector = null,
        IMediaPreparationService? mediaPreparationService = null)
    {
        this.settingsStore = settingsStore;
        this.modelCatalog = modelCatalog;
        this.availability = availability;
        this.downloadManager = downloadManager ?? new SelectedModelDownloadManager(modelCatalog, availability, new SelectedModelDownloader());
        this.runtimeSelector = runtimeSelector ?? new WhisperRuntimeSelector();
        this.vadModelManager = vadModelManager ?? new VadModelManager(new ApplicationPaths(), new VadModelDownloader());
        this.recognitionService = recognitionService ?? new WhisperRecognitionService(this.runtimeSelector, this.vadModelManager);
        this.mediaPreparationService = mediaPreparationService ?? new DirectMediaPreparationService();
        this.transcriptEnhancementService = transcriptEnhancementService ?? new UnavailableTranscriptEnhancementService();
        this.localModelInventory = localModelInventory ?? new LocalModelInventory(modelCatalog, availability);
        this.languageCatalog = languageCatalog;

        settings = settingsStore.Load();
        LanguageOptions = languageCatalog.Languages;
        TaskOptions =
        [
            new(TranscriptionTask.Transcribe, "Transcribe"),
            new(TranscriptionTask.TranslateToEnglish, "Translate to English")
        ];
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

    public IReadOnlyList<TranscriptionTaskOption> TaskOptions { get; }

    public IReadOnlyList<ProcessingDeviceOption> ProcessingDeviceOptions { get; }

    public IReadOnlyList<CpuThreadsOption> CpuThreadOptions { get; }

    public IReadOnlyList<AiProviderOption> AiProviderOptions { get; }

    public IReadOnlyList<AiContextSizeOption> AiContextSizeOptions { get; }

    public string AiBuiltInRecognitionHints => TranscriptEnhancementPromptBuilder.BuiltInRecognitionHints;

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
        set
        {
            if (string.Equals(settings.ModelsFolder, value, StringComparison.Ordinal))
            {
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

    public string RecognitionLanguageCode
    {
        get => settings.RecognitionLanguageCode;
        set => UpdateSettings(settings with { RecognitionLanguageCode = value }, nameof(RecognitionLanguageCode));
    }

    public TranscriptionTask SelectedTranscriptionTask
    {
        get => settings.Task;
        set => UpdateSettings(settings with { Task = value }, nameof(SelectedTranscriptionTask));
    }

    public string VocabularyContext
    {
        get => settings.VocabularyContext;
        set => UpdateSettings(settings with { VocabularyContext = value ?? string.Empty }, nameof(VocabularyContext));
    }

    public bool SkipSilence
    {
        get => settings.SkipSilence;
        set
        {
            if (value && !vadModelManager.IsAvailable)
            {
                return;
            }

            UpdateSettings(settings with { SkipSilence = value }, nameof(SkipSilence));
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

    public bool IsVadModelAvailable => vadModelManager.IsAvailable;

    public string? ProcessingDeviceRestartMessage =>
        runtimeSelector.RequiresRestart(settings.ProcessingDevice)
            ? "Restart required to change processing device."
            : null;

    public bool HasProcessingDeviceRestartMessage => ProcessingDeviceRestartMessage is not null;

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

    public async Task DownloadVadModelAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await vadModelManager.DownloadAsync(progress, cancellationToken);
        OnPropertyChanged(nameof(IsVadModelAvailable));
    }

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

    public async Task TranscribeAsync(TranscriptionQueueItem item, CancellationToken cancellationToken = default)
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

    public async Task TranscribeAllAsync(CancellationToken cancellationToken = default)
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

        var model = modelCatalog.Get(SelectedModelId);
        if (options.Task == TranscriptionTask.TranslateToEnglish && model.IsEnglishOnly)
        {
            guidance = "Translation requires a multilingual Whisper model.";
            return false;
        }

        if (options.SkipSilence && !vadModelManager.IsAvailable)
        {
            guidance = "Voice detection model is missing.";
            return false;
        }

        modelPath = availability.GetModelPath(ModelsFolder, SelectedModelId);
        return true;
    }

    private RecognitionOptions CreateRecognitionOptionsSnapshot()
    {
        var effectiveDevice = runtimeSelector.IsInitialized
            ? runtimeSelector.ActiveDevice
            : settings.ProcessingDevice;

        return new RecognitionOptions
        {
            LanguageCode = settings.RecognitionLanguageCode,
            Task = settings.Task,
            Prompt = settings.VocabularyContext,
            SkipSilence = settings.SkipSilence,
            ProcessingDevice = effectiveDevice,
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
        item.RecognitionResult = null;
        item.ProcessedTranscript = null;
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

            item.TranscriptionState = TranscriptionState.Transcribing;
            item.Progress = "0%";
            item.IsProgressIndeterminate = false;
            RefreshQueueActionState();

            var recognitionProgress = new Progress<double>(value =>
                item.Progress = $"{Math.Clamp(value, 0, 1) * 100:0}%");

            var result = await recognitionService.TranscribeAsync(
                modelPath,
                options,
                prepared.AudioPath,
                recognitionProgress,
                cancellationToken);

            item.RecognitionResult = result;
            item.Language = options.LanguageCode == "auto"
                ? result.DetectedLanguage is { } detected && TryGetLanguageName(detected, out var displayName)
                    ? displayName
                    : result.DetectedLanguage ?? "Auto"
                : languageCatalog.Get(options.LanguageCode).DisplayName;
            item.Progress = "100%";
            item.TranscriptionState = TranscriptionState.Completed;
            return true;
        }
        catch (OperationCanceledException)
        {
            item.TranscriptionState = TranscriptionState.Pending;
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
        item.IsProgressIndeterminate = false;
        item.Progress = "0%";
        RefreshQueueActionState();

        var progress = new Progress<double>(value =>
            item.Progress = $"{Math.Clamp(value, 0, 1) * 100:0}%");

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

    private void EnsureModelStorageCanChange()
    {
        if (QueueRows.Any(row => row.IsProcessing))
        {
            throw new InvalidOperationException("Models cannot be deleted while media processing is running.");
        }
    }

    private void RefreshQueueActionState()
    {
        foreach (var row in QueueRows)
        {
            row.CanTranscribe =
                IsSelectedModelAvailable &&
                !IsBatchActive &&
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
        OnPropertyChanged(nameof(ProcessingDeviceRestartMessage));
        OnPropertyChanged(nameof(HasProcessingDeviceRestartMessage));
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
