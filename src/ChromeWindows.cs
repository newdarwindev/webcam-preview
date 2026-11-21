using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

internal sealed class ChromeTab
{
    public readonly IntPtr Window;
    public readonly SelectionItemPattern Selection;

    public ChromeTab(IntPtr window, SelectionItemPattern selection)
    {
        Window = window;
        Selection = selection;
    }
}

internal static class ChromeWindows
{
    public static List<ChromeTab> GetTabs()
    {
        var handles = new List<IntPtr>();
        EnumWindows(delegate(IntPtr window, IntPtr unused)
        {
            if (IsBrowserWindow(window))
                handles.Add(window);
            return true;
        }, IntPtr.Zero);
        handles.Sort((first, second) => first.ToInt64().CompareTo(second.ToInt64()));

        var tabs = new List<ChromeTab>();
        var stripCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab),
            new PropertyCondition(AutomationElement.ClassNameProperty, "TabContainerImpl"));
        var tabCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
        foreach (IntPtr window in handles)
        {
            try
            {
                var root = AutomationElement.FromHandle(window);
                var strip = root.FindFirst(TreeScope.Descendants, stripCondition);
                if (strip == null)
                {
                    if (IsFullscreen(window))
                        tabs.Add(new ChromeTab(window, null));
                    continue;
                }
                foreach (AutomationElement item in strip.FindAll(TreeScope.Descendants, tabCondition))
                {
                    object pattern;
                    if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                        tabs.Add(new ChromeTab(window, (SelectionItemPattern)pattern));
                }
            }
            catch (ElementNotAvailableException)
            {
                // Chrome can close a window while its tabs are being read.
            }
        }
        return tabs;
    }

    public static bool IsBrowserWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero)
            return false;
        var name = new StringBuilder(64);
        if (GetClassName(window, name, name.Capacity) == 0 || name.ToString() != "Chrome_WidgetWin_1")
            return false;
        uint processId;
        GetWindowThreadProcessId(window, out processId);
        try
        {
            using (var process = Process.GetProcessById((int)processId))
                return string.Equals(process.ProcessName, "chrome", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsFullscreen(IntPtr window)
    {
        NativeRect rectangle;
        return GetWindowRect(window, out rectangle) &&
            Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom) ==
            Screen.FromHandle(window).Bounds;
    }

    public static bool SetFullscreen(IntPtr window, bool fullscreen)
    {
        if (!IsBrowserWindow(window))
            return false;
        if (IsFullscreen(window) == fullscreen)
            return true;

        Focus(window);
        if (fullscreen && EnterFullscreenFromMenu(window))
            return WaitForFullscreen(window, true);

        var activation = Stopwatch.StartNew();
        while (activation.ElapsedMilliseconds < 300 && GetForegroundWindow() != window)
            Thread.Sleep(15);
        if (GetForegroundWindow() != window)
            return false;

        const ushort f11 = 0x7A;
        const uint keyboard = 1;
        const uint keyUp = 2;
        var strokes = new[] {
            new Input { Type = keyboard, Data = new InputData { Keyboard = new KeyInput { Key = f11 } } },
            new Input { Type = keyboard, Data = new InputData { Keyboard = new KeyInput { Key = f11, Flags = keyUp } } }
        };
        if (SendInput((uint)strokes.Length, strokes, Marshal.SizeOf(typeof(Input))) != strokes.Length)
            return false;
        return WaitForFullscreen(window, fullscreen);
    }

    private static bool EnterFullscreenFromMenu(IntPtr window)
    {
        ExpandCollapsePattern menu = null;
        bool invoked = false;
        try
        {
            var root = AutomationElement.FromHandle(window);
            var button = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, "BrowserAppMenuButton"));
            object pattern;
            if (button == null || !button.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                return false;
            menu = (ExpandCollapsePattern)pattern;
            menu.Expand();
            var condition = new PropertyCondition(AutomationElement.ClassNameProperty, "FullscreenButton");
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < 450)
            {
                var fullscreen = root.FindFirst(TreeScope.Descendants, condition);
                if (fullscreen != null && fullscreen.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    invoked = true;
                    return true;
                }
                Thread.Sleep(20);
            }
            return false;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (menu != null && !invoked)
            {
                try
                {
                    menu.Collapse();
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (COMException) { }
            }
        }
    }

    private static bool WaitForFullscreen(IntPtr window, bool fullscreen)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 850 && IsFullscreen(window) != fullscreen)
            Thread.Sleep(20);
        return IsFullscreen(window) == fullscreen;
    }

    public static void Focus(IntPtr window)
    {
        if (IsIconic(window))
            ShowWindowAsync(window, 9);
        if (SetForegroundWindow(window) || GetForegroundWindow() == window)
            return;

        uint unused;
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out unused);
        uint currentThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread ||
            !AttachThreadInput(currentThread, foregroundThread, true))
            return;
        try
        {
            SetForegroundWindow(window);
        }
        finally
        {
            AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public KeyInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public ushort Key, ScanCode;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint source, uint destination, bool attach);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
