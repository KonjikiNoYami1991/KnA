using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Net;
using SevenZip;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Net.Http;

namespace TnA___Tanoshimi_no_Autohardsubber
{
    public partial class DownloadFFMPEG : Form
    {

        String temp_folder = String.Empty;
        String ffmpeg_zip = String.Empty;
        String dir = String.Empty;
        String link = "https://www.gyan.dev/ffmpeg/builds/";
        Boolean auto = false;

        public DownloadFFMPEG(String temp_folder, Boolean auto)
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US", false);
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US", false);
            InitializeComponent();
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            this.temp_folder = temp_folder;
            this.auto = auto;

            GetLink();
        }

        private void GetLink()
        {
            try
            {
                ll_64bit.Text = htmlparser(link);
                b_download.Enabled = true;
                if (auto == true)
                    Download();
            }
            catch (Exception ex)
            {
                b_download.Enabled = false;
                ll_64bit.BackColor = Color.DarkOrange;
                this.DialogResult = DialogResult.Abort;
                //this.Close();
            }

        }

        public String htmlparser(String url)
        {
            WebClient wb = new WebClient();
            wb.BaseAddress = url;
            wb.DownloadFile(wb.BaseAddress, temp_folder + "\\ffmpeg.html");

            String link_ffmpeg = String.Empty;

            String[] lines = File.ReadAllLines(temp_folder + "\\ffmpeg.html");

            File.Delete(temp_folder + "\\ffmpeg.html");

            List<String> links = new List<string>();

            foreach (String s in lines)
            {
                if (s.ToLower().Contains("release") && s.ToLower().Contains("full") && s.ToLower().Contains("ffmpeg") && s.ToLower().Contains("shared") == false)
                {
                    //MessageBox.Show(s);
                    links.Add(s.Split('"')[1]);
                }
            }
            foreach (String t in links)
            {
                if (t.ToLower().EndsWith("7z") == true)
                {
                    link_ffmpeg = t;
                    break;
                }
                else
                    link_ffmpeg = t;
            }
            return link_ffmpeg;
        }

        private void b_scarica_Click(object sender, EventArgs e)
        {
            Download();
        }

        public void Download()
        {
            WebClient wb = new WebClient();
            if (b_download.Text.StartsWith("D"))
            {
                try
                {
                    b_download.Text = "Abort download";
                    wb.BaseAddress = ll_64bit.Text;
                    ffmpeg_zip = temp_folder + "\\" + Path.GetFileName(wb.BaseAddress);
                    Uri u = new Uri(wb.BaseAddress);
                    wb.DownloadFileAsync(u, ffmpeg_zip);
                    ll_64bit.BackColor = Color.LightGreen;
                    wb.DownloadProgressChanged += wb_DownloadProgressChanged;
                    wb.DownloadFileCompleted += wb_DownloadFileCompleted;
                }
                catch (Exception ext)
                {
                    MessageBox.Show(ext.Message);
                }
            }
            else
            {
                wb.CancelAsync();
                b_download.Text = "Download FFmpeg";
                this.DialogResult = DialogResult.Abort;
                this.Close();
            }
        }

        void wb_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            if (e.Cancelled == false)
            {
                b_download.Text = "Download FFmpeg";
                ExtractEXE();
            }
            else
            {
                MessageBox.Show("Aborted", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                b_download.Text = "Download FFmpeg";
            }
        }

        void wb_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            pb_download.Value = e.ProgressPercentage;
            l_bytes.Text = MyRoundingFunction(e.BytesReceived / 1000000.0, 2).ToString() + " MB / " + MyRoundingFunction(e.TotalBytesToReceive / 1000000.0, 2).ToString() + " MB";
        }

        public static string MyRoundingFunction(double value, int decimalPlaces)
        {
            string formatter = "{0:f" + decimalPlaces + "}";
            return string.Format(formatter, value);
        }

        public void ExtractEXE()
        {
            try
            {
                SevenZipExtractor extr = new SevenZipExtractor(ffmpeg_zip);
                SevenZipExtractor.SetLibraryPath(Path.GetDirectoryName(Application.ExecutablePath) + "\\sevenzipsharp\\7z.dll");
                dir = Path.GetDirectoryName(ffmpeg_zip) + "\\" + Path.GetFileNameWithoutExtension(ffmpeg_zip);
                extr.BeginExtractArchive(dir);
                extr.Extracting += extr_Extracting;
                extr.FileExtractionFinished += extr_FileExtractionFinished;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, this.Text);
            }
        }

        void extr_FileExtractionFinished(object sender, FileInfoEventArgs e)
        {
            if ((Int32)e.PercentDone == 100)
            {
                foreach (String s in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (s.ToLower().Contains("ffmpeg.exe"))
                    {
                        File.Copy(s, temp_folder + "\\" + Path.GetFileName(s), true);
                        this.DialogResult = DialogResult.OK;
                        this.Close();
                    }
                }
                foreach (String s in Directory.GetDirectories(dir,"*",SearchOption.AllDirectories))
                {
                    if (s.ToLower().Contains("license"))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.CopyDirectory(s, Path.Combine(temp_folder, Path.GetFileName(s)), false);
                        this.DialogResult = DialogResult.OK;
                        this.Close();
                    }
                }
            }
        }

        void extr_Extracting(object sender, ProgressEventArgs e)
        {
            pb_estr_zip.Value = (Int32)e.PercentDone;
            l_perc_extr.Text = ((Int32)e.PercentDone).ToString() + "%";
        }

        private void ll_64bit_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                Process.Start(ll_64bit.Text);
            }
            catch { }
        }

        private void ll_zeranoe_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                Process.Start(link);
            }
            catch { }
        }
    }
}
