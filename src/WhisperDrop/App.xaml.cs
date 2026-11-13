using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using WhisperDrop.Media;
using WhisperDrop.Models;
using WhisperDrop.Settings;
using WhisperDrop.State;
using WhisperDrop.Updates;

namespace WhisperDrop;

public partial class App : Application
{
    private readonly IHost host;

    internal static Window MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();

        host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IApplicationPaths, ApplicationPaths>();
                services.AddSingleton<IUserSettingsStore, JsonUserSettingsStore>();
                services.AddSingleton<IRecognitionModelCatalog, ParakeetModelCatalog>();
                services.AddSingleton<IRecognitionLanguageCatalog, RecognitionLanguageCatalog>();
                services.AddSingleton<ISelectedModelAvailability, SelectedModelAvailability>();
                services.AddSingleton<ILocalModelInventory, LocalModelInventory>();
                services.AddSingleton<ISelectedModelDownloader, SelectedModelDownloader>();
                services.AddSingleton<ISelectedModelDownloadManager, SelectedModelDownloadManager>();
                services.AddSingleton<IRecognitionRuntimeSelector, ParakeetRuntimeSelector>();
                services.AddSingleton<IFfmpegRuntime, FfmpegRuntime>();
                services.AddSingleton<IMediaPreparationService, MediaPreparationService>();
                services.AddSingleton<IMicrophoneCaptureService>(_ => OperatingSystem.IsMacOS()
                    ? new MacOsMicrophoneCaptureService()
                    : OperatingSystem.IsWindows()
                        ? new WindowsMicrophoneCaptureService()
                        : new UnsupportedMicrophoneCaptureService());
                services.AddSingleton<IRecognitionService, ParakeetRecognitionService>();
                services.AddSingleton<TranscriptChunker>();
                services.AddSingleton<ITranscriptEnhancementDiagnostics, TranscriptEnhancementDiagnostics>();
                services.AddSingleton<ITranscriptEnhancementAgentFactory, OpenAiTranscriptEnhancementAgentFactory>();
                services.AddSingleton<ITranscriptEnhancementService, TranscriptEnhancementService>();
                services.AddSingleton<IApplicationVersionProvider>(_ => new ApplicationVersionProvider(typeof(App).Assembly));
                services.AddSingleton<IUpdateCheckService, GitHubUpdateCheckService>();
                services.AddSingleton<IProcessExecutor, ProcessExecutor>();
                services.AddSingleton<IApplicationUpdateEnvironment, DefaultApplicationUpdateEnvironment>();
                if (OperatingSystem.IsMacOS())
                    services.AddSingleton<IApplicationUpdateInstaller, MacOsHomebrewUpdateInstaller>();
                else
                    services.AddSingleton<IApplicationUpdateInstaller, UnsupportedApplicationUpdateInstaller>();
                services.AddSingleton<ISystemUriLauncher, SystemUriLauncher>();
                services.AddSingleton<InitialApplicationState>();
                services.AddSingleton<MainPage>();
            })
            .Build();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new Window
        {
            Title = "ParakeetDrop"
        };
        SetWindowIcon(MainWindow);
        MainWindow.Closed += (_, _) => host.Services.GetRequiredService<IMicrophoneCaptureService>().CleanupSession();
        MainWindow.Content = host.Services.GetRequiredService<MainPage>();
        var state = host.Services.GetRequiredService<InitialApplicationState>();
        ApplicationAcceptanceProbe.Record("launch", new { queueCount = state.QueueRows.Count,
            modelAvailable = state.IsSelectedModelAvailable, aiEnabled = state.AiPostProcessingEnabled });
        MainWindow.Activate();
    }

    internal static void CompleteApplicationUpdateRestart(MainPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        page.DispatcherQueue.TryEnqueue(() => MainWindow.Close());
    }

    private static void SetWindowIcon(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "ParakeetDrop.ico");
        if (File.Exists(iconPath))
            window.AppWindow.SetIcon(iconPath);
    }
}
