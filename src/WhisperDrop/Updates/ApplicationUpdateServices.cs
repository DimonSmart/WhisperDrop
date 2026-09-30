using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperDrop.Updates;

public sealed class ApplicationVersionProvider : IApplicationVersionProvider
{
    public ApplicationVersionProvider(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (ReleaseVersionParser.TryParse(informationalVersion, out var releaseVersion))
        {
            ReleaseVersion = releaseVersion;
            DisplayVersion = releaseVersion.ToString();
            return;
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is { Major: >= 0, Minor: >= 0, Build: >= 0 })
        {
            ReleaseVersion = new ReleaseVersion(
                assemblyVersion.Major,
                assemblyVersion.Minor,
                assemblyVersion.Build);
            DisplayVersion = ReleaseVersion.Value.ToString();
            return;
        }

        DisplayVersion = string.IsNullOrWhiteSpace(informationalVersion)
            ? "Unknown"
            : informationalVersion;
    }

    public string DisplayVersion { get; }

    public ReleaseVersion? ReleaseVersion { get; }
}

public sealed class GitHubUpdateCheckService : IUpdateCheckService, IDisposable
{
    private static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/DimonSmart/WhisperDrop/releases/latest");

    private readonly IApplicationVersionProvider versionProvider;
    private readonly HttpClient httpClient;
    private readonly TimeSpan timeout;
    private readonly bool ownsHttpClient;

    public GitHubUpdateCheckService(IApplicationVersionProvider versionProvider)
        : this(versionProvider, new HttpClient(), TimeSpan.FromSeconds(5), ownsHttpClient: true)
    {
    }

    internal GitHubUpdateCheckService(
        IApplicationVersionProvider versionProvider,
        HttpClient httpClient,
        TimeSpan timeout)
        : this(versionProvider, httpClient, timeout, ownsHttpClient: false)
    {
    }

    private GitHubUpdateCheckService(
        IApplicationVersionProvider versionProvider,
        HttpClient httpClient,
        TimeSpan timeout,
        bool ownsHttpClient)
    {
        this.versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.timeout = timeout;
        this.ownsHttpClient = ownsHttpClient;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (versionProvider.ReleaseVersion is not { } currentVersion)
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCancellation.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
            request.Headers.UserAgent.ParseAdd($"WhisperDrop/{currentVersion}");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCancellation.Token);

            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateCheckStatus.Unavailable);

            await using var responseStream =
                await response.Content.ReadAsStreamAsync(linkedCancellation.Token);
            using var document = await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: linkedCancellation.Token);

            var root = document.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagElement)
                || !ReleaseVersionParser.TryParse(tagElement.GetString(), out var latestVersion)
                || !root.TryGetProperty("html_url", out var urlElement)
                || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var releasePageUri)
                || releasePageUri.Scheme is not ("https" or "http"))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Unavailable);
            }

            return latestVersion > currentVersion
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, latestVersion, releasePageUri)
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, latestVersion, releasePageUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable);
        }
    }

    public void Dispose()
    {
        if (ownsHttpClient)
            httpClient.Dispose();
    }
}

public sealed class ProcessExecutor : IProcessExecutor
{
    public async Task<ProcessExecutionResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("An executable path is required.", nameof(executable));

        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start process: {executable}");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken);
        return new ProcessExecutionResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }
}

public sealed class DefaultApplicationUpdateEnvironment : IApplicationUpdateEnvironment
{
    public bool IsMacOS => OperatingSystem.IsMacOS();

    public string? ProcessPath => Environment.ProcessPath;

    public string? PathValue => Environment.GetEnvironmentVariable("PATH");

    public char PathSeparator => Path.PathSeparator;

    public bool FileExists(string path) => File.Exists(path);

    public bool IsExecutableFile(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return false;
        }
    }
}

public sealed class UnsupportedApplicationUpdateInstaller : IApplicationUpdateInstaller
{
    public Task<ApplicationUpdateAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ApplicationUpdateAvailability(
            false,
            "Automatic updates are not supported for this installation."));
    }

    public Task<ApplicationUpdateResult> InstallAsync(
        ReleaseVersion expectedVersion,
        IProgress<ApplicationUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ApplicationUpdateResult(
            ApplicationUpdateResultStatus.Unsupported,
            "Automatic updates are not supported for this installation."));
    }
}

