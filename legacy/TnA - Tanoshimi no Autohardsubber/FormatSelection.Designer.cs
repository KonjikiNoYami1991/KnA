namespace TnA___Tanoshimi_no_Autohardsubber
{
    partial class FormatSelection
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
            this.clb_ext = new System.Windows.Forms.CheckedListBox();
            this.b_ok = new System.Windows.Forms.Button();
            this.b_sel_all = new System.Windows.Forms.Button();
            this.b_des_all = new System.Windows.Forms.Button();
            this.b_inv_sel = new System.Windows.Forms.Button();
            this.b_annulla = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // clb_ext
            // 
            this.clb_ext.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.clb_ext.CheckOnClick = true;
            this.clb_ext.FormattingEnabled = true;
            this.clb_ext.Location = new System.Drawing.Point(12, 12);
            this.clb_ext.MultiColumn = true;
            this.clb_ext.Name = "clb_ext";
            this.clb_ext.Size = new System.Drawing.Size(392, 184);
            this.clb_ext.TabIndex = 0;
            // 
            // b_ok
            // 
            this.b_ok.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.b_ok.DialogResult = System.Windows.Forms.DialogResult.OK;
            this.b_ok.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.b_ok.Location = new System.Drawing.Point(410, 201);
            this.b_ok.Name = "b_ok";
            this.b_ok.Size = new System.Drawing.Size(85, 23);
            this.b_ok.TabIndex = 1;
            this.b_ok.Text = "OK";
            this.b_ok.UseVisualStyleBackColor = true;
            this.b_ok.Click += new System.EventHandler(this.b_ok_Click);
            // 
            // b_sel_all
            // 
            this.b_sel_all.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.b_sel_all.Location = new System.Drawing.Point(410, 12);
            this.b_sel_all.Name = "b_sel_all";
            this.b_sel_all.Size = new System.Drawing.Size(85, 42);
            this.b_sel_all.TabIndex = 3;
            this.b_sel_all.Text = "Select all";
            this.b_sel_all.UseVisualStyleBackColor = true;
            this.b_sel_all.Click += new System.EventHandler(this.b_sel_all_Click);
            // 
            // b_des_all
            // 
            this.b_des_all.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.b_des_all.Location = new System.Drawing.Point(410, 60);
            this.b_des_all.Name = "b_des_all";
            this.b_des_all.Size = new System.Drawing.Size(85, 42);
            this.b_des_all.TabIndex = 4;
            this.b_des_all.Text = "Deselect all";
            this.b_des_all.UseVisualStyleBackColor = true;
            this.b_des_all.Click += new System.EventHandler(this.b_des_all_Click);
            // 
            // b_inv_sel
            // 
            this.b_inv_sel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.b_inv_sel.Location = new System.Drawing.Point(410, 108);
            this.b_inv_sel.Name = "b_inv_sel";
            this.b_inv_sel.Size = new System.Drawing.Size(85, 42);
            this.b_inv_sel.TabIndex = 5;
            this.b_inv_sel.Text = "Invert selection";
            this.b_inv_sel.UseVisualStyleBackColor = true;
            this.b_inv_sel.Click += new System.EventHandler(this.b_inv_sel_Click);
            // 
            // b_annulla
            // 
            this.b_annulla.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.b_annulla.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.b_annulla.Location = new System.Drawing.Point(410, 230);
            this.b_annulla.Name = "b_annulla";
            this.b_annulla.Size = new System.Drawing.Size(85, 23);
            this.b_annulla.TabIndex = 6;
            this.b_annulla.Text = "Cancel";
            this.b_annulla.UseVisualStyleBackColor = true;
            this.b_annulla.Click += new System.EventHandler(this.b_annulla_Click);
            // 
            // FormatSelection
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(507, 262);
            this.Controls.Add(this.b_annulla);
            this.Controls.Add(this.b_inv_sel);
            this.Controls.Add(this.b_des_all);
            this.Controls.Add(this.b_sel_all);
            this.Controls.Add(this.b_ok);
            this.Controls.Add(this.clb_ext);
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "FormatSelection";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Extensions to consider";
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.CheckedListBox clb_ext;
        private System.Windows.Forms.Button b_ok;
        private System.Windows.Forms.Button b_sel_all;
        private System.Windows.Forms.Button b_des_all;
        private System.Windows.Forms.Button b_inv_sel;
        private System.Windows.Forms.Button b_annulla;
    }
}