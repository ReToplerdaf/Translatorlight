namespace Translatorlight;

internal static class Program
{
    // Per-session name: every logged-in user may run their own copy.
    private const string InstanceMutexName = @"Local\Translatorlight.SingleInstance.9c41b7de";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var instanceLock = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isOnlyInstance);
        if (!isOnlyInstance)
        {
            MessageBox.Show(
                "Translatorlight уже запущен — значок ищите в области уведомлений.",
                "Translatorlight",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayApplicationContext());

        // Keeps the mutex owned for the whole lifetime of the message loop.
        GC.KeepAlive(instanceLock);
    }
}
