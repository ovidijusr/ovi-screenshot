using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Ovi.Screenshot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool isSmoke = args.Contains("--ui-smoke");
        using var singleInstance = isSmoke ? null : new Mutex(false, "Local\\OviScreenshot.App");
        bool ownsMutex = false;
        try
        {
            if (singleInstance is not null)
            {
                try { ownsMutex = singleInstance.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex) return;
            }
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new ScreenshotApp(args);
            });
        }
        finally { if (ownsMutex) singleInstance!.ReleaseMutex(); }
    }
}
