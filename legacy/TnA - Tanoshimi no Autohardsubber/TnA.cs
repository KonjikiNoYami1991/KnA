using FFmpeg_Output_Wrapper;
using IWshRuntimeLibrary;
using MediaInfoDotNet;
using MediaInfoLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TnA___Tanoshimi_no_Autohardsubber
{
    public partial class TnA : Form
    {
        readonly String GUIdir = Path.GetDirectoryName(Application.ExecutablePath);

        readonly String LOG_dir = Path.GetDirectoryName(Application.ExecutablePath) + "\\LOGS";

        readonly String SettingsFile = Path.GetDirectoryName(Application.ExecutablePath) + "\\settings\\settings.ini";

        readonly String ffmpeg = Path.GetDirectoryName(Application.ExecutablePath) + "\\x64\\ffmpeg_tna_x64.exe";

        readonly String mkvmerge = Path.GetDirectoryName(Application.ExecutablePath) + "\\x64\\mkvmerge_tna_x64.exe";

        readonly String scxvid = Path.GetDirectoryName(Application.ExecutablePath) + "\\x64\\scxvid_tna_x64.exe";

        readonly String temp_folder = Path.GetDirectoryName(Application.ExecutablePath) + "\\temp";
        readonly String fonts_folder = Path.GetDirectoryName(Application.ExecutablePath) + "\\x64\\fonts_v";

        String Filter = "Supported video files|";

        String TabFilelistLabel = "File list";

        String WhenAllIsFinished = "Do nothing";

        readonly String[] VideoEXT = { ".mkv", ".mp4", ".m2ts", ".ts", ".avi", ".mov", ".rmvb", ".ogm", ".flv", ".vob", ".mpg", ".mpeg", ".3gp", ".m4v" };

        //readonly String[] estensioni_audio = { ".m4a", ".flac", ".wav", ".aac", ".ogg", ".opus", ".tta", ".ac3", ".dts", ".mp3", ".mka" };

        String Arguments = String.Empty;

        String Duration = String.Empty;

        String fc = String.Empty;

        Boolean pause = false;

        public static Boolean ForceClose = false;

        String EncodingFile = "None";

        String FinalFile = String.Empty;

        String TnAVersion = String.Empty;

        public static DateTime LastUpdDate = new DateTime();

        List<String> ExtensionToImport = new List<String>();

        public static List<String> StatusToReset = new List<String>();

        String OldDate = String.Empty;

        Int32 PercentageIndex = 0, exit_code = Int32.MinValue, ElapsedSeconds = 0;

        Thread t;
        ThreadStart ts;

        System.Diagnostics.Process EncodeProcess = new System.Diagnostics.Process();
        System.Diagnostics.Process RemuxProcess = new System.Diagnostics.Process();

        DownloadFFMPEG df;

        [Flags]
        public enum ThreadAccess : int
        {
            TERMINATE = (0x0001),
            SUSPEND_RESUME = (0x0002),
            GET_CONTEXT = (0x0008),
            SET_CONTEXT = (0x0010),
            SET_INFORMATION = (0x0020),
            QUERY_INFORMATION = (0x0040),
            SET_THREAD_TOKEN = (0x0080),
            IMPERSONATE = (0x0100),
            DIRECT_IMPERSONATION = (0x0200)
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
        [DllImport("kernel32.dll")]
        static extern uint SuspendThread(IntPtr hThread);
        [DllImport("kernel32.dll")]
        static extern int ResumeThread(IntPtr hThread);
        [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        public TnA()
        {

            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US", false);
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US", false);
            InitializeComponent();

            //MessageBox.Show(SystemInformation.PowerStatus.PowerLineStatus.ToString());

            var screen = Screen.FromPoint(MousePosition);
            this.StartPosition = FormStartPosition.Manual;
            this.Left = screen.Bounds.Left + screen.Bounds.Width / 2 - this.Width / 2;
            this.Top = screen.Bounds.Top + screen.Bounds.Height / 2 - this.Height / 2;

            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            cmb_profile.Items.Clear();
            cmb_quality.Items.Clear();
            cmb_resolution.Items.Clear();
            cmb_subs.Items.Clear();

            for (Int32 i = 0; i < profile.Items.Count; i++)
            {
                cmb_profile.Items.Add(profile.Items[i].ToString());
            }
            for (Int32 i = 0; i < quality.Items.Count; i++)
            {
                cmb_quality.Items.Add(quality.Items[i].ToString());
            }
            for (Int32 i = 0; i < res.Items.Count; i++)
            {
                cmb_resolution.Items.Add(res.Items[i].ToString());
            }
            for (Int32 i = 0; i < subtitle_mode.Items.Count; i++)
            {
                cmb_subs.Items.Add(subtitle_mode.Items[i].ToString());
            }

            sc_log.Panel2Collapsed = true;

            if (System.IO.File.Exists((Application.StartupPath + "\\TnA.7z")))
                System.IO.File.Delete(Application.StartupPath + "\\TnA.7z");

            foreach (String s in VideoEXT)
            {
                Filter += "*" + s.ToLower() + ";" + "*" + s.ToUpper() + ";";
            }
            Filter = Filter.Trim(';');
            Filter += "|";
            foreach (String s in VideoEXT)
            {
                Filter += "File " + s.Trim('.').ToUpper() + "|*" + s.ToLower() + ";" + "*" + s.ToUpper() + "|";
            }
            Filter = Filter.Trim('|');
            STOP();
            ActionWhenFinishedToolStripComboBox.Text = "Do nothing";
            cmb_subs.Text = "Hardsub";

            TnAVersion = this.Text.Split(' ')[5].Replace("v", String.Empty);

            nextClipboardViewer = (IntPtr)SetClipboardViewer((int)this.Handle);
            AboutToolStripMenuItem.Image = SystemIcons.Information.ToBitmap();

            if (Directory.Exists(LOG_dir) == false)
                Directory.CreateDirectory(LOG_dir);

            if (Directory.Exists(Path.GetDirectoryName(SettingsFile)) == false)
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));

            if (!Directory.Exists(temp_folder))
            {
                Directory.CreateDirectory(temp_folder);
            }
            else
            {
                foreach (String s in Directory.GetFiles(temp_folder))
                    System.IO.File.Delete(s);
            }

            if (Directory.Exists(fonts_folder))
            {
                Directory.Delete(fonts_folder, true);
            }
            if (!System.IO.File.Exists(ffmpeg))
            {
                this.Enabled = false;
                bgw_downloadffmpeg.RunWorkerAsync();
            }

            switch (IntPtr.Size)
            {
                case 8:
                    this.Text += " - x64 Build";
                    break;
                default:
                    this.Text += " - x86 Build";
                    break;
            }

            ReadSettings(SettingsFile);

            b_start.Enabled = false;
            this.Activate();
            if (Environment.GetCommandLineArgs().Count() > 1)
            {
                List<String> d = new List<String>();
                for (Int32 i = 1; i < Environment.GetCommandLineArgs().Count(); i++)
                    d.Add(Environment.GetCommandLineArgs()[i]);
                RetriveFiles(d.ToArray());
            }

            bgw_updateschecker.RunWorkerAsync();
        }


        private static void SuspendProcess(int pid)
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);

            if (process.ProcessName == string.Empty)
                return;

            foreach (System.Diagnostics.ProcessThread pT in process.Threads)
            {
                IntPtr pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                if (pOpenThread == IntPtr.Zero)
                {
                    continue;
                }

                SuspendThread(pOpenThread);

                CloseHandle(pOpenThread);
            }
        }

        public static void ResumeProcess(int pid)
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);

            if (process.ProcessName == string.Empty)
                return;

            foreach (System.Diagnostics.ProcessThread pT in process.Threads)
            {
                IntPtr pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

                if (pOpenThread == IntPtr.Zero)
                {
                    continue;
                }

                var suspendCount = 0;
                do
                {
                    suspendCount = ResumeThread(pOpenThread);
                } while (suspendCount > 0);

                CloseHandle(pOpenThread);
            }
        }



        [DllImport("User32.dll")]
        protected static extern int SetClipboardViewer(int hWndNewViewer);

        [DllImport("User32.dll", CharSet = CharSet.Auto)]
        public static extern bool ChangeClipboardChain(IntPtr hWndRemove, IntPtr hWndNewNext);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int SendMessage(IntPtr hwnd, int wMsg, IntPtr wParam, IntPtr lParam);

        IntPtr nextClipboardViewer;

        public void ReadSettings_WindowsState(String Value)
        {
            switch (Value)
            {
                case "Maximized":
                    this.WindowState = FormWindowState.Maximized;
                    break;
                default:
                    this.WindowState = FormWindowState.Normal;
                    break;
                case "Minimized":
                    this.WindowState = FormWindowState.Minimized;
                    break;
            }
        }

        public void ReadSettings_WindowWidth(String Value)
        {
            try
            {
                if (Convert.ToInt32(Value) >= 943)
                    this.Width = Convert.ToInt32(Value);
            }
            catch { }
        }

        public void ReadSettings_WindowHeight(String Value)
        {
            try
            {
                if (Convert.ToInt32(Value) >= 622)
                    this.Height = Convert.ToInt32(Value);
            }
            catch { }
        }

        public void ReadSettings_Profile(String Value)
        {
            if (cmb_profile.Items.Contains(Value))
                cmb_profile.Text = Value;
            else
                cmb_profile.Text = "Bluray AAC";
        }

        public void ReadSettings_Subtitle(String Value)
        {
            if (cmb_subs.Items.Contains(Value))
                cmb_subs.Text = Value;
            else
                cmb_subs.Text = "Hardsub";
        }

        public void ReadSettings_Resolution(String Value)
        {
            if (cmb_resolution.Items.Contains(Value))
                cmb_resolution.Text = Value;
            else
                cmb_resolution.Text = "720p";
        }

        public void ReadSettings_Quality(String Value)
        {
            if (cmb_quality.Items.Contains(Value))
                cmb_quality.Text = Value;
            else
                cmb_quality.Text = "Normal";
        }

        public Int32 ReadSettings_DataUpd(String Value)
        {
            Int32 Freq;
            switch (Value)
            {
                case "m":
                    manualToolStripMenuItem.CheckState = CheckState.Checked;
                    EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryDayToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryThreeDaysToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryWeekToolStripMenuItem.CheckState = CheckState.Unchecked;
                    Freq = -1;
                    break;
                case "s":
                    manualToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Checked;
                    EveryDayToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryThreeDaysToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryWeekToolStripMenuItem.CheckState = CheckState.Unchecked;
                    Freq = 0;
                    break;
                case "1":
                    manualToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryDayToolStripMenuItem.CheckState = CheckState.Checked;
                    EveryThreeDaysToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryWeekToolStripMenuItem.CheckState = CheckState.Unchecked;
                    Freq = 1;
                    break;
                case "3":
                    manualToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryDayToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryThreeDaysToolStripMenuItem.CheckState = CheckState.Checked;
                    EveryWeekToolStripMenuItem.CheckState = CheckState.Unchecked;
                    Freq = 3;
                    break;
                case "7":
                    manualToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryDayToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryThreeDaysToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryWeekToolStripMenuItem.CheckState = CheckState.Checked;
                    Freq = 7;
                    break;
                default:
                    manualToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Checked;
                    EveryDayToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryThreeDaysToolStripMenuItem.CheckState = CheckState.Unchecked;
                    EveryWeekToolStripMenuItem.CheckState = CheckState.Unchecked;
                    Freq = 0;
                    break;
            }
            return Freq;
        }

        public void ReadSettings_Clipboard(String Value)
        {
            if (Value == "y")
                ActivateClipboardMonitoringToolStripMenuItem.CheckState = CheckState.Checked;
            else
                ActivateClipboardMonitoringToolStripMenuItem.CheckState = CheckState.Unchecked;
        }

        public void ReadSettings(String FileINI)
        {
            if (System.IO.File.Exists(SettingsFile))
            {
                IniFile ini = new IniFile(SettingsFile);

                if (ini.KeyExists("WindowState"))
                {
                    ReadSettings_WindowsState(ini.Read("WindowState"));
                }

                if (this.WindowState != FormWindowState.Minimized && this.WindowState != FormWindowState.Maximized)
                {
                    if (ini.KeyExists("Width"))
                        ReadSettings_WindowWidth(ini.Read("Width"));
                    if (ini.KeyExists("Height"))
                        ReadSettings_WindowHeight(ini.Read("Height"));
                }

                if (ini.KeyExists("profile"))
                {
                    ReadSettings_Profile(ini.Read("profile"));
                }
                else
                    cmb_profile.Text = "Bluray AAC";

                if (ini.KeyExists("qual"))
                {
                    ReadSettings_Quality(ini.Read("qual"));
                }
                else
                    cmb_quality.Text = "Normal";

                if (ini.KeyExists("res"))
                {
                    ReadSettings_Resolution(ini.Read("res"));
                }
                else
                    cmb_resolution.Text = "720p";

                if (ini.KeyExists("subs"))
                {
                    ReadSettings_Subtitle(ini.Read("subs"));
                }
                else
                    cmb_subs.Text = "Hardsub";

                if (ini.KeyExists("last_upd"))
                {
                    OldDate = ini.Read("last_upd");
                    Int32 fr = -2;
                    if (ini.KeyExists("upd_freq"))
                    {
                        fr = ReadSettings_DataUpd(ini.Read("upd_freq"));
                    }

                    if (fr != -2)
                    {
                        if (fr == 0)
                        {
                            this.Enabled = false;
                            bgw_update.RunWorkerAsync();
                        }
                        if (fr >= 1)
                        {
                            DateTime Date = DateTime.Now;
                            try
                            {
                                DateTime dt = DateTime.ParseExact(OldDate, "dd/MM/yyyy", new CultureInfo("en-US"));
                                TimeSpan diff = Date - dt;
                                if (diff.Days >= fr)
                                {
                                    this.Enabled = false;
                                    bgw_update.RunWorkerAsync();
                                }
                            }
                            catch
                            {
                                EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Checked;
                            }
                        }
                    }
                    else
                    {
                        this.Enabled = false;
                        bgw_update.RunWorkerAsync();
                    }
                }
                if (ini.KeyExists("monit_clipb") == true)
                {
                    ReadSettings_Clipboard(ini.Read("monit_clipb"));
                }
            }
            else
            {
                cmb_profile.Text = "Bluray AAC";
                cmb_resolution.Text = "720p";
                cmb_quality.Text = "Normal";
            }
        }

        protected override void WndProc(ref Message m)
        {
            // defined in winuser.h
            const int WM_DRAWCLIPBOARD = 0x308;
            const int WM_CHANGECBCHAIN = 0x030D;

            switch (m.Msg)
            {
                case WM_DRAWCLIPBOARD:
                    DisplayClipboardData();
                    SendMessage(nextClipboardViewer, m.Msg, m.WParam, m.LParam);
                    break;

                case WM_CHANGECBCHAIN:
                    if (m.WParam == nextClipboardViewer)
                        nextClipboardViewer = m.LParam;
                    else
                        SendMessage(nextClipboardViewer, m.Msg, m.WParam, m.LParam);
                    break;

                default:
                    base.WndProc(ref m);
                    break;
            }
        }

        void DisplayClipboardData()
        {
            try
            {
                if (ActivateClipboardMonitoringToolStripMenuItem.CheckState == CheckState.Checked && ActivateClipboardMonitoringToolStripMenuItem.Enabled == true)
                {
                    System.Collections.Specialized.StringCollection coll = Clipboard.GetFileDropList();
                    String[] f = new string[coll.Count];
                    coll.CopyTo(f, 0);
                    RetriveFiles(f);
                }
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString());
            }
        }

        private void DGV_video_DragEnter(object sender, DragEventArgs e)
        {
            if (DGV_video.ReadOnly == false)
            {
                String[] dati = (String[])e.Data.GetData(DataFormats.FileDrop);
                foreach (String s in dati)
                {
                    if (Path.HasExtension(s) == true)
                    {
                        if (VideoEXT.Contains(Path.GetExtension(s).ToLower()) == true)
                        {
                            e.Effect = DragDropEffects.Copy;
                            break;
                        }
                    }
                    else
                    {
                        foreach (String t in Directory.GetFiles(s, "*", SearchOption.AllDirectories))
                        {
                            if (VideoEXT.Contains(Path.GetExtension(t).ToLower()) == true)
                            {
                                e.Effect = DragDropEffects.Copy;
                                break;
                            }
                        }
                    }
                }
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void DGV_video_DragDrop(object sender, DragEventArgs e)
        {
            if (DGV_video.ReadOnly == false)
            {
                String[] dati = (String[])e.Data.GetData(DataFormats.FileDrop);
                RetriveFiles(dati);
            }
        }

        public void RetriveFiles(String[] dati)
        {
            List<String> temp = new List<String>();
            Boolean es_cart = false;
            try
            {
                foreach (String s in dati)
                {

                    if (Path.HasExtension(s) == true)
                    {
                        if (VideoEXT.Contains(Path.GetExtension(s).ToLower()) == true)
                        {
                            DGV_video.Rows.Add(Path.GetFileName(s), cmb_profile.Text, cmb_resolution.Text, cmb_quality.Text, cmb_subs.Text, "READY", Path.GetDirectoryName(s));
                        }
                    }
                    else
                    {
                        FormatSelection sf = new FormatSelection(VideoEXT);
                        sf.ShowInTaskbar = false;
                        sf.Icon = this.Icon;
                        sf.TopLevel = true;
                        sf.StartPosition = FormStartPosition.CenterParent;
                        if (es_cart == false)
                        {
                            if (sf.ShowDialog() == DialogResult.OK)
                            {
                                es_cart = true;
                            }
                        }
                        if (es_cart == true)
                        {
                            ExtensionToImport = TnA___Tanoshimi_no_Autohardsubber.FormatSelection.Formats;
                            foreach (String t in Directory.GetFiles(s, "*", SearchOption.AllDirectories))
                            {
                                if (ExtensionToImport.Contains(Path.GetExtension(t).ToLower()) == true)
                                {
                                    DGV_video.Rows.Add(Path.GetFileName(t), cmb_profile.Text, cmb_resolution.Text, cmb_quality.Text, cmb_subs.Text, "READY", Path.GetDirectoryName(t));
                                }
                                else
                                {
                                    if (ExtensionToImport.Contains("TUTTI"))
                                    {
                                        if (VideoEXT.Contains(Path.GetExtension(t).ToLower()) == true)
                                        {
                                            DGV_video.Rows.Add(Path.GetFileName(t), cmb_profile.Text, cmb_resolution.Text, cmb_quality.Text, cmb_subs.Text, "READY", Path.GetDirectoryName(t));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (DGV_video.Rows.Count > 0)
                {
                    b_start.Enabled = true;
                    tb_help.Visible = false;
                }
                temp.Clear();
                tab_autohardsubber.Text = TabFilelistLabel + " (count: " + DGV_video.Rows.Count.ToString() + ")";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            CheckPathLength();
        }

        private void b_avvia_Click(object sender, EventArgs e)
        {
            Boolean FFmpegEXEFound = false, start = true;

            foreach (String s in System.IO.Directory.GetFiles(Path.GetDirectoryName(ffmpeg)))
            {
                if (Path.GetFileName(s).ToLower().Contains(Path.GetFileName(ffmpeg)))
                {
                    FFmpegEXEFound = true;
                    break;
                }
            }
            if (b_start.Text.StartsWith("S") && FFmpegEXEFound == true)
            {
                ActivateClipboardMonitoringToolStripMenuItem.Enabled = false;
                DGV_video.ClearSelection();
                DGV_video.ContextMenuStrip.Enabled = false;
                PercentageIndex = 0;
                Boolean AlreadyDone = false;
                foreach (DataGridViewRow r in DGV_video.Rows)
                {
                    if (r.Cells["status"].Value.ToString().ToLower().Contains("ready"))
                    {
                        AlreadyDone = false;
                    }
                    else
                    {
                        AlreadyDone = true;
                        break;
                    }
                }
                if (AlreadyDone == true)
                {
                    FilesToProcess sp = new FilesToProcess();
                    if (sp.ShowDialog() == DialogResult.OK)
                    {
                        foreach (DataGridViewRow r in DGV_video.Rows)
                        {
                            if (StatusToReset.Contains(r.Cells["status"].Value.ToString().Split(' ')[0]))
                            {
                                r.Cells["status"].Value = "READY - 0,00%";
                                r.Cells["status"].Style.BackColor = Color.White;
                            }
                            start = true;
                        }
                    }
                    else
                    {
                        start = false;
                    }
                }
                if (start == true)
                {
                    ts = new ThreadStart(encode);
                    t = new Thread(ts);
                    t.Start();
                    t_elapsed_time.Start();
                    b_start.Text = "STOP";
                    ResetSettingsToolStripMenuItem3.Enabled = false;
                    ResetSettingsToolStripMenuItem3.ShortcutKeys = Keys.None;
                    b_start.Image = (Image)TnA___Tanoshimi_no_Autohardsubber.Properties.Resources.stop;
                    b_start.FlatAppearance.MouseOverBackColor = Color.OrangeRed;
                    b_pause.Enabled = true;
                    b_add_folder.Enabled = false;
                    b_add_files.Enabled = false;
                    b_paste.Enabled = false;
                    b_remove.Enabled = false;
                    DGV_video.ReadOnly = true;
                    ToolsToolStripMenuItem.Enabled = false;
                    EditToolStripMenuItem.Enabled = false;
                    AboutToolStripMenuItem.Enabled = false;
                    pb_tot.Value = 0;
                    ts_perc.Text = pb_tot.Value.ToString() + "%";
                    rtb_encode.Text = rtb_subs.Text = String.Empty;
                    l_speed.Text = "Speed: 0";
                    l_estimated_size.Text = "Estimated size: 0";
                    ts_perc.Text = "0,00%";
                    l_size.Text = "Size: 0";
                    l_remaining_time.Text = "Remaining time: 00:00:00";
                    l_pos.Text = "Position: 00:00:00";
                    EncodingFile = "None";
                }
            }
            else
            {
                if (FFmpegEXEFound == true)
                {
                    b_pause.Image = TnA___Tanoshimi_no_Autohardsubber.Properties.Resources.pause;
                    b_pause.Text = "PAUSE";
                    b_start.Text = "START";
                    b_start.Image = (Image)TnA___Tanoshimi_no_Autohardsubber.Properties.Resources.play;
                    b_start.FlatAppearance.MouseOverBackColor = Color.LawnGreen;
                    b_pause.Enabled = false;
                    b_add_folder.Enabled = true;
                    b_add_files.Enabled = true;
                    b_paste.Enabled = true;
                    b_remove.Enabled = true;
                    ResetSettingsToolStripMenuItem3.Enabled = true;
                    ResetSettingsToolStripMenuItem3.ShortcutKeys = (Keys)Shortcut.CtrlR;
                    DGV_video.ReadOnly = false;
                    t_elapsed_time.Stop();
                    ActivateClipboardMonitoringToolStripMenuItem.Enabled = true;
                    DGV_video.ContextMenuStrip.Enabled = true;
                    DGV_video.Columns[DGV_video.Columns["input"].Index].ReadOnly = true;
                    DGV_video.Columns[DGV_video.Columns["status"].Index].ReadOnly = true;
                    DGV_video.Columns[DGV_video.Columns["source_path"].Index].ReadOnly = true;
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "STOPPED - " + ts_perc.Text;
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Style.BackColor = Color.Yellow;
                    ToolsToolStripMenuItem.Enabled = true;
                    EditToolStripMenuItem.Enabled = true;
                    AboutToolStripMenuItem.Enabled = true;
                    STOP();
                    pause = false;
                    EncodingFile = "None";
                }
                else
                {
                    var scelta = MessageBox.Show("FFmpeg not found. Download it now?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Stop);

                    if (scelta == DialogResult.Yes)
                    {
                        DF(true);
                        if (df.DialogResult == DialogResult.OK)
                        {
                            DialogResult inizio = MessageBox.Show("Start encode now?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                            if (inizio == DialogResult.Yes)
                            {
                                b_avvia_Click(sender, e);
                            }
                        }
                        else
                            MessageBox.Show("FFmpeg.exe is required. This program will not work without it.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    }
                    else
                        MessageBox.Show("FFmpeg.exe is required. This program will not work without it.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
            }
        }

        public void encode()
        {
            for (Int32 q = 0; q < DGV_video.Rows.Count; q++)
            {
                this.Invoke((MethodInvoker)delegate ()
                {
                    l_speed.Text = "Speed: 0";
                    l_estimated_size.Text = "Estimated size: 0";
                    ts_perc.Text = "0,00%";
                    l_size.Text = "Size: 0";
                    l_remaining_time.Text = "Remaining time: 00:00:00";
                    l_pos.Text = "Position: 00:00:00";
                });
                if (DGV_video.Rows[q].Cells["status"].Value.ToString().ToLower().StartsWith("ready"))
                {
                    this.Invoke((MethodInvoker)delegate ()
                    {
                        rtb_encode.Text = rtb_subs.Text = String.Empty;
                    });
                    DataGridViewRow d = DGV_video.Rows[q];
                    ElapsedSeconds = 0;
                    Arguments = String.Empty;
                    PercentageIndex = d.Index;
                    EncodingFile = "Removing temporary folders...";
                    if (!Directory.Exists(temp_folder))
                    {
                        Directory.CreateDirectory(temp_folder);
                    }
                    else
                    {
                        foreach (String s in Directory.GetFiles(temp_folder))
                            System.IO.File.Delete(s);
                    }
                    this.Invoke((MethodInvoker)delegate ()
                    {
                        ts_progress.Text = "None";
                    });

                    String VideoFile = Path.Combine(d.Cells[DGV_video.Columns["source_path"].Index].Value.ToString(), d.Cells[DGV_video.Columns["input"].Index].Value.ToString());
                    String Profile = d.Cells[DGV_video.Columns["profile"].Index].Value.ToString();
                    String Quality = d.Cells[DGV_video.Columns["quality"].Index].Value.ToString();
                    String Res = d.Cells[DGV_video.Columns["res"].Index].Value.ToString();
                    String SubsMode = d.Cells[DGV_video.Columns["subtitle_mode"].Index].Value.ToString();

                    List<String> OverbordingLines = new List<String>();

                    List<String> file_sub = new List<String>();

                    Boolean ass = false, stop = false;

                    if (Path.GetExtension(VideoFile).ToLower() == ".mkv" && Profile.StartsWith("Remux") == false && Profile.StartsWith("Workraw") == false && SubsMode.StartsWith("Hard") && Profile.StartsWith("Gen") == false)
                    {
                        this.Invoke((MethodInvoker)delegate ()
                        {
                            ts_progress.Text = "Finding attachments and subtitles...";
                        });

                        foreach (String s in Directory.GetFiles(temp_folder))
                        {
                            System.IO.File.Delete(s);
                        }

                        String temp_ffmpeg = String.Empty;

                        if (System.IO.File.Exists(ffmpeg))
                        {
                            temp_ffmpeg = temp_folder + "\\" + Path.GetFileName(ffmpeg);
                            System.IO.File.Copy(ffmpeg, temp_ffmpeg, true);
                        }

                        Tuple<String, String> SubID = new Tuple<string, string>(String.Empty, String.Empty);

                        System.Diagnostics.ProcessStartInfo psi_extract = new System.Diagnostics.ProcessStartInfo();

                        Environment.CurrentDirectory = temp_folder;

                        foreach (MediaInfoDotNet.Models.TextStream t in new MediaFile(VideoFile).Text)
                        {
                            if (t.CodecId.ToLower().Contains("s_text/ass"))
                            {
                                if (t.Default == true)
                                {
                                    SubID = new Tuple<string, string>((t.ID - 1).ToString(), ".ass");
                                }
                                if (t.Forced)
                                {
                                    SubID = new Tuple<string, string>((t.ID - 1).ToString(), ".ass");
                                    break;
                                }
                            }
                            if (t.CodecId.ToLower().Contains("s_text/utf"))
                            {
                                if (t.Default == true)
                                {
                                    SubID = new Tuple<string, string>((t.ID - 1).ToString(), ".srt");
                                }
                                if (t.Forced == true)
                                {
                                    SubID = new Tuple<string, string>((t.ID - 1).ToString(), ".srt");
                                    break;
                                }
                            }
                        }

                        if (SubID.Item1 == String.Empty)
                        {
                            if (new MediaFile(VideoFile).Text.Count > 0)
                            {
                                if (new MediaFile(VideoFile).Text[0].CodecId.ToLower().Contains("s_text/ass"))
                                    SubID = new Tuple<string, string>((new MediaFile(VideoFile).Text[0].ID - 1).ToString(), ".ass");
                                if (new MediaFile(VideoFile).Text[0].CodecId.ToLower().Contains("s_text/utf"))
                                    SubID = new Tuple<string, string>((new MediaFile(VideoFile).Text[0].ID - 1).ToString(), ".srt");
                            }
                        }

                        psi_extract = new System.Diagnostics.ProcessStartInfo();
                        psi_extract.CreateNoWindow = true;
                        psi_extract.UseShellExecute = false;
                        psi_extract.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;

                        psi_extract.FileName = Path.GetFileName(temp_ffmpeg);

                        if (String.IsNullOrWhiteSpace(SubID.Item1) == false)
                        {
                            psi_extract.Arguments = " -y -i \"" + VideoFile + "\" ";
                            this.Invoke((MethodInvoker)delegate ()
                            {
                                ts_progress.Text = "Extracting subtitles...";
                            });
                            psi_extract.Arguments += " -map 0:" + SubID.Item1 + " -c:s copy \"" + Path.GetDirectoryName(ffmpeg) + "\\subs0" + SubID.Item2 + "\"";
                            file_sub.Add(Path.GetDirectoryName(ffmpeg) + "\\subs0" + SubID.Item2);

                            System.Diagnostics.Process.Start(psi_extract).WaitForExit();
                        }

                        this.Invoke((MethodInvoker)delegate ()
                        {
                            ts_progress.Text = "Extracting attachments...";
                        });

                        psi_extract = new System.Diagnostics.ProcessStartInfo();
                        psi_extract.CreateNoWindow = true;
                        psi_extract.UseShellExecute = false;
                        psi_extract.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;

                        temp_ffmpeg = String.Empty;

                        if (System.IO.File.Exists(ffmpeg))
                        {
                            temp_ffmpeg = temp_folder + "\\" + Path.GetFileName(ffmpeg);
                            System.IO.File.Copy(ffmpeg, temp_ffmpeg, true);
                        }

                        psi_extract.FileName = Path.GetFileName(temp_ffmpeg);

                        psi_extract.Arguments = " -dump_attachment:t \"\" -i \"" + VideoFile + "\" NUL";

                        System.Diagnostics.Process.Start(psi_extract).WaitForExit();

                        Environment.CurrentDirectory = GUIdir;
                        foreach (String s in Directory.GetFiles(temp_folder))
                        {
                            if (Path.GetExtension(s).ToLower() == ".exe")
                                System.IO.File.Delete(s);
                        }
                        if (Directory.Exists(fonts_folder))
                        {
                            Directory.Delete(fonts_folder, true);
                        }
                        Directory.Move(temp_folder, fonts_folder);
                    }

                    Arguments = String.Empty;

                    EncodingFile = Path.GetFileName(VideoFile);

                    //Thread.Sleep(2000);

                    if (stop == false)
                    {
                        Int32 H = Convert.ToInt32(d.Cells["res"].Value.ToString().Replace("p", String.Empty));
                        this.Invoke((MethodInvoker)delegate ()
                        {

                        });
                        switch (Profile)
                        {
                            case "Remux MKV":
                                remux_mkv(VideoFile, Profile);
                                break;
                            case "Remux MP4":
                                remux_mp4(VideoFile, Profile, Quality, H / 9 * 16, H);
                                break;
                            case "Keyframes generation":
                                GeneraKeyframes(VideoFile, H / 9 * 16, H);
                                break;
                            default:
                                Encode(VideoFile, Quality, file_sub, fonts_folder, ass, Profile, H / 9 * 16, H, SubsMode);
                                break;
                        }

                        Thread.Sleep(100);
                    }
                }
            }
            Environment.CurrentDirectory = GUIdir;
            Thread.Sleep(500);
            switch (WhenAllIsFinished)
            {
                case "Close application":
                    ForceClose = true;
                    this.Close();
                    break;
                case "Stand-by":
                    Application.SetSuspendState(PowerState.Suspend, true, true);
                    break;
                case "Shutdown":
                    System.Diagnostics.Process.Start("shutdown", "/s /t 120");
                    if (MessageBox.Show("This PC will shutdown in 2 minutes starting from " + DateTime.Now.TimeOfDay.ToString() + ".\nClick on OK to abort.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Information) == DialogResult.OK)
                    {
                        System.Diagnostics.Process.Start("shutdown", "/a");
                        MessageBox.Show("Shutdown aborted.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    break;
                case "Reboot":
                    System.Diagnostics.Process.Start("shutdown", "/r /t 0");
                    break;
            }
            this.Invoke((MethodInvoker)delegate ()
            {
                b_start.Text = "START";
                b_start.Image = (Image)TnA___Tanoshimi_no_Autohardsubber.Properties.Resources.play;
                b_start.FlatAppearance.MouseOverBackColor = Color.LawnGreen;
                b_pause.Enabled = false;
                b_add_folder.Enabled = true;
                b_add_files.Enabled = true;
                b_paste.Enabled = true;
                b_remove.Enabled = true;
                ResetSettingsToolStripMenuItem3.Enabled = true;
                ResetSettingsToolStripMenuItem3.ShortcutKeys = (Keys)Shortcut.CtrlR;
                DGV_video.ReadOnly = false;
                t_elapsed_time.Stop();
                ActivateClipboardMonitoringToolStripMenuItem.Enabled = true;
                DGV_video.ContextMenuStrip.Enabled = true;
                DGV_video.Columns[DGV_video.Columns["input"].Index].ReadOnly = true;
                DGV_video.Columns[DGV_video.Columns["status"].Index].ReadOnly = true;
                DGV_video.Columns[DGV_video.Columns["source_path"].Index].ReadOnly = true;
                ToolsToolStripMenuItem.Enabled = true;
                EditToolStripMenuItem.Enabled = true;
                AboutToolStripMenuItem.Enabled = true;
                EncodingFile = "None";
            });
        }

        public void GeneraKeyframes(String v, Int32 largh, Int32 alt)
        {
            String txt = Path.GetDirectoryName(v) + "\\" + Path.GetFileNameWithoutExtension(v) + " - Keyframes.txt";
            FinalFile = Path.Combine(Path.GetDirectoryName(v), Path.GetFileNameWithoutExtension(v));

            Duration = TimeSpan.FromMilliseconds(new MediaFile(v).Video[0].Duration).ToString(@"hh\:mm\:ss");

            this.Invoke((MethodInvoker)delegate ()
            {
                ts_progress.Text = "Keyframes generation of '" + Path.GetFileNameWithoutExtension(v) + "'";
            });

            Environment.CurrentDirectory = Path.GetDirectoryName(scxvid);
            var psi = new System.Diagnostics.ProcessStartInfo();
            EncodeProcess = new System.Diagnostics.Process();

            String temp = ResizeAuto(new GetVideoTracks(new MediaFile(v)).AspectRatio, largh, new GetVideoTracks(new MediaFile(v)).Width, alt, new GetVideoTracks(new MediaFile(v)).Height);
            if (String.IsNullOrWhiteSpace(temp) == false)
            {
                temp = " -vf " + temp.Trim(',');
            }
            else
                temp = String.Empty;

            psi.FileName = "cmd.exe";

            psi.Arguments = "/c " + Path.GetFileNameWithoutExtension(ffmpeg) + " -i \"" + v + "\" -f yuv4mpegpipe " + temp + " -pix_fmt yuv420p -vsync drop - | " + Path.GetFileNameWithoutExtension(scxvid) + " \"" + txt + "\"";

            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            EncodeProcess.OutputDataReceived += Processo_codifica_OutputDataReceived;
            EncodeProcess.ErrorDataReceived += Processo_codifica_ErrorDataReceived;
            EncodeProcess.StartInfo = psi;
            EncodeProcess.Start();
            EncodeProcess.BeginOutputReadLine();
            EncodeProcess.BeginErrorReadLine();
            EncodeProcess.WaitForExit();
            EncodeProcess.CancelOutputRead();
            EncodeProcess.CancelErrorRead();
            exit_code = EncodeProcess.ExitCode;
            if (exit_code == 0)
            {
                this.Invoke((MethodInvoker)delegate ()
                {
                    ts_perc.Text = "100,00%";
                    pb_tot.Value = pb_tot.Maximum;
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "OK - " + ts_perc.Text;
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Style.BackColor = Color.LightGreen;
                });
            }
            else
            {
                this.Invoke((MethodInvoker)delegate ()
                {
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "ERROR - " + ts_perc.Text;
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Style.BackColor = Color.Red;
                });
            }

            CreateLOG();

        }

        public void remux_mp4(String v, String prof, String qual, Int32 largh, Int32 alt)
        {
            String RemuxArguments = " -y -i \"" + v + "\"";
            String OutputMP4 = Path.GetDirectoryName(v) + "\\" + Path.GetFileNameWithoutExtension(v) + "[" + prof + "].mp4";
            FinalFile = OutputMP4;

            MediaFile m = new MediaFile(v);

            Int32 VideoIndex = 0, AudioIndex = 0;
            foreach (MediaInfoDotNet.Models.VideoStream vid in m.Video)
            {
                if (vid.Format.ToLower().Contains("mpeg") || vid.Format.ToLower().Contains("avc") || vid.Format.ToLower().Contains("hevc"))
                {
                    RemuxArguments += " -map 0:" + (vid.ID - 1).ToString() + " -c:v:" + VideoIndex.ToString() + " copy";
                    RemuxArguments += " -r " + vid.FrameRate.ToString().Replace(",", ".");
                    fc = Math.Round((Double)vid.FrameCount/* / 1000.0*/, 0, MidpointRounding.AwayFromZero).ToString();
                    Double altezza = vid.Height;
                    Double larghezza = vid.Width;
                    String aspect = Math.Round(larghezza / altezza, 3, MidpointRounding.AwayFromZero).ToString();
                    Duration = TimeSpan.FromMilliseconds(vid.Duration).ToString(@"hh\:mm\:ss");
                    //if (durata.Contains("."))
                    //    durata = durata.Remove(durata.IndexOf(".")).Trim();
                }
                else
                {
                    RemuxArguments += " -map 0:" + (vid.ID - 1).ToString() + " -c:v:" + VideoIndex.ToString() + " libx264";
                    RemuxArguments += " -r " + vid.FrameRate.ToString().Replace(",", ".");
                    fc = Math.Round((Double)vid.FrameCount/* / 1000.0*/, 0, MidpointRounding.AwayFromZero).ToString();
                    Double altezza = vid.Height;
                    if (altezza % 2 != 0)
                        altezza--;
                    Double larghezza = vid.Width;
                    if (larghezza % 2 != 0)
                        larghezza--;
                    String aspect = Math.Round(larghezza / altezza, 3, MidpointRounding.AwayFromZero).ToString();
                    Duration = TimeSpan.FromMilliseconds(vid.Duration).ToString("HH:mm:ss");
                    //if (durata.Contains("."))
                    //    durata = durata.Remove(durata.IndexOf(".")).Trim();
                    switch (qual)
                    {
                        case "Very high":
                            Arguments += " -crf:v:" + VideoIndex.ToString() + " 14 -preset:v veryslow -aq-mode 3";
                            break;
                        case "High":
                            Arguments += " -crf:v:" + VideoIndex.ToString() + " 17 -preset:v veryslow -aq-mode 3";
                            break;
                        case "Above normal":
                            Arguments += " -crf:v:" + VideoIndex.ToString() + " 18.5 -preset:v slower -aq-mode 3";
                            break;
                        case "Normal":
                            Arguments += " -crf:v:" + VideoIndex.ToString() + " 20 -preset:v medium -aq-mode 2";
                            break;
                        case "Below normal":
                            Arguments += " -crf:v:" + VideoIndex.ToString() + " 22 -preset:v fast -aq-mode 1";
                            break;
                        case "Low":
                            Arguments += " -crf:v:" + VideoIndex.ToString() + " 25 -preset:v fast -aq-mode 1";
                            break;
                        case "Very low":
                            Arguments += " -crf:v:" + VideoIndex.ToString() + " 27 -preset:v fast -aq-mode 1";
                            break;
                        case "Draft":
                            Arguments += " -crf:v:" + VideoIndex.ToString() + " 36 -preset:v superfast -aq-mode 1";
                            break;
                    }

                    RemuxArguments += " -profile:v:" + VideoIndex.ToString() + " high -level:v:" + VideoIndex.ToString() + " 4.1";
                    String temp = ResizeAuto(new GetVideoTracks(new MediaFile(v)).AspectRatio, largh, new GetVideoTracks(new MediaFile(v)).Width, alt, new GetVideoTracks(new MediaFile(v)).Height);
                    if (String.IsNullOrWhiteSpace(temp) == false)
                    {
                        temp = "," + temp;
                    }
                    else
                        temp = String.Empty;
                    if (vid.miGetString("ScanType").ToLower().Trim().StartsWith("i") == true)
                    {
                        temp += ",yadif";
                    }
                    RemuxArguments += " -vf " + temp.Trim(',');
                }
                VideoIndex++;
            }
            foreach (MediaInfoDotNet.Models.AudioStream aud in m.Audio)
            {
                GetAudioTracks ca = new GetAudioTracks(aud, m.filePath);
                String AudioChannels = String.Empty;
                Boolean IsLossless = ca.Lossless;

                AudioChannels = ca.Channels.ToString();

                if (ca.Format.ToLower().Contains("ac-3") || ca.Format.ToLower().StartsWith("mpeg") || ca.Format.ToLower().Contains("aac"))
                {
                    RemuxArguments += " -map 0:" + ca.Index.ToString() + " -c:a:" + AudioIndex.ToString() + " copy";
                }
                else
                {
                    if (IsLossless == true)
                    {
                        RemuxArguments += " -map 0:" + ca.Index.ToString() + " -c:a:" + AudioIndex.ToString() + " alac -ac:" + AudioIndex.ToString() + " " + AudioChannels;
                    }
                    else
                    {
                        RemuxArguments += " -map 0:" + ca.Index.ToString() + " -c:a:" + AudioIndex.ToString() + " aac -ac:" + AudioIndex.ToString() + " " + AudioChannels;
                        switch (qual)
                        {
                            case "Very high":
                                RemuxArguments += " -b:a:" + AudioIndex.ToString() + " " + (192 * (Convert.ToInt32(AudioChannels) / 2)).ToString() + "k";
                                break;
                            case "High":
                                RemuxArguments += " -b:a:" + AudioIndex.ToString() + " " + (160 * (Convert.ToInt32(AudioChannels) / 2)).ToString() + "k";
                                break;
                            case "Above medium":
                                RemuxArguments += " -b:a:" + AudioIndex.ToString() + " " + (144 * (Convert.ToInt32(AudioChannels) / 2)).ToString() + "k";
                                break;
                            case "Normal":
                                RemuxArguments += " -b:a:" + AudioIndex.ToString() + " " + (112 * (Convert.ToInt32(AudioChannels) / 2)).ToString() + "k";
                                break;
                            case "Below normal":
                                RemuxArguments += " -b:a:" + AudioIndex.ToString() + " " + (96 * (Convert.ToInt32(AudioChannels) / 2)).ToString() + "k";
                                break;
                            case "Low":
                                RemuxArguments += " -b:a:" + AudioIndex.ToString() + " " + (64 * (Convert.ToInt32(AudioChannels) / 2)).ToString() + "k";
                                break;
                            case "Very low":
                                RemuxArguments += " -b:a:" + AudioIndex.ToString() + " " + (48 * (Convert.ToInt32(AudioChannels) / 2)).ToString() + "k";
                                break;
                            case "Draft":
                                RemuxArguments += " -b:a:" + AudioIndex.ToString() + " " + (32 * (Convert.ToInt32(AudioChannels) / 2)).ToString() + "k";
                                break;
                        }
                    }
                }
                AudioIndex++;
            }

            RemuxArguments += " \"" + OutputMP4 + "\"";

            //MessageBox.Show(comando_remux);

            Environment.CurrentDirectory = Path.GetDirectoryName(ffmpeg);
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
            EncodeProcess = new System.Diagnostics.Process();
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;

            psi.FileName = Path.GetFileName(ffmpeg);

            psi.Arguments = RemuxArguments;

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            EncodeProcess.OutputDataReceived += Processo_codifica_OutputDataReceived;
            EncodeProcess.ErrorDataReceived += Processo_codifica_ErrorDataReceived;
            EncodeProcess.StartInfo = psi;
            EncodeProcess.Start();
            EncodeProcess.BeginOutputReadLine();
            EncodeProcess.BeginErrorReadLine();
            EncodeProcess.WaitForExit();
            EncodeProcess.CancelOutputRead();
            EncodeProcess.CancelErrorRead();
            exit_code = EncodeProcess.ExitCode;
            if (exit_code == 0)
            {
                this.Invoke((MethodInvoker)delegate ()
                {
                    ts_perc.Text = "100,00%";
                    pb_tot.Value = pb_tot.Maximum;
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "OK - " + ts_perc.Text;
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Style.BackColor = Color.LightGreen;
                });
            }
            else
            {
                this.Invoke((MethodInvoker)delegate ()
                {
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "ERROR - " + ts_perc.Text;
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Style.BackColor = Color.Red;
                });
            }

            CreateLOG();

        }

        public void remux_mkv(String v, String prof)
        {
            Environment.CurrentDirectory = Path.GetDirectoryName(mkvmerge);
            String OutputMKV = Path.GetDirectoryName(v) + "\\" + Path.GetFileNameWithoutExtension(v) + "[" + prof + "].mkv";
            FinalFile = OutputMKV;
            String RemuxArguments = " -o \"" + OutputMKV + "\" \"" + v + "\"";
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
            psi.Arguments = RemuxArguments;
            psi.FileName = Path.GetFileName(mkvmerge);
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            RemuxProcess = new System.Diagnostics.Process();
            RemuxProcess.StartInfo = psi;
            RemuxProcess.ErrorDataReceived += Pr_ErrorDataReceived;
            RemuxProcess.OutputDataReceived += Pr_OutputDataReceived;
            this.Invoke((MethodInvoker)delegate ()
            {
                ts_progress.Text = "Remuxing file '" + Path.GetFileName(v) + "'";
            });
            RemuxProcess.Start();
            RemuxProcess.BeginErrorReadLine();
            RemuxProcess.BeginOutputReadLine();
            RemuxProcess.WaitForExit();
            RemuxProcess.CancelErrorRead();
            RemuxProcess.CancelOutputRead();
            exit_code = RemuxProcess.ExitCode;
            switch (RemuxProcess.ExitCode)
            {
                case 0:
                    this.Invoke((MethodInvoker)delegate ()
                    {
                        ts_perc.Text = "100,00%";
                        pb_tot.Value = pb_tot.Maximum;
                        DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "OK - " + ts_perc.Text;
                        DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Style.BackColor = Color.LightGreen;
                    });
                    break;
                case 1:
                    this.Invoke((MethodInvoker)delegate ()
                    {
                        DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "WARNING - " + ts_perc.Text;
                        DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Style.BackColor = Color.LightGoldenrodYellow;
                    });
                    break;
                default:
                    this.Invoke((MethodInvoker)delegate ()
                    {
                        DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "ERROR - " + ts_perc.Text;
                        DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Style.BackColor = Color.Red;
                    });
                    break;

            }

            CreateLOG();

            this.Invoke((MethodInvoker)delegate ()
            {
                ts_progress.Text = "None";
            });
        }

        public void CreateLOG()
        {
            if (Directory.Exists(LOG_dir) == false)
            {
                Directory.CreateDirectory(LOG_dir);
            }
            try
            {
                this.Invoke((MethodInvoker)delegate ()
                {
                    List<String> LOG_info = rtb_encode.Lines.ToList();
                    LOG_info.RemoveAll(String.IsNullOrWhiteSpace);
                    LOG_info.Add("\n");

                    List<String> LOG_subs = rtb_subs.Lines.ToList();
                    LOG_subs.RemoveAll(String.IsNullOrWhiteSpace);

                    LOG_info.AddRange(LOG_subs);

                    System.IO.File.WriteAllLines(LOG_dir + "\\" + Path.GetFileNameWithoutExtension(FinalFile) + " - LOG.txt", LOG_info.ToArray());
                });
            }
            catch { }
        }

        private void Pr_OutputDataReceived(object sender, System.Diagnostics.DataReceivedEventArgs e)
        {
            String s = e.Data;
            String Number = String.Empty;
            if (String.IsNullOrWhiteSpace(s) == false)
            {
                if (s.StartsWith("Progress"))
                {
                    this.Invoke((MethodInvoker)delegate ()
                    {
                        Number = s.Replace("Progress:", String.Empty);
                        Number = Number.Replace("%", String.Empty);
                        Number = Number.Trim();
                        pb_tot.Value = Convert.ToInt32(Number);

                        ts_perc.Text = pb_tot.Value.ToString() + ".00%";
                        DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "RUNNING - " + ts_perc.Text;
                    });
                }
                else
                {
                    this.Invoke((MethodInvoker)delegate ()
                    {
                        rtb_encode.Text += s + "\n";
                        rtb_encode.SelectionStart = rtb_encode.Text.Length;
                        rtb_encode.ScrollToCaret();
                    });
                }
            }
        }

        private void Pr_ErrorDataReceived(object sender, System.Diagnostics.DataReceivedEventArgs e)
        {
            String s = e.Data;
            String numero = String.Empty;
            if (String.IsNullOrWhiteSpace(s) == false)
            {
                if (s.StartsWith("Progress"))
                {
                    this.Invoke((MethodInvoker)delegate ()
                    {
                        numero = s.Replace("Progress:", String.Empty);
                        numero = numero.Replace("%", String.Empty);
                        numero = numero.Trim();
                        pb_tot.Value = Convert.ToInt32(numero);
                        ts_perc.Text = pb_tot.Value.ToString() + ".00%";
                        DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "RUNNING - " + ts_perc.Text;
                    });
                }
                else
                {
                    this.Invoke((MethodInvoker)delegate ()
                    {
                        rtb_encode.Text += s + "\n";
                    });
                }
            }
        }

        public String ResizeAuto(String AspectR, Int32 OutW, Double InW, Int32 OutH, Double InH)
        {
            String temp = String.Empty;

            if (Convert.ToDouble(InW / InH) >= (4.0 / 3.0))
            {
                Int32 FinalW = CalculateOutW(Convert.ToInt32(InW), OutW, Convert.ToInt32(InH));
                if (FinalW > 0)
                {
                    temp = ",scale=\"" + FinalW + ":trunc(ow/a/2)*2\"";
                }
                else
                {
                    temp = String.Empty;
                }
            }
            else
            {
                if (Convert.ToInt32(InH) > OutH)
                {
                    if (InH % 2 == 0)
                        InH = OutH;
                    temp = ",scale=\"trunc(oh*a/2)*2:" + InH + "\"";
                }
                else
                {
                    temp = String.Empty;
                }
            }
            return temp;
        }

        public Int32 CalculateOutW(Int32 InW, Int32 OutW, Int32 InH)
        {
            Int32 temp = 0;
            if (InW < OutW)
            {
                if (InW % 2 == 0)
                {
                    if (InH % 2 == 0)
                        temp = 0;
                    else
                        temp = InW;
                }
                else
                {
                    temp = InW - 1;
                }
            }
            else
            {
                if (InW > OutW)
                {
                    temp = OutW;
                }
                else
                {
                    temp = 0;
                }
            }
            return temp;
        }

        public Int32 CalculateOutH(Int32 InH, Int32 OutH, Int32 OutW)
        {
            Int32 temp = 0;
            if (InH < OutH)
            {
                if (InH % 2 == 0)
                {
                    if (OutW % 2 == 0)
                        temp = 0;
                    else
                        temp = InH;
                }
                else
                {
                    temp = InH - 1;
                }
            }
            else
            {
                if (InH > OutH)
                {
                    temp = OutH;
                }
                else
                {
                    temp = 0;
                }
            }
            return temp;
        }

        private void Processo_codifica_ErrorDataReceived(object sender, System.Diagnostics.DataReceivedEventArgs e)
        {
            this.Invoke((MethodInvoker)delegate ()
                {
                    try
                    {
                        FFmpegOutputWrapperNET ff = new FFmpegOutputWrapperNET(e.Data.Trim());
                        String temp_extra = e.Data;
                        if (e.Data.Trim().ToLower().StartsWith("frame"))
                        {
                            try
                            {
                                Int32 frames = Convert.ToInt32(ff.Frames);
                                pb_tot.Maximum = Convert.ToInt32(TimeSpan.Parse(Duration).TotalSeconds);
                                String fps = ff.Fps;
                                l_speed.Text = "Speed: " + fps + " fps";
                                String size = ff.Size.Replace("kB", "");
                                l_size.Text = "Size: " + Arrotonda(Convert.ToDouble(size) / 1024.0, 2) + " MB";
                                l_pos.Text = "Position: " + ff.Time.ToString();
                                ts_progress.Text = "Encoding file \"" + EncodingFile + "\"";
                                ts_perc.Text = Arrotonda(((Double)pb_tot.Value / pb_tot.Maximum) * 100.0, 2) + "%";
                                pb_tot.Value = Convert.ToInt32(ff.Time.TotalSeconds);


                                DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "RUNNING - " + ts_perc.Text;
                                DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Style.BackColor = Color.White;

                                String bitrate = ff.Bitrate.Remove(ff.Bitrate.IndexOf("k"));

                                if (fps.Contains("."))
                                    fps = fps.Remove(fps.IndexOf("."));
                                Int32 tempo_restante = ((Convert.ToInt32(fc) - Convert.ToInt32(ff.Frames)) / Convert.ToInt32(ff.Fps));
                                l_remaining_time.Text = "Remaining time: " + TimeSpan.FromSeconds(tempo_restante).ToString(@"hh\:mm\:ss");
                                Double bitrate_kb = Convert.ToDouble(bitrate.Remove(bitrate.IndexOf("."))) / 8 / 1024.0;
                                Double dimens_stimata = Math.Round(bitrate_kb * TimeSpan.Parse(Duration).TotalSeconds, 2);
                                l_estimated_size.Text = "Estimated size: " + dimens_stimata + " MB";
                            }
                            catch// (Exception ex)
                            {
                                //MessageBox.Show(ex.Message);
                                //DGV_log.Rows.Insert(0, DateTime.Now.ToString(), ex.Message + " -> " + ecc);
                            }
                        }
                        else
                        {
                            if (temp_extra.ToLower().Trim().Contains("parsed_ass"))
                            {
                                rtb_subs.Text += l_pos.Text.Remove(0, l_pos.Text.LastIndexOf(' ') + 1) + " ---> " + temp_extra + "\n";
                                rtb_subs.SelectionStart = rtb_subs.Text.Length;
                                rtb_subs.ScrollToCaret();
                            }
                            else
                            {
                                if (temp_extra.ToLower().Trim().StartsWith("frame") == false)
                                {
                                    rtb_encode.Text += temp_extra + "\n";
                                    rtb_encode.SelectionStart = rtb_encode.Text.Length;
                                    rtb_encode.ScrollToCaret();
                                }
                            }
                        }
                    }
                    catch { }
                });
        }

        private void Processo_codifica_OutputDataReceived(object sender, System.Diagnostics.DataReceivedEventArgs e)
        {
            this.Invoke((MethodInvoker)delegate ()
            {
                try
                {
                    if (String.IsNullOrWhiteSpace(e.Data.Trim()) == false)
                    {
                        rtb_encode.Text = String.Empty;
                        rtb_encode.Text += e.Data;
                    }
                }
                catch { }
            });
        }

        public void StartEncode()
        {
            Environment.CurrentDirectory = Path.GetDirectoryName(ffmpeg);
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
            EncodeProcess = new System.Diagnostics.Process();
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            if (Environment.Is64BitOperatingSystem == true)
            {
                psi.FileName = Path.GetFileName(ffmpeg);
            }
            //psi.FileName = "cmd.exe";
            psi.Arguments = Arguments;// + " 2>" + Path.GetFileName(ffmpeg_txt);

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            EncodeProcess.OutputDataReceived += Processo_codifica_OutputDataReceived;
            EncodeProcess.ErrorDataReceived += Processo_codifica_ErrorDataReceived;
            EncodeProcess.StartInfo = psi;
            EncodeProcess.Start();
            EncodeProcess.BeginOutputReadLine();
            EncodeProcess.BeginErrorReadLine();
            EncodeProcess.WaitForExit();
            EncodeProcess.CancelOutputRead();
            EncodeProcess.CancelErrorRead();
            exit_code = EncodeProcess.ExitCode;
            if (exit_code == 0)
            {
                this.Invoke((MethodInvoker)delegate ()
                {
                    ts_perc.Text = "100,00%";
                    pb_tot.Value = pb_tot.Maximum;
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "OK - " + ts_perc.Text;
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Style.BackColor = Color.LightGreen;
                });
            }
            else
            {
                this.Invoke((MethodInvoker)delegate ()
                {
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Value = "ERROR - " + ts_perc.Text;
                    DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["status"].Index].Style.BackColor = Color.Red;
                });
            }

            CreateLOG();
        }

        public void Encode(String video, String quality, List<String> sub, String ff, Boolean sub_ass, String prof, Int32 W, Int32 H, String submode)
        {
            MediaFile media = new MediaFile(video);

            List<GetAudioTracks> ca = new List<GetAudioTracks>();
            GetVideoTracks cv = new GetVideoTracks(media);

            EncodeProfileSettings.VideoQuality.VeryHigh qVH = new EncodeProfileSettings.VideoQuality.VeryHigh(prof);
            EncodeProfileSettings.VideoQuality.High qH = new EncodeProfileSettings.VideoQuality.High(prof);
            EncodeProfileSettings.VideoQuality.AboveNormal qAN = new EncodeProfileSettings.VideoQuality.AboveNormal(prof);
            EncodeProfileSettings.VideoQuality.Normal qN = new EncodeProfileSettings.VideoQuality.Normal(prof);
            EncodeProfileSettings.VideoQuality.BelowNormal qBN = new EncodeProfileSettings.VideoQuality.BelowNormal(prof);
            EncodeProfileSettings.VideoQuality.Low qL = new EncodeProfileSettings.VideoQuality.Low(prof);
            EncodeProfileSettings.VideoQuality.VeryLow qVL = new EncodeProfileSettings.VideoQuality.VeryLow(prof);
            EncodeProfileSettings.VideoQuality.Draft qD = new EncodeProfileSettings.VideoQuality.Draft(prof);

            EncodeProfileSettings.Parameters VideoArguments = new EncodeProfileSettings.Parameters(media);

            fc = Math.Round((Double)cv.Framecount, 0, MidpointRounding.AwayFromZero).ToString();


            Boolean interl = cv.Interlaced;

            this.Invoke((MethodInvoker)delegate ()
            {
                if (submode.StartsWith("H"))
                {
                    sc_log.Panel2Collapsed = false;
                }
                else
                    sc_log.Panel2Collapsed = true;
            });

            foreach (MediaInfoDotNet.Models.AudioStream a in media.Audio)
            {
                ca.Add(new GetAudioTracks(a, media.filePath));
            }

            this.Arguments = " -y -i \"" + video + "\" -map 0:" + cv.Index;

            this.Arguments += " -c:v:" + cv.Index + " " + qVH.CODEC;
            if (prof.ToLower().Contains("xbox") && Convert.ToDouble(cv.Framerate) > 30)
                cv.Framerate = "30";

            cv.Framerate = cv.Framerate.ToString().Replace(",", ".");

            if (Convert.ToDouble(cv.Framerate) > 0)
                this.Arguments += " -r " + cv.Framerate;

            Duration = cv.Duration.ToString();

            switch (quality)
            {
                case "Very High":
                    switch (prof.Split(' ')[0])
                    {
                        case "XviD":
                            this.Arguments += " -q:v:" + cv.Index.ToString() + " " + qVH.QP.ToString() + " " + VideoArguments.XVID;
                            break;
                        default:
                            this.Arguments += " -crf: " + qVH.CRF.ToString().Replace(',', '.') + " -preset:v " + qVH.Preset + " -aq-mode " + qVH.AQmode.ToString();
                            break;
                    }
                    break;
                case "High":
                    switch (prof.Split(' ')[0])
                    {
                        case "XviD":
                            this.Arguments += " -q:v:" + cv.Index.ToString() + " " + qH.QP.ToString() + " " + VideoArguments.XVID;
                            break;
                        default:
                            this.Arguments += " -crf: " + qH.CRF.ToString().Replace(',', '.') + " -preset:v " + qH.Preset + " -aq-mode " + qH.AQmode.ToString();
                            break;
                    }
                    break;
                case "Above normal":
                    switch (prof.Split(' ')[0])
                    {
                        case "XviD":
                            this.Arguments += " -q:v:" + cv.Index.ToString() + " " + qAN.QP.ToString() + " " + VideoArguments.XVID;
                            break;
                        default:
                            this.Arguments += " -crf: " + qAN.CRF.ToString().Replace(',', '.') + " -preset:v " + qAN.Preset + " -aq-mode " + qAN.AQmode.ToString();
                            break;
                    }
                    break;
                case "Normal":
                    switch (prof.Split(' ')[0])
                    {
                        case "XviD":
                            this.Arguments += " -q:v:" + cv.Index.ToString() + " " + qN.QP.ToString() + " " + VideoArguments.XVID;
                            break;
                        default:
                            this.Arguments += " -crf: " + qN.CRF.ToString().Replace(',', '.') + " -preset:v " + qN.Preset + " -aq-mode " + qN.AQmode.ToString();
                            break;
                    }
                    break;
                case "Below normal":
                    switch (prof.Split(' ')[0])
                    {
                        case "XviD":
                            this.Arguments += " -q:v:" + cv.Index.ToString() + " " + qBN.QP.ToString() + " " + VideoArguments.XVID;
                            break;
                        default:
                            this.Arguments += " -crf: " + qBN.CRF.ToString().Replace(',', '.') + " -preset:v " + qBN.Preset + " -aq-mode " + qBN.AQmode.ToString();
                            break;
                    }
                    break;
                case "Low":
                    switch (prof.Split(' ')[0])
                    {
                        case "XviD":
                            this.Arguments += " -q:v:" + cv.Index.ToString() + " " + qL.QP.ToString() + " " + VideoArguments.XVID;
                            break;
                        default:
                            this.Arguments += " -crf: " + qL.CRF.ToString().Replace(',', '.') + " -preset:v " + qL.Preset + " -aq-mode " + qL.AQmode.ToString();
                            break;
                    }
                    break;
                case "Very low":
                    switch (prof.Split(' ')[0])
                    {
                        case "XviD":
                            this.Arguments += " -q:v:" + cv.Index.ToString() + " " + qVL.QP.ToString() + " " + VideoArguments.XVID;
                            break;
                        default:
                            this.Arguments += " -crf: " + qVL.CRF.ToString().Replace(',', '.') + " -preset:v " + qVL.Preset + " -aq-mode " + qVL.AQmode.ToString();
                            break;
                    }
                    break;
                case "Draft":
                    switch (prof.Split(' ')[0])
                    {
                        case "XviD":
                            this.Arguments += " -q:v:" + cv.Index.ToString() + " " + qD.QP.ToString() + " " + VideoArguments.XVID;
                            break;
                        default:
                            this.Arguments += " -crf: " + qD.CRF.ToString().Replace(',', '.') + " -preset:v " + qD.Preset + " -aq-mode " + qD.AQmode.ToString();
                            break;
                    }
                    break;
            }
            switch (prof.Split(' ')[0])
            {
                case "Bluray":
                    this.Arguments += " " + VideoArguments.BLURAY;
                    break;
                case "Streaming":
                    if (prof.ToLower().Contains("h.264"))
                        this.Arguments += " " + VideoArguments.STREAMING;
                    else
                        this.Arguments += " " + VideoArguments.H265;
                    break;
                case "Workraw":
                    this.Arguments += " " + VideoArguments.WORKRAW;
                    break;
                case "XviD":
                    this.Arguments += " " + VideoArguments.XVID;
                    break;
            }
            if (ca.Count > 0)
            {
                List<GetAudioTracks> temp3 = new List<GetAudioTracks>();
                Int32 AudioCounter = 0;
                if (submode.StartsWith("H"))
                {
                    foreach (GetAudioTracks c in ca)
                    {
                        if (c.Default == true || c.Forced == true)
                        {
                            temp3.Clear();
                            temp3.Add(c);
                        }
                    }
                    if (temp3.Count == 0)
                    {
                        temp3.Clear();
                        temp3.Add(new GetAudioTracks(media.Audio[0], media.filePath));
                    }
                }
                else
                {
                    temp3 = ca;
                }

                foreach (GetAudioTracks c in temp3)
                {
                    AudioProfileSettings AudioArguments = new AudioProfileSettings(prof, quality, c);

                    switch (c.Format)
                    {
                        case "MP3":
                            if (prof.StartsWith("XviD"))
                            {
                                if (c.VBR == true)
                                {
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -b:a:" + AudioCounter + " " + AudioArguments.Bitrate + "k";
                                }
                                else
                                {
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " copy";
                                }
                            }
                            else
                            {
                                if (prof.StartsWith("Work"))
                                {
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -q:a:" + AudioCounter + " " + AudioArguments.Q;
                                }
                                else
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -b:a:" + AudioCounter + " " + AudioArguments.Bitrate + "k";
                            }
                            break;
                        case "AAC":
                            if (prof.StartsWith("Bluray AAC") || prof.StartsWith("PS3") || prof.StartsWith("Xbox") || prof.StartsWith("Streaming"))
                            {
                                if (prof.StartsWith("Streaming") && c.Channels > 2)
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -b:a:" + AudioCounter + " " + AudioArguments.Bitrate + "k";
                                else
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " copy";
                            }
                            else
                            {
                                if (prof.StartsWith("Work"))
                                {
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -q:a:" + AudioCounter + " " + AudioArguments.Q;
                                }
                                else
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -b:a:" + AudioCounter + " " + AudioArguments.Bitrate + "k";
                            }
                            break;
                        case "AC-3":
                            if (prof.StartsWith("Bluray AC3"))
                            {
                                this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " copy";
                            }
                            else
                            {
                                if (prof.StartsWith("Work"))
                                {
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -q:a:" + AudioCounter + " " + AudioArguments.Q;
                                }
                                else
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -b:a:" + AudioCounter + " " + AudioArguments.Bitrate + "k";
                            }
                            break;
                        case "Vorbis":
                            if (prof.StartsWith("Work"))
                            {
                                if (c.Channels > 1)
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -q:a:" + AudioCounter + " " + AudioArguments.Q;
                                else
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " copy";
                            }
                            else
                            {
                                this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -q:a:" + AudioCounter + " " + AudioArguments.Q;
                            }
                            break;
                        default:
                            if (prof.StartsWith("Work"))
                            {
                                if (c.Channels > 2)
                                    this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -q:a:" + AudioCounter + " " + AudioArguments.Q;
                                this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -q:a:" + AudioCounter + " " + AudioArguments.Q;
                            }
                            else
                                this.Arguments += " -map 0:" + c.Index.ToString() + " -c:a:" + AudioCounter + " " + AudioArguments.CODEC + " -ac:a:" + AudioCounter + " " + AudioArguments.Channels + " -b:a:" + AudioCounter + " " + AudioArguments.Bitrate + "k";
                            break;
                    }
                    AudioCounter++;
                }
            }
            else
            {
                this.Arguments += " -an";
            }

            if (submode.StartsWith("S"))
            {
                if (media.Text.Count > 0)
                    this.Arguments += " -map 0:s:? -map 0:t:? -c:t copy -c:s copy";
            }

            if (submode.StartsWith("N"))
            {
                this.Arguments += " -sn";
            }

            this.Arguments += Filters(sub, ff, cv, W, H, submode) + " -aspect " + cv.AspectRatio;

            if (prof.ToLower().Contains("xvid"))
            {
                if (submode.StartsWith("H") || submode.StartsWith("N"))
                {
                    FinalFile = Path.GetDirectoryName(video) + "\\" + Path.GetFileNameWithoutExtension(video) + "[" + prof + " " + H.ToString() + "p, " + quality + "].avi";
                }
                else
                {
                    FinalFile = Path.GetDirectoryName(video) + "\\" + Path.GetFileNameWithoutExtension(video) + "[" + prof + " " + H.ToString() + "p, " + quality + "].mkv";
                    this.Arguments = this.Arguments.Replace(" -f avi", string.Empty);
                }
            }
            else
            {
                if (prof.StartsWith("Work") == true)
                {
                    FinalFile = Path.GetDirectoryName(video) + "\\" + Path.GetFileNameWithoutExtension(video) + "[" + prof + " " + H.ToString() + "p, " + quality + "].mkv";
                }
                else
                {
                    if (submode.StartsWith("H") || submode.StartsWith("N"))
                    {
                        FinalFile = Path.GetDirectoryName(video) + "\\" + Path.GetFileNameWithoutExtension(video) + "[" + prof + " " + H.ToString() + "p, " + quality + "].mp4";
                    }
                    else
                    {
                        FinalFile = Path.GetDirectoryName(video) + "\\" + Path.GetFileNameWithoutExtension(video) + "[" + prof + " " + H.ToString() + "p, " + quality + "].mkv";
                    }
                }
            }

            if (ca != null)
                this.Arguments += " -metadata:s:a title=\"\"";
            this.Arguments += " -metadata description=\"Encoded with " + this.Text + " by Tanoshimi no Sekai Fansub. Come to visit us at https://tnsfansub.com\" \"" + FinalFile + "\"";
            this.Invoke((MethodInvoker)delegate ()
            {
                //Clipboard.SetText(this.Arguments);
            });
            StartEncode();
        }

        public String Filters(List<String> subs, String dir_ff, GetVideoTracks cv, Int32 W, Int32 H, String submode)
        {
            String temp = String.Empty;

            if (cv.Interlaced == true)
            {
                temp += ",yadif";
            }

            if (submode.StartsWith("H"))
            {
                for (Int32 a = 0; a < subs.Count; a++)
                {
                    if (Path.GetExtension(subs[a]) == ".ass")
                        temp += ",\"ass=" + Path.GetFileName(subs[a]) + ":fontsdir=" + Path.GetFileName(dir_ff) + "\"";
                    else
                        temp += ",\"subtitles=" + Path.GetFileName(subs[a]) + ":fontsdir=" + Path.GetFileName(dir_ff) + "\"";
                }
            }

            temp += ResizeAuto(cv.AspectRatio, W, cv.Width, H, cv.Height);
            if (String.IsNullOrWhiteSpace(temp) == false)
            {
                return " -vf " + temp.Trim(',');
            }
            else
                return String.Empty;
        }

        public void STOP()
        {
            try
            {
                if (t != null)
                    t.Abort();
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ffmpeg)))
                {
                    p.Kill();
                }
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(mkvmerge)))
                {
                    p.Kill();
                }
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(scxvid)))
                {
                    p.Kill();
                }
            }
            catch { }
        }

        private void TnA_FormClosing(object sender, FormClosingEventArgs e)
        {
            this.SetTopLevel(true);
            if (ForceClose == true)
            {
                e.Cancel = false;
                CLOSE();
            }
            else
            {
                if (MessageBox.Show("Close this application?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    CLOSE();
                }
                else
                {
                    e.Cancel = true;
                }
            }
        }

        public void CLOSE()
        {
            try
            {
                STOP();
                if (Directory.Exists(temp_folder))
                    Directory.Delete(temp_folder, true);
                if (Directory.Exists(fonts_folder))
                {
                    Directory.Delete(fonts_folder, true);
                }
                foreach (String s in Directory.GetFiles(Path.GetDirectoryName(ffmpeg)))
                {
                    if (Path.GetFileName(s).ToLower().Contains("sub"))
                        System.IO.File.Delete(s);
                }
            }
            catch { }
            IniFile ini = new IniFile(SettingsFile);
            ini.Write("WindowState", this.WindowState.ToString());
            if (manualToolStripMenuItem.CheckState == CheckState.Checked)
                ini.Write("upd_freq", "m");
            if (EveryTimeProgramStartsToolStripMenuItem.CheckState == CheckState.Checked)
                ini.Write("upd_freq", "s");
            if (EveryDayToolStripMenuItem.CheckState == CheckState.Checked)
                ini.Write("upd_freq", "1");
            if (EveryThreeDaysToolStripMenuItem.CheckState == CheckState.Checked)
                ini.Write("upd_freq", "3");
            if (EveryWeekToolStripMenuItem.CheckState == CheckState.Checked)
                ini.Write("upd_freq", "7");
        }

        private void rimuoviIFileSelezionatiToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow d in DGV_video.SelectedRows)
            {
                DGV_video.Rows.Remove(d);
            }
            if (DGV_video.Rows.Count == 0)
            {
                b_start.Enabled = false;
                tb_help.Visible = true;
            }
            tab_autohardsubber.Text = TabFilelistLabel + " (count: " + DGV_video.Rows.Count.ToString() + ")";
        }

        private void esciToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void confermaToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow d in DGV_video.SelectedRows)
            {
                d.Cells[DGV_video.Columns["profile"].Index].Value = cmb_profile.Text;
                String p = d.Cells[DGV_video.Columns["profile"].Index].Value.ToString();
            }
        }

        private void confermaToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow d in DGV_video.SelectedRows)
            {
                d.Cells[DGV_video.Columns["quality"].Index].Value = cmb_quality.Text;
            }
        }

        public static string Arrotonda(double value, int decimalPlaces)
        {
            string formatter = "{0:f" + decimalPlaces + "}";
            return string.Format(formatter, value);
        }

        private void scaricaFFmpegToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DF(false);
        }

        private void DF(Boolean auto)
        {
            df = new DownloadFFMPEG(temp_folder, auto);
            df.ShowInTaskbar = true;
            //df.Activate();
            DialogResult gui = df.ShowDialog();
            while (gui == DialogResult.Abort)
            {
                if (MessageBox.Show("Error while trying to download FFmpeg.\nRetry?", this.Text, MessageBoxButtons.RetryCancel, MessageBoxIcon.Error) == DialogResult.Retry)
                {
                    df = new DownloadFFMPEG(temp_folder, auto);
                    gui = df.ShowDialog();
                }
                else
                    gui = DialogResult.No;
            }
            if (gui == DialogResult.OK)
            {
                if (Environment.Is64BitOperatingSystem == true)
                {
                    try
                    {
                        System.IO.File.Copy(temp_folder + "\\ffmpeg.exe", Path.GetDirectoryName(ffmpeg) + "\\" + Path.GetFileName(ffmpeg), true);
                        MessageBox.Show("FFmpeg downloaded correctly.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        LastUpdDate = DateTime.Now;
                        IniFile ini = new IniFile(SettingsFile);
                        ini.Write("last_upd", LastUpdDate.ToShortDateString());
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                else
                {
                    try
                    {
                        System.IO.File.Copy(temp_folder + "\\ffmpeg.exe", Path.GetDirectoryName(ffmpeg) + "\\" + Path.GetFileName(ffmpeg), true);
                        MessageBox.Show("FFmpeg downloaded correctly.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        LastUpdDate = DateTime.Now;
                        IniFile ini = new IniFile(SettingsFile);
                        ini.Write("last_upd", LastUpdDate.ToShortDateString());

                        Environment.CurrentDirectory = Path.GetDirectoryName(ffmpeg);

                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                foreach (String s in System.IO.Directory.GetFiles(temp_folder, "*", SearchOption.AllDirectories))
                {
                    if (s.ToLower().Contains("license"))
                    {
                        System.IO.File.Copy(s, Path.Combine(Path.GetDirectoryName(ffmpeg), Path.GetFileName(s)), true);
                    }
                }
                foreach (String s in System.IO.Directory.GetDirectories(temp_folder))
                    System.IO.Directory.Delete(s, true);
                foreach (String s in Directory.GetFiles(temp_folder))
                    System.IO.File.Delete(s);

            }
        }

        private void testFFmpegToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (System.IO.Directory.GetFiles(Path.GetDirectoryName(ffmpeg)).Count() > 0)
            {
                foreach (String s in System.IO.Directory.GetFiles(Path.GetDirectoryName(ffmpeg)))
                {
                    if (Path.GetFileName(s).ToLower().Contains(Path.GetFileName(ffmpeg)))
                    {
                        Environment.CurrentDirectory = Path.GetDirectoryName(ffmpeg);
                        System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                        psi.CreateNoWindow = true;
                        psi.UseShellExecute = false;
                        psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                        psi.FileName = "cmd.exe";
                        psi.Arguments = " /c " + Path.GetFileNameWithoutExtension(s) + " -encoders>enc.txt";
                        System.Diagnostics.Process.Start(psi).WaitForExit();
                        psi.Arguments = " /c " + Path.GetFileNameWithoutExtension(s) + " -filters>fil.txt";
                        System.Diagnostics.Process.Start(psi).WaitForExit();
                        if (System.IO.File.Exists(Path.GetDirectoryName(ffmpeg) + "\\enc.txt") && System.IO.File.Exists(Path.GetDirectoryName(ffmpeg) + "\\fil.txt"))
                        {
                            String[] enc = System.IO.File.ReadAllLines(Path.GetDirectoryName(ffmpeg) + "\\enc.txt");
                            System.IO.File.Delete(Path.GetDirectoryName(ffmpeg) + "\\enc.txt");
                            Boolean aac = false, libx264 = false, ac3 = false, vfscale = false, libx265 = false, hqdn3d = false, gradfun = false, xvid = false, mp3 = false, nvenc = false;
                            foreach (String t in enc)
                            {
                                if (t.Contains("AAC (Advanced Audio Coding)"))
                                    aac = true;
                                if (t.Contains("libx264"))
                                    libx264 = true;
                                if (t.Contains("libx265"))
                                    libx265 = true;
                                if (t.Contains("ATSC A/52A (AC-3)"))
                                    ac3 = true;
                                if (t.Contains("libxvid"))
                                    xvid = true;
                                if (t.Contains("libmp3lame"))
                                    mp3 = true;
                                if (t.Contains("nvenc"))
                                    nvenc = true;
                            }
                            String[] filters = System.IO.File.ReadAllLines(Path.GetDirectoryName(ffmpeg) + "\\fil.txt");
                            System.IO.File.Delete(Path.GetDirectoryName(ffmpeg) + "\\fil.txt");
                            Boolean ass = false;
                            foreach (String t in filters)
                            {
                                if (t.Contains("ass") && t.ToLower().Contains("render"))
                                    ass = true;
                                if (t.Contains("scale") && t.Contains("Scale the input video size and/or convert the image format."))
                                    vfscale = true;
                                if (t.Contains("hqdn3d"))
                                    hqdn3d = true;
                                if (t.Contains("gradfun"))
                                    gradfun = true;
                            }
                            TestFFMPEG test = new TestFFMPEG(libx264, aac, ac3, ass, libx265, vfscale, hqdn3d, gradfun, xvid, mp3, nvenc);
                            if (test.ShowDialog() == DialogResult.Yes)
                            {
                                MessageBox.Show("Test result: OK.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                            else
                            {
                                if (MessageBox.Show("Test failed. Download default FFmpeg build?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                                {
                                    scaricaFFmpegToolStripMenuItem_Click(sender, e);
                                    if (df.DialogResult == DialogResult.OK)
                                        testFFmpegToolStripMenuItem_Click(sender, e);
                                }
                            }
                        }
                        break;
                    }
                    else
                    {
                        if (MessageBox.Show("FFmpeg not found. Download it now?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Stop) == DialogResult.Yes)
                        {
                            scaricaFFmpegToolStripMenuItem_Click(sender, e);
                            if (df.DialogResult == DialogResult.OK)
                            {
                                if (MessageBox.Show("Begin FFmpeg test?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                                {
                                    testFFmpegToolStripMenuItem_Click(sender, e);
                                }
                            }
                            else
                                MessageBox.Show("A FFmpeg test is not mandatory.\nIt's recommended if you're not using the default build.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        else
                            MessageBox.Show("FFmpeg.exe is required. This program will not work without it.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    }
                }
            }
            else
            {
                if (MessageBox.Show("FFmpeg not found. Download it now?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Stop) == DialogResult.Yes)
                {
                    scaricaFFmpegToolStripMenuItem_Click(sender, e);
                    if (df.DialogResult == DialogResult.OK)
                    {
                        if (MessageBox.Show("Begin FFmpeg test?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            testFFmpegToolStripMenuItem_Click(sender, e);
                        }
                    }
                    else
                        MessageBox.Show("A FFmpeg test is not mandatory.\nIt's recommended if you're not using the default build.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                    MessageBox.Show("FFmpeg.exe is required. This program will not work without it.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
        }

        private void IncollaToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var data = new System.Collections.Specialized.StringCollection();
            if (Clipboard.GetFileDropList().Count > 0)
                data = Clipboard.GetFileDropList();
            else
            {
                if (Clipboard.ContainsText() == true)
                {
                    String[] c = Clipboard.GetText().Split('\n');
                    for (Int32 i = 0; i < c.Count() - 1; i++)
                        data.Add(c[i]);
                }
            }
            String[] temp = new String[data.Count];
            data.CopyTo(temp, 0);
            RetriveFiles(temp);
        }

        private void salvaImpostazioniToolStripMenuItem_Click(object sender, EventArgs e)
        {
            IniFile ini = new IniFile(SettingsFile);

            ini.Write("profile", cmb_profile.Text);
            ini.Write("res", cmb_resolution.Text);
            ini.Write("qual", cmb_quality.Text);
            ini.Write("subs", cmb_subs.Text);

            if (ActivateClipboardMonitoringToolStripMenuItem.CheckState == CheckState.Checked)
                ini.Write("monit_clipb", "y");
            else
                ini.Write("monit_clipb", "n");
        }

        private void bgw_update_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                if (DateTime.Now.ToShortDateString().CompareTo(OldDate) > 0)
                {
                    DateTime data = new DateTime(Convert.ToInt32(OldDate.Split('/')[2]), Convert.ToInt32(OldDate.Split('/')[1]), Convert.ToInt32(OldDate.Split('/')[0]));
                    TimeSpan t = new TimeSpan();
                    t = DateTime.Today - data;
                    if (t.TotalDays <= 1)
                    {
                        if (MessageBox.Show("This FFmpeg build is " + t.TotalDays.ToString() + " day old.\nUpdate?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            DF(true);
                        }
                    }
                    else
                    {
                        if (MessageBox.Show("This FFmpeg build is " + t.TotalDays.ToString() + " days old.\nUpdate?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            DF(true);
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                MessageBox.Show("Date not valid: " + OldDate + "\n\n" + exc.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                //IniFile ini = new IniFile(file_settings);
                //ini.Write("last_upd", DateTime.Now.ToShortDateString());
            }
        }

        private void bgw_update_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            this.Enabled = true;
            this.Activate();
        }

        private void bgw_downloadffmpeg_DoWork(object sender, DoWorkEventArgs e)
        {
            if (MessageBox.Show("FFmpeg not found. Download it now?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                DF(true);
            else
                MessageBox.Show("FFmpeg.exe is required. This program will not work without it.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
        }

        private void bgw_downloadffmpeg_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            this.Enabled = true;
            this.Activate();
        }

        private void infoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            About a = new About(this.Text, this.Text.Split(' ')[5]);
            a.Icon = this.Icon;
            a.ShowInTaskbar = false;
            a.ShowDialog();
        }

        private void ll_icons_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start(ll_icons.Text);
        }

        private void b_agg_files_Click(object sender, EventArgs e)
        {
            open_video.Filter = Filter;
            if (open_video.ShowDialog() == DialogResult.OK)
            {
                foreach (String s in open_video.FileNames)
                {
                    DGV_video.Rows.Add(Path.GetFileName(s), cmb_profile.Text, cmb_resolution.Text, cmb_quality.Text, cmb_subs.Text, "READY", Path.GetDirectoryName(s));
                }
                b_start.Enabled = true;
                tab_autohardsubber.Text = TabFilelistLabel + " (count: " + DGV_video.Rows.Count.ToString() + ")";
                tb_help.Visible = false;
                CheckPathLength();
            }
        }

        void CheckPathLength()
        {
            if (DGV_video.Rows.Count > 0)
            {
                for (Int32 i = 0; i < DGV_video.Rows.Count; i++)
                {
                    if (Path.Combine(DGV_video.Rows[i].Cells["source_path"].Value.ToString(), DGV_video.Rows[i].Cells["input"].Value.ToString()).Length > 220)
                    {
                        DGV_video.Rows[i].Cells["input"].Style.BackColor = Color.Orange;
                    }
                }
                DGV_video.ClearSelection();
            }
        }

        private void b_agg_cart_Click(object sender, EventArgs e)
        {
            if (open_folder.ShowDialog() == DialogResult.OK)
            {
                FormatSelection sf = new FormatSelection(VideoEXT);
                sf.Icon = this.Icon;
                sf.TopLevel = true;
                sf.StartPosition = FormStartPosition.CenterParent;
                sf.ShowInTaskbar = false;
                if (sf.ShowDialog() == DialogResult.OK)
                {
                    foreach (String s in Directory.GetFiles(open_folder.SelectedPath, "*", SearchOption.AllDirectories))
                    {
                        if (TnA___Tanoshimi_no_Autohardsubber.FormatSelection.Formats.Contains(Path.GetExtension(s).ToLower()) == true)
                        {
                            DGV_video.Rows.Add(Path.GetFileName(s), cmb_profile.Text, cmb_resolution.Text, cmb_quality.Text, cmb_subs.Text, "READY", Path.GetDirectoryName(s));
                        }
                    }
                }
                if (DGV_video.Rows.Count > 0)
                {
                    b_start.Enabled = true;
                    tb_help.Visible = false;
                    tab_autohardsubber.Text = TabFilelistLabel + " (count: " + DGV_video.Rows.Count.ToString() + ")";
                }
                CheckPathLength();
            }
        }

        private void b_incolla_Click(object sender, EventArgs e)
        {
            IncollaToolStripMenuItem_Click(sender, e);
        }

        private void B_rimuovi_Click(object sender, EventArgs e)
        {
            rimuoviIFileSelezionatiToolStripMenuItem_Click(sender, e);
        }

        private void TnA_ResizeEnd(object sender, EventArgs e)
        {
            IniFile ini = new IniFile(SettingsFile);
            ini.Write("Width", this.Width.ToString());
            ini.Write("Heigth", this.Height.ToString());
        }

        private void MonitoraGliAppuntiToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (ActivateClipboardMonitoringToolStripMenuItem.CheckState == CheckState.Checked)
            {
                ActivateClipboardMonitoringToolStripMenuItem.CheckState = CheckState.Unchecked;
            }
            else
            {
                ActivateClipboardMonitoringToolStripMenuItem.CheckState = CheckState.Checked;
            }
        }

        private void ripristinaImpostazioniToolStripMenuItem3_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Reset settings and restart application?", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                ForceClose = true;
                if (System.IO.File.Exists(SettingsFile))
                {
                    System.IO.File.Delete(SettingsFile);
                }

                System.IO.File.Delete(ffmpeg);

                foreach (String s in System.IO.Directory.GetFiles(LOG_dir, "*", SearchOption.TopDirectoryOnly))
                {
                    System.IO.File.Delete(s);
                }
                Application.Restart();
            }
        }

        private void manualeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (manualToolStripMenuItem.CheckState == CheckState.Unchecked)
            {
                manualToolStripMenuItem.CheckState = CheckState.Checked;
                EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryDayToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryThreeDaysToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryWeekToolStripMenuItem.CheckState = CheckState.Unchecked;
            }
        }

        private void iToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (EveryTimeProgramStartsToolStripMenuItem.CheckState == CheckState.Unchecked)
            {
                manualToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Checked;
                EveryDayToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryThreeDaysToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryWeekToolStripMenuItem.CheckState = CheckState.Unchecked;
            }
        }

        private void unGiornoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (EveryDayToolStripMenuItem.CheckState == CheckState.Unchecked)
            {
                manualToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryDayToolStripMenuItem.CheckState = CheckState.Checked;
                EveryThreeDaysToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryWeekToolStripMenuItem.CheckState = CheckState.Unchecked;
            }
        }

        private void ogniTreGiorniToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (EveryThreeDaysToolStripMenuItem.CheckState == CheckState.Unchecked)
            {
                manualToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryDayToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryThreeDaysToolStripMenuItem.CheckState = CheckState.Checked;
                EveryWeekToolStripMenuItem.CheckState = CheckState.Unchecked;
            }
        }

        private void ogniSettimanaToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (EveryWeekToolStripMenuItem.CheckState == CheckState.Unchecked)
            {
                manualToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryTimeProgramStartsToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryDayToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryThreeDaysToolStripMenuItem.CheckState = CheckState.Unchecked;
                EveryWeekToolStripMenuItem.CheckState = CheckState.Checked;
            }
        }

        private void oKToolStripMenuItem_Click(object sender, EventArgs e)
        {
            for (Int32 i = DGV_video.Rows.Count - 1; i >= 0; i--)
            {
                if (DGV_video.Rows[i].Cells[DGV_video.Columns["status"].Index].Value.ToString().Trim().ToLower().Contains(oKToolStripMenuItem.Text.ToLower().Trim()))
                    DGV_video.Rows.RemoveAt(DGV_video.Rows[i].Index);
            }
        }

        private void fermatoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            for (Int32 i = DGV_video.Rows.Count - 1; i >= 0; i--)
            {
                if (DGV_video.Rows[i].Cells[DGV_video.Columns["status"].Index].Value.ToString().ToLower().Trim().Contains(StoppedToolStripMenuItem.Text.ToLower().Trim()))
                    DGV_video.Rows.RemoveAt(DGV_video.Rows[i].Index);
            }
        }

        private void timer_tempo_Tick(object sender, EventArgs e)
        {
            TimeSpan tp = TimeSpan.FromSeconds((Double)ElapsedSeconds);
            l_elapsed_time.Text = "Elapsed time: " + tp.ToString(@"hh\:mm\:ss");
            ElapsedSeconds++;
        }

        private void creaToolStripMenuItem_Click(object sender, EventArgs e)
        {
            String link = Environment.GetFolderPath(Environment.SpecialFolder.SendTo) + Path.DirectorySeparatorChar + Path.GetFileNameWithoutExtension(Application.ExecutablePath) + ".lnk";
            var shell = new WshShell();
            var shortcut = shell.CreateShortcut(link) as IWshShortcut;
            shortcut.TargetPath = Application.ExecutablePath;
            shortcut.WorkingDirectory = Application.StartupPath;
            shortcut.Save();
        }

        private void cancellaToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                System.IO.File.Delete(Environment.GetFolderPath(Environment.SpecialFolder.SendTo) + "\\" + Path.GetFileNameWithoutExtension(Application.ExecutablePath) + ".lnk");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.ToString());
            }
        }

        private void toolStripComboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            WhenAllIsFinished = ActionWhenFinishedToolStripComboBox.Text;
        }

        private void DGV_video_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (DGV_video.Rows.Count > 0)
            {
                if (e.ColumnIndex == DGV_video.Columns["profile"].Index)
                {
                    String p = DGV_video.Rows[e.RowIndex].Cells[DGV_video.Columns["profile"].Index].Value.ToString();
                }
            }
        }

        private void b_pause_Click(object sender, EventArgs e)
        {
            PauseResume();
        }

        void PauseResume()
        {
            if (pause == false)
            {

                if (DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["profile"].Index].Value.ToString().ToLower().Contains("mkv") == false && DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["profile"].Index].Value.ToString().ToLower().Contains("keyframe") == false)
                    SuspendProcess(EncodeProcess.Id);
                else
                {
                    if (DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["profile"].Index].Value.ToString().ToLower().Contains("mkv"))
                        SuspendProcess(RemuxProcess.Id);
                    else
                    {
                        SuspendProcess(EncodeProcess.Id);
                        SuspendProcess(EncodeProcess.Id);
                    }
                }
                pause = true;
                b_pause.Image = new Bitmap(TnA___Tanoshimi_no_Autohardsubber.Properties.Resources.play, new Size(TnA___Tanoshimi_no_Autohardsubber.Properties.Resources.pause.Width, TnA___Tanoshimi_no_Autohardsubber.Properties.Resources.pause.Height));
                b_pause.Text = "RESUME";
            }
            else
            {

                if (DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["profile"].Index].Value.ToString().ToLower().Contains("mkv") == false && DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["profile"].Index].Value.ToString().ToLower().Contains("keyframe") == false)
                    ResumeProcess(EncodeProcess.Id);
                else
                {
                    if (DGV_video.Rows[PercentageIndex].Cells[DGV_video.Columns["profile"].Index].Value.ToString().ToLower().Contains("mkv"))
                        ResumeProcess(RemuxProcess.Id);
                    else
                    {
                        ResumeProcess(EncodeProcess.Id);
                        ResumeProcess(EncodeProcess.Id);
                    }
                }
                pause = false;
                b_pause.Image = TnA___Tanoshimi_no_Autohardsubber.Properties.Resources.pause;
                b_pause.Text = "PAUSE";
            }
        }

        private void erroreToolStripMenuItem_Click(object sender, EventArgs e)
        {
            for (Int32 i = DGV_video.Rows.Count - 1; i >= 0; i--)
            {
                if (DGV_video.Rows[i].Cells[DGV_video.Columns["status"].Index].Value.ToString().ToLower().Trim().Contains(ErrorToolStripMenuItem.Text.ToLower().Trim()))
                    DGV_video.Rows.RemoveAt(DGV_video.Rows[i].Index);
            }
        }

        private void controllaAggiornamentiToolStripMenuItem_Click(object sender, EventArgs e)
        {
            controlla_aggiornamenti(false);
        }

        public void controlla_aggiornamenti(Boolean Hidden)
        {
            try
            {
                Updater.TnA_Updater updater = new Updater.TnA_Updater(this.Text.Split(' ')[5], Hidden, this.Icon, this.Text, false, Application.StartupPath);
                try
                {
                    switch (updater.DialogResult)
                    {
                        case DialogResult.Yes:
                            NewVersionAvailableToolStripMenuItem.Visible = true;
                            CheckUpdatesToolStripMenuItem.Visible = false;
                            VersionHistoryToolStripMenuItem.Visible = false;
                            break;
                        case DialogResult.None:
                            NewVersionAvailableToolStripMenuItem.Visible = false;
                            CheckUpdatesToolStripMenuItem.Visible = true;
                            VersionHistoryToolStripMenuItem.Visible = true;
                            break;
                            //default:
                            //    updater.ShowDialog();
                            //    break;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void confermaToolStripMenuItem2_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow d in DGV_video.SelectedRows)
            {
                d.Cells[DGV_video.Columns["res"].Index].Value = cmb_resolution.Text;
            }
        }

        private void NuovaVersioneDisponibileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            controlla_aggiornamenti(false);
        }

        private void Bgw_updateschecker_DoWork(object sender, DoWorkEventArgs e)
        {
            controlla_aggiornamenti(true);
        }

        private void ApriCartellaLogsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start(LOG_dir);
        }

        private void ConfermaToolStripMenuItem3_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow d in DGV_video.SelectedRows)
            {
                d.Cells[DGV_video.Columns["subtitle_mode"].Index].Value = cmb_subs.Text;
            }
        }

        private void Rtb_sottotitoli_LinkClicked(object sender, LinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start(e.LinkText);
        }

        private void cancellaSelezioneToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DGV_video.ClearSelection();
        }

        private void AllFilesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DGV_video.Rows.Clear();
        }

        private void VisualizzaCronologiaVersioniiToolStripMenuItem_Click(object sender, EventArgs e)
        {
            VersionHistory c = new VersionHistory(this.Text, this.Icon);
            c.ShowDialog();
        }

        private void DGV_video_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            DGV_video.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    class IniFile
    {
        string Path;
        string EXE = Assembly.GetExecutingAssembly().GetName().Name;

        [DllImport("kernel32")]
        static extern long WritePrivateProfileString(string Section, string Key, string Value, string FilePath);

        [DllImport("kernel32")]
        static extern int GetPrivateProfileString(string Section, string Key, string Default, StringBuilder RetVal, int Size, string FilePath);

        public IniFile(string IniPath = null)
        {
            Path = new FileInfo(IniPath ?? EXE + ".ini").FullName.ToString();
        }

        public string Read(string Key, string Section = null)
        {
            var RetVal = new StringBuilder(255);
            GetPrivateProfileString(Section ?? EXE, Key, "", RetVal, 255, Path);
            return RetVal.ToString();
        }

        public void Write(string Key, string Value, string Section = null)
        {
            WritePrivateProfileString(Section ?? EXE, Key, Value, Path);
        }

        public void DeleteKey(string Key, string Section = null)
        {
            Write(Key, null, Section ?? EXE);
        }

        public void DeleteSection(string Section = null)
        {
            Write(null, null, Section ?? EXE);
        }

        public bool KeyExists(string Key, string Section = null)
        {
            return Read(Key, Section).Length > 0;
        }

    }

    public class GetAudioTracks
    {
        public String Format { get; set; }
        public String Profile { get; set; }
        public Double Channels { get; set; }
        public Int32 Index { get; set; }
        public Boolean VBR { get; set; }
        public Boolean Forced { get; set; }
        public Boolean Default { get; set; }
        public Boolean Lossless { get; set; }

        public GetAudioTracks(MediaInfoDotNet.Models.AudioStream a, String File)
        {
            Get(a, File);
        }

        protected void Get(MediaInfoDotNet.Models.AudioStream a, String File)
        {
            Forced = a.Forced;
            Default = a.Default;
            Format = a.Format;

            if (a.CompressionMode.ToLower().EndsWith("y"))
                Lossless = false;
            else
                Lossless = true;

            if (String.IsNullOrWhiteSpace(Format))
            {
                Format = String.Empty;
            }

            Profile = a.FormatProfile;

            if (String.IsNullOrWhiteSpace(Profile))
            {
                Profile = String.Empty;
            }

            if (System.IO.Path.GetExtension(File).ToLower().Contains("ts") == false)
            {
                Index = a.ID - 1;
            }
            else
            {
                String Binary = a.ID.ToString();
                Binary = Binary.Substring(Binary.IndexOf("("));
                Binary = Binary.Trim('(');
                Binary = Binary.Trim(')');
                Binary = Binary.Replace("0x", String.Empty);
                Index = Convert.ToInt32(Binary, 2);
                Index = Index - (Convert.ToInt32(Binary, 2) - 1);
            }

            Channels = Convert.ToDouble(a.Channels);

            if (a.BitRateMode == "CBR")
            {
                VBR = true;
            }
            else
            {
                VBR = false;
            }
        }
    }

    public class GetVideoTracks
    {
        public String File { get; set; }
        public Int32 Index { get; set; }
        public String AspectRatio { get; set; }
        public Double Width { get; set; }
        public Double Height { get; set; }
        public String Framerate { get; set; }
        public Boolean Interlaced { get; set; }
        public TimeSpan Duration { get; set; }
        public Int32 Framecount { get; set; }

        public GetVideoTracks(MediaFile media)
        {
            Get(media);
        }

        protected void Get(MediaFile media)
        {
            File = media.filePath;
            if (System.IO.Path.GetExtension(File).ToLower().Contains("ts") == false)
            {
                Index = media.Video[0].ID - 1;
            }
            else
            {
                MediaInfo MI = new MediaInfo();
                MI.Open(File);
                String Binary = String.Empty;
                foreach (String s in MI.Inform().Split('\n'))
                {
                    if (s.Trim().ToLower().StartsWith("video"))
                    {
                        Binary = Binary.Substring(Binary.IndexOf("("));
                        Binary = Binary.Trim('(');
                        Binary = Binary.Trim(')');
                        Binary = Binary.Replace("0x", String.Empty);
                        Index = Convert.ToInt32(Binary, 2);
                        Index = Index - (Convert.ToInt32(Binary, 2) - 1);
                    }
                }
            }
            AspectRatio = media.Video[0].DisplayAspectRatio.ToString().Replace(",", ".");
            if (String.IsNullOrWhiteSpace(AspectRatio))
                AspectRatio = String.Empty;
            Framerate = (media.Video[0].FrameRate).ToString();
            Duration = TimeSpan.FromMilliseconds(media.Video[0].Duration);
            Framecount = media.Video[0].FrameCount;
            switch (media.Video[0].miGetString("ScanType").ToLower().Trim())
            {
                case "progressive":
                    Interlaced = false;
                    break;
                case "interlaced":
                    Interlaced = true;
                    break;
                default:
                    Interlaced = false;
                    break;
            }
            Width = media.Video[0].Width;
            Height = media.Video[0].Height;
        }
    }

    public class EncodeProfileSettings
    {
        public class Parameters
        {
            public String BLURAY { get; }
            public String STREAMING { get; }
            public String XVID { get; }
            public String WORKRAW { get; }
            public String H265 { get; }

            public Parameters(MediaFile media)
            {
                BLURAY = " -profile:v high -level:v 4.1 -bluray-compat 1 -maxrate 50000k -bufsize 70000k -pix_fmt yuv420p";
                STREAMING = " -maxrate 20000k -bufsize 20000k -profile:v high -level:v 4.1 -pix_fmt yuv420p -bluray-compat 1 -x264opts cabac=0:weightp=0:weightb=0:sync-lookahead=0:sliced-threads=1:b-pyramid=0 -keyint_min " + CalculateGOP(media).ToString() + " -g " + CalculateGOP(media).ToString();
                XVID = " -fflags +genpts -f avi -vtag XVID -bf 2 -level 5 -use_odml -1 -qmax 10 -qmin 1 -pix_fmt yuv420p -flags +mv4+loop+qpel+aic -qcomp 1.0 -subcmp 7 -mbcmp 7 -precmp 7 -subq 11 -me_range 1023 -mbd rd -profile:v mpeg4_asp -trellis 2";
                WORKRAW = " -profile:v high -level:v 4.1 -pix_fmt yuv420p -maxrate 20000k -bufsize 20000k -partitions +parti4x4+parti8x8+partp8x8+partb8x8 -x264opts b-pyramid=0 -sn -tune fastdecode";
                H265 = " -profile:v main -maxrate 50000k -bufsize 70000k -pix_fmt yuv420p -x265-params pmode=1:pme=1:psy-rd=4:rdoq-level=1:psy-rdoq=10:weightb=1:me=umh:subme=4:rdpenalty=1:open-gop=0:rc-lookahead=40:bframes=4:rd=4:b-adapt=2";
            }

            protected Int32 CalculateGOP(MediaFile media)
            {
                return (Int32)Math.Ceiling(Convert.ToDecimal(media.Video[0].FrameRate)) * 10;
            }
        }

        public class VideoQuality
        {
            public class VeryHigh
            {
                public Double CRF { get; }
                public Double QP { get; }
                public String Preset { get; }
                public Double Bitrate { get; }
                public Double AQmode { get; }
                public Double AQstrength { get; }
                public String CODEC { get; }

                public VeryHigh(String Profile)
                {
                    if (Profile.ToLower().Contains("xvid") == true)
                    {
                        QP = 1;
                        CRF = Double.NaN;
                        Preset = String.Empty;
                        AQmode = Double.NaN;
                        AQstrength = Double.NaN;
                        CODEC = "libxvid";
                        Bitrate = Double.NaN;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw") == true)
                        {
                            CRF = 20;
                            QP = Double.NaN;
                            AQstrength = 1.5;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("h.265") == true)
                            {
                                CRF = 17;
                                QP = Double.NaN;
                            }
                            else
                            {
                                CRF = 14;
                                QP = Double.NaN;
                            }
                        }
                        if (Profile.ToLower().Contains("h.265") == true)
                            CODEC = "libx265";
                        else
                            CODEC = "libx264";
                        Preset = "veryslow";
                        AQmode = 2;
                        Bitrate = Double.NaN;
                    }
                }
            }
            public class High
            {
                public Double CRF { get; }
                public Double QP { get; }
                public String Preset { get; }
                public Double Bitrate { get; }
                public Double AQmode { get; }
                public Double AQstrength { get; }
                public String CODEC { get; }

                public High(String Profile)
                {
                    if (Profile.ToLower().Contains("xvid") == true)
                    {
                        QP = 2;
                        CRF = Double.NaN;
                        Preset = String.Empty;
                        AQmode = Double.NaN;
                        AQstrength = Double.NaN;
                        CODEC = "libxvid";
                        Bitrate = Double.NaN;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw") == true)
                        {
                            CRF = 23;
                            QP = Double.NaN;
                            AQstrength = 1.5;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("h.265") == true)
                            {
                                CRF = 20;
                                QP = Double.NaN;
                            }
                            else
                            {
                                CRF = 17;
                                QP = Double.NaN;
                            }
                        }
                        if (Profile.ToLower().Contains("h.265") == true)
                            CODEC = "libx265";
                        else
                            CODEC = "libx264";
                        Preset = "veryslow";
                        AQmode = 2;
                        Bitrate = Double.NaN;
                    }
                }
            }
            public class AboveNormal
            {
                public Double CRF { get; }
                public Double QP { get; }
                public String Preset { get; }
                public Double Bitrate { get; }
                public Double AQmode { get; }
                public Double AQstrength { get; }
                public String CODEC { get; }

                public AboveNormal(String Profile)
                {
                    if (Profile.ToLower().Contains("xvid") == true)
                    {
                        QP = 3;
                        CRF = Double.NaN;
                        Preset = String.Empty;
                        AQmode = Double.NaN;
                        AQstrength = Double.NaN;
                        CODEC = "libxvid";
                        Bitrate = Double.NaN;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw") == true)
                        {
                            CRF = 26;
                            QP = Double.NaN;
                            AQstrength = 1.6;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("h.265") == true)
                            {
                                CRF = 21.5;
                                QP = Double.NaN;
                            }
                            else
                            {
                                CRF = 18.5;
                                QP = Double.NaN;
                            }
                        }
                        if (Profile.ToLower().Contains("h.265") == true)
                            CODEC = "libx265";
                        else
                            CODEC = "libx264";
                        Preset = "slower";
                        AQmode = 2;
                        Bitrate = Double.NaN;
                    }
                }
            }
            public class Normal
            {
                public Double CRF { get; }
                public Double QP { get; }
                public String Preset { get; }
                public Double Bitrate { get; }
                public Double AQmode { get; }
                public Double AQstrength { get; }
                public String CODEC { get; }

                public Normal(String Profile)
                {
                    if (Profile.ToLower().Contains("xvid") == true)
                    {
                        QP = 4;
                        CRF = Double.NaN;
                        Preset = String.Empty;
                        AQmode = Double.NaN;
                        AQstrength = Double.NaN;
                        CODEC = "libxvid";
                        Bitrate = Double.NaN;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw") == true)
                        {
                            CRF = 29;
                            QP = Double.NaN;
                            AQstrength = 1.9;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("h.265") == true)
                            {
                                CRF = 23;
                                QP = Double.NaN;
                            }
                            else
                            {
                                CRF = 20;
                                QP = Double.NaN;
                            }
                        }
                        if (Profile.ToLower().Contains("h.265") == true)
                            CODEC = "libx265";
                        else
                            CODEC = "libx264";
                        Preset = "medium";
                        AQmode = 3;
                        Bitrate = Double.NaN;
                    }
                }
            }
            public class BelowNormal
            {
                public Double CRF { get; }
                public Double QP { get; }
                public String Preset { get; }
                public Double Bitrate { get; }
                public Double AQmode { get; }
                public Double AQstrength { get; }
                public String CODEC { get; }

                public BelowNormal(String Profile)
                {
                    if (Profile.ToLower().Contains("xvid") == true)
                    {
                        QP = 5;
                        CRF = Double.NaN;
                        Preset = String.Empty;
                        AQmode = Double.NaN;
                        AQstrength = Double.NaN;
                        CODEC = "libxvid";
                        Bitrate = Double.NaN;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw") == true)
                        {
                            CRF = 32;
                            QP = Double.NaN;
                            AQstrength = 2;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("h.265") == true)
                            {
                                CRF = 25;
                                QP = Double.NaN;
                            }
                            else
                            {
                                CRF = 22;
                                QP = Double.NaN;
                            }
                        }
                        if (Profile.ToLower().Contains("h.265") == true)
                            CODEC = "libx265";
                        else
                            CODEC = "libx264";
                        Preset = "fast";
                        AQmode = 3;
                        Bitrate = Double.NaN;
                    }
                }
            }
            public class Low
            {
                public Double CRF { get; }
                public Double QP { get; }
                public String Preset { get; }
                public Double Bitrate { get; }
                public Double AQmode { get; }
                public Double AQstrength { get; }
                public String CODEC { get; }

                public Low(String Profile)
                {
                    if (Profile.ToLower().Contains("xvid") == true)
                    {
                        QP = 6;
                        CRF = Double.NaN;
                        Preset = String.Empty;
                        AQmode = Double.NaN;
                        AQstrength = Double.NaN;
                        CODEC = "libxvid";
                        Bitrate = Double.NaN;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw") == true)
                        {
                            CRF = 35;
                            QP = Double.NaN;
                            AQstrength = 2;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("h.265") == true)
                            {
                                CRF = 28;
                                QP = Double.NaN;
                            }
                            else
                            {
                                CRF = 25;
                                QP = Double.NaN;
                            }
                        }
                        if (Profile.ToLower().Contains("h.265") == true)
                            CODEC = "libx265";
                        else
                            CODEC = "libx264";
                        Preset = "fast";
                        AQmode = 3;
                        Bitrate = Double.NaN;
                    }
                }
            }
            public class VeryLow
            {
                public Double CRF { get; }
                public Double QP { get; }
                public String Preset { get; }
                public Double Bitrate { get; }
                public Double AQmode { get; }
                public Double AQstrength { get; }
                public String CODEC { get; }

                public VeryLow(String Profile)
                {
                    if (Profile.ToLower().Contains("xvid") == true)
                    {
                        QP = 7;
                        CRF = Double.NaN;
                        Preset = String.Empty;
                        AQmode = Double.NaN;
                        AQstrength = Double.NaN;
                        CODEC = "libxvid";
                        Bitrate = Double.NaN;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw") == true)
                        {
                            CRF = 38;
                            QP = Double.NaN;
                            AQstrength = 2;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("h.265") == true)
                            {
                                CRF = 30;
                                QP = Double.NaN;
                            }
                            else
                            {
                                CRF = 27;
                                QP = Double.NaN;
                            }
                        }
                        if (Profile.ToLower().Contains("h.265") == true)
                            CODEC = "libx265";
                        else
                            CODEC = "libx264";
                        Preset = "faster";
                        AQmode = 3;
                        Bitrate = Double.NaN;
                    }
                }
            }
            public class Draft
            {
                public Double CRF { get; }
                public Double QP { get; }
                public String Preset { get; }
                public Double Bitrate { get; }
                public Double AQmode { get; }
                public Double AQstrength { get; }
                public String CODEC { get; }

                public Draft(String Profile)
                {
                    if (Profile.ToLower().Contains("xvid") == true)
                    {
                        QP = 10;
                        CRF = Double.NaN;
                        Preset = String.Empty;
                        AQmode = Double.NaN;
                        AQstrength = Double.NaN;
                        CODEC = "libxvid";
                        Bitrate = Double.NaN;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw") == true)
                        {
                            CRF = 45;
                            QP = Double.NaN;
                            AQstrength = 2;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("h.265") == true)
                            {
                                CRF = 39;
                                QP = Double.NaN;
                            }
                            else
                            {
                                CRF = 36;
                                QP = Double.NaN;
                            }
                        }
                        if (Profile.ToLower().Contains("h.265") == true)
                            CODEC = "libx265";
                        else
                            CODEC = "libx264";
                        Preset = "superfast";
                        AQmode = 1;
                        Bitrate = Double.NaN;
                    }
                }
            }
        }
    }

    public class AudioProfileSettings
    {
        public Double Bitrate { get; set; }
        public Double Q { get; set; }
        public Double Channels { get; set; }
        public String CODEC { get; set; }

        public AudioProfileSettings(String Profile, String Quality, GetAudioTracks Audio)
        {
            switch (Quality)
            {
                case "Very high":
                    if (Profile.ToLower().Contains("xvid"))
                    {
                        Bitrate = 160 * Convert.ToInt32(Audio.Channels);
                        Q = Double.NaN;
                        CODEC = "libmp3lame";
                        if (Audio.Channels >= 2)
                            Channels = 2;
                        else
                            Channels = Audio.Channels;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw"))
                        {
                            if (Audio.Channels >= 1)
                                Channels = 1;
                            else
                                Channels = Audio.Channels;
                            Bitrate = Double.NaN;
                            Q = 7;
                            CODEC = "libvorbis";
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("ac3"))
                            {
                                Bitrate = 96 * Convert.ToInt32(Audio.Channels);
                                Q = Double.NaN;
                                CODEC = "ac3";
                                Channels = Audio.Channels;
                            }
                            else
                            {
                                if (Profile.ToLower().Contains("streaming"))
                                {
                                    if (Audio.Channels >= 2)
                                        Channels = 2;
                                    else
                                        Channels = Audio.Channels;
                                }
                                else
                                    Channels = Audio.Channels;
                                Bitrate = 192 * Convert.ToInt32(Channels) / 2;
                                Q = Double.NaN;
                                CODEC = "aac";
                            }
                        }
                    }
                    break;
                case "High":
                    if (Profile.ToLower().Contains("xvid"))
                    {
                        Bitrate = 128 * Convert.ToInt32(Audio.Channels);
                        Q = Double.NaN;
                        CODEC = "libmp3lame";
                        if (Audio.Channels >= 2)
                            Channels = 2;
                        else
                            Channels = Audio.Channels;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw"))
                        {
                            if (Audio.Channels >= 1)
                                Channels = 1;
                            else
                                Channels = Audio.Channels;
                            Bitrate = Double.NaN;
                            Q = 6;
                            CODEC = "libvorbis";
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("ac3"))
                            {
                                Bitrate = 96 * Convert.ToInt32(Audio.Channels);
                                Q = Double.NaN;
                                CODEC = "ac3";
                                Channels = Audio.Channels;
                            }
                            else
                            {
                                if (Profile.ToLower().Contains("streaming"))
                                {
                                    if (Audio.Channels >= 2)
                                        Channels = 2;
                                    else
                                        Channels = Audio.Channels;
                                }
                                else
                                    Channels = Audio.Channels;
                                Bitrate = 160 * Convert.ToInt32(Channels) / 2;
                                Q = Double.NaN;
                                CODEC = "aac";
                            }
                        }
                    }
                    break;
                case "Above normal":
                    if (Profile.ToLower().Contains("xvid"))
                    {
                        Bitrate = 112 * Convert.ToInt32(Audio.Channels);
                        Q = Double.NaN;
                        CODEC = "libmp3lame";
                        if (Audio.Channels >= 2)
                            Channels = 2;
                        else
                            Channels = Audio.Channels;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw"))
                        {
                            if (Audio.Channels >= 1)
                                Channels = 1;
                            else
                                Channels = Audio.Channels;
                            Bitrate = Double.NaN;
                            Q = 5;
                            CODEC = "libvorbis";
                            Channels = Audio.Channels;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("ac3"))
                            {
                                Bitrate = 80 * Convert.ToInt32(Audio.Channels);
                                Q = Double.NaN;
                                CODEC = "ac3";
                                Channels = Audio.Channels;
                            }
                            else
                            {
                                if (Profile.ToLower().Contains("streaming"))
                                {
                                    if (Audio.Channels >= 2)
                                        Channels = 2;
                                    else
                                        Channels = Audio.Channels;
                                }
                                else
                                    Channels = Audio.Channels;
                                Bitrate = 144 * Convert.ToInt32(Channels) / 2;
                                Q = Double.NaN;
                                CODEC = "aac";
                            }
                        }
                    }
                    break;
                case "Normal":
                    if (Profile.ToLower().Contains("xvid"))
                    {
                        Bitrate = 96 * Convert.ToInt32(Audio.Channels);
                        Q = Double.NaN;
                        CODEC = "libmp3lame";
                        if (Audio.Channels >= 2)
                            Channels = 2;
                        else
                            Channels = Audio.Channels;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw"))
                        {
                            Bitrate = Double.NaN;
                            Q = 4;
                            CODEC = "libvorbis";
                            if (Audio.Channels >= 1)
                                Channels = 1;
                            else
                                Channels = Audio.Channels;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("ac3"))
                            {
                                Bitrate = 80 * Convert.ToInt32(Audio.Channels);
                                Q = Double.NaN;
                                CODEC = "ac3";
                                Channels = Audio.Channels;
                            }
                            else
                            {
                                if (Profile.ToLower().Contains("streaming"))
                                {
                                    if (Audio.Channels >= 2)
                                        Channels = 2;
                                    else
                                        Channels = Audio.Channels;
                                }
                                else
                                    Channels = Audio.Channels;
                                Bitrate = 128 * Convert.ToInt32(Channels) / 2;
                                Q = Double.NaN;
                                CODEC = "aac";
                            }
                        }
                    }
                    break;
                case "Below normal":
                    if (Profile.ToLower().Contains("xvid"))
                    {
                        Bitrate = 80 * Convert.ToInt32(Audio.Channels);
                        Q = Double.NaN;
                        CODEC = "libmp3lame";
                        if (Audio.Channels >= 2)
                            Channels = 2;
                        else
                            Channels = Audio.Channels;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw"))
                        {
                            Bitrate = Double.NaN;
                            Q = 3;
                            CODEC = "libvorbis";
                            if (Audio.Channels >= 1)
                                Channels = 1;
                            else
                                Channels = Audio.Channels;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("ac3"))
                            {
                                Bitrate = 80 * Convert.ToInt32(Audio.Channels);
                                Q = Double.NaN;
                                CODEC = "ac3";
                                Channels = Audio.Channels;
                            }
                            else
                            {
                                if (Profile.ToLower().Contains("streaming"))
                                {
                                    if (Audio.Channels >= 2)
                                        Channels = 2;
                                    else
                                        Channels = Audio.Channels;
                                }
                                else
                                    Channels = Audio.Channels;
                                Bitrate = 96 * Convert.ToInt32(Channels) / 2;
                                Q = Double.NaN;
                                CODEC = "aac";
                            }
                        }
                    }
                    break;
                case "Low":
                    if (Profile.ToLower().Contains("xvid"))
                    {
                        Bitrate = 64 * Convert.ToInt32(Audio.Channels);
                        Q = Double.NaN;
                        CODEC = "libmp3lame";
                        if (Audio.Channels >= 2)
                            Channels = 2;
                        else
                            Channels = Audio.Channels;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw"))
                        {
                            Bitrate = Double.NaN;
                            Q = 2;
                            CODEC = "libvorbis";
                            if (Audio.Channels >= 1)
                                Channels = 1;
                            else
                                Channels = Audio.Channels;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("ac3"))
                            {
                                Bitrate = 64 * Convert.ToInt32(Audio.Channels);
                                Q = Double.NaN;
                                CODEC = "ac3";
                                Channels = Audio.Channels;
                            }
                            else
                            {
                                if (Profile.ToLower().Contains("streaming"))
                                {
                                    if (Audio.Channels >= 2)
                                        Channels = 2;
                                    else
                                        Channels = Audio.Channels;
                                }
                                else
                                    Channels = Audio.Channels;
                                Bitrate = 64 * Convert.ToInt32(Channels) / 2;
                                Q = Double.NaN;
                                CODEC = "aac";
                            }
                        }
                    }
                    break;
                case "Very low":
                    if (Profile.ToLower().Contains("xvid"))
                    {
                        Bitrate = 48 * Convert.ToInt32(Audio.Channels);
                        Q = Double.NaN;
                        CODEC = "libmp3lame";
                        if (Audio.Channels >= 2)
                            Channels = 2;
                        else
                            Channels = Audio.Channels;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw"))
                        {
                            Bitrate = Double.NaN;
                            Q = 1;
                            CODEC = "libvorbis";
                            if (Audio.Channels >= 1)
                                Channels = 1;
                            else
                                Channels = Audio.Channels;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("ac3"))
                            {
                                Bitrate = 48 * Convert.ToInt32(Audio.Channels);
                                Q = Double.NaN;
                                CODEC = "ac3";
                                Channels = Audio.Channels;
                            }
                            else
                            {
                                if (Profile.ToLower().Contains("streaming"))
                                {
                                    if (Audio.Channels >= 2)
                                        Channels = 2;
                                    else
                                        Channels = Audio.Channels;
                                }
                                else
                                    Channels = Audio.Channels;
                                Bitrate = 48 * Convert.ToInt32(Channels) / 2;
                                Q = Double.NaN;
                                CODEC = "aac";
                            }
                        }
                    }
                    break;
                case "Draft":
                    if (Profile.ToLower().Contains("xvid"))
                    {
                        Bitrate = 32 * Convert.ToInt32(Audio.Channels);
                        Q = Double.NaN;
                        CODEC = "libmp3lame";
                        if (Audio.Channels >= 2)
                            Channels = 2;
                        else
                            Channels = Audio.Channels;
                    }
                    else
                    {
                        if (Profile.ToLower().Contains("workraw"))
                        {
                            Bitrate = Double.NaN;
                            Q = 0;
                            CODEC = "libvorbis";
                            if (Audio.Channels >= 1)
                                Channels = 1;
                            else
                                Channels = Audio.Channels;
                        }
                        else
                        {
                            if (Profile.ToLower().Contains("ac3"))
                            {
                                Bitrate = 32 * Convert.ToInt32(Audio.Channels);
                                Q = Double.NaN;
                                CODEC = "ac3";
                                Channels = Audio.Channels;
                            }
                            else
                            {

                                if (Profile.ToLower().Contains("streaming"))
                                {
                                    if (Audio.Channels >= 2)
                                        Channels = 2;
                                    else
                                        Channels = Audio.Channels;
                                }
                                else
                                    Channels = Audio.Channels;
                                Bitrate = 32 * Convert.ToInt32(Channels) / 2;
                                Q = Double.NaN;
                                CODEC = "aac";
                            }
                        }
                    }
                    break;
            }
        }
    }
}