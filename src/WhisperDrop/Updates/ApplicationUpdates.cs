using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperDrop.Updates;

public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;

        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
}

public static class ReleaseVersionParser
{
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var metadataIndex = normalized.IndexOf('+', StringComparison.Ordinal);
        if (metadataIndex >= 0)
            normalized = normalized[..metadataIndex];

        if (normalized.Contains('-'))
            return false;

        var parts = normalized.Split('.');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            || major < 0
            || minor < 0
            || patch < 0)
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch);
        return true;
    }
}

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Unavailable
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    ReleaseVersion? LatestVersion = null,
    Uri? ReleasePageUri = null);

public sealed record ApplicationUpdateAvailability(
    bool IsAvailable,
    string? Reason = null);

public sealed record ApplicationUpdateProgress(
    string Message,
    bool CanCancel);

public enum ApplicationUpdateResultStatus
{
    Succeeded,
    Unsupported,
    PackageNotPublished,
    QuarantineFailed,
    Failed
}

public sealed record ApplicationUpdateResult(
    ApplicationUpdateResultStatus Status,
    string Message);

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IApplicationVersionProvider
{
    string DisplayVersion { get; }

    ReleaseVersion? ReleaseVersion { get; }
}

public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

public interface IApplicationUpdateInstaller
{
    Task<ApplicationUpdateAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default);

    Task<ApplicationUpdateResult> InstallAsync(
        ReleaseVersion expectedVersion,
        IProgress<ApplicationUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IProcessExecutor
{
    Task<ProcessExecutionResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public interface IApplicationUpdateEnvironment
{
    bool IsMacOS { get; }

    string? ProcessPath { get; }

    string? PathValue { get; }

    char PathSeparator { get; }

    bool FileExists(string path);

    bool IsExecutableFile(string path);
}

public interface ISystemUriLauncher
{
    Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default);
}
