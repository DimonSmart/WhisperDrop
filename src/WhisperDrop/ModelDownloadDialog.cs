using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhisperDrop.Models;
using WhisperDrop.State;

namespace WhisperDrop;

internal static class ModelDownloadDialog
{
    public static async Task ShowAsync(FrameworkElement owner, InitialApplicationState ViewModel, string preselectedModelId)
    {
        var modelPicker = new ComboBox
        {
            MinWidth = 380,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = "DownloadDisplayName",
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
            XamlRoot = owner.XamlRoot,
            Title = "Download Parakeet Ultra model",
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
                ? $"Exact download size: {model.ApproximateSize}"
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

    private static Style GetStyle(string key) => (Style)Application.Current.Resources[key];
}
