using System;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32; // Added to resolve RegistryKey and Registry

class Program
{
    private static string currentPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
    private static string appName = "Reboot Utility";

    [STAThread]
    static void Main()
    {
        if (!IsAdmin())
        {
            RequestElevation();
            return;
        }

        CleanRegistry();
        CleanStartupFolders();
        CleanTaskScheduler();

        Console.WriteLine("Cleanup completed. Only the current startup entry for " + appName + " should remain.");
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }

    private static bool IsAdmin()
    {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private static void RequestElevation()
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = currentPath,
                Verb = "runas",
                UseShellExecute = true
            };
            Process.Start(info);
            Environment.Exit(0);
        }
        catch (Exception)
        {
            Console.WriteLine("Elevation failed. Please run as administrator manually.");
        }
    }

    private static void CleanRegistry()
    {
        // Clean HKCU Run key
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
        {
            if (key != null)
            {
                foreach (string valueName in key.GetValueNames())
                {
                    if (valueName == appName)
                    {
                        string value = (string)key.GetValue(valueName);
                        if (value != currentPath && !string.IsNullOrEmpty(value))
                        {
                            key.DeleteValue(valueName);
                            Console.WriteLine("Removed duplicate HKCU Run entry: " + valueName);
                        }
                    }
                }
            }
        }

        // Clean HKLM Run key (requires admin)
        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
        {
            if (key != null)
            {
                foreach (string valueName in key.GetValueNames())
                {
                    if (valueName == appName)
                    {
                        string value = (string)key.GetValue(valueName);
                        if (value != currentPath && !string.IsNullOrEmpty(value))
                        {
                            key.DeleteValue(valueName);
                            Console.WriteLine("Removed duplicate HKLM Run entry: " + valueName);
                        }
                    }
                }
            }
        }
    }

    private static void CleanStartupFolders()
    {
        string[] startupPaths = new string[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        };

        foreach (string path in startupPaths)
        {
            try
            {
                string[] files = Directory.GetFiles(path, "RebootUtility*.exe");
                foreach (string file in files)
                {
                    if (Path.GetFileName(file) != Path.GetFileName(currentPath))
                    {
                        File.Delete(file);
                        Console.WriteLine("Removed startup folder entry: " + file);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error cleaning " + path + ": " + ex.Message);
            }
        }
    }

    private static void CleanTaskScheduler()
    {
        try
        {
            Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/query /fo csv",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            string[] lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                if (line.Contains(appName))
                {
                    string taskName = line.Split(',')[0].Trim('\"');
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "schtasks",
                        Arguments = "/delete /tn \"" + taskName + "\" /f",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }).WaitForExit();
                    Console.WriteLine("Removed Task Scheduler task: " + taskName);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error cleaning Task Scheduler: " + ex.Message);
        }
    }
}