namespace QBLockServiceSetup;

// One server program per Windows session, so there is only ever one tray icon.
//
//  - A normal (double-click) launch TAKES OVER from a running copy: it asks that copy to exit and
//    waits. That is what lets a freshly downloaded setup install/upgrade while an older copy is
//    sitting in the tray.
//  - The automatic start-in-tray launch never takes over: if a copy is already running it just exits.
internal static class SingleInstance
{
    private const string MutexName     = @"Local\QBLockServiceSetup.Instance";
    private const string ExitEventName = @"Local\QBLockServiceSetup.Exit";

    // Kept for the life of the process (the mutex is what marks us as "the" instance).
    private static Mutex? _mutex;
    private static EventWaitHandle? _exitEvent;
    private static RegisteredWaitHandle? _wait;

    // True if this process is now the one instance. onExitRequested runs on a pool thread when a
    // newer launch asks this copy to go away.
    public static bool TryBecomePrimary(bool trayLaunch, Action onExitRequested)
    {
        _mutex     = new Mutex(false, MutexName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);

        if (!Acquire(0))
        {
            if (trayLaunch) return false;     // automatic start must not disturb a running copy

            // Only signal when a copy really exists: an AutoReset event nobody consumes would stay
            // set and make *this* process exit as soon as it starts listening below.
            _exitEvent.Set();
            if (!Acquire(8000)) return false; // the other copy is busy (e.g. installing)
        }

        _wait = ThreadPool.RegisterWaitForSingleObject(
            _exitEvent, (_, _) => onExitRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    private static bool Acquire(int milliseconds)
    {
        try { return _mutex!.WaitOne(milliseconds); }
        catch (AbandonedMutexException) { return true; }   // the previous owner died; we own it now
    }
}
