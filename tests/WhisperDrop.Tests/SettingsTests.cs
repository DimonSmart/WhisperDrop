using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Models;
using WhisperDrop.Settings;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ParakeetDrop.Tests", Guid.NewGuid().ToString("N"));
    [Fact]
    public void Defaults_use_Parakeet_and_do_not_scan_legacy_locations()
    {
        var paths = new ApplicationPaths(root);
        var settings = new JsonUserSettingsStore(paths).Load();
        Assert.Equal(ParakeetModelCatalog.ModelId, settings.SelectedModelId);
        Assert.Equal(ProcessingDevice.Auto, settings.ProcessingDevice);
        Assert.Null(settings.CpuThreads);
        Assert.Equal(paths.DefaultModelsFolder, settings.ModelsFolder);
        Assert.Contains("ParakeetDrop", new ApplicationPaths().SettingsFilePath);
        Assert.False(Directory.Exists(root));
        Assert.Single(new ParakeetModelCatalog().Models);
    }
    [Fact]
    public void Round_trip_preserves_settings_and_writes_atomically_without_removed_controls()
    {
        var store = new JsonUserSettingsStore(new ApplicationPaths(root));
        var settings = new UserSettings { ModelsFolder = root, CpuThreads = 1, ProcessingDevice = ProcessingDevice.Cpu,
            AiPostProcessing = new() { Model = "custom", Instructions = "verbatim", Enabled = true } };
        store.Save(settings);
        Assert.Equal(settings, store.Load());
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        var json = File.ReadAllText(new ApplicationPaths(root).SettingsFilePath);
        Assert.DoesNotContain("Whisper", json);
        Assert.DoesNotContain("Vocabulary", json);
        Assert.DoesNotContain("Language", json);
    }
    [Fact]
    public void Dictation_settings_persist_and_default_to_enabled()
    {
        var store = new JsonUserSettingsStore(new ApplicationPaths(root));
        Assert.True(store.Load().Dictation.Enabled);
        Assert.Equal("Ctrl+Shift+Space", store.Load().Dictation.Hotkey);
        store.Save(new UserSettings
        {
            Dictation = new() { Enabled = false, Hotkey = "Ctrl+Alt+D", InsertAfterRecognition = false }
        });
        var settings = store.Load().Dictation;
        Assert.False(settings.Enabled);
        Assert.Equal("Ctrl+Alt+D", settings.Hotkey);
        Assert.False(settings.InsertAfterRecognition);
    }

    [Fact]
    public void Invalid_new_values_normalize_independently()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(new ApplicationPaths(root).SettingsFilePath,
            "{\"ModelsFolder\":\"custom\",\"ProcessingDevice\":\"Quantum\",\"CpuThreads\":99999,\"SelectedModelId\":\"unsupported\",\"AiPostProcessing\":{\"Model\":\"keep\"}}");
        var loaded = new JsonUserSettingsStore(new ApplicationPaths(root)).Load();
        Assert.Equal("custom", loaded.ModelsFolder);
        Assert.Equal("keep", loaded.AiPostProcessing.Model);
        Assert.Equal(ProcessingDevice.Auto, loaded.ProcessingDevice);
        Assert.Null(loaded.CpuThreads);
        Assert.Equal(ParakeetModelCatalog.ModelId, loaded.SelectedModelId);
    }
    [Fact]
    public void Malformed_field_types_do_not_discard_unrelated_valid_settings()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(new ApplicationPaths(root).SettingsFilePath,
            "{\"ModelsFolder\":\"custom\",\"ProcessingDevice\":true,\"CpuThreads\":\"bad\",\"SelectedModelId\":42,\"AiPostProcessing\":{\"Enabled\":{},\"Model\":\"keep\"}}");
        var loaded = new JsonUserSettingsStore(new ApplicationPaths(root)).Load();
        Assert.Equal("custom", loaded.ModelsFolder);
        Assert.Equal("keep", loaded.AiPostProcessing.Model);
        Assert.False(loaded.AiPostProcessing.Enabled);
        Assert.Equal(ProcessingDevice.Auto, loaded.ProcessingDevice);
        Assert.Null(loaded.CpuThreads);
    }
    [Fact]
    public void Model_availability_requires_exact_size_and_hash_offline()
    {
        var catalog = new TestModelCatalog(); var availability = new SelectedModelAvailability(catalog);
        Directory.CreateDirectory(root);
        var path = availability.GetModelPath(root, TestModelCatalog.Model.Id);
        Assert.False(availability.IsAvailable(root, TestModelCatalog.Model.Id));
        File.WriteAllText(path, "wrong"); Assert.False(availability.IsAvailable(root, TestModelCatalog.Model.Id));
        File.WriteAllBytes(path, TestModelCatalog.Bytes); Assert.True(availability.IsAvailable(root, TestModelCatalog.Model.Id));
        File.AppendAllText(path, "x"); Assert.False(availability.IsAvailable(root, TestModelCatalog.Model.Id));
    }
    [Fact]
    public async Task Download_verifies_then_atomically_installs_and_reports_progress()
    {
        var catalog = new TestModelCatalog(); var availability = new SelectedModelAvailability(catalog);
        var manager = new SelectedModelDownloadManager(catalog, availability, new FakeDownloader());
        await manager.DownloadAsync(root, TestModelCatalog.Model.Id, null, default);
        Assert.True(availability.IsAvailable(root, TestModelCatalog.Model.Id));
        Assert.Empty(Directory.GetFiles(root, "*.download"));
        var installed = Assert.Single(new LocalModelInventory(catalog, availability).GetInstalled(root));
        Assert.Equal(5, installed.SizeBytes);
    }
    [Theory]
    [InlineData("error")]
    [InlineData("cancel")]
    [InlineData("hash")]
    [InlineData("size")]
    public async Task Failed_or_cancelled_download_never_installs_partial_bytes(string mode)
    {
        var catalog = new TestModelCatalog(); var availability = new SelectedModelAvailability(catalog);
        var manager = new SelectedModelDownloadManager(catalog, availability, new FakeDownloader(mode));
        await Assert.ThrowsAnyAsync<Exception>(() => manager.DownloadAsync(root, TestModelCatalog.Model.Id, null, default));
        Assert.False(availability.IsAvailable(root, TestModelCatalog.Model.Id));
        Assert.Empty(Directory.GetFiles(root));
    }
    [Fact]
    public async Task Cancellation_during_a_download_cleans_the_temporary_file()
    {
        var catalog = new TestModelCatalog(); var availability = new SelectedModelAvailability(catalog);
        var transport = new PausingDownloader();
        var manager = new SelectedModelDownloadManager(catalog, availability, transport);
        using var cancellation = new CancellationTokenSource();
        var task = manager.DownloadAsync(root, TestModelCatalog.Model.Id, null, cancellation.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(availability.IsAvailable(root, TestModelCatalog.Model.Id));
        Assert.Single(Directory.GetFiles(root, "*.download"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(Directory.GetFiles(root));
    }

    private sealed class PausingDownloader : ISelectedModelDownloader
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task DownloadAsync(RecognitionModel model, Stream stream, IProgress<ModelDownloadProgress>? progress, CancellationToken token)
        {
            await stream.WriteAsync(new byte[] { 1, 2 }, token);
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
    }

    [Fact]
    public async Task HTTP_downloader_copies_bytes_and_reports_the_pinned_total()
    {
        using var client = new HttpClient(new BytesHandler());
        using var destination = new MemoryStream();
        var progress = new SynchronousProgress();
        await new SelectedModelDownloader(client).DownloadAsync(TestModelCatalog.Model, destination, progress, default);
        Assert.Equal(TestModelCatalog.Bytes, destination.ToArray());
        Assert.Equal(new ModelDownloadProgress(5, 5), progress.Last);
    }
    private sealed class BytesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestModelCatalog.Bytes) });
    }
    private sealed class SynchronousProgress : IProgress<ModelDownloadProgress>
    {
        public ModelDownloadProgress? Last;
        public void Report(ModelDownloadProgress value) => Last = value;
    }
    [Fact]
    public void Deletion_is_catalog_bounded_and_keeps_unrelated_files()
    {
        var catalog = new TestModelCatalog(); var availability = new SelectedModelAvailability(catalog);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(availability.GetModelPath(root, TestModelCatalog.Model.Id), TestModelCatalog.Bytes);
        File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");
        var inventory = new LocalModelInventory(catalog, availability);
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.Delete(root, "../keep.txt"));
        inventory.DeleteAll(root);
        Assert.Equal("keep.txt", Path.GetFileName(Assert.Single(Directory.GetFiles(root))));
    }
    [Theory]
    [InlineData(404, "missing")]
    [InlineData(429, "rate limiting")]
    [InlineData(503, "network")]
    public async Task Download_errors_are_actionable(int status, string expected)
    {
        using var client = new HttpClient(new ResponseHandler(status));
        using var destination = new MemoryStream();
        var error = await Assert.ThrowsAsync<IOException>(() => new SelectedModelDownloader(client).DownloadAsync(TestModelCatalog.Model, destination, null, default));
        Assert.Contains(expected, error.Message);
    }
    private sealed class ResponseHandler(int code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code));
    }
    private sealed class FakeDownloader(string mode = "ok") : ISelectedModelDownloader
    {
        public async Task DownloadAsync(RecognitionModel model, Stream stream, IProgress<ModelDownloadProgress>? progress, CancellationToken token)
        {
            await stream.WriteAsync(mode == "hash" ? "wrong"u8.ToArray() : mode == "size" ? [1] : TestModelCatalog.Bytes, token);
            if (mode == "error") throw new IOException("network failed");
            if (mode == "cancel") throw new OperationCanceledException();
            progress?.Report(new(TestModelCatalog.Bytes.Length, model.SizeBytes));
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
