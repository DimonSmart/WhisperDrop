using System;
using WhisperDrop.Settings;
namespace WhisperDrop.State;

public sealed class RecognitionConfigurationException : InvalidOperationException
{
    public RecognitionConfigurationException(string message) : base(message) { }
    public RecognitionConfigurationException(string message, Exception inner) : base(message, inner) { }
}

public interface IRecognitionRuntimeSelector
{
    bool IsInitialized { get; }
    ProcessingDevice ActiveDevice { get; }
    string? Backend { get; }
    bool RequiresModelReload(ProcessingDevice requestedDevice);
    void MarkInitialized(ProcessingDevice requestedDevice, string backend);
}

public sealed class ParakeetRuntimeSelector : IRecognitionRuntimeSelector
{
    private ProcessingDevice requested;
    public bool IsInitialized { get; private set; }
    public ProcessingDevice ActiveDevice => Backend is "metal" or "vulkan" ? ProcessingDevice.Gpu : ProcessingDevice.Cpu;
    public string? Backend { get; private set; }
    public bool RequiresModelReload(ProcessingDevice requestedDevice) => IsInitialized && requested != requestedDevice;
    public void MarkInitialized(ProcessingDevice requestedDevice, string backend)
    {
        requested = requestedDevice;
        Backend = backend;
        IsInitialized = true;
    }
    internal static int[] BackendOrder(ProcessingDevice device)
    {
        var gpuBackend = OperatingSystem.IsWindows() ? 3 : 2;
        return device switch
        {
            ProcessingDevice.Cpu => [1],
            ProcessingDevice.Gpu => [gpuBackend],
            ProcessingDevice.Auto => [gpuBackend, 1],
            _ => throw new ArgumentOutOfRangeException(nameof(device))
        };
    }
}
