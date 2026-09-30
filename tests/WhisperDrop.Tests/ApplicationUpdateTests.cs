using System.Net;
using WhisperDrop.Updates;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class ApplicationUpdateTests
{
    [Theory]
    [InlineData("0.1.10", 0, 1, 10)]
    [InlineData("v0.1.10", 0, 1, 10)]
    [InlineData("0.1.10+metadata", 0, 1, 10)]
    public void ReleaseVersionParser_accepts_supported_versions(
        string value,
        int major,
        int minor,
        int patch)
    {
        Assert.True(ReleaseVersionParser.TryParse(value, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-beta")]
    public void ReleaseVersionParser_rejects_malformed_versions(string value) =>
        Assert.False(ReleaseVersionParser.TryParse(value, out _));

    [Fact]
    public void ReleaseVersion_comparison_is_numeric() =>
        Assert.True(new ReleaseVersion(0, 1, 9) < new ReleaseVersion(0, 1, 10));

    [Fact]
    public async Task Newer_GitHub_release_is_reported_as_available()
    {
        string? userAgent = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(JsonResponse(
                """{"tag_name":"v0.1.11","html_url":"https://github.com/DimonSmart/WhisperDrop/releases/tag/v0.1.11"}"""));
        }));
        using var service = new GitHubUpdateCheckService(
            new StubVersionProvider(new ReleaseVersion(0, 1, 10)),
            httpClient,
            TimeSpan.FromSeconds(1));

        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(new ReleaseVersion(0, 1, 11), result.LatestVersion);
        Assert.Equal("WhisperDrop/0.1.10", userAgent);
        Assert.Equal(
            "https://github.com/DimonSmart/WhisperDrop/releases/tag/v0.1.11",
            result.ReleasePageUri?.AbsoluteUri);
    }

    [Fact]
    public async Task Equal_release_is_reported_as_up_to_date()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(
                """{"tag_name":"v0.1.10","html_url":"https://github.com/DimonSmart/WhisperDrop/releases/tag/v0.1.10"}"""))));
        using var service = new GitHubUpdateCheckService(
            new StubVersionProvider(new ReleaseVersion(0, 1, 10)),
            httpClient,
            TimeSpan.FromSeconds(1));

        Assert.Equal(UpdateCheckStatus.UpToDate, (await service.CheckAsync()).Status);
    }

    [Fact]
    public async Task Invalid_GitHub_response_is_unavailable()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        using var service = new GitHubUpdateCheckService(
            new StubVersionProvider(new ReleaseVersion(0, 1, 10)),
            httpClient,
            TimeSpan.FromSeconds(1));

        Assert.Equal(UpdateCheckStatus.Unavailable, (await service.CheckAsync()).Status);
    }

    [Fact]
    public async Task Mac_updater_is_available_only_for_standard_Homebrew_installation()
    {
        var environment = CreateMacEnvironment();
        var executor = new QueueProcessExecutor(Success());
        var installer = new MacOsHomebrewUpdateInstaller(executor, environment);

        var available = await installer.GetAvailabilityAsync();

        Assert.True(available.IsAvailable);
        Assert.Equal("/opt/homebrew/bin/brew", executor.Calls.Single().Executable);
        Assert.Equal(
            new[] { "list", "--cask", MacOsHomebrewUpdateInstaller.CaskName },
            executor.Calls.Single().Arguments);

        environment.ProcessPath = "/tmp/WhisperDrop/bin/Debug/WhisperDrop";
        Assert.False((await new MacOsHomebrewUpdateInstaller(
            new QueueProcessExecutor(),
            environment).GetAvailabilityAsync()).IsAvailable);
    }

    [Fact]
    public async Task Mac_updater_runs_targeted_upgrade_verification_quarantine_and_relaunch()
    {
        var executor = new QueueProcessExecutor(
            Success(),
            Success(),
            Success("""{"casks":[{"version":"0.1.11"}]}"""),
            Success(),
            Success("0.1.11\n"),
            Success(),
            Failure(),
            Success());
        var installer = new MacOsHomebrewUpdateInstaller(executor, CreateMacEnvironment());

        var result = await installer.InstallAsync(new ReleaseVersion(0, 1, 11));

        Assert.Equal(ApplicationUpdateResultStatus.Succeeded, result.Status);
        Assert.Equal(new[] { "update" }, executor.Calls[1].Arguments);
        Assert.Equal(
            new[] { "info", "--cask", "--json=v2", MacOsHomebrewUpdateInstaller.CaskName },
            executor.Calls[2].Arguments);
        Assert.Equal(
            new[]
            {
                "upgrade",
                "--cask",
                "--no-quit",
                "--appdir=/Applications",
                MacOsHomebrewUpdateInstaller.CaskName
            },
            executor.Calls[3].Arguments);
        Assert.False(executor.Calls[3].CanBeCanceled);
        Assert.Equal("/usr/bin/plutil", executor.Calls[4].Executable);
        Assert.Equal("/usr/bin/xattr", executor.Calls[5].Executable);
        Assert.Equal("/usr/bin/xattr", executor.Calls[6].Executable);
        Assert.Equal("/usr/bin/open", executor.Calls[7].Executable);
    }

    [Fact]
    public async Task Publication_lag_does_not_install_an_older_cask()
    {
        var executor = new QueueProcessExecutor(
            Success(),
            Success(),
            Success("""{"casks":[{"version":"0.1.10"}]}"""));
        var installer = new MacOsHomebrewUpdateInstaller(executor, CreateMacEnvironment());

        var result = await installer.InstallAsync(new ReleaseVersion(0, 1, 11));

        Assert.Equal(ApplicationUpdateResultStatus.PackageNotPublished, result.Status);
        Assert.DoesNotContain(executor.Calls, call => call.Arguments.Contains("upgrade"));
    }

    [Fact]
    public async Task Quarantine_failure_does_not_relaunch()
    {
        var executor = new QueueProcessExecutor(
            Success(),
            Success(),
            Success("""{"casks":[{"version":"0.1.11"}]}"""),
            Success(),
            Success("0.1.11"),
            Failure());
        var installer = new MacOsHomebrewUpdateInstaller(executor, CreateMacEnvironment());

        var result = await installer.InstallAsync(new ReleaseVersion(0, 1, 11));

        Assert.Equal(ApplicationUpdateResultStatus.QuarantineFailed, result.Status);
        Assert.DoesNotContain(executor.Calls, call => call.Executable == "/usr/bin/open");
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

    private static ProcessExecutionResult Success(string standardOutput = "") =>
        new(0, standardOutput, string.Empty);

    private static ProcessExecutionResult Failure(string standardError = "") =>
        new(1, string.Empty, standardError);

    private static StubUpdateEnvironment CreateMacEnvironment()
    {
        var environment = new StubUpdateEnvironment
        {
            IsMacOS = true,
            ProcessPath = "/Applications/WhisperDrop.app/Contents/MacOS/WhisperDrop"
        };
        environment.Files.Add("/opt/homebrew/bin/brew");
        environment.ExecutableFiles.Add("/opt/homebrew/bin/brew");
        return environment;
    }

    private sealed class StubVersionProvider : IApplicationVersionProvider
    {
        public StubVersionProvider(ReleaseVersion? version)
        {
            ReleaseVersion = version;
            DisplayVersion = version?.ToString() ?? "Unknown";
        }

        public string DisplayVersion { get; }

        public ReleaseVersion? ReleaseVersion { get; }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            this.handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class StubUpdateEnvironment : IApplicationUpdateEnvironment
    {
        public bool IsMacOS { get; set; }

        public string? ProcessPath { get; set; }

        public string? PathValue { get; set; }

        public char PathSeparator => ':';

        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ExecutableFiles { get; } = new(StringComparer.Ordinal);

        public bool FileExists(string path) => Files.Contains(path);

        public bool IsExecutableFile(string path) => ExecutableFiles.Contains(path);
    }

    private sealed class QueueProcessExecutor : IProcessExecutor
    {
        private readonly Queue<ProcessExecutionResult> results;

        public QueueProcessExecutor(params ProcessExecutionResult[] results) =>
            this.results = new Queue<ProcessExecutionResult>(results);

        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ProcessCall(
                executable,
                arguments.ToArray(),
                cancellationToken.CanBeCanceled));

            if (results.Count == 0)
                throw new InvalidOperationException("No fake process result was configured.");

            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed record ProcessCall(
        string Executable,
        string[] Arguments,
        bool CanBeCanceled);
}
