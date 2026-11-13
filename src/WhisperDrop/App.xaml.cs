using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using WhisperDrop.Dictation;
using WhisperDrop.Media;
using WhisperDrop.Models;
using WhisperDrop.Settings;
using WhisperDrop.State;
using WhisperDrop.Updates;

namespace WhisperDrop;

public partial class App : Application
{
    private readonly IHost host;
    private WindowsDictationCoordinator? dictation;
    private Window? settingsWindow;
    private bool isExiting;

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
        MainWindow.AppWindow.Closing += (_, args) =>
        {
            var state = host.Services.GetRequiredService<InitialApplicationState>();
            if (WindowClosePolicy.ShouldHideOnClose(state.HideToTrayOnClose, dictation is not null, isExiting) &&
                HideToTray())
            {
                args.Cancel = true;
            }
        };
        MainWindow.Closed += (_, _) =>
        {
            settingsWindow?.Close();
            dictation?.Dispose();
            host.Services.GetRequiredService<IMicrophoneCaptureService>().CleanupSession();
            host.Dispose();
        };
        MainWindow.Content = host.Services.GetRequiredService<MainPage>();
        var state = host.Services.GetRequiredService<InitialApplicationState>();
        ApplicationAcceptanceProbe.Record("launch", new { queueCount = state.QueueRows.Count,
            modelAvailable = state.IsSelectedModelAvailable, aiEnabled = state.AiPostProcessingEnabled });
        MainWindow.Activate();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                dictation = new WindowsDictationCoordinator(state,
                    host.Services.GetRequiredService<IMicrophoneCaptureService>(),
                    callback => MainWindow.DispatcherQueue.TryEnqueue(() => callback()),
                    ShowMainWindow,
                    ExitFromTray);
            }
            catch (Exception exception)
            {
                state.SetDictationStatus("Background dictation unavailable: " + exception.Message);
            }
        }
    }

    internal bool HideToTray()
    {
        if (!OperatingSystem.IsWindows() || dictation is null) return false;

        var handle = GetMainWindowHandle();
        if (handle == IntPtr.Zero || !IsWindow(handle)) return false;

        // WinRT.Interop.WindowNative returns an Uno AppWindow ID on Skia Desktop,
        // not a Win32 HWND. Use the native host HWND for these Win32 calls.
        ShowWindow(handle, 0); // SW_HIDE
        if (IsWindowVisible(handle)) return false;

        settingsWindow?.Close();
        return true;
    }

    private void ShowMainWindow()
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = GetMainWindowHandle();
            if (handle != IntPtr.Zero && IsWindow(handle))
            {
                ShowWindow(handle, IsIconic(handle) ? 9 : 5); // SW_RESTORE / SW_SHOW
                SetForegroundWindow(handle);
            }
        }

        MainWindow.Activate();
    }

    private static IntPtr GetMainWindowHandle() =>
        Uno.UI.Xaml.WindowHelper.GetNativeWindow(MainWindow) is Uno.UI.NativeElementHosting.Win32NativeWindow native
            ? native.Hwnd
            : IntPtr.Zero;

    internal void ShowSettings()
    {
        if (settingsWindow is not null)
        {
            settingsWindow.Activate();
            return;
        }

        var window = new Window { Title = "ParakeetDrop — Settings" };
        settingsWindow = window;
        SetWindowIcon(window);
        window.Content = new SettingsPage(host.Services.GetRequiredService<InitialApplicationState>(), window);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 920, Height = 650 });
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(settingsWindow, window)) settingsWindow = null;
        };
        window.Activate();
    }

    internal static void CompleteApplicationUpdateRestart(MainPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        page.DispatcherQueue.TryEnqueue(() =>
        {
            if (Current is App app) app.ExitFromTray();
            else MainWindow.Close();
        });
    }

    internal void ExitApplication()
    {
        isExiting = true;
        MainWindow.Close();
    }

    private void ExitFromTray() => ExitApplication();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void SetWindowIcon(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "ParakeetDrop.ico");
        if (File.Exists(iconPath))
            window.AppWindow.SetIcon(iconPath);
    }
}
