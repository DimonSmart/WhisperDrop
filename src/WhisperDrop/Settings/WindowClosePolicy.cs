namespace WhisperDrop.Settings;

public static class WindowClosePolicy
{
    // Explicit Exit always wins. If the tray is unavailable, do not leave an invisible process.
    public static bool ShouldHideOnClose(bool hideOnClose, bool trayAvailable, bool explicitExit) =>
        hideOnClose && trayAvailable && !explicitExit;
}
