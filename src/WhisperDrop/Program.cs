using System;
using Uno.UI.Hosting;

namespace WhisperDrop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var builder = UnoPlatformHostBuilder.Create();

        if (OperatingSystem.IsWindows())
        {
            builder.UseWin32(options => options.RenderingBackend(Win32RenderingBackend.Vulkan));
        }
        else if (OperatingSystem.IsMacOS())
        {
            builder.UseMacOS();
        }
        else
        {
            throw new PlatformNotSupportedException("ParakeetDrop currently supports macOS on Apple Silicon. Windows is planned for a later release.");
        }

        var host = builder.App(() => new App()).Build();

        host.RunAsync().GetAwaiter().GetResult();
    }
}
