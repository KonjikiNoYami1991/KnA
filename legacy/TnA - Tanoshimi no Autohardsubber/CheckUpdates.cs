using SevenZip;
using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Updater
{
    public partial class TnA_Updater : Form
    {

        String link_tna = String.Empty;

        String AppName, AppFolder;

        Boolean Auto = false;

        public TnA_Updater(String NewVersion, Boolean Hidden, Icon Icon, String AppName, Boolean Auto, String AppFolder)
        {
            InitializeComponent();

            this.AppName = AppName;
            this.AppFolder = AppFolder;

            this.Auto = Auto;

            this.Icon = Icon;
            l_installed_version.Text = ("Installed version: " + NewVersion).Replace("v", String.Empty);
            Check(Hidden);
        }

        public void Check(Boolean Hidden)
        {
            String link_ini = "https://www.dropbox.com/s/5ppwjapyd51sf1g/vers.ini?dl=1";
            String IniVers = Path.GetDirectoryName(Application.ExecutablePath) + "\\version.ini";
            try
            {
                WebClient update = new WebClient();
                update.BaseAddress = link_ini;
                update.DownloadFile(update.BaseAddress, IniVers);
                IniFile ReadVersion = new IniFile(IniVers);
                l_new_version.Text = "New version: " + ReadVersion.Read("n", "Versioni");
                link_tna = ReadVersion.Read("l", "Versioni");
                File.Delete(IniVers);

                if (l_installed_version.Text.Split(' ')[2] == l_new_version.Text.Split(' ')[2])
                {
                    if (Hidden == false)
                        MessageBox.Show("The newest version (" + l_installed_version.Text.Split(' ')[2] + ") is already installed.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    this.DialogResult = DialogResult.None;
                    this.Close();
                }
                else
                {
                    if (Hidden == true)
                    {
                        this.DialogResult = DialogResult.Yes;
                        this.Close();
                    }
                    else
                    {
                        if (this.Visible == false)
                            this.ShowDialog();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, this.Text);
                this.DialogResult = DialogResult.Abort;
                this.Close();
            }
        }

        private void b_verifica_Click(object sender, EventArgs e)
        {
            b_update.Enabled = false;
            b_cancel.Enabled = false;
            b_check.Text = "CHECKING...";
            b_check.Update();
            Check(false);
            b_check.Text = "CHECK AGAIN";
            b_update.Enabled = true;
            b_cancel.Enabled = true;
        }

        private void b_agg_Click(object sender, EventArgs e)
        {
            WebClient update = new WebClient();
            update.BaseAddress = link_tna;
            if (b_update.Text.StartsWith("DOWN"))
            {
                b_check.Enabled = false;
                b_cancel.Enabled = false;
                b_update.Text = "ABORT";
                update.DownloadFileAsync(new Uri(update.BaseAddress), Path.GetDirectoryName(Application.ExecutablePath) + "\\TnA.7z");
                update.DownloadProgressChanged += Update_DownloadProgressChanged;
                update.DownloadFileCompleted += Update_DownloadFileCompleted;
            }
            else
            {
                b_update.Text = "DOWNLOAD UPDATE";
                b_check.Enabled = true;
                b_cancel.Enabled = true;
                update.CancelAsync();
            }
        }

        private void Update_DownloadFileCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(Path.Combine(Application.StartupPath, "Updater.exe"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                //Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.DialogResult = DialogResult.Abort;
            }
        }

        private void Extr_FileExtractionFinished(object sender, FileInfoEventArgs e)
        {
            if ((Int32)e.PercentDone == 100)
            {
                MessageBox.Show("Update complete.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                //MessageBox.Show("Aggiornamento non riuscito.\nPercentuale estrazione: " + e.PercentDone.ToString(), this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Extr_ExtractionFinished(object sender, EventArgs e)
        {

        }

        private void b_cancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        private void Update_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            this.Invoke((MethodInvoker)delegate ()
            {
                pb_perc_down.Value = e.ProgressPercentage;
                l_dim_down.Text = Math.Round(e.BytesReceived / 1024.0 / 1024.0, 2).ToString() + "/" + Math.Round(e.TotalBytesToReceive / 1024.0 / 1024.0, 2).ToString() + " MB downloaded";
            });
        }
    }

    class IniFile   // revision 10
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
}
