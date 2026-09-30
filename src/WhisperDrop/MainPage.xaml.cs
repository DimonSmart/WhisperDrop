using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WhisperDrop.Models;
using WhisperDrop.State;

namespace WhisperDrop;

public sealed partial class MainPage : Page
{
    public MainPage(InitialApplicationState viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateQueuePresentation();
    }

    public InitialApplicationState ViewModel { get; }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add(".wav");
        InitializeForMainWindow(picker);
        var files = await picker.PickMultipleFilesAsync().AsTask();
        AddFiles(files.Select(file => file.Path));
    }

    private async void BrowseModelsFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        InitializeForMainWindow(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.ModelsFolder = folder.Path;
        }
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.DownloadSelectedModelAsync();

    private void TranscribeNav_Click(object sender, RoutedEventArgs e)
    {
        TranscribeNavButton.IsChecked = true;
        ViewModel.SelectedTabIndex = InitialApplicationState.TranscribeTabIndex;
    }

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        SettingsNavButton.IsChecked = true;
        ViewModel.SelectedTabIndex = InitialApplicationState.SettingsTabIndex;
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            SetDropZoneActive(false);
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        DropFeedback.Text = "Drop to add WAV files";
        SetDropZoneActive(true);
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        ResetDropZone();
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        ResetDropZone();
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
        if (sender is Button { Tag: TranscriptionQueueItem item }) await ViewModel.TranscribeAsync(item);
    }

    private async void TranscribeAll_Click(object sender, RoutedEventArgs e) => await ViewModel.TranscribeAllAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TranscriptionQueueItem { Transcript: not null } item }) CopyToClipboard(item.Transcript);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) => CopyToClipboard(ViewModel.GetCompletedTranscriptsForCopy());

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
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

    private void SetDropZoneActive(bool isActive)
    {
        DropZone.BorderBrush = GetBrush(isActive ? "AccentBrush" : "ControlBorderBrush");
        DropZone.Background = GetBrush(isActive ? "AccentSubtleBrush" : "DropZoneBackgroundBrush");
    }

    private void ResetDropZone()
    {
        DropFeedback.Text = "WAV files only";
        SetDropZoneActive(false);
    }

    private static Brush GetBrush(string key) => (Brush)Application.Current.Resources[key];
}
