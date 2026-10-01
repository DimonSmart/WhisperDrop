using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
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
                services.AddSingleton<IWhisperModelCatalog, WhisperModelCatalog>();
                services.AddSingleton<IRecognitionLanguageCatalog, RecognitionLanguageCatalog>();
                services.AddSingleton<ISelectedModelAvailability, SelectedModelAvailability>();
                services.AddSingleton<ILocalModelInventory, LocalModelInventory>();
                services.AddSingleton<ISelectedModelDownloader, SelectedModelDownloader>();
                services.AddSingleton<ISelectedModelDownloadManager, SelectedModelDownloadManager>();
                services.AddSingleton<IWhisperRuntimeSelector, WhisperRuntimeSelector>();
                services.AddSingleton<IVadModelDownloader, VadModelDownloader>();
                services.AddSingleton<IVadModelManager, VadModelManager>();
                services.AddSingleton<IRecognitionService, WhisperRecognitionService>();
                services.AddSingleton<TranscriptChunker>();
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
            Title = "WhisperDrop"
        };
        SetWindowIcon(MainWindow);
        MainWindow.Content = host.Services.GetRequiredService<MainPage>();
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

        var iconPath = Path.Combine(AppContext.BaseDirectory, "WhisperDrop.ico");
        if (File.Exists(iconPath))
            window.AppWindow.SetIcon(iconPath);
    }
}
