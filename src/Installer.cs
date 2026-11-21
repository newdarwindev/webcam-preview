using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Installer
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Install();
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Webcam Preview installation failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Install()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebcamPreview");
        string executable = Path.Combine(directory, "WebcamPreview.exe");
        string staging = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(directory);
        try
        {
            using (Stream payload = typeof(Installer).Assembly.GetManifestResourceStream("WebcamPreview.exe"))
            using (var destination = new FileStream(staging, FileMode.CreateNew, FileAccess.Write))
                payload.CopyTo(destination);

            StopInstalledPreview(executable);
            if (File.Exists(executable))
                File.Replace(staging, executable, null);
            else
                File.Move(staging, executable);

            CreateShortcuts(directory, executable);
            Process.Start(new ProcessStartInfo(executable) {
                WorkingDirectory = directory, UseShellExecute = true
            });
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    private static void StopInstalledPreview(string executable)
    {
        foreach (Process process in Process.GetProcessesByName("WebcamPreview"))
        {
            using (process)
            {
                string path;
                try
                {
                    path = process.MainModule.FileName;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    continue;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (!string.Equals(path, executable, StringComparison.OrdinalIgnoreCase) || process.HasExited)
                    continue;
                process.CloseMainWindow();
                if (!process.WaitForExit(5000))
                    throw new InvalidOperationException("Close Webcam Preview and run the installer again.");
            }
        }
    }

    private static void CreateShortcuts(string directory, string executable)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        try
        {
            SaveShortcut(shell, Environment.SpecialFolder.Programs, directory, executable);
            SaveShortcut(shell, Environment.SpecialFolder.Startup, directory, executable);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void SaveShortcut(dynamic shell, Environment.SpecialFolder folder,
        string directory, string executable)
    {
        string shortcutPath = Path.Combine(Environment.GetFolderPath(folder), "Webcam Preview.lnk");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        try
        {
            shortcut.TargetPath = executable;
            shortcut.WorkingDirectory = directory;
            shortcut.Description = "Open the borderless webcam preview";
            shortcut.Save();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shortcut);
        }
    }
}
