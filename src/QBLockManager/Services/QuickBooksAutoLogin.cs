using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace QBLockManager.Services;

public static class QuickBooksAutoLogin
{
    // Tracks dialog HWNDs that were successfully handled — prevents re-attempting
    // the same login dialog after a successful submit. Only added on success so
    // failed attempts (timing issues, focus lost) are retried naturally.
    private static readonly HashSet<IntPtr> _succeeded = new();
    private static readonly object _lock = new();

    public static void BeginAutoLogin(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return;
        Task.Run(() => RunWatcher(password));
    }

    public static void ResetAttempts()
    {
        lock (_lock) _succeeded.Clear();
    }

    // ── Background watcher ────────────────────────────────────────────────────

    private static void RunWatcher(string password)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            try { if (TryLoginAllWindows(password)) return; }
            catch { }
            Thread.Sleep(500);
        }
    }

    private static bool TryLoginAllWindows(string password)
    {
        var procs = Process.GetProcessesByName("QBW32")
            .Concat(Process.GetProcessesByName("QBW"))
            .ToArray();

        if (procs.Length == 0) return false;

        foreach (var proc in procs)
        {
            try
            {
                var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, proc.Id));

                foreach (AutomationElement win in windows)
                {
                    try { if (TryLoginInWindow(win, password)) return true; }
                    catch { }
                }
            }
            catch { }
        }
        return false;
    }

    private static bool TryLoginInWindow(AutomationElement window, string password)
    {
        // Find password edit control
        AutomationElementCollection edits;
        try
        {
            edits = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        }
        catch { return false; }

        AutomationElement? target = null;
        foreach (AutomationElement e in edits)
        {
            try { if (e.Current.IsPassword && target == null) target = e; }
            catch { }
        }
        if (target == null && edits.Count == 1) target = edits[0];
        if (target == null) return false;

        IntPtr editHwnd = IntPtr.Zero;
        try { editHwnd = new IntPtr(target.Current.NativeWindowHandle); }
        catch { }

        // dialogHwnd = the MauiForm login dialog (parent of the edit control)
        IntPtr dialogHwnd = editHwnd != IntPtr.Zero ? GetParent(editHwnd) : IntPtr.Zero;
        if (dialogHwnd == IntPtr.Zero) return false;

        // Skip dialogs we already successfully handled (same QB session)
        lock (_lock) { if (_succeeded.Contains(dialogHwnd)) return false; }

        // ── Fill password ─────────────────────────────────────────────────────

        bool filled = false;
        try
        {
            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                ((ValuePattern)vp).SetValue(password);
                filled = true;
            }
        }
        catch { }

        if (!filled && editHwnd != IntPtr.Zero)
        {
            try { SetText(editHwnd, password); filled = true; }
            catch { }
        }

        if (!filled) return false;

        Thread.Sleep(150);

        // ── Submit via real Enter keystroke (SendInput) ───────────────────────
        // QB uses Intuit's Maui UI framework — WM_COMMAND IDOK is silently
        // ignored. AttachThreadInput joins our input queue to QB's so that
        // SetFocus is reliable across processes, then SendInput fires a real
        // OS-level Enter that Maui processes normally.

        bool submitted = false;
        try
        {
            var targetTid  = GetWindowThreadProcessId(dialogHwnd, out _);
            var currentTid = GetCurrentThreadId();
            bool attached  = targetTid != 0 && AttachThreadInput(currentTid, targetTid, true);
            try
            {
                SetForegroundWindow(dialogHwnd);
                if (editHwnd != IntPtr.Zero) SetFocus(editHwnd);
                Thread.Sleep(80);
                SendEnter();
                submitted = true;
            }
            finally
            {
                if (attached) AttachThreadInput(currentTid, targetTid, false);
            }
        }
        catch { }

        if (!submitted) return false;

        // Only mark as handled after confirmed fill + submit
        lock (_lock) { _succeeded.Add(dialogHwnd); }
        return true;
    }

    private static void SendEnter()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = VK_RETURN;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = VK_RETURN;
        inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    // INPUT.type (4 bytes) + 4 bytes padding + union (32 bytes) = 40 bytes on x64
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint   INPUT_KEYBOARD  = 1;
    private const uint   KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_RETURN       = 0x0D;
    private const uint   WM_SETTEXT      = 0x000C;

    private static void SetText(IntPtr hwnd, string text) =>
        SendMessage(hwnd, WM_SETTEXT, IntPtr.Zero, text);
}
