using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperDrop.Dictation;

/// <summary>
/// Win32 tray, passive low-level keyboard hook, registered hotkey and non-activating
/// recording overlay. Owns one dedicated message-pump thread, independent of the XAML window.
/// </summary>
public sealed class WindowsDictationPlatform : IDisposable
{
    private const uint WmAppTray = 0x8001, WmAppConfigure = 0x8002, WmAppOverlay = 0x8003, WmAppNotification = 0x8004;
    private const uint WmHotkey = 0x0312, WmTimer = 0x0113, WmClose = 0x0010;
    private const uint WmPowerBroadcast = 0x0218, WmWtsSessionChange = 0x02B1;
    private const uint WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmKeyUp = 0x0101, WmSysKeyUp = 0x0105;
    private const int WhKeyboardLl = 13, HotkeyId = 10, TimerId = 10;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint MfString = 0, MfSeparator = 0x800, TpmReturnCmd = 0x100, TpmRightButton = 0x2;
    private const int SwHide = 0, SwShowNoActivate = 8;
    private const uint WsPopup = 0x80000000, WsBorder = 0x00800000, SsCenter = 1, SsCenterImage = 0x200;
    private const uint WsExTopmost = 8, WsExTransparent = 0x20, WsExToolWindow = 0x80,
        WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    private const uint LwaAlpha = 2, ModNoRepeat = 0x4000;
    private readonly object gate = new();
    private readonly Action<Action> dispatch;
    private readonly Action pressed, released, showWindow, toggleEnabled, exit;
    private readonly Action<string> report;
    private readonly Thread worker;
    private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WindowProc windowProc;
    private readonly KeyboardProc keyboardProc;
    private readonly string windowClassName = "ParakeetDrop.Dictation." + Guid.NewGuid().ToString("N");

    private IntPtr window, overlay, hook, icon, module;
    private bool ownsIcon, trayAdded, registered, held, enabled, overlayWanted, disposed;
    private HotkeySpec configured = HotkeySpec.Default;
    private HotkeySpec activeHotkey = HotkeySpec.Default;
    private TaskCompletionSource<bool>? configurationResult;
    private string? configurationError;
    private string? pendingNotification;

    public void ShowNotification(string message)
    {
        lock (gate)
        {
            if (disposed) return;
            pendingNotification = message;
            PostMessage(window, WmAppNotification, IntPtr.Zero, IntPtr.Zero);
        }
    }


    public WindowsDictationPlatform(Action<Action> dispatch, Action pressed, Action released,
        Action showWindow, Action toggleEnabled, Action exit, Action<string> report)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        this.dispatch = dispatch;
        this.pressed = pressed;
        this.released = released;
        this.showWindow = showWindow;
        this.toggleEnabled = toggleEnabled;
        this.exit = exit;
        this.report = report;
        windowProc = HandleWindowMessage;
        keyboardProc = HandleKey;
        worker = new Thread(MessageLoop) { IsBackground = true, Name = "ParakeetDrop Win32 dictation" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        started.Task.GetAwaiter().GetResult();
    }

    public string? RegistrationError => configurationError;

    public bool Configure(string hotkey, bool enable)
    {
        if (!HotkeySpec.TryParse(hotkey, out var parsed))
        {
            configurationError = "Invalid shortcut. Use e.g. Ctrl+Shift+Space or Ctrl+Alt+D.";
            return false;
        }

        TaskCompletionSource<bool> result;
        lock (gate)
        {
            if (disposed) return false;
            configured = parsed;
            enabled = enable;
            result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            configurationResult = result;
            if (!PostMessage(window, WmAppConfigure, IntPtr.Zero, IntPtr.Zero))
            {
                configurationError = "Could not update the background hotkey.";
                return false;
            }
        }
        return result.Task.GetAwaiter().GetResult();
    }