public sealed class MacOsHomebrewUpdateInstaller : IApplicationUpdateInstaller
{
    internal const string CaskName = "dimonsmart/whisperdrop/whisperdrop";
    internal const string ApplicationBundle = "/Applications/WhisperDrop.app";
    internal const string BundleExecutablePrefix = "/Applications/WhisperDrop.app/Contents/MacOS/";
    internal const string InfoPlist = "/Applications/WhisperDrop.app/Contents/Info.plist";

    private readonly IProcessExecutor processExecutor;
    private readonly IApplicationUpdateEnvironment environment;

    public MacOsHomebrewUpdateInstaller(
        IProcessExecutor processExecutor,
        IApplicationUpdateEnvironment environment)
    {
        this.processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public async Task<ApplicationUpdateAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!environment.IsMacOS)
            return Unavailable("Automatic installation is currently supported only on macOS.");

        var processPath = environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)
            || !processPath.StartsWith(BundleExecutablePrefix, StringComparison.Ordinal))
        {
            return Unavailable("This WhisperDrop instance is not running from /Applications/WhisperDrop.app.");
        }

        var brewPath = FindBrewPath();
        if (brewPath is null)
            return Unavailable("Homebrew was not found.");

        try
        {
            var installed = await processExecutor.RunAsync(
                brewPath,
                ["list", "--cask", CaskName],
                cancellationToken);

            return installed.ExitCode == 0
                ? new ApplicationUpdateAvailability(true)
                : Unavailable("WhisperDrop is not installed through the supported Homebrew Cask.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Unavailable("The Homebrew installation could not be verified.");
        }
    }

    public async Task<ApplicationUpdateResult> InstallAsync(
        ReleaseVersion expectedVersion,
        IProgress<ApplicationUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var availability = await GetAvailabilityAsync(cancellationToken);
            if (!availability.IsAvailable)
            {
                return new ApplicationUpdateResult(
                    ApplicationUpdateResultStatus.Unsupported,
                    availability.Reason ?? "Automatic update is unavailable.");
            }

            var brewPath = FindBrewPath();
            if (brewPath is null)
            {
                return new ApplicationUpdateResult(
                    ApplicationUpdateResultStatus.Unsupported,
                    "Homebrew was not found.");
            }

            progress?.Report(new ApplicationUpdateProgress(
                "Updating Homebrew metadata...",
                CanCancel: true));

            var brewUpdate = await processExecutor.RunAsync(
                brewPath,
                ["update"],
                cancellationToken);
            if (brewUpdate.ExitCode != 0)
                return Failed("Homebrew metadata could not be updated.");

            progress?.Report(new ApplicationUpdateProgress(
                "Checking the Homebrew package...",
                CanCancel: true));

            var brewInfo = await processExecutor.RunAsync(
                brewPath,
                ["info", "--cask", "--json=v2", CaskName],
                cancellationToken);
            if (brewInfo.ExitCode != 0)
                return Failed("The WhisperDrop Homebrew package could not be inspected.");

            if (!TryReadCaskVersion(brewInfo.StandardOutput, out var caskVersion))
                return Failed("The WhisperDrop Homebrew package reported an invalid version.");

            if (caskVersion < expectedVersion)
            {
                return new ApplicationUpdateResult(
                    ApplicationUpdateResultStatus.PackageNotPublished,
                    $"WhisperDrop {expectedVersion} has been released, but the Homebrew package is not available yet. Please try again shortly.");
            }

            progress?.Report(new ApplicationUpdateProgress(
                $"Installing WhisperDrop {expectedVersion}...",
                CanCancel: false));

            var upgrade = await processExecutor.RunAsync(
                brewPath,
                ["upgrade", "--cask", "--no-quit", "--appdir=/Applications", CaskName],
                CancellationToken.None);

            var upgradeReportedAlreadyCurrent =
                upgrade.StandardOutput.Contains("already up-to-date", StringComparison.OrdinalIgnoreCase)
                || upgrade.StandardError.Contains("already up-to-date", StringComparison.OrdinalIgnoreCase);
            if (upgrade.ExitCode != 0 && !upgradeReportedAlreadyCurrent)
                return Failed("Homebrew could not update WhisperDrop.");

            progress?.Report(new ApplicationUpdateProgress(
                "Verifying the installed version...",
                CanCancel: false));

            var installedVersion = await processExecutor.RunAsync(
                "/usr/bin/plutil",
                ["-extract", "CFBundleShortVersionString", "raw", "-o", "-", InfoPlist],
                CancellationToken.None);
            if (installedVersion.ExitCode != 0
                || !ReleaseVersionParser.TryParse(installedVersion.StandardOutput, out var verifiedVersion)
                || verifiedVersion < expectedVersion)
            {
                return Failed("The updated WhisperDrop version could not be verified.");
            }

            progress?.Report(new ApplicationUpdateProgress(
                "Removing macOS quarantine...",
                CanCancel: false));

            var quarantineRemoval = await processExecutor.RunAsync(
                "/usr/bin/xattr",
                ["-dr", "com.apple.quarantine", ApplicationBundle],
                CancellationToken.None);
            if (quarantineRemoval.ExitCode != 0)
                return QuarantineFailed();

            var quarantineCheck = await processExecutor.RunAsync(
                "/usr/bin/xattr",
                ["-p", "com.apple.quarantine", ApplicationBundle],
                CancellationToken.None);
            if (quarantineCheck.ExitCode == 0)
                return QuarantineFailed();

            progress?.Report(new ApplicationUpdateProgress(
                "Restarting WhisperDrop...",
                CanCancel: false));

            var relaunch = await processExecutor.RunAsync(
                "/usr/bin/open",
                ["-n", ApplicationBundle],
                CancellationToken.None);
            if (relaunch.ExitCode != 0)
                return Failed("WhisperDrop was updated, but the new application instance could not be started.");

            return new ApplicationUpdateResult(
                ApplicationUpdateResultStatus.Succeeded,
                $"WhisperDrop {verifiedVersion} was installed successfully.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failed("WhisperDrop could not be updated.");
        }
    }

    private string? FindBrewPath()
    {
        var pathValue = environment.PathValue;
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(
                         environment.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(directory, "brew");
                if (environment.IsExecutableFile(candidate))
                    return candidate;
            }
        }

        foreach (var candidate in new[] { "/opt/homebrew/bin/brew", "/usr/local/bin/brew" })
        {
            if (environment.FileExists(candidate) && environment.IsExecutableFile(candidate))
                return candidate;
        }

        return null;
    }

    private static bool TryReadCaskVersion(string json, out ReleaseVersion version)
    {
        version = default;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("casks", out var casks)
                || casks.ValueKind != JsonValueKind.Array
                || casks.GetArrayLength() == 0)
            {
                return false;
            }

            var firstCask = casks[0];
            return firstCask.TryGetProperty("version", out var versionElement)
                   && ReleaseVersionParser.TryParse(versionElement.GetString(), out version);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ApplicationUpdateAvailability Unavailable(string reason) =>
        new(false, reason);

    private static ApplicationUpdateResult Failed(string message) =>
        new(ApplicationUpdateResultStatus.Failed, message);

    private static ApplicationUpdateResult QuarantineFailed() =>
        new(
            ApplicationUpdateResultStatus.QuarantineFailed,
            "WhisperDrop was updated, but macOS quarantine could not be removed. The current instance will remain open. Run: /usr/bin/xattr -dr com.apple.quarantine /Applications/WhisperDrop.app");
}

public sealed class SystemUriLauncher : ISystemUriLauncher
{
    public Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();

        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http"))
            throw new ArgumentException("Only absolute HTTP(S) URLs can be opened.", nameof(uri));

        _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })
            ?? throw new InvalidOperationException("The system browser could not be started.");
        return Task.CompletedTask;
    }
}
