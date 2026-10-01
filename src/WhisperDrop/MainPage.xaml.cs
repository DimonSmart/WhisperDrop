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

    private async void DownloadSelectedModel_Click(object sender, RoutedEventArgs e) =>
        await ShowDownloadModelDialogAsync(ViewModel.SelectedModelId);

    private async void DownloadModel_Click(object sender, RoutedEventArgs e) =>
        await ShowDownloadModelDialogAsync(ViewModel.SelectedModelId);

    private void TranscribeNav_Click(object sender, RoutedEventArgs e)
    {
        TranscribeNavButton.IsChecked = true;
        ViewModel.SelectedTabIndex = InitialApplicationState.TranscribeTabIndex;
    }

    private void ModelsNav_Click(object sender, RoutedEventArgs e)
    {
        ModelsNavButton.IsChecked = true;
        ViewModel.SelectedTabIndex = InitialApplicationState.ModelsTabIndex;
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
        DropFeedback.Text = "Drop to add audio or video files";
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
        if (sender is Button { Tag: TranscriptionQueueItem item } &&
            await EnsureVadModelForCurrentSettingsAsync())
        {
            await ViewModel.TranscribeAsync(item);
        }
    }

    private async void TranscribeAll_Click(object sender, RoutedEventArgs e)
    {
        if (await EnsureVadModelForCurrentSettingsAsync())
        {
            await ViewModel.TranscribeAllAsync();
        }
    }

    private async void SkipSilence_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox)
        {
            return;
        }

        if (checkBox.IsChecked != true)
        {
            ViewModel.SkipSilence = false;
            checkBox.IsChecked = false;
            return;
        }

        if (!ViewModel.IsVadModelAvailable &&
            !await ShowVadModelDownloadDialogAsync())
        {
            checkBox.IsChecked = false;
            return;
        }

        ViewModel.SkipSilence = true;
        checkBox.IsChecked = ViewModel.SkipSilence;
    }

    private async Task<bool> EnsureVadModelForCurrentSettingsAsync()
    {
        if (!ViewModel.SkipSilence || ViewModel.IsVadModelAvailable)
        {
            return true;
        }

        return await ShowVadModelDownloadDialogAsync();
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

    private async void TestAiConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.TestAiConnectionAsync();
        }
        catch (OperationCanceledException)
        {
            // No user-visible error is needed for explicit cancellation.
        }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(ViewModel.GetCompletedTranscriptsForCopy());

    private async void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LocalModelInfo model })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete {model.DisplayName}?",
            Content = $"Delete {model.SizeText} from the models folder?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            ViewModel.DeleteModel(model.Id);
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Could not delete model", exception.Message);
        }
    }

    private async void DeleteAllModels_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasInstalledModels)
        {
            return;
        }

        var summary = ViewModel.InstalledModelsSummary;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete all downloaded models?",
            Content = $"This will remove {summary} from the models folder. Other files in that folder are left untouched.",
            PrimaryButtonText = "Delete all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            ViewModel.DeleteAllModels();
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync("Could not delete models", exception.Message);
        }
    }

    private async Task<bool> ShowVadModelDownloadDialogAsync()
    {
        if (ViewModel.IsVadModelAvailable)
        {
            return true;
        }

        var description = new TextBlock
        {
            Text = "Voice detection requires an additional model.",
            Style = GetStyle("SecondaryTextStyle"),
            TextWrapping = TextWrapping.Wrap
        };
        var progressBar = new ProgressBar
        {
            Maximum = 1,
            IsIndeterminate = false,
            Visibility = Visibility.Collapsed
        };
        var status = new TextBlock
        {
            Style = GetStyle("SecondaryTextStyle"),
            TextWrapping = TextWrapping.Wrap
        };
        var downloadButton = new Button
        {
            Content = "Download",
            Style = GetStyle("PrimaryButtonStyle")
        };
        var closeButton = new Button
        {
            Content = "Cancel",
            Style = GetStyle("SecondaryButtonStyle")
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        actions.Children.Add(closeButton);
        actions.Children.Add(downloadButton);

        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 360
        };
        content.Children.Add(description);
        content.Children.Add(progressBar);
        content.Children.Add(status);
        content.Children.Add(actions);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Voice detection",
            Content = content
        };

        var downloaded = false;
        CancellationTokenSource? cancellation = null;

        closeButton.Click += (_, _) =>
        {
            if (cancellation is null)
            {
                dialog.Hide();
            }
            else
            {
                cancellation.Cancel();
            }
        };

        downloadButton.Click += async (_, _) =>
        {
            cancellation = new CancellationTokenSource();
            downloadButton.IsEnabled = false;
            closeButton.Content = "Cancel";
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;
            status.Text = "Downloading voice detection model…";

            var progress = new Progress<double>(value =>
            {
                if (value >= 1)
                {
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = 1;
                    status.Text = "Downloaded.";
                }
            });

            try
            {
                await ViewModel.DownloadVadModelAsync(progress, cancellation.Token);
                downloaded = true;
                dialog.Hide();
            }
            catch (OperationCanceledException)
            {
                status.Text = "Download canceled.";
            }
            catch (Exception exception)
            {
                status.Text = $"Download failed: {exception.Message}";
            }
            finally
            {
                cancellation.Dispose();
                cancellation = null;
                downloadButton.IsEnabled = true;
                closeButton.Content = "Cancel";
                progressBar.IsIndeterminate = false;
            }
        };

        await dialog.ShowAsync();
        cancellation?.Cancel();
        cancellation?.Dispose();
        return downloaded;
    }

    private async Task ShowDownloadModelDialogAsync(string preselectedModelId)
    {
        var modelPicker = new ComboBox
        {
            MinWidth = 380,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = "DisplayName",
            ItemsSource = ViewModel.ModelOptions,
            Style = GetStyle("SettingsComboBoxStyle")
        };

        modelPicker.SelectedItem =
            ViewModel.ModelOptions.FirstOrDefault(model => model.Id == preselectedModelId) ??
            ViewModel.ModelOptions.First();

        var details = new TextBlock
        {
            Style = GetStyle("SecondaryTextStyle"),
            TextWrapping = TextWrapping.Wrap
        };
        var status = new TextBlock
        {
            Style = GetStyle("SecondaryTextStyle"),
            TextWrapping = TextWrapping.Wrap
        };
        var progressBar = new ProgressBar
        {
            Maximum = 1,
            Value = 0,
            Visibility = Visibility.Collapsed
        };
        var downloadButton = new Button
        {
            Content = "Download",
            Style = GetStyle("PrimaryButtonStyle")
        };
        var closeButton = new Button
        {
            Content = "Close",
            Style = GetStyle("SecondaryButtonStyle")
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        actions.Children.Add(closeButton);
        actions.Children.Add(downloadButton);

        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 420
        };
        content.Children.Add(modelPicker);
        content.Children.Add(details);
        content.Children.Add(progressBar);
        content.Children.Add(status);
        content.Children.Add(actions);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Download Whisper model",
            Content = content
        };

        CancellationTokenSource? downloadCancellation = null;

        void RefreshSelectedModel()
        {
            if (modelPicker.SelectedItem is not RecognitionModelOption model)
            {
                return;
            }

            var installed = ViewModel.InstalledModels.FirstOrDefault(item => item.Id == model.Id);
            details.Text = installed is null
                ? $"Approximate download size: {model.ApproximateSize}"
                : $"Installed · {installed.SizeText}";
            downloadButton.IsEnabled = installed is null;
        }

        modelPicker.SelectionChanged += (_, _) =>
        {
            status.Text = string.Empty;
            RefreshSelectedModel();
        };

        closeButton.Click += (_, _) =>
        {
            if (downloadCancellation is null)
            {
                dialog.Hide();
            }
            else
            {
                downloadCancellation.Cancel();
            }
        };

        downloadButton.Click += async (_, _) =>
        {
            if (modelPicker.SelectedItem is not RecognitionModelOption model || ViewModel.IsModelDownloaded(model.Id))
            {
                RefreshSelectedModel();
                return;
            }

            downloadCancellation = new CancellationTokenSource();
            modelPicker.IsEnabled = false;
            downloadButton.IsEnabled = false;
            closeButton.Content = "Cancel";
            progressBar.Visibility = Visibility.Visible;
            progressBar.Value = 0;
            progressBar.IsIndeterminate = true;
            status.Text = $"Downloading {model.DisplayName}…";

            var progress = new Progress<ModelDownloadProgress>(update =>
            {
                if (update.TotalBytes is > 0)
                {
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = (double)update.BytesReceived / update.TotalBytes.Value;
                    status.Text =
                        $"{LocalModelInfo.FormatBytes(update.BytesReceived)} / {LocalModelInfo.FormatBytes(update.TotalBytes.Value)}";
                }
                else
                {
                    progressBar.IsIndeterminate = true;
                    status.Text = $"Downloading {model.DisplayName}…";
                }
            });

            try
            {
                await ViewModel.DownloadModelAsync(model.Id, progress, downloadCancellation.Token);
                dialog.Hide();
            }
            catch (OperationCanceledException)
            {
                status.Text = "Download canceled.";
            }
            catch (Exception exception)
            {
                status.Text = $"Download failed: {exception.Message}";
            }
            finally
            {
                downloadCancellation.Dispose();
                downloadCancellation = null;
                modelPicker.IsEnabled = true;
                closeButton.Content = "Close";
                progressBar.IsIndeterminate = false;
                RefreshSelectedModel();
            }
        };

        RefreshSelectedModel();
        await dialog.ShowAsync();
        downloadCancellation?.Cancel();
        downloadCancellation?.Dispose();
    }

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
        DropFeedback.Text = "Common audio and video formats";
        SetDropZoneActive(false);
    }

    private static Style GetStyle(string key) => (Style)Application.Current.Resources[key];

    private static Brush GetBrush(string key) => (Brush)Application.Current.Resources[key];
}
