using System;
using System.Windows.Forms;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using System.Threading;
using System.Diagnostics;
using System.Security.Principal;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Linq;
using System.IO;
using System.Text;
using System.Net;

// Assembly-level attributes
[assembly: AssemblyVersion("1.56.0.0")]
[assembly: AssemblyFileVersion("1.56.0.0")]
[assembly: AssemblyProduct("Reboot Utility")]

namespace SystemTrayReboot
{
    class Program : Form
    {
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private TimeSpan rebootTime;
        private string pingServer;
        private bool dontRebootIfPingFails;
        private bool isFirstRun;
        private volatile bool rebootActive;          // volatile: read on timer thread, written on UI thread
        private string[] selectedDays = new string[] { };
        private System.Timers.Timer timer;
        private System.Timers.Timer updateTimer;
        private EventWaitHandle showSettingsEvent;
        private CancellationTokenSource listenerCts;  // For clean shutdown of listener thread
        private const string RegistryPath = @"SOFTWARE\SystemTrayReboot";
        private const string MutexName = "Global\\SystemTrayRebootMutex";
        private const string EventName = "Global\\SystemTrayRebootShowSettings";
        private Form settingsForm;
        private const string AppVersion = "1.56";
        private string latestVersion = null;
        private volatile bool updateAvailable = false; // volatile: written on threadpool, read on UI
        private DateTime lastRebootFired = DateTime.MinValue; // Guard against double-fire in same minute
        private volatile bool pingRetryPending = false;       // True when first ping failed and retry is scheduled
        private DateTime pingRetryTime = DateTime.MinValue;   // When to attempt the retry ping
        private const int TimerIntervalMs = 60000;
        private const int PingTimeoutMs = 1000;
        private const string RebootTimeFormat = @"hh\:mm\:ss"; // Fixed format for stable registry round-trip

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint ExtractIconEx(string szFileName, int nIconIndex, out IntPtr hLarge, out IntPtr hSmall, uint nIcons);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        [STAThread]
        static void Main(string[] args)
        {
            string action = null;
            TimeSpan? tempRebootTime = null;
            string tempPingServer = null;
            bool? tempDontRebootIfPingFails = null;
            bool? tempStartupCheck = null;
            string[] tempSelectedDays = null;

            if (args.Length > 0)
            {
                if (args[0] == "-export")
                {
                    ExportSettingsToRegFile();
                    return;
                }
                else if (args[0] == "reset")
                {
                    action = "reset";
                }
                else
                {
                    string[] parts = args[0].Split(';');
                    if (parts.Length >= 4)
                    {
                        TimeSpan rt;
                        if (TimeSpan.TryParse(parts[0], out rt))
                            tempRebootTime = rt;
                        tempPingServer = parts[1];
                        bool drif;
                        if (bool.TryParse(parts[2], out drif))
                            tempDontRebootIfPingFails = drif;
                        bool sc;
                        if (bool.TryParse(parts[3], out sc))
                            tempStartupCheck = sc;
                        if (parts.Length > 4 && !string.IsNullOrEmpty(parts[4]))
                            tempSelectedDays = parts[4].Split(',');
                    }
                    action = "save";
                }
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Signal existing instance to show settings
                    try
                    {
                        using (EventWaitHandle evt = EventWaitHandle.OpenExisting(EventName))
                        {
                            evt.Set();
                        }
                    }
                    catch { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Program(action, tempRebootTime, tempPingServer, tempDontRebootIfPingFails, tempStartupCheck, tempSelectedDays));
            }
        }

