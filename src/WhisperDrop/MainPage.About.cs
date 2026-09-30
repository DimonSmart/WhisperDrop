using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WhisperDrop.Updates;

namespace WhisperDrop;

public sealed partial class MainPage
{
    private readonly IApplicationVersionProvider applicationVersionProvider = null!;
    private readonly IUpdateCheckService updateCheckService = null!;
    private readonly IApplicationUpdateInstaller applicationUpdateInstaller = null!;
    private readonly ISystemUriLauncher systemUriLauncher = null!;
    private int applicationUpdateInProgress;

    private async void About_Click(object sender, RoutedEventArgs e) =>
        await ShowAboutAsync();

    private async Task ShowAboutAsync()
    {
        using var cancellation = new CancellationTokenSource();

        var statusText = new TextBlock
        {
            Text = "Checking for updates...",
            TextWrapping = TextWrapping.Wrap
        };
        var updateButton = new Button
        {
            Content = "Update now",
            Style = GetStyle("PrimaryButtonStyle"),
            Visibility = Visibility.Collapsed
        };
        var releaseButton = new Button
        {
            Content = "Open release page",
            Style = GetStyle("SecondaryButtonStyle"),
            Visibility = Visibility.Collapsed
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        actions.Children.Add(updateButton);
        actions.Children.Add(releaseButton);

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "WhisperDrop",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Version: {applicationVersionProvider.DisplayVersion}"
        });
        content.Children.Add(statusText);
        content.Children.Add(actions);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "About WhisperDrop",
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        Uri? releasePageUri = null;
        ReleaseVersion? availableVersion = null;

        releaseButton.Click += async (_, _) =>
        {
            if (releasePageUri is null) return;

            try
            {
                await systemUriLauncher.OpenUriAsync(releasePageUri);
            }
            catch
            {
                statusText.Text = "Unable to open release page.";
            }
        };

        updateButton.Click += async (_, _) =>
        {
            if (availableVersion is not { } version) return;

            dialog.Hide();
            await Task.Delay(20);
            await RunApplicationUpdateAsync(version);
        };

        async void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            try
            {
                var result = await updateCheckService.CheckAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();

                switch (result.Status)
                {
                    case UpdateCheckStatus.UpToDate:
                        statusText.Text = result.LatestVersion is { } upToDateVersion
                            ? $"You are up to date.\nLatest version: {upToDateVersion}"
                            : "You are up to date.";
                        releasePageUri = result.ReleasePageUri;
                        break;

                    case UpdateCheckStatus.UpdateAvailable when result.LatestVersion is { } latest:
                        availableVersion = latest;
                        releasePageUri = result.ReleasePageUri;
                        statusText.Text = $"New version {latest} is available.";
                        releaseButton.Visibility = releasePageUri is null
                            ? Visibility.Collapsed
                            : Visibility.Visible;

                        var availability = await applicationUpdateInstaller.GetAvailabilityAsync(
                            cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        updateButton.Visibility = availability.IsAvailable
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                        break;

                    default:
                        statusText.Text = "Unable to check for updates.";
                        break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                statusText.Text = "Unable to check for updates.";
            }
        }

        dialog.Opened += Dialog_Opened;
        dialog.Closed += (_, _) => cancellation.Cancel();
        await dialog.ShowAsync();
    }

    private async Task RunApplicationUpdateAsync(ReleaseVersion expectedVersion)
    {
        if (Interlocked.CompareExchange(ref applicationUpdateInProgress, 1, 0) != 0)
            return;

        try
        {
            using var cancellation = new CancellationTokenSource();
            var statusText = new TextBlock
            {
                Text = "Updating Homebrew metadata...",
                TextWrapping = TextWrapping.Wrap
            };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new ProgressRing
            {
                IsActive = true,
                Width = 24,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Left
            });
            content.Children.Add(statusText);

            var canCancel = true;
            var dialogOpen = true;
            var progressDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Updating WhisperDrop",
                Content = content,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            progressDialog.CloseButtonClick += (_, args) =>
            {
                if (!canCancel)
                {
                    args.Cancel = true;
                    return;
                }

                cancellation.Cancel();
            };
            progressDialog.Closed += (_, _) => dialogOpen = false;

            var progress = new Progress<ApplicationUpdateProgress>(value =>
            {
                statusText.Text = value.Message;
                canCancel = value.CanCancel;
                progressDialog.CloseButtonText = value.CanCancel ? "Cancel" : string.Empty;
            });

            var dialogOperation = progressDialog.ShowAsync();
            ApplicationUpdateResult? result = null;
            var wasCancelled = false;
            try
            {
                result = await applicationUpdateInstaller.InstallAsync(
                    expectedVersion,
                    progress,
                    cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                wasCancelled = true;
            }
            finally
            {
                if (dialogOpen)
                    progressDialog.Hide();

                await dialogOperation;
            }

            if (wasCancelled || result is null)
                return;

            if (result.Status == ApplicationUpdateResultStatus.Succeeded)
            {
                Interlocked.Exchange(ref applicationUpdateInProgress, 0);
                App.CompleteApplicationUpdateRestart(this);
                return;
            }

            await ShowUpdateMessageAsync(
                result.Status == ApplicationUpdateResultStatus.PackageNotPublished
                    ? "Update not available in Homebrew yet"
                    : "Unable to update WhisperDrop",
                result.Message);
        }
        finally
        {
            Interlocked.Exchange(ref applicationUpdateInProgress, 0);
        }
    }

    private async Task ShowUpdateMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }
}
