using System;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;
using System.Globalization;
using System.Threading;

namespace TnA___Tanoshimi_no_Autohardsubber
{
    public partial class About : Form
    {
        
        public About(String Title, String Version)
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US", false);
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US", false);
            InitializeComponent();
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            l_title.Text = Title;
            l_vers.Text = "Version: " + Version;
            this.Text = "About - " + Title;
        }

        private void pb_logo_Click(object sender, EventArgs e)
        {
            Process.Start("https://tnsfansub.com/");
        }

        private void pb_logo_MouseEnter(object sender, EventArgs e)
        {
            l_site.Visible = true;
        }

        private void pb_logo_MouseLeave(object sender, EventArgs e)
        {
            l_site.Visible = false;
        }

        private void ll_source_code_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("https://github.com/KonjikiNoYami1991/TnA");
        }

        private void ll_license_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("notepad.exe", Application.StartupPath + "\\LICENSE");
        }

        private void ll_rel_page_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start("https://github.com/KonjikiNoYami1991/TnA/releases");
        }
    }
}
