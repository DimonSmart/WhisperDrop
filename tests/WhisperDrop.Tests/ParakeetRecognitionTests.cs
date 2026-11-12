using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WhisperDrop.Models;
using WhisperDrop.Settings;
using WhisperDrop.State;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class ParakeetRecognitionTests
{
    [Theory]
    [InlineData(0, false, false, true)]
    [InlineData(0, false, true, false)]
    [InlineData(18, false, false, false)]
    [InlineData(19, false, false, false)]
    public void Only_successful_complete_native_output_is_complete(int status, bool aborted, bool truncated, bool complete) =>
        Assert.Equal(complete, ParakeetRuntime.CheckRunStatus(status, aborted, truncated, default));
    [Theory]
    [InlineData(13, false)]
    [InlineData(0, true)]
    public void Native_abort_is_cancellation(int status, bool aborted) =>
        Assert.Throws<OperationCanceledException>(() => ParakeetRuntime.CheckRunStatus(status, aborted, false, default));
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(14)]
    public void Non_success_never_becomes_a_transcript(int status) =>
        Assert.Throws<InvalidOperationException>(() => ParakeetRuntime.CheckRunStatus(status, false, false, default));
    [Fact]
    public void Backend_failure_has_CPU_guidance() => Assert.Contains("CPU",
        Assert.Throws<RecognitionConfigurationException>(() => ParakeetRuntime.CheckRunStatus(8, false, false, default)).Message);
    [Fact]
    public void Device_selection_prefers_platform_GPU_then_CPU_and_forced_CPU_never_asks_for_GPU()
    {
        var gpuBackend = OperatingSystem.IsWindows() ? 3 : 2;
        Assert.Equal([gpuBackend, 1], ParakeetRuntimeSelector.BackendOrder(ProcessingDevice.Auto));
        Assert.Equal([1], ParakeetRuntimeSelector.BackendOrder(ProcessingDevice.Cpu));
        Assert.Equal([gpuBackend], ParakeetRuntimeSelector.BackendOrder(ProcessingDevice.Gpu));
        var selector = new ParakeetRuntimeSelector();
        selector.MarkInitialized(ProcessingDevice.Auto, "cpu");
        Assert.Equal(ProcessingDevice.Cpu, selector.ActiveDevice);
        Assert.False(selector.RequiresModelReload(ProcessingDevice.Auto));
        Assert.True(selector.RequiresModelReload(ProcessingDevice.Gpu));
        selector.MarkInitialized(ProcessingDevice.Gpu, OperatingSystem.IsWindows() ? "vulkan" : "metal");
        Assert.Equal(ProcessingDevice.Gpu, selector.ActiveDevice);
    }
    [Fact]
    public void Auto_falls_back_when_platform_GPU_is_unavailable_and_GPU_reports_guidance()
    {
        var gpuBackend = OperatingSystem.IsWindows() ? 3 : 2;
        var attempts = new List<int>();
        var model = ParakeetRuntime.SelectBackend(ProcessingDevice.Auto, backend =>
        {
            attempts.Add(backend);
            return backend == gpuBackend ? (8, null) : (0, new StubModel());
        });
        Assert.Equal([gpuBackend, 1], attempts);
        Assert.Equal("cpu", model.Backend);
        attempts.Clear();
        var error = Assert.Throws<RecognitionConfigurationException>(() => ParakeetRuntime.SelectBackend(ProcessingDevice.Gpu, backend =>
        {
            attempts.Add(backend); return (8, null);
        }));
        Assert.Equal([gpuBackend], attempts);
        Assert.Contains("Auto or CPU", error.Message);
        attempts.Clear();
        Assert.Throws<RecognitionConfigurationException>(() => ParakeetRuntime.SelectBackend(ProcessingDevice.Auto, backend =>
        {
            attempts.Add(backend); return (4, null);
        }));
        Assert.Equal([gpuBackend], attempts); // An invalid artifact is not a backend-availability failure.
    }
    private sealed class StubModel : IParakeetModel
    {
        public string Backend => "cpu";
        public IParakeetSession CreateSession(int? threads) => throw new NotSupportedException();
        public void Dispose() { }
    }
    [Fact]
    public void Managed_ABI_matches_pinned_header_layout_on_supported_architectures()
    {
        Assert.Equal(32, Marshal.SizeOf<ParakeetNative.LoadParams>());
        Assert.Equal(24, Marshal.SizeOf<ParakeetNative.SessionParams>());
        Assert.Equal(104, Marshal.SizeOf<ParakeetNative.RunParams>());
        Assert.Equal(56, Marshal.SizeOf<ParakeetNative.Segment>());
        Assert.Equal(24, Marshal.SizeOf<ParakeetNative.BackendParams>());
        Assert.Equal(64, Marshal.SizeOf<ParakeetNative.DeviceInfo>());
        Assert.Equal(24, Marshal.OffsetOf<ParakeetNative.LoadParams>("NoMmap").ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<ParakeetNative.RunParams>("Family").ToInt32());
    }
    [Fact]
    public async Task Recognition_is_serialized_model_is_reused_and_sessions_are_freed_before_reload()
    {
        var runtime = new FakeRuntime();
        using var service = CreateService(runtime);
        var first = service.TranscribeAsync("model", new(), Fixture);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.TranscribeAsync("model", new(), Fixture);
        Assert.Equal(1, runtime.SessionCount);
        runtime.Release.Set();
        await Task.WhenAll(first, second);
        Assert.Equal(1, runtime.LoadCount);
        Assert.Equal(2, runtime.SessionCount);
        var third = await service.TranscribeAsync("model", new() { ProcessingDevice = ProcessingDevice.Cpu }, Fixture);
        Assert.Equal("cpu", third.Backend);
        Assert.Equal(2, runtime.LoadCount);
        service.UnloadModel();
        Assert.Equal(0, runtime.ActiveSessions);
        Assert.Equal(2, runtime.ModelDisposals);
    }
    [Fact]
    public async Task Unload_waits_for_native_return_and_callback_token_remains_usable_after_GC()
    {
        var runtime = new FakeRuntime();
        using var service = CreateService(runtime);
        using var cancellation = new CancellationTokenSource();
        var call = service.TranscribeAsync("model", new(), Fixture, cancellationToken: cancellation.Token);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var unloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unload = Task.Run(() => { unloadStarted.SetResult(); service.UnloadModel(); });
        await unloadStarted.Task;
        Assert.Equal(0, runtime.ModelDisposals);
        GC.Collect(); GC.WaitForPendingFinalizers();
        cancellation.Cancel();
        runtime.Release.Set();
        await Assert.ThrowsAsync<OperationCanceledException>(() => call);
        await unload;
        Assert.Equal(0, runtime.ActiveSessions);
        Assert.Equal(1, runtime.ModelDisposals);
        service.Dispose();
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TranscribeAsync("model", new(), Fixture));
    }
    [Fact]
    public async Task Invalid_model_is_rejected_before_loading_native_code()
    {
        var runtime = new FakeRuntime();
        using var service = new ParakeetRecognitionService(new ParakeetRuntimeSelector(), runtime, (_, _) => Task.FromResult(false));
        await Assert.ThrowsAsync<RecognitionConfigurationException>(() => service.TranscribeAsync("model", new(), Fixture));
        Assert.Equal(0, runtime.LoadCount);
    }
    [Fact]
    public async Task Session_is_freed_after_native_failure_and_model_can_be_reused()
    {
        var runtime = new FakeRuntime { Fail = true }; runtime.Release.Set();
        using var service = CreateService(runtime);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.TranscribeAsync("model", new(), Fixture));
        Assert.Equal(0, runtime.ActiveSessions);
        runtime.Fail = false;
        var result = await service.TranscribeAsync("model", new(), Fixture);
        Assert.Equal("ok", result.Transcript);
        Assert.Equal(1, runtime.LoadCount);
    }
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "test.wav");
    private static ParakeetRecognitionService CreateService(FakeRuntime runtime) =>
        new(new ParakeetRuntimeSelector(), runtime, (_, _) => Task.FromResult(true));
    private sealed class FakeRuntime : IParakeetRuntime
    {
        public int LoadCount, SessionCount, ActiveSessions, ModelDisposals;
        public bool Fail;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release = new();
        public IParakeetModel Load(string path, ProcessingDevice device) { LoadCount++; return new Model(this); }
        private sealed class Model(FakeRuntime owner) : IParakeetModel
        {
            public string Backend => "cpu";
            public IParakeetSession CreateSession(int? threads) { owner.SessionCount++; owner.ActiveSessions++; return new Session(owner); }
            public void Dispose() { Assert.Equal(0, owner.ActiveSessions); owner.ModelDisposals++; }
        }
        private sealed class Session(FakeRuntime owner) : IParakeetSession
        {
            public RecognitionResult Run(float[] samples, CancellationToken token)
            {
                owner.Started.TrySetResult();
                Assert.True(owner.Release.Wait(TimeSpan.FromSeconds(10)));
                token.ThrowIfCancellationRequested();
                if (owner.Fail) throw new InvalidDataException("native error");
                return new("ok", null);
            }
            public void Dispose() => owner.ActiveSessions--;
        }
    }
}
