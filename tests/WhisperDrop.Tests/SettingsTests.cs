using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Models;
using WhisperDrop.Settings;
using WhisperDrop.State;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WhisperDrop.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Defaults_use_the_platform_application_data_models_folder_base_and_auto()
    {
        var paths = new ApplicationPaths(root);
        var settings = new JsonUserSettingsStore(paths).Load();

        Assert.Equal(Path.Combine(root, "Models"), settings.ModelsFolder);
        Assert.Equal("base", settings.SelectedModelId);
        Assert.Equal("auto", settings.RecognitionLanguageCode);
    }

    [Fact]
    public void Save_replaces_settings_and_load_retains_the_user_choices()
    {
        var paths = new ApplicationPaths(root);
        var store = new JsonUserSettingsStore(paths);
        var selectedFolder = Path.Combine(root, "custom-models");

        store.Save(new UserSettings
        {
            ModelsFolder = selectedFolder,
            SelectedModelId = "small-en",
            RecognitionLanguageCode = "es"
        });

        var loaded = store.Load();
        Assert.Equal(selectedFolder, loaded.ModelsFolder);
        Assert.Equal("small-en", loaded.SelectedModelId);
        Assert.Equal("es", loaded.RecognitionLanguageCode);
        Assert.DoesNotContain(Directory.EnumerateFiles(root), path => Path.GetFileName(path).Contains(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Model_path_and_presence_follow_the_selected_catalog_model()
    {
        var catalog = new WhisperModelCatalog();
        var availability = new SelectedModelAvailability(catalog);
        var modelsFolder = Path.Combine(root, "models");
        var expectedPath = Path.Combine(modelsFolder, "ggml-base.bin");

        Assert.Equal(expectedPath, availability.GetModelPath(modelsFolder, "base"));
        Assert.False(availability.IsAvailable(modelsFolder, "base"));

        Directory.CreateDirectory(modelsFolder);
        File.WriteAllText(expectedPath, "complete model");

        Assert.True(availability.IsAvailable(modelsFolder, "base"));
    }

    [Fact]
    public void Local_inventory_reports_actual_file_sizes_and_ignores_unrelated_files()
    {
        var catalog = new WhisperModelCatalog();
        var availability = new SelectedModelAvailability(catalog);
        var inventory = new LocalModelInventory(catalog, availability);
        var modelsFolder = Path.Combine(root, "models");
        Directory.CreateDirectory(modelsFolder);
        File.WriteAllBytes(availability.GetModelPath(modelsFolder, "base"), new byte[1536]);
        File.WriteAllText(Path.Combine(modelsFolder, "notes.txt"), "keep");

        var installed = inventory.GetInstalled(modelsFolder);

        var model = Assert.Single(installed);
        Assert.Equal("base", model.Id);
        Assert.Equal(1536, model.SizeBytes);
        Assert.Equal("1.5 KB", model.SizeText);
    }

    [Fact]
    public void Delete_all_removes_only_known_model_files()
    {
        var catalog = new WhisperModelCatalog();
        var availability = new SelectedModelAvailability(catalog);
        var inventory = new LocalModelInventory(catalog, availability);
        var modelsFolder = Path.Combine(root, "models");
        Directory.CreateDirectory(modelsFolder);
        File.WriteAllText(availability.GetModelPath(modelsFolder, "base"), "base");
        File.WriteAllText(availability.GetModelPath(modelsFolder, "small"), "small");
        var unrelatedPath = Path.Combine(modelsFolder, "notes.txt");
        File.WriteAllText(unrelatedPath, "keep");

        inventory.DeleteAll(modelsFolder);

        Assert.Empty(inventory.GetInstalled(modelsFolder));
        Assert.True(File.Exists(unrelatedPath));
    }

    [Fact]
    public void Settings_are_retained_without_any_workspace_data()
    {
        var paths = new ApplicationPaths(root);
        var store = new JsonUserSettingsStore(paths);
        store.Save(new UserSettings
        {
            ModelsFolder = Path.Combine(root, "models"),
            SelectedModelId = "medium",
            RecognitionLanguageCode = "fr"
        });

        var json = File.ReadAllText(paths.SettingsFilePath);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("progress", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Download_finalizes_a_complete_model_without_leaving_a_download_file()
    {
        var catalog = new WhisperModelCatalog();
        var availability = new SelectedModelAvailability(catalog);
        var downloader = new TestDownloader("complete model");
        var manager = new SelectedModelDownloadManager(catalog, availability, downloader);
        var modelsFolder = Path.Combine(root, "models");

        await manager.DownloadAsync(modelsFolder, "base", progress: null, CancellationToken.None);

        Assert.True(availability.IsAvailable(modelsFolder, "base"));
        Assert.Equal("complete model", File.ReadAllText(availability.GetModelPath(modelsFolder, "base")));
        Assert.Empty(Directory.EnumerateFiles(modelsFolder, "*.download"));
    }

    [Fact]
    public async Task Download_error_cleans_up_incomplete_file_and_never_marks_the_model_available()
    {
        var catalog = new WhisperModelCatalog();
        var availability = new SelectedModelAvailability(catalog);
        var manager = new SelectedModelDownloadManager(catalog, availability, new TestDownloader(exception: new IOException("network failed")));
        var modelsFolder = Path.Combine(root, "models");

        await Assert.ThrowsAsync<IOException>(() => manager.DownloadAsync(modelsFolder, "base", progress: null, CancellationToken.None));

        Assert.False(availability.IsAvailable(modelsFolder, "base"));
        Assert.Empty(Directory.EnumerateFiles(modelsFolder, "*.download"));
    }

    [Fact]
    public async Task Selected_model_is_reevaluated_and_never_downloads_until_requested()
    {
        var paths = new ApplicationPaths(root);
        var catalog = new WhisperModelCatalog();
        var availability = new SelectedModelAvailability(catalog);
        var downloader = new TestDownloader("base model");
        var state = new InitialApplicationState(
            new JsonUserSettingsStore(paths),
            catalog,
            new RecognitionLanguageCatalog(),
            availability,
            new SelectedModelDownloadManager(catalog, availability, downloader));

        Assert.Equal(SelectedModelDownloadState.NotDownloaded, state.SelectedModelDownloadState);
        Assert.Equal(0, downloader.CallCount);
        Assert.Empty(state.InstalledModels);

        await state.DownloadSelectedModelAsync();

        Assert.Equal(1, downloader.CallCount);
        Assert.Equal(SelectedModelDownloadState.Downloaded, state.SelectedModelDownloadState);
        Assert.Single(state.InstalledModels);

        state.SelectedModelId = "small";
        Assert.Equal(SelectedModelDownloadState.NotDownloaded, state.SelectedModelDownloadState);
        state.ModelsFolder = Path.Combine(root, "another-models-folder");
        Assert.Equal(SelectedModelDownloadState.NotDownloaded, state.SelectedModelDownloadState);
        Assert.Empty(state.InstalledModels);
    }

    [Fact]
    public async Task Download_failure_is_an_actionable_selected_model_error()
    {
        var paths = new ApplicationPaths(root);
        var catalog = new WhisperModelCatalog();
        var availability = new SelectedModelAvailability(catalog);
        var state = new InitialApplicationState(
            new JsonUserSettingsStore(paths),
            catalog,
            new RecognitionLanguageCatalog(),
            availability,
            new SelectedModelDownloadManager(catalog, availability, new TestDownloader(exception: new IOException("folder unavailable"))));

        await state.DownloadSelectedModelAsync();

        Assert.Equal(SelectedModelDownloadState.Error, state.SelectedModelDownloadState);
        Assert.True(state.CanDownloadSelectedModel);
        Assert.Contains("Download failed", state.SelectedModelStatus);
    }

    private sealed class TestDownloader : ISelectedModelDownloader
    {
        private readonly string content;
        private readonly Exception? exception;

        public TestDownloader(string content = "", Exception? exception = null)
        {
            this.content = content;
            this.exception = exception;
        }

        public int CallCount { get; private set; }

        public async Task DownloadAsync(RecognitionModel model, Stream destination, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            CallCount++;
            if (exception is not null)
            {
                await destination.WriteAsync(new byte[] { 1 }, cancellationToken);
                throw exception;
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            await destination.WriteAsync(bytes, cancellationToken);
            progress?.Report(new ModelDownloadProgress(bytes.Length, bytes.Length));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
