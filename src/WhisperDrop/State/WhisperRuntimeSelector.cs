using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
                RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
                break;

            case ProcessingDevice.Gpu:
                if (!OperatingSystem.IsMacOS() ||
                    RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                {
                    throw new RecognitionConfigurationException(
                        "GPU acceleration is not available on this system. Choose Auto or CPU.");
                }

                // The supported macOS ARM64 package contains the Metal backend.
                // UseGpu controls whether the factory may use it.
                RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
                break;

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
