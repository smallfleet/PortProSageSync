using System.Diagnostics;

namespace PortProSage.Admin;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        CloseOtherRunningInstances();
        Application.Run(new MainForm());
    }

    /// <summary>Only one Admin instance should ever run at a time - a forgotten,
    /// already-running instance polling result.json in the background (with old
    /// code still loaded in memory, even after a newer build is on disk) was
    /// confirmed live 2026-08-12 as the actual cause of a file-locking bug that
    /// aborted a production sync run, and separately caused real confusion about
    /// whether a rebuilt fix had actually taken effect. Killing the GUI process
    /// here does NOT stop any Manual Run it might be tracking - that's a separate
    /// PortProSage.Service.exe --run-once process, unaffected by its parent Admin
    /// window closing.</summary>
    private static void CloseOtherRunningInstances()
    {
        try
        {
            var currentId = Process.GetCurrentProcess().Id;
            var others = Process.GetProcessesByName("PortProSage.Admin").Where(p => p.Id != currentId).ToList();
            if (others.Count == 0) return;

            foreach (var other in others)
            {
                try { other.Kill(); other.WaitForExit(3000); }
                catch { /* best effort - never block startup over a stale process we can't close */ }
                finally { other.Dispose(); }
            }

            MessageBox.Show(
                $"Closed {others.Count} other already-running PortProSage Admin window(s) before starting - only " +
                "one instance should run at a time. (A forgotten, already-running instance was confirmed as the " +
                "cause of a recent file-locking bug during a production run.)",
                "Other instance(s) closed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch
        {
            // Best effort - never block startup over this.
        }
    }
}
