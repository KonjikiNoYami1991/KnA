using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Globalization;
using System.Threading;

namespace TnA___Tanoshimi_no_Autohardsubber
{
    public partial class FormatSelection : Form
    {

        public static List<String> Formats = new List<string>();

        public FormatSelection(String[] Ext)
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US", false);
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US", false);
            InitializeComponent();
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            this.Text = "Extensions to consider";
            clb_ext.Items.Clear();
            clb_ext.Sorted = true;
            foreach(String s in Ext)
            {
                clb_ext.Items.Add("Only " + s);
            }
        }

        private void b_ok_Click(object sender, EventArgs e)
        {
            Save();
        }

        public void Save()
        {
            Formats = new List<String>();
            for (Int32 i = 0; i < clb_ext.CheckedItems.Count; i++)
            {
                String temp = clb_ext.GetItemText(clb_ext.CheckedItems[i]);
                temp = temp.Replace("Only ", "");
                temp = temp.Trim();
                Formats.Add(temp);
                //MessageBox.Show(TnA.formati_scelti[i]);
            }
        }

        private void b_sel_all_Click(object sender, EventArgs e)
        {
            for(Int32 i=0; i<clb_ext.Items.Count; i++)
            {
                clb_ext.SetItemChecked(i, true);
            }
        }

        private void b_des_all_Click(object sender, EventArgs e)
        {
            for (Int32 i = 0; i < clb_ext.Items.Count; i++)
            {
                clb_ext.SetItemChecked(i, false);
            }
        }

        private void b_inv_sel_Click(object sender, EventArgs e)
        {
            for (Int32 i = 0; i < clb_ext.Items.Count; i++)
            {
                if (clb_ext.GetItemCheckState(i) == CheckState.Unchecked)
                    clb_ext.SetItemChecked(i, true);
                else
                    clb_ext.SetItemChecked(i, false);
            }
        }

        private void b_annulla_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }
    }
}
