using System;
using System.Runtime.InteropServices;

namespace WhisperDrop.Dictation;

public enum TextInsertionStatus
{
    NotRequested,
    Pending,
    Submitted,
    Inserted,
    Skipped,
    Failed
}

/// <summary>
/// A short-lived UI Automation identity. It is never serialized or retained in the history.
/// All methods must be called from the same UI/COM apartment.
/// </summary>
public sealed class WindowsTextInputTarget : IDisposable
{
    private static readonly Guid AutomationClsid = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid AutomationIid = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");

    private readonly IntPtr automation;
    private readonly IntPtr element;
    private readonly IntPtr foregroundWindow;
    private readonly uint processId;
    private bool disposed;

    private WindowsTextInputTarget(IntPtr automation, IntPtr element, IntPtr foregroundWindow, uint processId)
    {
        this.automation = automation;
        this.element = element;
        this.foregroundWindow = foregroundWindow;
        this.processId = processId;
    }

    public static WindowsTextInputTarget? Capture()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0)
            return null;

        IntPtr automation = IntPtr.Zero;
        IntPtr element = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref AutomationClsid, IntPtr.Zero, 1,
                ref AutomationIid, out automation));
            var getFocused = Method<GetFocusedElementDelegate>(automation, 8);
            Marshal.ThrowExceptionForHR(getFocused(automation, out element));
            if (element == IntPtr.Zero || !IsEditable(element, pid))
                return null;

            var target = new WindowsTextInputTarget(automation, element, window, pid);
            automation = IntPtr.Zero;
            element = IntPtr.Zero;
            return target;
        }
        catch (COMException)
        {
            return null; // Fail closed when a provider does not expose reliable accessibility.
        }
        finally
        {
            if (element != IntPtr.Zero) Marshal.Release(element);
            if (automation != IntPtr.Zero) Marshal.Release(automation);
        }
    }

    public TextInsertionStatus Insert(string? text)
    {
        if (disposed) throw new ObjectDisposedException(nameof(WindowsTextInputTarget));
        if (string.IsNullOrWhiteSpace(text))
            return TextInsertionStatus.Skipped;

        if (!IsWindow(foregroundWindow) || GetForegroundWindow() != foregroundWindow ||
            GetWindowThreadProcessId(foregroundWindow, out var currentPid) == 0 || currentPid != processId)
            return TextInsertionStatus.Skipped;

        IntPtr current = IntPtr.Zero;
        try
        {
            var focused = Method<GetFocusedElementDelegate>(automation, 8);
            Marshal.ThrowExceptionForHR(focused(automation, out current));
            if (current == IntPtr.Zero || !IsEditable(current, processId))
                return TextInsertionStatus.Skipped;

            var compare = Method<CompareElementsDelegate>(automation, 3);
            Marshal.ThrowExceptionForHR(compare(automation, element, current, out var same));
            if (same == 0)
                return TextInsertionStatus.Skipped;

            // Do not change the clipboard, activate another window, or repeat injection.
            // SendInput reports submitted events, not proof that an application accepted the text.
            return SubmitUnicode(text) ? TextInsertionStatus.Submitted : TextInsertionStatus.Failed;
        }
        catch (COMException)
        {
            return TextInsertionStatus.Skipped;
        }
        finally
        {
            if (current != IntPtr.Zero) Marshal.Release(current);
        }
    }

    private static bool IsEditable(IntPtr element, uint processId)
    {
        // UIA_EditControlTypeId and UIA_DocumentControlTypeId. A document may be an
        // editable editor surface; reject it unless it is actually keyboard-focused.
        var type = ReadInt(element, 21);
        return (type is 50004 or 50030) &&
               ReadInt(element, 20) == unchecked((int)processId) &&
               ReadInt(element, 26) != 0 && // HasKeyboardFocus
               ReadInt(element, 27) != 0 && // IsKeyboardFocusable
               ReadInt(element, 28) != 0 && // IsEnabled
               ReadInt(element, 35) == 0;   // IsPassword
    }

    private static int ReadInt(IntPtr element, int methodIndex)
    {
        var getter = Method<GetIntDelegate>(element, methodIndex);
        Marshal.ThrowExceptionForHR(getter(element, out var value));
        return value;
    }

    private static T Method<T>(IntPtr instance, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size));

    private static bool SubmitUnicode(string text)
    {
        // UTF-16 surrogate pairs are sent in order. CR/LF is converted to one Enter.
        var inputs = new System.Collections.Generic.List<Input>(text.Length * 2);
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                index++;
            if (ch is '\r' or '\n')
            {
                inputs.Add(Key(0x0D, 0, 0));
                inputs.Add(Key(0x0D, 0, 0x0002));
            }
            else
            {
                inputs.Add(Key(0, ch, 0x0004));
                inputs.Add(Key(0, ch, 0x0004 | 0x0002));
            }
        }

        if (inputs.Count == 0)
            return false;

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        return sent == inputs.Count;
    }

    private static Input Key(ushort key, char scan, uint flags) =>
        new() { Type = 1, Keyboard = new KeyboardInput { VirtualKey = key, Scan = scan, Flags = flags } };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Marshal.Release(element);
        Marshal.Release(automation);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFocusedElementDelegate(IntPtr self, out IntPtr element);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CompareElementsDelegate(IntPtr self, IntPtr first, IntPtr second, out int same);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIntDelegate(IntPtr self, out int value);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // Windows x64 INPUT: 8-byte header followed by a 32-byte union.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context,
        ref Guid iid, out IntPtr instance);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
