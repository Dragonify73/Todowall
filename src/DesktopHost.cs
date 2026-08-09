using System;

namespace TodoWall
{
    internal enum HostMode
    {
        /// <summary>A normal top-level window that never activates and is kept pinned to
        /// the bottom of the z-order. Explorer's desktop is always the very bottom window,
        /// so this lands just above the wallpaper and below every real window - without
        /// depending on Explorer's internals at all. Clickable. The reliable default.</summary>
        Floating,

        /// <summary>Reparented into the window that hosts the desktop icons, as a genuine
        /// WS_CHILD. Truly welded to the desktop, but depends on Explorer's window layout,
        /// which differs between Windows builds and breaks when Explorer restarts.</summary>
        DesktopChild,

        /// <summary>Reparented behind the icon layer. Purely decorative - Explorer's icon
        /// list view sits on top and swallows every click.</summary>
        BehindIcons
    }

    /// <summary>
    /// Explorer's desktop is a small stack of windows, bottom to top:
    ///
    ///     WorkerW              &lt;- paints the wallpaper (only after the 0x052C nudge)
    ///     Progman / WorkerW    &lt;- hosts SHELLDLL_DefView
    ///       SHELLDLL_DefView
    ///         SysListView32    &lt;- the icons, and the thing that eats mouse input
    ///
    /// Which window hosts SHELLDLL_DefView moves around between builds, so it is looked
    /// up rather than assumed.
    /// </summary>
    internal static class DesktopHost
    {
        const uint WM_SPAWN_WORKER = 0x052C;
        const uint GA_PARENT = 1;

        public static HostMode ParseMode(string s)
        {
            if (string.Equals(s, "DesktopChild", StringComparison.OrdinalIgnoreCase)) return HostMode.DesktopChild;
            if (string.Equals(s, "BehindIcons", StringComparison.OrdinalIgnoreCase)) return HostMode.BehindIcons;
            return HostMode.Floating;
        }

        // ------------------------------------------------------------ floating mode

        /// <summary>Make sure the window is a plain, non-activating, top-level tool window.</summary>
        public static void MakeFloating(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            if (Native.GetAncestor(hwnd, GA_PARENT) != Native.GetDesktopWindow())
                Native.SetParent(hwnd, IntPtr.Zero);

            long style = Native.GetWindowLongSafe(hwnd, Native.GWL_STYLE).ToInt64();
            style &= ~((long)Native.WS_CHILD);
            style |= (long)(uint)Native.WS_POPUP;
            Native.SetWindowLongSafe(hwnd, Native.GWL_STYLE, new IntPtr(style));

            long ex = Native.GetWindowLongSafe(hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_TOOLWINDOW;      // no Alt+Tab, no taskbar button
            ex |= Native.WS_EX_NOACTIVATE;      // clicking it never steals focus
            ex &= ~((long)Native.WS_EX_APPWINDOW);
            Native.SetWindowLongSafe(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }

        /// <summary>Drop to the bottom of the z-order - just above Explorer's desktop.</summary>
        public static void SinkToBottom(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // ------------------------------------------------------------ reparented modes

        public static IntPtr ResolveParent(HostMode mode)
        {
            IntPtr progman = Native.FindWindow("Progman", null);

            if (mode == HostMode.DesktopChild)
            {
                // Become a sibling of the icon layer, so we draw above the wallpaper.
                //
                // Do NOT send WM_SPAWN_WORKER here: it makes Explorer create a WorkerW
                // that takes over wallpaper painting, which changes the stack underneath us.
                IntPtr host = FindDefViewHost();
                return host != IntPtr.Zero ? host : progman;
            }

            // Behind-icons mode needs the wallpaper/icon sandwich to exist.
            if (progman != IntPtr.Zero)
            {
                IntPtr unused;
                Native.SendMessageTimeout(progman, WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero,
                    Native.SMTO_NORMAL, 1200, out unused);
            }
            IntPtr worker = FindWallpaperWorkerW();
            return worker != IntPtr.Zero ? worker : progman;
        }

        static IntPtr FindDefViewHost()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            if (progman != IntPtr.Zero &&
                Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                return progman;

            IntPtr host = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (Native.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    host = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return host;
        }

        static IntPtr FindWallpaperWorkerW()
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (Native.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    IntPtr sibling = Native.FindWindowEx(IntPtr.Zero, hWnd, "WorkerW", null);
                    if (sibling != IntPtr.Zero) { found = sibling; return false; }
                }
                return true;
            }, IntPtr.Zero);

            if (found == IntPtr.Zero)
            {
                IntPtr progman = Native.FindWindow("Progman", null);
                if (progman != IntPtr.Zero)
                    found = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            }
            return found;
        }

        /// <summary>Reparent as a real child. WS_CHILD is the part that matters: without it
        /// Windows leaves the window parented but unrendered.</summary>
        public static bool AttachChild(IntPtr hwnd, IntPtr parent)
        {
            if (hwnd == IntPtr.Zero || parent == IntPtr.Zero) return false;
            if (!Native.IsWindow(parent)) return false;

            long ex = Native.GetWindowLongSafe(hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_TOOLWINDOW;
            ex &= ~((long)Native.WS_EX_APPWINDOW);
            Native.SetWindowLongSafe(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

            if (Native.SetParent(hwnd, parent) == IntPtr.Zero && Native.GetLastErrorCode() != 0)
            {
                // Most common cause: we are elevated and Explorer is not.
                Log.Write("SetParent failed, win32 error " + Native.GetLastErrorCode());
            }

            long style = Native.GetWindowLongSafe(hwnd, Native.GWL_STYLE).ToInt64();
            style |= (long)Native.WS_CHILD;
            style &= ~((long)(uint)Native.WS_POPUP);
            Native.SetWindowLongSafe(hwnd, Native.GWL_STYLE, new IntPtr(style));

            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

            // GetParent returns the OWNER for popups and lies here; GetAncestor does not.
            return Native.GetAncestor(hwnd, GA_PARENT) == parent;
        }

        public static bool IsChildOf(IntPtr hwnd, IntPtr parent)
        {
            if (hwnd == IntPtr.Zero || parent == IntPtr.Zero) return false;
            if (!Native.IsWindow(hwnd) || !Native.IsWindow(parent)) return false;
            return Native.GetAncestor(hwnd, GA_PARENT) == parent;
        }

        // ------------------------------------------------------------ placement

        /// <summary>Screen coordinates - correct for a top-level (WS_POPUP) window.</summary>
        public static void PlaceOnScreen(IntPtr hwnd, int x, int y, int w, int h)
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        /// <summary>Parent-client coordinates - correct once the window is WS_CHILD.</summary>
        public static void PlaceInParent(IntPtr hwnd, IntPtr parent, int screenX, int screenY, int w, int h)
        {
            int x = screenX, y = screenY;
            Native.RECT pr;
            if (parent != IntPtr.Zero && Native.GetWindowRect(parent, out pr))
            {
                x -= pr.Left;
                y -= pr.Top;
            }
            Native.SetWindowPos(hwnd, Native.HWND_TOP, x, y, w, h,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        public static Native.RECT RectOf(IntPtr hwnd)
        {
            Native.RECT r;
            Native.GetWindowRect(hwnd, out r);
            return r;
        }
    }
}
