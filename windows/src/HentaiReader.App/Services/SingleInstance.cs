using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HentaiReader.App.Services;

/// <summary>
/// One running app per Windows session. The named mutex <c>HentaiReader.Running</c> marks a running
/// instance; the installer waits on the same name (<c>AppMutex</c>), and its <c>Global\</c> twin
/// covers other sessions of an all-users installation for the installer only.
/// </summary>
public static partial class SingleInstance
{
    public const string MutexName = "HentaiReader.Running";

    /// <summary>Command-line flag of a restart (language change): wait for the old process to exit.</summary>
    public const string RestartFlag = "--restart";

    private static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(10);
    private static Mutex? _session;
    private static Mutex? _global;

    /// <summary>
    /// True when this process is the only instance; it then holds the mutexes until exit or
    /// <see cref="Release"/>. A restarted process waits for its predecessor to release them.
    /// </summary>
    public static bool TryAcquire(bool restarting)
    {
        var deadline = DateTime.UtcNow + (restarting ? RestartWait : TimeSpan.Zero);
        while (true)
        {
            var mutex = new Mutex(false, MutexName, out var createdNew);
            if (createdNew)
            {
                _session = mutex;
                _global = TryCreate(@"Global\" + MutexName);
                return true;
            }
            mutex.Dispose();
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(200);
        }
    }

    /// <summary>Lets a restarted process take over before this one has fully exited.</summary>
    public static void Release()
    {
        _session?.Dispose();
        _global?.Dispose();
        _session = _global = null;
    }

    /// <summary>Brings the window of the already running instance to the front.</summary>
    public static void ActivateOther()
    {
        var self = Environment.ProcessId;
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == self || process.SessionId != current.SessionId || process.MainWindowHandle == IntPtr.Zero) continue;
                var window = process.MainWindowHandle;
                if (IsIconic(window)) ShowWindow(window, SwRestore);
                SetForegroundWindow(window);
                return;
            }
        }
    }

    private static Mutex? TryCreate(string name)
    {
        try
        {
            return new Mutex(false, name);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    private const int SwRestore = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr window);
}
