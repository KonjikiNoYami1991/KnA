using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TnA___Tanoshimi_no_Autohardsubber
{
    public partial class FilesToProcess : Form
    {

        public FilesToProcess()
        {
            InitializeComponent();
            TnA.StatusToReset = new List<String>();
        }

        private void b_ok_Click(object sender, EventArgs e)
        {
            for(Int32 i=0; i < clb_stati.CheckedItems.Count; i++)
            {
                TnA.StatusToReset.Add(clb_stati.GetItemText(clb_stati.CheckedItems[i]));
            }
            switch (TnA.StatusToReset.Count)
            {
                case 0:
                    MessageBox.Show("Non è stato selezionato alcuno stato da processare.", this.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    TnA.StatusToReset.Clear();
                    this.DialogResult = DialogResult.None;
                    break;
                default:
                    this.DialogResult = DialogResult.OK;
                    break;
            }
        }

        private void b_sel_all_Click(object sender, EventArgs e)
        {
            for (Int32 i = 0; i < clb_stati.Items.Count; i++)
            {
                clb_stati.SetItemCheckState(i, CheckState.Checked);
            }
        }

        private void b_desel_all_Click(object sender, EventArgs e)
        {
            for (Int32 i = 0; i < clb_stati.Items.Count; i++)
            {
                clb_stati.SetItemCheckState(i, CheckState.Unchecked);
            }
        }
    }
}