    public void SetRecordingOverlay(bool visible)
    {
        lock (gate)
        {
            if (disposed) return;
            overlayWanted = visible;
            PostMessage(window, WmAppOverlay, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void MessageLoop()
    {
        try
        {
            module = GetModuleHandle(null);
            var wc = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProc = windowProc,
                Instance = module,
                ClassName = windowClassName
            };
            if (RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException("Unable to register the dictation message window.");

            window = CreateWindowEx(0, windowClassName, "ParakeetDrop background",
                0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (window == IntPtr.Zero)
                throw new InvalidOperationException("Unable to start the background message loop.");

            overlay = CreateWindowEx(WsExTopmost | WsExToolWindow | WsExNoActivate | WsExTransparent | WsExLayered,
                "STATIC", "●  Recording…", WsPopup | WsBorder | SsCenter | SsCenterImage,
                Math.Max(0, GetSystemMetrics(0) - 230), 40, 210, 42,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (overlay == IntPtr.Zero || !SetLayeredWindowAttributes(overlay, 0, 232, LwaAlpha))
                throw new InvalidOperationException("Unable to create the recording indicator.");

            var iconPath = Path.Combine(AppContext.BaseDirectory, "ParakeetDrop.ico");
            if (File.Exists(iconPath))
            {
                icon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x10);
                ownsIcon = icon != IntPtr.Zero;
            }
            if (icon == IntPtr.Zero)
                icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
            trayAdded = NotifyIcon(NimAdd);
            if (!trayAdded)
                throw new InvalidOperationException("Unable to create the ParakeetDrop tray icon.");

            hook = SetWindowsHookEx(WhKeyboardLl, keyboardProc, module, 0);
            if (hook == IntPtr.Zero)
                throw new InvalidOperationException("Unable to install the global hotkey release monitor.");
            if (SetTimer(window, (IntPtr)TimerId, 250, IntPtr.Zero) == IntPtr.Zero)
                throw new InvalidOperationException("Unable to monitor missed hotkey releases.");
            WTSRegisterSessionNotification(window, 0);
            started.TrySetResult(true);

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            started.TrySetException(exception);
            dispatch(() => report(exception.Message));
        }
        finally
        {
            Release();
            if (registered) UnregisterHotKey(window, HotkeyId);
            if (window != IntPtr.Zero) { KillTimer(window, (IntPtr)TimerId); WTSUnRegisterSessionNotification(window); }
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            if (trayAdded) NotifyIcon(NimDelete);
            if (ownsIcon) DestroyIcon(icon);
            if (overlay != IntPtr.Zero) DestroyWindow(overlay);
            if (window != IntPtr.Zero) DestroyWindow(window);
            UnregisterClass(windowClassName, module);
        }
    }

    private IntPtr HandleWindowMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam)
    {
        try
        {
            switch (message)
            {
                case WmAppConfigure:
                    ApplyHotkeyConfiguration();
                    return IntPtr.Zero;
                case WmAppOverlay:
                    bool visible;
                    lock (gate) visible = overlayWanted;
                    ShowWindow(overlay, visible ? SwShowNoActivate : SwHide);
                    return IntPtr.Zero;
                case WmAppNotification:
                    string? messageText;
                    lock (gate) { messageText = pendingNotification; pendingNotification = null; }
                    if (trayAdded && !string.IsNullOrWhiteSpace(messageText))
                    {
                        var balloon = new NotifyIconData
                        {
                            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
                            Window = window,
                            Id = 1,
                            Flags = NifInfo,
                            Info = messageText.Length > 240 ? messageText[..240] : messageText,
                            InfoTitle = "ParakeetDrop",
                            Tip = "ParakeetDrop"
                        };
                        Shell_NotifyIcon(NimModify, ref balloon);
                    }
                    return IntPtr.Zero;
                case WmAppTray:
                    switch ((uint)lparam.ToInt64())
                    {
                        case 0x0203: dispatch(showWindow); break; // WM_LBUTTONDBLCLK
                        case 0x0405: dispatch(showWindow); break; // NIN_BALLOONUSERCLICK
                        case 0x0205: ShowTrayMenu(); break;       // WM_RBUTTONUP
                    }
                    return IntPtr.Zero;
                case WmTimer:
                    if (held && !IsHeld(activeHotkey)) Release();
                    return IntPtr.Zero;
                case WmWtsSessionChange:
                case WmPowerBroadcast:
                    Release();
                    return IntPtr.Zero;
                case WmClose:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                case WmHotkey:
                    return IntPtr.Zero; // Registration reserves the shortcut; hook tracks its lifecycle.
            }
        }
        catch (Exception exception)
        {
            dispatch(() => report(exception.Message));
        }

        return DefWindowProc(hwnd, message, wparam, lparam);
    }

    private void ApplyHotkeyConfiguration()
    {
        HotkeySpec requested;
        bool requestedEnabled;
        TaskCompletionSource<bool>? completion;
        lock (gate)
        {
            requested = configured;
            requestedEnabled = enabled;
            completion = configurationResult;
            configurationResult = null;
        }

        var previouslyEnabled = registered;
        var previous = activeHotkey;
        if (registered) UnregisterHotKey(window, HotkeyId);
        registered = false;
        Release();

        if (requestedEnabled && !RegisterHotKey(window, HotkeyId,
                requested.Modifiers | ModNoRepeat, requested.Key))
        {
            configurationError = "Shortcut unavailable or reserved by Windows. Choose another combination.";
            if (previouslyEnabled)
                registered = RegisterHotKey(window, HotkeyId, previous.Modifiers | ModNoRepeat, previous.Key);
            activeHotkey = previous;
            completion?.TrySetResult(false);
            dispatch(() => report(configurationError));
            return;
        }

        registered = requestedEnabled;
        activeHotkey = requested;
        configurationError = null;
        completion?.TrySetResult(true);
    }

    private IntPtr HandleKey(int code, IntPtr wparam, IntPtr lparam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KeyboardEvent>(lparam);
            var message = (uint)wparam.ToInt64();
            if (message is WmKeyDown or WmSysKeyDown)
            {
                if (registered && !held && info.Key == activeHotkey.Key && ModifiersHeld(activeHotkey))
                {
                    held = true; // Prevent OS-generated KeyDown repeat.
                    dispatch(pressed);
                }
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                if (held && (info.Key == activeHotkey.Key ||
                    ((activeHotkey.Modifiers & HotkeySpec.Ctrl) != 0 && info.Key is 0x11 or 0xA2 or 0xA3) ||
                    ((activeHotkey.Modifiers & HotkeySpec.Shift) != 0 && info.Key is 0x10 or 0xA0 or 0xA1) ||
                    ((activeHotkey.Modifiers & HotkeySpec.Alt) != 0 && info.Key is 0x12 or 0xA4 or 0xA5)))
                    Release();
            }
        }
        return CallNextHookEx(hook, code, wparam, lparam);
    }

    private static bool ModifiersHeld(HotkeySpec hotkey) =>
        ((hotkey.Modifiers & HotkeySpec.Ctrl) == 0 || Down(0x11)) &&
        ((hotkey.Modifiers & HotkeySpec.Shift) == 0 || Down(0x10)) &&
        ((hotkey.Modifiers & HotkeySpec.Alt) == 0 || Down(0x12));

    private static bool IsHeld(HotkeySpec hotkey) =>
        Down((int)hotkey.Key) &&
        ((hotkey.Modifiers & HotkeySpec.Ctrl) == 0 || Down(0x11)) &&
        ((hotkey.Modifiers & HotkeySpec.Shift) == 0 || Down(0x10)) &&
        ((hotkey.Modifiers & HotkeySpec.Alt) == 0 || Down(0x12));

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private void Release()
    {
        if (!held) return;
        held = false;
        dispatch(released);
    }

    private void ShowTrayMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MfString, 100, "Open ParakeetDrop");
            AppendMenu(menu, MfString, 101, enabled ? "Disable Dictation" : "Enable Dictation");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, 102, "Exit");
            GetCursorPos(out var point);
            SetForegroundWindow(window);
            var choice = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, point.X, point.Y,
                0, window, IntPtr.Zero);
            if (choice == 100) dispatch(showWindow);
            else if (choice == 101) dispatch(toggleEnabled);
            else if (choice == 102) dispatch(exit);
        }
        finally { DestroyMenu(menu); }
    }

    private bool NotifyIcon(uint operation)
    {
        var info = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window,
            Id = 1, Flags = NifMessage | NifIcon | NifTip, CallbackMessage = WmAppTray,
            Icon = icon, Tip = "ParakeetDrop"
        };
        return Shell_NotifyIcon(operation, ref info);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (window != IntPtr.Zero)
                PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
        if (Thread.CurrentThread != worker) worker.Join(TimeSpan.FromSeconds(2));
    }

    public readonly record struct HotkeySpec(uint Modifiers, uint Key)
    {
        public const uint Alt = 1, Ctrl = 2, Shift = 4;
        public static HotkeySpec Default => new(Ctrl | Shift, 0x20);

        public static bool TryParse(string? input, out HotkeySpec spec)
        {
            spec = default;
            if (string.IsNullOrWhiteSpace(input)) return false;
            var parts = input.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            uint modifiers = 0, key = 0;
            foreach (var part in parts)
            {
                switch (part.ToUpperInvariant())
                {
                    case "CTRL": case "CONTROL": modifiers |= Ctrl; break;
                    case "SHIFT": modifiers |= Shift; break;
                    case "ALT": modifiers |= Alt; break;
                    case "SPACE": if (key != 0) return false; key = 0x20; break;
                    default:
                        var token = part.ToUpperInvariant();
                        if (key != 0) return false;
                        if (token.Length == 1 && token[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
                            key = token[0];
                        else if (token.Length > 1 && token[0] == 'F' &&
                            int.TryParse(token[1..], out var f) && f is >= 1 and <= 12)
                            key = (uint)(0x70 + f - 1);
                        else return false;
                        break;
                }
            }
            if (modifiers == 0 || key == 0) return false;
            spec = new(modifiers, key);
            return true;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProc WindowProc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardEvent
    {
        public uint Key, ScanCode, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam, LParam;
        public uint Time;
        public Point Position;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message msg, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, KeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr resource);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
