using WhisperDrop.Settings;
using Xunit;

namespace WhisperDrop.Tests;

public sealed class WindowClosePolicyTests
{
    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    public void Closing_hides_only_with_opt_in_and_available_tray_unless_explicit_exit(
        bool hideOnClose, bool trayAvailable, bool explicitExit, bool expected)
    {
        Assert.Equal(expected, WindowClosePolicy.ShouldHideOnClose(hideOnClose, trayAvailable, explicitExit));
    }
}
