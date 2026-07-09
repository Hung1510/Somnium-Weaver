using System.Runtime.InteropServices;

namespace SomniumWeaver.Services;

/// <summary>
/// makes the window render *behind* the desktop icons, like animated wallpaper.
///
/// how it works (the classic Progman/WorkerW trick): sending 0x052C to Progman asks it to
/// split the icon layer (SHELLDLL_DefView) into its own WorkerW and spawn a SECOND WorkerW
/// behind it for wallpaper. we find that second WorkerW and SetParent our window into it.
///
/// caveats worth knowing: the exact window layout varies across Windows 10/11 builds -- on
/// some, SHELLDLL_DefView sits directly under Progman with no separate WorkerW, so we fall
/// back to parenting to Progman itself. because a reparented WPF window's own Left/Top get
/// unreliable, we size it with the native MoveWindow to fill the parent's client area.
/// </summary>
public static class WallpaperMode
{
    private const uint SMTO_NORMAL = 0x0000;

    public static IntPtr Enable(IntPtr hwnd)
    {
        IntPtr target = FindWallpaperHost();
        SetParent(hwnd, target);
        if (GetClientRect(target, out RECT r))
            MoveWindow(hwnd, 0, 0, r.Right - r.Left, r.Bottom - r.Top, true);
        return target;
    }

    public static void Disable(IntPtr hwnd)
    {
        // detach back to the desktop; the caller restores size/position/topmost.
        SetParent(hwnd, IntPtr.Zero);
    }

    private static IntPtr FindWallpaperHost()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            // nudge Progman into spawning the wallpaper WorkerW (no-op if already there).
            SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);
        }

        IntPtr workerw = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                // the wallpaper WorkerW is the sibling directly after this window.
                workerw = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);

        return workerw != IntPtr.Zero ? workerw : progman; // fallback: Progman itself
    }

    // ---- interop ----

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                                    uint flags, uint timeout, out IntPtr result);
}
