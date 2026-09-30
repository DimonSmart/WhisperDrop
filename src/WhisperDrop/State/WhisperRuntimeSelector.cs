using System;
using System.Collections.Generic;
using Whisper.net;
using Whisper.net.LibraryLoader;
using WhisperDrop.Settings;

namespace WhisperDrop.State;

public sealed class RecognitionConfigurationException : InvalidOperationException
{
    public RecognitionConfigurationException(string message)
        : base(message)
    {
    }

    public RecognitionConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IWhisperRuntimeSelector
{
    bool IsInitialized { get; }

    ProcessingDevice ActiveDevice { get; }

    bool RequiresRestart(ProcessingDevice requestedDevice);

    WhisperFactoryOptions ConfigureBeforeFirstUse(ProcessingDevice requestedDevice);

    void MarkInitialized();
}

public sealed class WhisperRuntimeSelector : IWhisperRuntimeSelector
{
    private readonly object sync = new();
    private bool configured;
    private bool initialized;
    private ProcessingDevice configuredDevice = ProcessingDevice.Auto;

    public bool IsInitialized
    {
        get
        {
            lock (sync)
            {
                return initialized;
            }
        }
    }

    public ProcessingDevice ActiveDevice
    {
        get
        {
            lock (sync)
            {
                return configuredDevice;
            }
        }
    }

    public bool RequiresRestart(ProcessingDevice requestedDevice)
    {
        lock (sync)
        {
            return initialized && requestedDevice != configuredDevice;
        }
    }

    public WhisperFactoryOptions ConfigureBeforeFirstUse(ProcessingDevice requestedDevice)
    {
        lock (sync)
        {
            if (initialized)
            {
                return CreateFactoryOptions(configuredDevice);
            }

            if (!configured || requestedDevice != configuredDevice)
            {
                ConfigureRuntime(requestedDevice);
                configuredDevice = requestedDevice;
                configured = true;
            }

            return CreateFactoryOptions(configuredDevice);
        }
    }

    public void MarkInitialized()
    {
        lock (sync)
        {
            if (!configured)
            {
                throw new InvalidOperationException("Whisper runtime must be configured before it is marked initialized.");
            }

            initialized = true;
        }
    }

    private static void ConfigureRuntime(ProcessingDevice device)
    {
        switch (device)
        {
            case ProcessingDevice.Auto:
            case ProcessingDevice.Cpu:
                // WhisperDrop currently ships the portable Whisper.net CPU runtime.
                // Auto therefore delegates to the only backend included in this build.
                RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
                break;

            case ProcessingDevice.Gpu:
                throw new RecognitionConfigurationException(
                    "GPU acceleration is not available in this WhisperDrop build. Choose Auto or CPU.");

            default:
                throw new ArgumentOutOfRangeException(nameof(device), device, null);
        }
    }

    private static WhisperFactoryOptions CreateFactoryOptions(ProcessingDevice device)
    {
        var options = WhisperFactoryOptions.Default;
        options.UseGpu = device != ProcessingDevice.Cpu;
        return options;
    }
}
