using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class NotificationMover
{
    #region P/Invoke

    delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder cls, int max);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const uint EVENT_OBJECT_SHOW           = 0x8002;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint SWP_NOSIZE     = 0x0001;
    const uint SWP_NOZORDER   = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const int SM_CXSCREEN = 0;
    const int SM_CYSCREEN = 1;

    #endregion

    static int screenW, screenH;
    static bool debug;
    static WinEventDelegate hookDelegate;
    static System.Threading.Mutex singleInstanceMutex;

    [STAThread]
    static void Main(string[] args)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();

        bool createdNew;
        singleInstanceMutex = new System.Threading.Mutex(true, "NotificationMoverSingleInstance", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show("NotificationMover はすでに起動しています。", "NotificationMover",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        debug = false;
        foreach (string a in args)
            if (a == "--debug") debug = true;

        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        if (!IsTaskRegistered())
        {
            RegisterTask(exePath);
            MessageBox.Show(
                "タスクスケジューラに登録しました。\n次回ログオン時から自動起動します。",
                "NotificationMover", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        screenW = GetSystemMetrics(SM_CXSCREEN);
        screenH = GetSystemMetrics(SM_CYSCREEN);

        hookDelegate = OnWinEvent;
        IntPtr hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

        if (hook == IntPtr.Zero)
        {
            Log("Failed to install WinEvent hook.");
            return;
        }

        Log(string.Format("Started. Screen={0}x{1}", screenW, screenH));

        var trayMenu = new ContextMenu();
        trayMenu.MenuItems.Add("終了", delegate(object s, EventArgs e)
        {
            UnhookWinEvent(hook);
            Application.Exit();
        });

        var tray = new NotifyIcon()
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath),
            Text = "NotificationMover",
            ContextMenu = trayMenu,
            Visible = true
        };

        tray.ShowBalloonTip(3000, "NotificationMover", "起動しました。通知を右上に移動します。", ToolTipIcon.Info);

        Application.Run();

        tray.Visible = false;
        tray.Dispose();
    }

    static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!IsWindowVisible(hwnd)) return;

        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) return;

        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return;

        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, 256);
        string cls = sb.ToString();

        int pid;
        GetWindowThreadProcessId(hwnd, out pid);
        string procName = "";
        try { procName = Process.GetProcessById(pid).ProcessName; } catch { }

        // Debug: log all windows near bottom-right to identify notification window
        if (debug && IsNearBottomRight(rect) && w > 50 && h > 30)
            Log(string.Format("[ALL] ev={0} cls={1} proc={2} pos=({3},{4}) size={5}x{6}",
                eventType, cls, procName, rect.Left, rect.Top, w, h));

        if (!IsNotificationWindow(cls, procName)) return;

        // Only move if window is near bottom-right (showing animation)
        if (!IsNearBottomRight(rect)) return;

        if (debug)
            Log(string.Format("[MATCH] ev={0} cls={1} proc={2} pos=({3},{4}) size={5}x{6}",
                eventType, cls, procName, rect.Left, rect.Top, w, h));

        // Move to top-right with a slight delay via ThreadPool
        IntPtr hwndCapture = hwnd;
        ThreadPool.QueueUserWorkItem(delegate(object state)
        {
            Thread.Sleep(50);
            MoveToTopRight(hwndCapture);
        });
    }

    static void MoveToTopRight(IntPtr hwnd)
    {
        int margin = 10;
        int stableCount = 0;

        // Keep moving until the window stays at top-right (or disappears)
        for (int i = 0; i < 60; i++)  // up to 1.8 seconds
        {
            if (!IsWindowVisible(hwnd)) break;

            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) break;

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) break;

            int newX = screenW - w - margin;
            int newY = margin;

            // If already at target, count stable frames and eventually stop
            if (rect.Left == newX && rect.Top == newY)
            {
                stableCount++;
                if (stableCount >= 5) break;
                Thread.Sleep(30);
                continue;
            }

            stableCount = 0;
            SetWindowPos(hwnd, IntPtr.Zero, newX, newY, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

            if (debug && i == 0)
                Log(string.Format("[MOVE] hwnd={0} -> ({1},{2})", hwnd, newX, newY));

            Thread.Sleep(30);
        }
    }

    static bool IsTaskRegistered()
    {
        var psi = new ProcessStartInfo("schtasks.exe", "/query /tn NotificationMover /fo LIST")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (var p = Process.Start(psi))
        {
            p.WaitForExit();
            return p.ExitCode == 0;
        }
    }

    static void RegisterTask(string exePath)
    {
        string args = string.Format(
            "/create /tn NotificationMover /tr \"\\\"{0}\\\"\" /sc ONLOGON /ru {1} /f /rl LIMITED",
            exePath, Environment.UserName);
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using (var p = Process.Start(psi))
            p.WaitForExit();
    }

    static bool IsNotificationWindow(string cls, string procName)
    {
        return procName == "ShellExperienceHost" && cls == "Windows.UI.Core.CoreWindow";
    }

    static bool IsNearBottomRight(RECT rect)
    {
        return (screenW - rect.Right) < 60 && (screenH - rect.Bottom) < 200;
    }

    static void Log(string msg)
    {
        if (!debug) return;
        string logPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
            "debug.log");
        System.IO.File.AppendAllText(logPath,
            DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
    }
}
