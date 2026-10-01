using System;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using WhisperDrop.Models;

namespace WhisperDrop.UI;

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        GetBrush((value as string) switch
        {
            "Preparing" or "Transcribing" or "Improving" => "AccentBrush",
            "Completed" => "SuccessBrush",
            "Error" or "AI failed" => "ErrorBrush",
            _ => "MutedTextBrush"
        });

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static Brush GetBrush(string key) => (Brush)Application.Current.Resources[key];
}

public sealed class StatusToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (value as string) switch
        {
            "Preparing" or "Transcribing" or "Improving" => "\uE895",
            "Completed" => "\uE8FB",
            "Error" or "AI failed" => "\uE783",
            _ => "\uE917"
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class StatusEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var status = value as string;
        var expected = (parameter as string)?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return expected.Any(item => string.Equals(status, item, StringComparison.Ordinal))
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class SelectedModelDownloadStateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not SelectedModelDownloadState state)
        {
            return Visibility.Collapsed;
        }

        return (parameter as string) switch
        {
            "DownloadAction" when state is SelectedModelDownloadState.NotDownloaded or SelectedModelDownloadState.Error => Visibility.Visible,
            "DetailedStatus" when state is SelectedModelDownloadState.Downloading or SelectedModelDownloadState.Error => Visibility.Visible,
            _ => Visibility.Collapsed
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class ProgressTextToDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0d;
        }

        return double.TryParse(text.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var progress)
            ? Math.Clamp(progress, 0d, 100d)
            : 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        IsMatch(value, parameter) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static bool IsMatch(object value, object parameter) =>
        value is int index &&
        int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected) &&
        index == expected;
}

public sealed class IndexEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int index &&
        int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected) &&
        index == expected;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class QueueCountToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        $"Files ({(value is int count ? count : 0)})";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
