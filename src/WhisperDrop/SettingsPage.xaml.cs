using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.Storage.Pickers;
using WhisperDrop.Models;
using WhisperDrop.Settings;
using WhisperDrop.State;

namespace WhisperDrop;

public sealed partial class SettingsPage : Page
{
    private readonly Window owner;

    public SettingsPage(InitialApplicationState viewModel, Window owner)
    {
        ViewModel = viewModel;
        this.owner = owner;
        InitializeComponent();
        SelectSection(SettingsSection.Recognition);
    }

    public InitialApplicationState ViewModel { get; }

    private void Section_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string value } &&
            Enum.TryParse<SettingsSection>(value, out var section))
        {
            SelectSection(section);
        }
    }

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SettingsSearch.Text;
        RecognitionNav.Opacity = SettingsNavigationCatalog.Matches(SettingsSection.Recognition, query) ? 1 : 0.35;
        ModelsNav.Opacity = SettingsNavigationCatalog.Matches(SettingsSection.Models, query) ? 1 : 0.35;
        DictationNav.Opacity = SettingsNavigationCatalog.Matches(SettingsSection.Dictation, query) ? 1 : 0.35;
        AiNav.Opacity = SettingsNavigationCatalog.Matches(SettingsSection.Ai, query) ? 1 : 0.35;

        if (SettingsNavigationCatalog.FirstMatch(query) is { } section)
        {
            SelectSection(section);
        }
        else
        {
            // Preserve selection when query is empty/unmatched without dimming it.
            if (RecognitionNav.IsChecked == true) RecognitionNav.Opacity = 1;
            if (ModelsNav.IsChecked == true) ModelsNav.Opacity = 1;
            if (DictationNav.IsChecked == true) DictationNav.Opacity = 1;
            if (AiNav.IsChecked == true) AiNav.Opacity = 1;
        }
    }

    private void SelectSection(SettingsSection section)
    {
        SetNavigationVisual(RecognitionNav, section == SettingsSection.Recognition);
        SetNavigationVisual(ModelsNav, section == SettingsSection.Models);
        SetNavigationVisual(DictationNav, section == SettingsSection.Dictation);
        SetNavigationVisual(AiNav, section == SettingsSection.Ai);

        RecognitionSection.Visibility = section == SettingsSection.Recognition ? Visibility.Visible : Visibility.Collapsed;
        ModelsSection.Visibility = section == SettingsSection.Models ? Visibility.Visible : Visibility.Collapsed;
        DictationSection.Visibility = section == SettingsSection.Dictation ? Visibility.Visible : Visibility.Collapsed;
        AiSection.Visibility = section == SettingsSection.Ai ? Visibility.Visible : Visibility.Collapsed;
        SettingsContentScroll.ChangeView(null, 0, null);
    }

    private static void SetNavigationVisual(ToggleButton button, bool selected)
    {
        button.IsChecked = selected;
        button.Background = (Brush)Application.Current.Resources[
            selected ? "AccentSubtleBrush" : "AppBackgroundBrush"];
        button.BorderBrush = (Brush)Application.Current.Resources[
            selected ? "AccentBrush" : "AppBackgroundBrush"];
        button.Foreground = (Brush)Application.Current.Resources[
            selected ? "PrimaryTextBrush" : "SecondaryTextBrush"];
        // Search dims nonmatching categories, but must not obscure the current selection.
        if (selected) button.Opacity = 1;
    }

    private async void BrowseModelsFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        if (OperatingSystem.IsWindows())
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.ModelsFolder = folder.Path;
    }

    private async void DownloadSelectedModel_Click(object sender, RoutedEventArgs e) =>
        await ModelDownloadDialog.ShowAsync(this, ViewModel, ViewModel.SelectedModelId);

    private async void DownloadModel_Click(object sender, RoutedEventArgs e) =>
        await ModelDownloadDialog.ShowAsync(this, ViewModel, ViewModel.SelectedModelId);

    private async void TestAiConnection_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.TestAiConnectionAsync(); }
        catch (OperationCanceledException) { }
    }

    private async void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LocalModelInfo model }) return;
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = $"Delete {model.DisplayName}?",
            Content = $"Delete {model.SizeText} from the models folder?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        try { ViewModel.DeleteModel(model.Id); }
        catch (Exception error) { await ShowErrorDialogAsync("Could not delete model", error.Message); }
    }

    private async void DeleteAllModels_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasInstalledModels) return;
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = "Delete all downloaded models?",
            Content = $"This will remove {ViewModel.InstalledModelsSummary} from the models folder. Other files in that folder are left untouched.",
            PrimaryButtonText = "Delete all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        try { ViewModel.DeleteAllModels(); }
        catch (Exception error) { await ShowErrorDialogAsync("Could not delete models", error.Message); }
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = title,
            Content = message,
            CloseButtonText = "Close"
        }.ShowAsync();
    }
}
