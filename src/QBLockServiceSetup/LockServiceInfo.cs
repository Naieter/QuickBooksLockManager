using System.ServiceProcess;

namespace QBLockServiceSetup;

internal static class LockServiceInfo
{
    public const string ServiceName = "QBLockService";

    // The service's current state, or null when it is not installed.
    public static ServiceControllerStatus? Status()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;   // throws InvalidOperationException if the service does not exist
        }
        catch { return null; }
    }
}
