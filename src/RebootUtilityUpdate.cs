using System;
using System.IO;
using System.Diagnostics;
using Microsoft.Win32;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 3)
        {
            return;
        }

        string cleanup = args[0];
        string currentPath = args[1];
        string targetPath = args[2];
        // Optional arg 4: PID of the main process to wait for before replacing the exe.
        int mainPid = 0;
        if (args.Length > 3) int.TryParse(args[3], out mainPid);

        try
        {
            // Clean up old startup entries if instructed
            if (cleanup == "cleanup")
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        string[] names = key.GetValueNames();
                        foreach (string name in names)
                        {
                            string value = (string)key.GetValue(name);
                            string fileName = Path.GetFileName(value);
                            if ((name.StartsWith("Reboot Utility") && value != targetPath) ||
                                (fileName.StartsWith("tmp") && fileName.EndsWith(".tmp.exe")))
                            {
                                key.DeleteValue(name, false);
                            }
                        }
                        // Ensure the installed path is set for startup
                        key.SetValue("Reboot Utility", targetPath);
                    }
                }
            }

            // Wait for the main process to fully exit before replacing its exe file.
            // Using WaitForExit (when PID is available) is more reliable than a fixed sleep
            // because it returns immediately once the process releases its file lock,
            // regardless of machine load.  Fall back to a 3-second sleep if no PID was passed.
            if (mainPid > 0)
            {
                try
                {
                    Process mainProc = Process.GetProcessById(mainPid);
                    mainProc.WaitForExit(5000); // 5-second ceiling to avoid hanging indefinitely
                }
                catch (ArgumentException) { } // process already exited — continue
            }
            else
            {
                System.Threading.Thread.Sleep(3000);
            }

            // Replace the original executable — Copy+Delete is safer than Delete+Move:
            // if Move failed after Delete the install would be broken; Copy with overwrite
            // replaces atomically from the OS perspective and leaves the source for cleanup.
            File.Copy(currentPath, targetPath, true);
            File.Delete(currentPath);

            // Launch the new version
            Process.Start(targetPath);

            // Self-delete via a CMD batch — File.Delete on a running exe always fails on Windows
            // because the process holds a file lock on itself.
            string updaterPath = Process.GetCurrentProcess().MainModule.FileName;
            string selfDeleteBatch = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".bat");
            using (StreamWriter sw = new StreamWriter(selfDeleteBatch))
            {
                sw.WriteLine("@echo off");
                sw.WriteLine("ping 127.0.0.1 -n 3 >nul");
                sw.WriteLine("del \"" + updaterPath + "\"");
                sw.WriteLine("del \"%~f0\"");
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/C \"" + selfDeleteBatch + "\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show(
                "Update error: " + ex.Message,
                "Reboot Utility Update",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
    }
}