        private static void ExportSettingsToRegFile()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegistryPath, false))
                {
                    if (key == null)
                    {
                        Console.WriteLine("No settings found to export.");
                        return;
                    }

                    string rebootTime = (string)key.GetValue("RebootTime", "06:00:00");
                    string pingServer = (string)key.GetValue("PingServer", "google.com");
                    int dontRebootIfPingFails = (int)key.GetValue("DontRebootIfPingFails", 0);
                    string selectedDays = (string)key.GetValue("SelectedDays", "Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday");

                    string regContent = "Windows Registry Editor Version 5.00" + Environment.NewLine +
                                       Environment.NewLine +
                                       "[HKEY_LOCAL_MACHINE\\SOFTWARE\\SystemTrayReboot]" + Environment.NewLine +
                                       "\"RebootTime\"=\"" + rebootTime + "\"" + Environment.NewLine +
                                       "\"PingServer\"=\"" + pingServer + "\"" + Environment.NewLine +
                                       "\"DontRebootIfPingFails\"=" + dontRebootIfPingFails + Environment.NewLine +
                                       "\"SelectedDays\"=\"" + selectedDays + "\"";

                    string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RebootUtilitySettings.reg");
                    File.WriteAllText(filePath, regContent, Encoding.Unicode);
                    Console.WriteLine("Settings exported to " + filePath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error exporting settings: " + ex.Message);
            }
        }

        public Program(string action = null, TimeSpan? tempRebootTime = null, string tempPingServer = null, bool? tempDontRebootIfPingFails = null, bool? tempStartupCheck = null, string[] tempSelectedDays = null)
        {
            // Load settings from registry
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegistryPath, false))
                {
                    if (key == null)
                    {
                        isFirstRun = true;
                    }
                    else
                    {
                        string rt = (string)key.GetValue("RebootTime");
                        isFirstRun = string.IsNullOrEmpty(rt);
                        rebootTime = string.IsNullOrEmpty(rt) ? new TimeSpan(6, 0, 0) : TimeSpan.Parse(rt);
                        pingServer = (string)key.GetValue("PingServer", "google.com");
                        dontRebootIfPingFails = (int)key.GetValue("DontRebootIfPingFails", 0) != 0;
                        string daysStr = (string)key.GetValue("SelectedDays", "");
                        selectedDays = string.IsNullOrEmpty(daysStr) ? new string[] { } : daysStr.Split(',');
                    }
                }
            }
            catch (Exception)
            {
                MessageBox.Show("Unable to load settings from registry. Run as administrator for full access.", "Registry Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                isFirstRun = true;
            }

            rebootActive = !isFirstRun;

            if (isFirstRun)
            {
                rebootTime = new TimeSpan(6, 0, 0);
                pingServer = "google.com";
                dontRebootIfPingFails = false;
            }

            // If elevated with persisted state, apply and save/reset automatically
            if (action != null)
            {
                if (action == "save" && tempRebootTime.HasValue && !string.IsNullOrEmpty(tempPingServer) && tempDontRebootIfPingFails.HasValue && tempStartupCheck.HasValue)
                {
                    rebootTime = tempRebootTime.Value;
                    pingServer = tempPingServer;
                    dontRebootIfPingFails = tempDontRebootIfPingFails.Value;
                    if (tempSelectedDays != null && tempSelectedDays.Length > 0)
                        selectedDays = tempSelectedDays;
                    try
                    {
                        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath))
                        {
                            key.SetValue("RebootTime", rebootTime.ToString(RebootTimeFormat));
                            key.SetValue("PingServer", pingServer);
                            key.SetValue("DontRebootIfPingFails", dontRebootIfPingFails ? 1 : 0, RegistryValueKind.DWord);
                            key.SetValue("SelectedDays", string.Join(",", selectedDays));
                        }
                        SetRunAtStartup(tempStartupCheck.Value);
                        isFirstRun = false;
                        rebootActive = true;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Failed to save in elevated mode: " + ex.Message, "Save Failed", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageBoxOptions.ServiceNotification);
                    }
                }
                else if (action == "reset")
                {
                    try
                    {
                        Registry.LocalMachine.DeleteSubKeyTree(RegistryPath, false);
                        SetRunAtStartup(false);
                        isFirstRun = true;
                        rebootActive = false;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Failed to reset in elevated mode: " + ex.Message, "Reset Failed", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageBoxOptions.ServiceNotification);
                    }
                }
            }

            // IPC event for single-instance show-settings signal
            showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

            // FIX: Listener thread with cancellation support — prevents ObjectDisposedException crash on shutdown
            listenerCts = new CancellationTokenSource();
            Thread listenerThread = new Thread(() => ListenForShowSettings(listenerCts.Token));
            listenerThread.IsBackground = true;
            listenerThread.Start();

            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open", null, OnOpen);
            trayMenu.Items.Add("Exit", null, OnExit);

            trayIcon = new NotifyIcon
            {
                ContextMenuStrip = trayMenu,
                Visible = true,
                Text = "Reboot Utility " + AppVersion
            };
            trayIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OnOpen(s, e); };

            // Load icon from SHELL32.dll index 27 (power symbol)
            IntPtr hLarge, hSmall;
            string dllPath = Environment.ExpandEnvironmentVariables("%SystemRoot%\\System32\\SHELL32.dll");
            if (ExtractIconEx(dllPath, 27, out hLarge, out hSmall, 1) > 0)
            {
                trayIcon.Icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hSmall).Clone();
                DestroyIcon(hSmall);
                DestroyIcon(hLarge);
            }
            else
            {
                trayIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            timer = new System.Timers.Timer(TimerIntervalMs);
            timer.AutoReset = true;
            timer.Elapsed += CheckReboot;
            timer.Start();

            // FIX: Single update-check path — one-shot timer fires after 1s, then switches to 24h auto-repeat.
            // Removed the duplicate immediate CheckForUpdate() call that caused two concurrent requests at startup.
            updateTimer = new System.Timers.Timer(1000);
            updateTimer.AutoReset = false; // One-shot; manually restarted with 24h interval after first fire
            updateTimer.Elapsed += (s, e) =>
            {
                CheckForUpdate();
                updateTimer.Interval = 24 * 60 * 60 * 1000;
                updateTimer.AutoReset = true;
                updateTimer.Start();
            };
            updateTimer.Start();

            if (isFirstRun || action != null)
            {
                ShowSettingsDialog();
            }

            // FIX: Null-guard before Activate (settingsForm may be null if dialog was not shown)
            if (action != null && settingsForm != null && !settingsForm.IsDisposed)
            {
                settingsForm.Activate();
            }
        }

        private void CheckForUpdate()
        {
            string fetched = null;
            bool available = false;

            try
            {
                using (WebClient wc = new WebClient())
                {
                    fetched = wc.DownloadString("https://raw.githubusercontent.com/S07734/Reboot-Utility-with-Online-Check/main/version.txt").Trim();
                    Version currentVer = new Version(AppVersion);
                    Version latestVer = new Version(fetched);
                    available = latestVer > currentVer;
                }
            }
            catch (Exception)
            {
                // Silently ignore network / parse errors
            }

            latestVersion = fetched;
            updateAvailable = available;

            // Refresh UI on the main thread; guard against form being disposed between null-check and Invoke
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                try
                {
                    settingsForm.Invoke((MethodInvoker)delegate { UpdateVersionStatus(); });
                }
                catch (ObjectDisposedException) { }
            }
        }

        private void UpdateVersionStatus()
        {
            if (settingsForm == null || settingsForm.IsDisposed) return;

            // Remove all three version controls in one pass before re-adding
            settingsForm.Controls.RemoveByKey("versionStatusLabel");
            settingsForm.Controls.RemoveByKey("updateLink");
            settingsForm.Controls.RemoveByKey("checkUpdateLink");

            Label versionStatusLabel = new Label
            {
                Name = "versionStatusLabel",
                Text = updateAvailable ?
                    "Update available (Current: " + AppVersion + ", Latest: " + latestVersion + ")" :
                    "Up to date (Version: " + AppVersion + ")",
                Location = new System.Drawing.Point(14, 312),
                Size = new System.Drawing.Size(280, 22),
                Font = new System.Drawing.Font("Segoe UI", 8F),
                BackColor = System.Drawing.Color.FromArgb(245, 246, 248)
            };
            settingsForm.Controls.Add(versionStatusLabel);
            versionStatusLabel.BringToFront();

            if (!updateAvailable)
            {
                LinkLabel checkUpdateLink = new LinkLabel
                {
                    Name = "checkUpdateLink",
                    Text = "Check for Updates",
                    Location = new System.Drawing.Point(settingsForm.ClientSize.Width - 130, 312),
                    Size = new System.Drawing.Size(120, 22),
                    LinkColor = System.Drawing.Color.FromArgb(0, 120, 215),
                    ActiveLinkColor = System.Drawing.Color.Red,
                    Font = new System.Drawing.Font("Segoe UI", 8F),
                    BackColor = System.Drawing.Color.FromArgb(245, 246, 248)
                };
                checkUpdateLink.Click += (s, e) => { CheckForUpdate(); };
                settingsForm.Controls.Add(checkUpdateLink);
                checkUpdateLink.BringToFront();
            }
            else
            {
                LinkLabel updateLink = new LinkLabel
                {
                    Name = "updateLink",
                    Text = "Update Now",
                    Location = new System.Drawing.Point(settingsForm.ClientSize.Width - 90, 312),
                    Size = new System.Drawing.Size(80, 22),
                    LinkColor = System.Drawing.Color.FromArgb(0, 120, 215),
                    ActiveLinkColor = System.Drawing.Color.Red,
                    Font = new System.Drawing.Font("Segoe UI", 8F),
                    BackColor = System.Drawing.Color.FromArgb(245, 246, 248)
                };
                updateLink.Click += (s, e) => { PerformUpdate(); };
                settingsForm.Controls.Add(updateLink);
                updateLink.BringToFront();
            }
            // Form height is fixed at 368 — no resize needed
        }

        private void PerformUpdate()
        {
            if (!updateAvailable || string.IsNullOrEmpty(latestVersion)) return;

            try
            {
                string downloadUrl = "https://raw.githubusercontent.com/S07734/Reboot-Utility-with-Online-Check/main/bin/latest/RebootUtility.exe";
                // Named temp file so a leftover from a previous crash is always cleaned up first
                string tempPath = Path.Combine(Path.GetTempPath(), "RebootUtility_update.exe");
                if (File.Exists(tempPath)) File.Delete(tempPath);

                using (WebClient wc = new WebClient())
                {
                    wc.DownloadFile(downloadUrl, tempPath);
                }

                // Validate: downloaded file must be a PE executable (MZ magic bytes).
                // Catches cases where the server returns a 302/404 HTML error page instead of the binary.
                byte[] header = new byte[2];
                using (FileStream fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read))
                {
                    fs.Read(header, 0, 2);
                }
                if (header[0] != 0x4D || header[1] != 0x5A) // "MZ"
                {
                    File.Delete(tempPath);
                    MessageBox.Show(
                        "Update failed: the server returned an invalid file (not an executable).\n" +
                        "The server files may be temporarily unavailable — try again later.",
                        "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Validate: downloaded version must match what version.txt advertised.
                // Catches the common deployment mistake of uploading version.txt before RebootUtility.exe,
                // which causes the old binary to be silently re-installed with no visible effect.
                try
                {
                    System.Diagnostics.FileVersionInfo vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(tempPath);
                    if (!string.IsNullOrEmpty(vi.FileVersion))
                    {
                        string[] dlParts = vi.FileVersion.Split('.');
                        string[] exParts = latestVersion.Split('.');
                        int dlMajor = dlParts.Length > 0 ? int.Parse(dlParts[0]) : 0;
                        int dlMinor = dlParts.Length > 1 ? int.Parse(dlParts[1]) : 0;
                        int exMajor = exParts.Length > 0 ? int.Parse(exParts[0]) : 0;
                        int exMinor = exParts.Length > 1 ? int.Parse(exParts[1]) : 0;
                        bool tooOld = dlMajor < exMajor || (dlMajor == exMajor && dlMinor < exMinor);
                        if (tooOld)
                        {
                            File.Delete(tempPath);
                            MessageBox.Show(
                                "Update failed: downloaded version " + dlMajor + "." + dlMinor +
                                " but version.txt advertises " + latestVersion + ".\n" +
                                "The server files are out of sync — try again later.",
                                "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }
                }
                catch (Exception) { /* version check is best-effort; proceed if it cannot be read */ }

                string currentPath = Assembly.GetExecutingAssembly().Location;
                string updaterPath = Path.Combine(Path.GetDirectoryName(currentPath), "RebootUtilityUpdate.exe");

                if (File.Exists(updaterPath))
                {
                    // Launch the dedicated updater helper elevated so it can overwrite the installed
                    // exe regardless of where it lives.  Elevation is requested here — on the user's
                    // explicit "Update Now" click — so it never fires on unattended startup.
                    // Args: "cleanup" instructs the helper to tidy the startup registry entry.
                    try
                    {
                        // Pass our PID as arg 4 so the updater can WaitForExit instead
                        // of relying on a fixed sleep — more reliable under heavy load.
                        int pid = Process.GetCurrentProcess().Id;
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = updaterPath,
                            Arguments = "cleanup \"" + tempPath + "\" \"" + currentPath + "\" " + pid,
                            Verb = "runas",
                            UseShellExecute = true
                        });
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        // Error 1223 = user cancelled the UAC prompt; abort silently.
                        if (ex.NativeErrorCode != 1223)
                            MessageBox.Show("Update failed: " + ex.Message, "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                        return;
                    }
                }
                else
                {
                    // Fallback: batch-based update (no elevation; may fail on protected installs).
                    string newPath = currentPath + ".new";
                    File.Copy(tempPath, newPath, true);
                    File.Delete(tempPath);

                    string batchPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".bat");
                    using (StreamWriter sw = new StreamWriter(batchPath))
                    {
                        sw.WriteLine("@echo off");
                        sw.WriteLine("ping 127.0.0.1 -n 3 >nul");
                        sw.WriteLine("move /Y \"" + newPath + "\" \"" + currentPath + "\"");
                        sw.WriteLine("if exist \"" + currentPath + "\" start \"\" \"" + currentPath + "\"");
                        sw.WriteLine("del \"%~f0\"");
                    }
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/C \"" + batchPath + "\"",
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                }

                OnExit(null, null);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Update failed: " + ex.Message, "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // FIX: Accepts a CancellationToken and uses WaitOne(timeout) so the thread exits cleanly
        // on shutdown instead of blocking forever on a disposed EventWaitHandle.
        private void ListenForShowSettings(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (showSettingsEvent.WaitOne(500) && !ct.IsCancellationRequested)
                    {
                        try
                        {
                            this.Invoke((MethodInvoker)delegate { ShowSettingsDialog(); });
                        }
                        catch (ObjectDisposedException) { break; }
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception) { break; }
            }
        }

        private void OnOpen(object sender, EventArgs e)
        {
            ShowSettingsDialog();
        }

        private void OnExit(object sender, EventArgs e)
        {
            if (listenerCts != null) listenerCts.Cancel();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            Application.Exit(); // FIX: removed redundant Environment.Exit(0) that made Application.Exit() a no-op
        }

        private void ShowSettingsDialog()
        {
            if (settingsForm != null && !settingsForm.IsDisposed && settingsForm.Visible)
            {
                settingsForm.Activate();
                UpdateDialogTitle();
                UpdateVersionStatus();
                return;
            }

            if (settingsForm == null || settingsForm.IsDisposed)
            {
                settingsForm = new Form
                {
                    Text = "Reboot Utility",
                    Size = new System.Drawing.Size(500, 368),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    StartPosition = FormStartPosition.CenterScreen,
                    Font = new System.Drawing.Font("Segoe UI", 9F),
                    BackColor = System.Drawing.Color.FromArgb(245, 246, 248)
                };

                TimeSpan initialRebootTime = rebootTime;
                string initialPingServer = pingServer;
                bool initialDontRebootIfPingFails = dontRebootIfPingFails;
                bool initialRunAtStartup = IsRunAtStartup();
                string[] initialSelectedDays = selectedDays.ToArray();

                // ── Header panel ──────────────────────────────────────────────────────────
                Panel headerPanel = new Panel
                {
                    Name = "headerPanel",
                    Location = new System.Drawing.Point(0, 0),
                    Size = new System.Drawing.Size(500, 48),
                    BackColor = System.Drawing.Color.FromArgb(30, 30, 30)
                };

                Label headerAppLabel = new Label
                {
                    Text = "Reboot Utility",
                    ForeColor = System.Drawing.Color.White,
                    Font = new System.Drawing.Font("Segoe UI", 11F, System.Drawing.FontStyle.Bold),
                    Location = new System.Drawing.Point(14, 9),
                    AutoSize = true
                };

                Label headerStatusLabel = new Label
                {
                    Name = "headerStatusLabel",
                    Text = rebootActive ? "\u25cf Schedule Active" : "\u25cf Schedule Inactive",
                    ForeColor = rebootActive ? System.Drawing.Color.FromArgb(72, 199, 116) : System.Drawing.Color.FromArgb(255, 160, 64),
                    Font = new System.Drawing.Font("Segoe UI", 8F),
                    Location = new System.Drawing.Point(14, 30),
                    AutoSize = true,
                    BackColor = System.Drawing.Color.FromArgb(30, 30, 30)
                };

                headerPanel.Controls.Add(headerAppLabel);
                headerPanel.Controls.Add(headerStatusLabel);

                // ── SCHEDULE section ──────────────────────────────────────────────────────
                Label scheduleSectionLabel = new Label
                {
                    Text = "SCHEDULE",
                    Location = new System.Drawing.Point(14, 57),
                    AutoSize = true,
                    ForeColor = System.Drawing.Color.FromArgb(108, 117, 125),
                    Font = new System.Drawing.Font("Segoe UI", 7.5F, System.Drawing.FontStyle.Bold)
                };

                Panel scheduleDivider = new Panel
                {
                    Location = new System.Drawing.Point(14, 72),
                    Size = new System.Drawing.Size(472, 1),
                    BackColor = System.Drawing.Color.FromArgb(222, 226, 230)
                };

                Label timeLabel = new Label
                {
                    Text = "Time",
                    Location = new System.Drawing.Point(14, 82),
                    AutoSize = true,
                    ForeColor = System.Drawing.Color.FromArgb(108, 117, 125),
                    Font = new System.Drawing.Font("Segoe UI", 8F)
                };
                DateTimePicker timePicker = new DateTimePicker
                {
                    Format = DateTimePickerFormat.Time,
                    ShowUpDown = true,
                    Location = new System.Drawing.Point(90, 79),
                    Size = new System.Drawing.Size(160, 20),
                    Value = DateTime.Today.Add(rebootTime),
                    BackColor = System.Drawing.Color.White
                };

                // Track the pre-fuzzy time so unchecking restores the original picker value
                TimeSpan preFuzzyTime = rebootTime;
                CheckBox fuzzyTimeCheck = new CheckBox
                {
                    Text = "Fuzzy Time",
                    Location = new System.Drawing.Point(264, 81),
                    AutoSize = true,
                    Checked = false,
                    BackColor = System.Drawing.Color.FromArgb(245, 246, 248)
                };
                ToolTip fuzzyTimeToolTip = new ToolTip();
                fuzzyTimeToolTip.SetToolTip(fuzzyTimeCheck, "Randomises the reboot time within \u00b15 minutes of the set time. Unchecking restores the original time.");
                fuzzyTimeCheck.CheckedChanged += (s, e) =>
                {
                    if (fuzzyTimeCheck.Checked)
                    {
                        preFuzzyTime = timePicker.Value.TimeOfDay;
                        Random rand = new Random();
                        int secondsOffset = rand.Next(-300, 301);
                        TimeSpan newTime = preFuzzyTime.Add(new TimeSpan(0, 0, secondsOffset));
                        if (newTime < TimeSpan.Zero) newTime = TimeSpan.Zero;
                        if (newTime >= TimeSpan.FromHours(24)) newTime = new TimeSpan(23, 59, 0);
                        timePicker.Value = DateTime.Today.Add(newTime);
                    }
                    else
                    {
                        timePicker.Value = DateTime.Today.Add(preFuzzyTime);
                    }
                };

                Label daysLabel = new Label
                {
                    Text = "Days",
                    Location = new System.Drawing.Point(14, 107),
                    AutoSize = true,
                    ForeColor = System.Drawing.Color.FromArgb(108, 117, 125),
                    Font = new System.Drawing.Font("Segoe UI", 8F)
                };
                CheckBox monCheck = new CheckBox { Text = "M",  Location = new System.Drawing.Point(90,  107), Size = new System.Drawing.Size(32, 20), Checked = selectedDays.Contains("Monday"),    BackColor = System.Drawing.Color.FromArgb(245, 246, 248) };
                CheckBox tueCheck = new CheckBox { Text = "T",  Location = new System.Drawing.Point(123, 107), Size = new System.Drawing.Size(32, 20), Checked = selectedDays.Contains("Tuesday"),   BackColor = System.Drawing.Color.FromArgb(245, 246, 248) };
                CheckBox wedCheck = new CheckBox { Text = "W",  Location = new System.Drawing.Point(156, 107), Size = new System.Drawing.Size(32, 20), Checked = selectedDays.Contains("Wednesday"), BackColor = System.Drawing.Color.FromArgb(245, 246, 248) };
                CheckBox thuCheck = new CheckBox { Text = "Th", Location = new System.Drawing.Point(189, 107), Size = new System.Drawing.Size(36, 20), Checked = selectedDays.Contains("Thursday"),  BackColor = System.Drawing.Color.FromArgb(245, 246, 248) };
                CheckBox friCheck = new CheckBox { Text = "F",  Location = new System.Drawing.Point(226, 107), Size = new System.Drawing.Size(32, 20), Checked = selectedDays.Contains("Friday"),    BackColor = System.Drawing.Color.FromArgb(245, 246, 248) };
                CheckBox satCheck = new CheckBox { Text = "Sa", Location = new System.Drawing.Point(259, 107), Size = new System.Drawing.Size(36, 20), Checked = selectedDays.Contains("Saturday"),  BackColor = System.Drawing.Color.FromArgb(245, 246, 248) };
                CheckBox sunCheck = new CheckBox { Text = "Su", Location = new System.Drawing.Point(296, 107), Size = new System.Drawing.Size(36, 20), Checked = selectedDays.Contains("Sunday"),    BackColor = System.Drawing.Color.FromArgb(245, 246, 248) };

                bool allDays = selectedDays.Contains("Monday") && selectedDays.Contains("Tuesday") &&
                               selectedDays.Contains("Wednesday") && selectedDays.Contains("Thursday") &&
                               selectedDays.Contains("Friday") && selectedDays.Contains("Saturday") &&
                               selectedDays.Contains("Sunday");
                CheckBox allCheck = new CheckBox
                {
                    Text = "All",
                    Location = new System.Drawing.Point(340, 107),
                    Size = new System.Drawing.Size(46, 20),
                    Checked = allDays,
                    BackColor = System.Drawing.Color.FromArgb(245, 246, 248)
                };

                ToolTip dayToolTip = new ToolTip();
                dayToolTip.SetToolTip(monCheck, "Monday");
                dayToolTip.SetToolTip(tueCheck, "Tuesday");
                dayToolTip.SetToolTip(wedCheck, "Wednesday");
                dayToolTip.SetToolTip(thuCheck, "Thursday");
                dayToolTip.SetToolTip(friCheck, "Friday");
                dayToolTip.SetToolTip(satCheck, "Saturday");
                dayToolTip.SetToolTip(sunCheck, "Sunday");
                dayToolTip.SetToolTip(allCheck, "Check or uncheck all days");

                // ── NETWORK section ───────────────────────────────────────────────────────
                Label networkSectionLabel = new Label
                {
                    Text = "NETWORK",
                    Location = new System.Drawing.Point(14, 133),
                    AutoSize = true,
                    ForeColor = System.Drawing.Color.FromArgb(108, 117, 125),
                    Font = new System.Drawing.Font("Segoe UI", 7.5F, System.Drawing.FontStyle.Bold)
                };

                Panel networkDivider = new Panel
                {
                    Location = new System.Drawing.Point(14, 148),
                    Size = new System.Drawing.Size(472, 1),
                    BackColor = System.Drawing.Color.FromArgb(222, 226, 230)
                };

                Label serverLabel = new Label
                {
                    Text = "Server",
                    Location = new System.Drawing.Point(14, 158),
                    AutoSize = true,
                    ForeColor = System.Drawing.Color.FromArgb(108, 117, 125),
                    Font = new System.Drawing.Font("Segoe UI", 8F)
                };
                TextBox serverBox = new TextBox
                {
                    Location = new System.Drawing.Point(90, 155),
                    Size = new System.Drawing.Size(170, 20),
                    Text = pingServer,
                    BackColor = System.Drawing.Color.White
                };

                Button testButton = new Button
                {
                    Text = "Test Server",
                    Location = new System.Drawing.Point(272, 153),
                    Size = new System.Drawing.Size(90, 24),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = System.Drawing.Color.FromArgb(0, 120, 215),
                    ForeColor = System.Drawing.Color.White
                };

                Label pingResultLabel = new Label
                {
                    Text = "",
                    Location = new System.Drawing.Point(370, 157),
                    Size = new System.Drawing.Size(110, 16),
                    Font = new System.Drawing.Font("Segoe UI", 8F),
                    BackColor = System.Drawing.Color.FromArgb(245, 246, 248)
                };

                testButton.Click += (s, e) =>
                {
                    string host = serverBox.Text.Trim();
                    if (string.IsNullOrEmpty(host))
                    {
                        pingResultLabel.Text = "No host entered";
                        pingResultLabel.ForeColor = System.Drawing.Color.FromArgb(255, 160, 64);
                        return;
                    }
                    testButton.Enabled = false;
                    testButton.Text = "Testing...";
                    bool pingSuccess = PingServer(host);
                    testButton.Text = "Test Server";
                    testButton.Enabled = true;
                    pingResultLabel.Text = pingSuccess ? "Ping Successful!" : "Ping Failed!";
                    pingResultLabel.ForeColor = pingSuccess
                        ? System.Drawing.Color.FromArgb(72, 199, 116)
                        : System.Drawing.Color.FromArgb(220, 53, 69);
                };

                CheckBox pingCheck = new CheckBox
                {
                    Text = "Don't reboot if ping fails (retries after 2 min)",
                    Location = new System.Drawing.Point(90, 182),
                    Size = new System.Drawing.Size(340, 20),
                    Checked = dontRebootIfPingFails,
                    BackColor = System.Drawing.Color.FromArgb(245, 246, 248)
                };

                // ── SYSTEM section ────────────────────────────────────────────────────────
                Label systemSectionLabel = new Label
                {
                    Text = "SYSTEM",
                    Location = new System.Drawing.Point(14, 207),
                    AutoSize = true,
                    ForeColor = System.Drawing.Color.FromArgb(108, 117, 125),
                    Font = new System.Drawing.Font("Segoe UI", 7.5F, System.Drawing.FontStyle.Bold)
                };

                Panel systemDivider = new Panel
                {
                    Location = new System.Drawing.Point(14, 222),
                    Size = new System.Drawing.Size(472, 1),
                    BackColor = System.Drawing.Color.FromArgb(222, 226, 230)
                };

                CheckBox startupCheck = new CheckBox
                {
                    Text = "Run at system startup",
                    Location = new System.Drawing.Point(90, 230),
                    Size = new System.Drawing.Size(290, 20),
                    Checked = initialRunAtStartup,
                    BackColor = System.Drawing.Color.FromArgb(245, 246, 248)
                };

                // ── Button area ───────────────────────────────────────────────────────────
                Panel buttonDivider = new Panel
                {
                    Location = new System.Drawing.Point(0, 262),
                    Size = new System.Drawing.Size(500, 1),
                    BackColor = System.Drawing.Color.FromArgb(222, 226, 230)
                };

                Button saveButton = new Button
                {
                    Name = "saveButton",
                    Text = "Save",
                    Location = new System.Drawing.Point(90, 270),
                    Size = new System.Drawing.Size(100, 28),
                    FlatStyle = FlatStyle.Flat,
                    Enabled = isFirstRun
                };
                saveButton.BackColor = isFirstRun ? System.Drawing.Color.FromArgb(0, 120, 215) : System.Drawing.Color.LightGray;
                saveButton.ForeColor = isFirstRun ? System.Drawing.Color.White : System.Drawing.Color.Gray;
                saveButton.EnabledChanged += (s, e) =>
                {
                    saveButton.BackColor = saveButton.Enabled ? System.Drawing.Color.FromArgb(0, 120, 215) : System.Drawing.Color.LightGray;
                    saveButton.ForeColor = saveButton.Enabled ? System.Drawing.Color.White : System.Drawing.Color.Gray;
                };

                Button resetButton = new Button
                {
                    Name = "resetButton",
                    Text = "Reset",
                    Location = new System.Drawing.Point(206, 270),
                    Size = new System.Drawing.Size(100, 28),
                    FlatStyle = FlatStyle.Flat,
                    Enabled = !isFirstRun
                };
                resetButton.BackColor = !isFirstRun ? System.Drawing.Color.FromArgb(220, 53, 69) : System.Drawing.Color.LightGray;
                resetButton.ForeColor = !isFirstRun ? System.Drawing.Color.White : System.Drawing.Color.Gray;
                resetButton.EnabledChanged += (s, e) =>
                {
                    resetButton.BackColor = resetButton.Enabled ? System.Drawing.Color.FromArgb(220, 53, 69) : System.Drawing.Color.LightGray;
                    resetButton.ForeColor = resetButton.Enabled ? System.Drawing.Color.White : System.Drawing.Color.Gray;
                };

                // ── Footer divider ────────────────────────────────────────────────────────
                Panel footerDivider = new Panel
                {
                    Location = new System.Drawing.Point(0, 306),
                    Size = new System.Drawing.Size(500, 1),
                    BackColor = System.Drawing.Color.FromArgb(222, 226, 230)
                };

                // ── Button click handlers ─────────────────────────────────────────────────
                saveButton.Click += (s, e) =>
                {
                    string[] newDays = new string[] {
                        monCheck.Checked ? "Monday"    : null,
                        tueCheck.Checked ? "Tuesday"   : null,
                        wedCheck.Checked ? "Wednesday" : null,
                        thuCheck.Checked ? "Thursday"  : null,
                        friCheck.Checked ? "Friday"    : null,
                        satCheck.Checked ? "Saturday"  : null,
                        sunCheck.Checked ? "Sunday"    : null
                    }.Where(d => d != null).ToArray();

                    if (newDays.Length == 0)
                    {
                        MessageBox.Show("Please select at least one reboot day.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    string newServer = serverBox.Text.Trim();
                    if (string.IsNullOrEmpty(newServer))
                    {
                        MessageBox.Show("Please enter a ping server address.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    rebootTime = timePicker.Value.TimeOfDay;
                    pingServer = newServer;
                    dontRebootIfPingFails = pingCheck.Checked;
                    selectedDays = newDays;

                    try
                    {
                        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath))
                        {
                            key.SetValue("RebootTime", rebootTime.ToString(RebootTimeFormat));
                            key.SetValue("PingServer", pingServer);
                            key.SetValue("DontRebootIfPingFails", dontRebootIfPingFails ? 1 : 0, RegistryValueKind.DWord);
                            key.SetValue("SelectedDays", string.Join(",", selectedDays));
                        }
                    }
                    catch (Exception)
                    {
                        string state = rebootTime.ToString(RebootTimeFormat) + ";" + pingServer + ";" + dontRebootIfPingFails + ";" + startupCheck.Checked + ";" + string.Join(",", selectedDays);
                        if (PromptForElevation())
                        {
                            RelaunchAsAdmin(state);
                            return;
                        }
                        MessageBox.Show("Unable to save settings to registry. Run as administrator to save.", "Registry Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    SetRunAtStartup(startupCheck.Checked);
                    isFirstRun = false;
                    rebootActive = true;
                    UpdateDialogTitle();
                    saveButton.Enabled = false;
                    resetButton.Enabled = true;
                    settingsForm.Close();
                };

                resetButton.Click += (s, e) =>
                {
                    if (MessageBox.Show("Are you sure you want to reset all settings? This will remove all traces except the exe file.", "Confirm Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    {
                        try
                        {
                            Registry.LocalMachine.DeleteSubKeyTree(RegistryPath, false);
                        }
                        catch (Exception)
                        {
                            if (PromptForElevation())
                            {
                                RelaunchAsAdmin("reset");
                                return;
                            }
                            MessageBox.Show("Unable to delete registry key. Run as administrator to reset.", "Registry Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }

                        SetRunAtStartup(false);

                        rebootTime = new TimeSpan(6, 0, 0);
                        pingServer = "google.com";
                        dontRebootIfPingFails = false;
                        selectedDays = new string[] { };
                        isFirstRun = true;
                        rebootActive = false;

                        timePicker.Value = DateTime.Today.Add(rebootTime);
                        serverBox.Text = pingServer;
                        pingCheck.Checked = dontRebootIfPingFails;
                        startupCheck.Checked = false;
                        allCheck.Checked = false;
                        monCheck.Checked = false;
                        tueCheck.Checked = false;
                        wedCheck.Checked = false;
                        thuCheck.Checked = false;
                        friCheck.Checked = false;
                        satCheck.Checked = false;
                        sunCheck.Checked = false;
                        pingResultLabel.Text = "";
                        saveButton.Enabled = true;
                        resetButton.Enabled = false;

                        UpdateDialogTitle();
                    }
                };

                // Change detection to enable Save button
                Action CheckForChanges = () =>
                {
                    if (isFirstRun) return;
                    bool changed = (timePicker.Value.TimeOfDay != initialRebootTime) ||
                                   (serverBox.Text.Trim() != initialPingServer) ||
                                   (pingCheck.Checked != initialDontRebootIfPingFails) ||
                                   (startupCheck.Checked != initialRunAtStartup) ||
                                   !initialSelectedDays.SequenceEqual(new string[] {
                                       monCheck.Checked ? "Monday"    : null,
                                       tueCheck.Checked ? "Tuesday"   : null,
                                       wedCheck.Checked ? "Wednesday" : null,
                                       thuCheck.Checked ? "Thursday"  : null,
                                       friCheck.Checked ? "Friday"    : null,
                                       satCheck.Checked ? "Saturday"  : null,
                                       sunCheck.Checked ? "Sunday"    : null
                                   }.Where(d => d != null).ToArray());
                    saveButton.Enabled = changed;
                };

                // Reentrancy guard: prevents allCheck and day checkboxes from triggering each other.
                bool allCheckUpdating = false;

                Action UpdateAllCheck = () =>
                {
                    if (allCheckUpdating) return;
                    allCheckUpdating = true;
                    allCheck.Checked = monCheck.Checked && tueCheck.Checked && wedCheck.Checked &&
                                       thuCheck.Checked && friCheck.Checked && satCheck.Checked && sunCheck.Checked;
                    allCheckUpdating = false;
                };

                allCheck.CheckedChanged += (s, e) =>
                {
                    if (allCheckUpdating) return;
                    allCheckUpdating = true;
                    bool val = allCheck.Checked;
                    monCheck.Checked = val;
                    tueCheck.Checked = val;
                    wedCheck.Checked = val;
                    thuCheck.Checked = val;
                    friCheck.Checked = val;
                    satCheck.Checked = val;
                    sunCheck.Checked = val;
                    allCheckUpdating = false;
                    CheckForChanges();
                };

                timePicker.ValueChanged     += (s, e) => CheckForChanges();
                serverBox.TextChanged       += (s, e) => CheckForChanges();
                pingCheck.CheckedChanged    += (s, e) => CheckForChanges();
                startupCheck.CheckedChanged += (s, e) => CheckForChanges();
                monCheck.CheckedChanged     += (s, e) => { UpdateAllCheck(); CheckForChanges(); };
                tueCheck.CheckedChanged     += (s, e) => { UpdateAllCheck(); CheckForChanges(); };
                wedCheck.CheckedChanged     += (s, e) => { UpdateAllCheck(); CheckForChanges(); };
                thuCheck.CheckedChanged     += (s, e) => { UpdateAllCheck(); CheckForChanges(); };
                friCheck.CheckedChanged     += (s, e) => { UpdateAllCheck(); CheckForChanges(); };
                satCheck.CheckedChanged     += (s, e) => { UpdateAllCheck(); CheckForChanges(); };
                sunCheck.CheckedChanged     += (s, e) => { UpdateAllCheck(); CheckForChanges(); };

                settingsForm.Controls.AddRange(new Control[] {
                    headerPanel,
                    scheduleSectionLabel, scheduleDivider,
                    timeLabel, timePicker, fuzzyTimeCheck,
                    daysLabel, monCheck, tueCheck, wedCheck, thuCheck, friCheck, satCheck, sunCheck, allCheck,
                    networkSectionLabel, networkDivider,
                    serverLabel, serverBox, testButton, pingResultLabel, pingCheck,
                    systemSectionLabel, systemDivider,
                    startupCheck,
                    buttonDivider, saveButton, resetButton,
                    footerDivider
                });

                settingsForm.FormClosing += (s, e) =>
                {
                    if (!rebootActive)
                    {
                        OnExit(s, e);
                    }
                    else
                    {
                        e.Cancel = true;
                        settingsForm.Hide();
                    }
                };
            }

            UpdateDialogTitle();

            Button sb = settingsForm.Controls["saveButton"] as Button;
            if (sb != null)
            {
                sb.Enabled = isFirstRun || sb.Enabled;
                sb.BackColor = sb.Enabled ? System.Drawing.Color.FromArgb(0, 120, 215) : System.Drawing.Color.LightGray;
                sb.ForeColor = sb.Enabled ? System.Drawing.Color.White : System.Drawing.Color.Gray;
            }
            Button rb = settingsForm.Controls["resetButton"] as Button;
            if (rb != null)
            {
                rb.Enabled = !isFirstRun;
                rb.BackColor = rb.Enabled ? System.Drawing.Color.FromArgb(220, 53, 69) : System.Drawing.Color.LightGray;
                rb.ForeColor = rb.Enabled ? System.Drawing.Color.White : System.Drawing.Color.Gray;
            }

            settingsForm.Show();
            settingsForm.Activate();
            UpdateVersionStatus();
        }

        private Control FindControlRecursive(Control root, string name)
        {
            if (root.Name == name) return root;
            foreach (Control child in root.Controls)
            {
                Control found = FindControlRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private void UpdateDialogTitle()
        {
            if (settingsForm == null || settingsForm.IsDisposed) return;
            settingsForm.Text = "Reboot Utility";
            Control lbl = FindControlRecursive(settingsForm, "headerStatusLabel");
            if (lbl != null)
            {
                lbl.Text = rebootActive ? "\u25cf Schedule Active" : "\u25cf Schedule Inactive";
                lbl.ForeColor = rebootActive ? System.Drawing.Color.FromArgb(72, 199, 116) : System.Drawing.Color.FromArgb(255, 160, 64);
            }
        }

        private bool IsRunAtStartup()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key != null)
                    {
                        string value = (string)key.GetValue(Application.ProductName);
                        // OrdinalIgnoreCase: Windows paths are case-insensitive; avoid false negative
                        // if the stored path differs only in drive-letter casing after an update.
                        return value != null && value.Equals(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                }
            }
            catch (Exception)
            {
                // FIX: Silently return false instead of showing a popup during form initialisation
                return false;
            }
        }

        private void SetRunAtStartup(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (enable)
                        key.SetValue(Application.ProductName, Application.ExecutablePath);
                    else
                        key.DeleteValue(Application.ProductName, false);
                }
            }
            catch (Exception)
            {
                MessageBox.Show("Unable to modify startup registry.", "Registry Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private bool PromptForElevation()
        {
            return MessageBox.Show("Administrator privileges are required for this action. Relaunch as admin?", "Elevation Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        private void RelaunchAsAdmin(string state = null)
        {
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = Assembly.GetExecutingAssembly().Location,
                    Verb = "runas",
                    // FIX: Quote the argument so spaces in ping server hostname don't corrupt arg[0] parsing
                    Arguments = state != null ? "\"" + state + "\"" : ""
                };
                Process.Start(info);
                OnExit(null, null);
            }
            catch (Exception)
            {
                MessageBox.Show("Failed to relaunch as administrator.", "Elevation Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CheckReboot(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!rebootActive) return;

            DateTime now = DateTime.Now;

            // Pending retry: first ping failed at reboot time; try once more after 2 minutes.
            if (pingRetryPending)
            {
                if (now >= pingRetryTime)
                {
                    pingRetryPending = false;
                    if (PingServer())
                    {
                        lastRebootFired = now;
                        Process.Start("shutdown", "/r /f /t 0");
                    }
                    // Second ping also failed — skip reboot for today.
                }
                return; // Still waiting, or retry just handled above.
            }

            string currentDay = now.DayOfWeek.ToString();
            if (!selectedDays.Contains(currentDay)) return;

            if (now.TimeOfDay.Hours == rebootTime.Hours && now.TimeOfDay.Minutes == rebootTime.Minutes)
            {
                // Guard against double-fire — timer drift can land in the same minute twice.
                if (lastRebootFired.Date == now.Date &&
                    lastRebootFired.Hour == now.Hour &&
                    lastRebootFired.Minute == now.Minute)
                    return;

                // Consume this reboot slot now so the guard above blocks any same-minute re-entry.
                lastRebootFired = now;

                if (dontRebootIfPingFails && !PingServer())
                {
                    pingRetryPending = true;
                    pingRetryTime = now.AddMinutes(2);
                    return;
                }

                Process.Start("shutdown", "/r /f /t 0");
            }
        }

        private bool PingServer()
        {
            return PingServer(pingServer);
        }

        private bool PingServer(string server)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = ping.Send(server, PingTimeoutMs);
                    return reply.Status == IPStatus.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            Visible = false;
            ShowInTaskbar = false;
            base.OnLoad(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (listenerCts != null) listenerCts.Cancel();
                if (listenerCts != null) listenerCts.Dispose();
                if (trayIcon != null) trayIcon.Dispose();
                if (timer != null) timer.Dispose();
                if (updateTimer != null) updateTimer.Dispose();
                if (showSettingsEvent != null) showSettingsEvent.Dispose();
                if (settingsForm != null) settingsForm.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
