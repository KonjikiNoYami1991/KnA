using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Net;
using System.Threading;

namespace TnA___Tanoshimi_no_Autohardsubber
{
    public partial class VersionHistory : Form
    {
        WebClient wb = new WebClient();

        String link = "https://tnsfansub.com:8080/utility/Backup-TnA/versions.txt";

        Thread t;
        ThreadStart ts;

        public VersionHistory(String AppName, Icon Icona)
        {
            InitializeComponent();
            this.Text = AppName + " - " + this.Text;
            this.Icon = Icona;

            wb = new WebClient();

            try
            {
                wb.DownloadFile(link, Path.Combine(Application.StartupPath, "history.txt"));
                foreach (String s in File.ReadAllLines(Path.Combine(Application.StartupPath, "history.txt")))
                {
                    lv_versioni.Items.Add(Path.GetFileNameWithoutExtension(s));
                }
                File.Delete(Path.Combine(Application.StartupPath, "history.txt"));
                l_backupcount.Text = "Backup trovati: " + lv_versioni.Items.Count.ToString();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void B_ripristina_Click(object sender, EventArgs e)
        {
            if (b_ripristina.Text.StartsWith("R"))
            {
                if (lv_versioni.SelectedItems.Count > 0)
                {
                    if (MessageBox.Show("Si è sicuri di voler ripristinare la versione selezionata del programma?\nL'operazione potrebbe non essere reversibile.", this.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        ts = new ThreadStart(LanciaAggiornamento);
                        t = new Thread(ts);
                        t.Start();
                    }
                }
                else
                {
                    MessageBox.Show("Selezionare la versione da ripristinare.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
            }
            else
            {
                wb.CancelAsync();
                b_ripristina.Text = "Ripristina la versione selezionata";
                lv_versioni.Enabled = true;
                l_avanz.Text = "0 / 0 MB";
                pb_avanz.Value = 0;
            }
        }

        public void LanciaAggiornamento()
        {
            wb.DownloadProgressChanged += Wb_DownloadProgressChanged;
            wb.DownloadFileCompleted += Wb_DownloadFileCompleted;
            try
            {
                this.Invoke((MethodInvoker)delegate ()
                {
                    wb.DownloadFileAsync(new Uri(Path.Combine(link.Replace("versions.txt", lv_versioni.SelectedItems[0].Text + ".7z"))), Path.Combine(Application.StartupPath, "TnA.7z"));
                    b_ripristina.Text = "ANNULLA";
                    lv_versioni.Enabled = false;
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Wb_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            if (e.Cancelled == false && e.Error == null)
                System.Diagnostics.Process.Start(Path.Combine(Application.StartupPath, "Updater.exe"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
            else
            {
                File.Delete(Path.Combine(Application.StartupPath, "TnA.7z"));
            }
        }

        private void Wb_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            pb_avanz.Value = e.ProgressPercentage;
            l_avanz.Text = (e.BytesReceived / 1024 / 1024).ToString() + " / " + (e.TotalBytesToReceive / 1024 / 1024).ToString() + " MB";
        }
    }
}
