using SevenZip;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CheckUpdates
{
    public partial class Updater : Form
    {
        public Updater()
        {
            InitializeComponent();

            if (Environment.GetCommandLineArgs().Count() > 0)
            {
                String processo_tna = Environment.GetCommandLineArgs()[1];

                System.Diagnostics.Process.GetProcessById(Convert.ToInt32(processo_tna)).Kill();
            }

            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;

            if (File.Exists(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "TnA.7z")) == true)
            {
                try
                {
                    SevenZipExtractor extr = new SevenZipExtractor(Application.StartupPath + "\\TnA.7z");
                    SevenZipExtractor.SetLibraryPath(Path.GetDirectoryName(Application.ExecutablePath) + "\\sevenzipsharp\\7z.dll");
                    var dir = Path.GetDirectoryName(Application.ExecutablePath);
                    extr.PreserveDirectoryStructure = true;
                    extr.BeginExtractArchive(dir);
                    extr.Extracting += Extr_Extracting;
                    extr.FileExtractionFinished += Extr_FileExtractionFinished;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, this.Text);
                }
            }
            else
            {
                try
                {
                    System.Diagnostics.Process.Start(Path.Combine(Application.StartupPath, "TnA - Tanoshimi no Autohardsubber.exe"));
                    this.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void Extr_FileExtractionFinished(object sender, FileInfoEventArgs e)
        {
            if (e.PercentDone == 100)
            {
                System.Diagnostics.Process.Start(Path.Combine(Application.StartupPath, "TnA - Tanoshimi no Autohardsubber.exe"));
                File.Delete(Application.StartupPath + "\\TnA.7z");

                this.Close();
            }
        }

        private void Extr_Extracting(object sender, ProgressEventArgs e)
        {
            this.Invoke((MethodInvoker)delegate ()
            {
                e.Cancel = false;
                l_agg.Text = "Updating of TnA - Tanoshimi no Autohardsubber: " + e.PercentDone.ToString() + "%";
                pb_avanz.Value = e.PercentDone;
            });
        }
    }
}
