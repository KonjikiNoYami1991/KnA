namespace TnA___Tanoshimi_no_Autohardsubber
{
    partial class FilesToProcess
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.b_cancel = new System.Windows.Forms.Button();
            this.b_ok = new System.Windows.Forms.Button();
            this.label1 = new System.Windows.Forms.Label();
            this.b_desel_all = new System.Windows.Forms.Button();
            this.clb_stati = new System.Windows.Forms.CheckedListBox();
            this.b_sel_all = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // b_cancel
            // 
            this.b_cancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.b_cancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.b_cancel.Location = new System.Drawing.Point(254, 142);
            this.b_cancel.Name = "b_cancel";
            this.b_cancel.Size = new System.Drawing.Size(75, 23);
            this.b_cancel.TabIndex = 0;
            this.b_cancel.Text = "Cancel";
            this.b_cancel.UseVisualStyleBackColor = true;
            // 
            // b_ok
            // 
            this.b_ok.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.b_ok.DialogResult = System.Windows.Forms.DialogResult.OK;
            this.b_ok.Location = new System.Drawing.Point(335, 142);
            this.b_ok.Name = "b_ok";
            this.b_ok.Size = new System.Drawing.Size(75, 23);
            this.b_ok.TabIndex = 1;
            this.b_ok.Text = "OK";
            this.b_ok.UseVisualStyleBackColor = true;
            this.b_ok.Click += new System.EventHandler(this.b_ok_Click);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(12, 9);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(274, 26);
            this.label1.TabIndex = 2;
            this.label1.Text = "Sono stati trovati dei file già processati almeno una volta.\r\nSelezionare i file " +
    "da processare.";
            // 
            // b_desel_all
            // 
            this.b_desel_all.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.b_desel_all.Location = new System.Drawing.Point(295, 94);
            this.b_desel_all.Name = "b_desel_all";
            this.b_desel_all.Size = new System.Drawing.Size(115, 23);
            this.b_desel_all.TabIndex = 3;
            this.b_desel_all.Text = "Deselect all";
            this.b_desel_all.UseVisualStyleBackColor = true;
            this.b_desel_all.Click += new System.EventHandler(this.b_desel_all_Click);
            // 
            // clb_stati
            // 
            this.clb_stati.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.clb_stati.CheckOnClick = true;
            this.clb_stati.FormattingEnabled = true;
            this.clb_stati.Items.AddRange(new object[] {
            "OK",
            "STOPPED",
            "WARNING",
            "ERROR",
            "READY"});
            this.clb_stati.Location = new System.Drawing.Point(12, 38);
            this.clb_stati.Name = "clb_stati";
            this.clb_stati.Size = new System.Drawing.Size(277, 79);
            this.clb_stati.TabIndex = 4;
            // 
            // b_sel_all
            // 
            this.b_sel_all.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.b_sel_all.Location = new System.Drawing.Point(295, 38);
            this.b_sel_all.Name = "b_sel_all";
            this.b_sel_all.Size = new System.Drawing.Size(115, 23);
            this.b_sel_all.TabIndex = 5;
            this.b_sel_all.Text = "Select all";
            this.b_sel_all.UseVisualStyleBackColor = true;
            this.b_sel_all.Click += new System.EventHandler(this.b_sel_all_Click);
            // 
            // FilesToProcess
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(422, 177);
            this.Controls.Add(this.b_sel_all);
            this.Controls.Add(this.clb_stati);
            this.Controls.Add(this.b_desel_all);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.b_ok);
            this.Controls.Add(this.b_cancel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Fixed3D;
            this.MaximizeBox = false;
            this.MaximumSize = new System.Drawing.Size(442, 220);
            this.MinimumSize = new System.Drawing.Size(442, 220);
            this.Name = "FilesToProcess";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "TnA - Selezione file da processare";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button b_cancel;
        private System.Windows.Forms.Button b_ok;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Button b_desel_all;
        private System.Windows.Forms.CheckedListBox clb_stati;
        private System.Windows.Forms.Button b_sel_all;
    }
}