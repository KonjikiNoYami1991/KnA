using System;
using System.Drawing;
using System.Windows.Forms;
using System.Globalization;
using System.Threading;

namespace TnA___Tanoshimi_no_Autohardsubber
{
    public partial class TestFFMPEG : Form
    {

        DialogResult Result = DialogResult.No;
        Boolean Close = false;

        public TestFFMPEG(Boolean x264, Boolean aac, Boolean ac3, Boolean ass, Boolean libx265, Boolean vfscale, Boolean hqdn3d, Boolean gradfun, Boolean xvid, Boolean mp3, Boolean nvenc)
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US", false);
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US", false);
            InitializeComponent();
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (nvenc == true)
            {
                DGV_test.Rows.Add("Video codec Nvidia H.264", "Found");
                Result = DialogResult.Yes;
            }
            else
            {
                DGV_test.Rows.Add("Video codec Nvidia H.264", "Not found");
                Result = DialogResult.No;
            }
            if (x264 == true)
            {
                DGV_test.Rows.Add("Video codec libx264", "Found");
                Result = DialogResult.Yes;
            }
            else
            {
                DGV_test.Rows.Add("Video codec libx264", "Not found");
                Result = DialogResult.No;
            }
            if ((libx265 == true))
            {
                DGV_test.Rows.Add("Video codec libx265", "Found");
                Result = DialogResult.Yes;
            }
            else
            {
                DGV_test.Rows.Add("Video codec libx265", "Not found");
                Result = DialogResult.No;
            }
            if ((xvid == true))
            {
                DGV_test.Rows.Add("Video codec libxvid", "Found");
                Result = DialogResult.Yes;
            }
            else
            {
                DGV_test.Rows.Add("Video codec libxvid", "Not found");
                Result = DialogResult.No;
            }
            if ((aac == true))
            {
                DGV_test.Rows.Add("Audio codec AAC", "Found");
                Result = DialogResult.Yes;
            }
            else
            {
                DGV_test.Rows.Add("Audio codec AAC", "Not found");
                Result = DialogResult.No;
            }
            if ((ac3 == true))
            {
                DGV_test.Rows.Add("Audio codec AC3", "Found");
                Result = DialogResult.Yes;
            }
            else
            {
                DGV_test.Rows.Add("Audio codec AC3", "Not found");
                Result = DialogResult.No;
            }
            if ((mp3 == true))
            {
                DGV_test.Rows.Add("Audio codec MP3", "Found");
                Result = DialogResult.Yes;
            }
            else
            {
                DGV_test.Rows.Add("Audio codec MP3", "Not found");
                Result = DialogResult.No;
            }
            if ((ass == true))
            {
                DGV_test.Rows.Add("Filtro video ASS", "Found");
                Result = DialogResult.Yes;
            }
            else
            {
                DGV_test.Rows.Add("Filtro video ASS", "Not found");
                Result = DialogResult.No;
            }
            if ((vfscale == true))
            {
                DGV_test.Rows.Add("Filtro video Scale", "Found");
                Result = DialogResult.Yes;
            }
            else
            {
                DGV_test.Rows.Add("Filtro video Scale", "Not found");
                Result = DialogResult.No;
            }
            if ((hqdn3d == true))
            {
                DGV_test.Rows.Add("Filtro video Hqdn3d", "Found");
                Result = DialogResult.Yes;
            }
            else
            {
                DGV_test.Rows.Add("Filtro video Hqdn3d", "Not found");
                Result = DialogResult.No;
            }
            if ((gradfun == true))
            {
                DGV_test.Rows.Add("Filtro video Gradfun", "Found");
                Result = DialogResult.Yes;
            }
            else
            {
                DGV_test.Rows.Add("Filtro video Gradfun", "Not found");
                Result = DialogResult.No;
            }
        }

        private void b_ok_Click(object sender, EventArgs e)
        {
            this.DialogResult = Result;
            Close = true;
            this.Close();
        }

        private void TestFFMPEG_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (Close == true)
                e.Cancel = false;
            else
                e.Cancel = true;
        }
    }
}
