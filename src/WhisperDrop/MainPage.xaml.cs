using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WhisperDrop.Media;
using WhisperDrop.Models;
using WhisperDrop.State;
using WhisperDrop.Updates;

namespace WhisperDrop;

public sealed partial class MainPage : Page
{
    public MainPage(
        InitialApplicationState viewModel,
        IApplicationVersionProvider applicationVersionProvider,
        IUpdateCheckService updateCheckService,
        IApplicationUpdateInstaller applicationUpdateInstaller,
        ISystemUriLauncher systemUriLauncher)
    {
        ViewModel = viewModel;
        this.applicationVersionProvider = applicationVersionProvider;
        this.updateCheckService = updateCheckService;
        this.applicationUpdateInstaller = applicationUpdateInstaller;
        this.systemUriLauncher = systemUriLauncher;
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateQueuePresentation();
    }

    public InitialApplicationState ViewModel { get; }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ShowSettings();
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        foreach (var extension in SupportedMediaFormats.Extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }
        InitializeForMainWindow(picker);
        var files = await picker.PickMultipleFilesAsync().AsTask();
        AddFiles(files.Select(file => file.Path));
    }

    private async void Microphone_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ToggleMicrophoneRecordingAsync();
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Microphone recording failed", exception.Message);
        }
    }

    private async void DownloadSelectedModel_Click(object sender, RoutedEventArgs e) =>
        await ShowDownloadModelDialogAsync(ViewModel.SelectedModelId);

    private void FileQueueWorkspace_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        SetFileQueueWorkspaceActive(true);
        e.Handled = true;
    }

    private void FileQueueWorkspace_DragLeave(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        SetFileQueueWorkspaceActive(false);
        e.Handled = true;
    }

    private async void FileQueueWorkspace_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        SetFileQueueWorkspaceActive(false);
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
        var items = await e.DataView.GetStorageItemsAsync().AsTask();
        AddFiles(items.OfType<StorageFile>().Select(file => file.Path));
    }

    private void QueueList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        UpdateQueuePresentation();

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscriptionQueueItem item })
        {
            ViewModel.RemoveQueueItem(item);
        }
    }

    private async void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscriptionQueueItem item })
        {
            try { await ViewModel.TranscribeAsync(item); }
            catch (OperationCanceledException) { }
        }
    }

    private async void TranscribeAll_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.TranscribeAllAsync(); }
        catch (OperationCanceledException) { }
    }

    private void CancelTranscription_Click(object sender, RoutedEventArgs e) => ViewModel.CancelTranscription();


    private async void PreviewTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TranscriptionQueueItem item } || !item.CanPreviewTranscript)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = $"Transcript preview — {item.FileName}",
            CloseButtonText = "Close",
            Content = new TextBox
            {
                Text = item.GetPreviewTranscript(),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 400
            }
        };
        dialog.Opened += (_, _) => ApplicationAcceptanceProbe.Record("preview", new { characters = item.GetPreviewTranscript().Length, readOnly = true });
        await dialog.ShowAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscriptionQueueItem { EffectiveTranscript: not null } item })
        {
            CopyToClipboard(item.EffectiveTranscript);
        }
    }

    private void CopyRaw_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscriptionQueueItem { RawTranscript: not null } item })
        {
            CopyToClipboard(item.RawTranscript);
        }
    }

    private void CopyAiInput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TranscriptionQueueItem item })
        {
            return;
        }

        var input = ViewModel.GetAiInputForCopy(item);
        if (!string.IsNullOrEmpty(input))
        {
            CopyToClipboard(input);
        }
    }

    private async void ImproveTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TranscriptionQueueItem item })
        {
            return;
        }

        try
        {
            await ViewModel.RunAiPostProcessingAsync(item);
        }
        catch (OperationCanceledException)
        {
            // The queue row already exposes the cancelled AI state.
        }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(ViewModel.GetCompletedTranscriptsForCopy());

    private Task ShowDownloadModelDialogAsync(string modelId) =>
        ModelDownloadDialog.ShowAsync(this, ViewModel, modelId);

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "Close"
        };
        await dialog.ShowAsync();
    }

    private static async void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARAKEETDROP_ACCEPTANCE_EVIDENCE")))
        {
            try
            {
                // The native clipboard bridge posts its write to the platform loop.
                await Task.Delay(150);
                var copied = await Clipboard.GetContent().GetTextAsync();
                ApplicationAcceptanceProbe.Record("copy", new { characters = text.Length, clipboardCharacters = copied.Length, clipboardMatches = copied == text });
            }
            catch (Exception)
            {
                ApplicationAcceptanceProbe.Record("copy", new { characters = text.Length, clipboardMatches = false });
            }
        }
    }

    private static void InitializeForMainWindow(object picker)
    {
        if (OperatingSystem.IsWindows())
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }
    }

    private void AddFiles(System.Collections.Generic.IEnumerable<string> filePaths)
    {
        ViewModel.AddFiles(filePaths);
        UpdateQueuePresentation();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InitialApplicationState.IsTranscribeWorkspaceEmpty))
        {
            UpdateQueuePresentation();
        }
        else if (e.PropertyName == nameof(InitialApplicationState.UnsupportedFormatMessage))
        {
            UnsupportedFormatInfo.Message = ViewModel.UnsupportedFormatMessage ?? string.Empty;
            UnsupportedFormatInfo.IsOpen = ViewModel.UnsupportedFormatMessage is not null;
        }
    }

    private void UpdateQueuePresentation()
    {
        EmptyState.Visibility = ViewModel.IsTranscribeWorkspaceEmpty ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = ViewModel.IsTranscribeWorkspaceEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetFileQueueWorkspaceActive(bool isActive)
    {
        FileQueueWorkspace.BorderBrush = GetBrush(isActive ? "AccentBrush" : "ControlBorderBrush");
        FileQueueWorkspace.Background = GetBrush(isActive ? "AccentSubtleBrush" : "FileQueueWorkspaceBackgroundBrush");
    }

    private static Style GetStyle(string key) => (Style)Application.Current.Resources[key];

    private static Brush GetBrush(string key) => (Brush)Application.Current.Resources[key];
}
