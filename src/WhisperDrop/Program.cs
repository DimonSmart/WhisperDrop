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
            builder.UseWin32();
        }
        else if (OperatingSystem.IsMacOS())
        {
            builder.UseMacOS();
        }
        else
        {
            throw new PlatformNotSupportedException("WhisperDrop supports Windows and macOS.");
        }

        var host = builder.App(() => new App()).Build();

        host.RunAsync().GetAwaiter().GetResult();
    }
}
