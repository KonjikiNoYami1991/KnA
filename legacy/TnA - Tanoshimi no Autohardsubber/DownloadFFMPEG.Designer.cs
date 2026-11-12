namespace TnA___Tanoshimi_no_Autohardsubber
{
    partial class DownloadFFMPEG
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
            this.ll_64bit = new System.Windows.Forms.LinkLabel();
            this.label2 = new System.Windows.Forms.Label();
            this.b_download = new System.Windows.Forms.Button();
            this.pb_download = new System.Windows.Forms.ProgressBar();
            this.l_bytes = new System.Windows.Forms.Label();
            this.pb_estr_zip = new System.Windows.Forms.ProgressBar();
            this.label3 = new System.Windows.Forms.Label();
            this.l_perc_extr = new System.Windows.Forms.Label();
            this.label4 = new System.Windows.Forms.Label();
            this.ll_zeranoe = new System.Windows.Forms.LinkLabel();
            this.SuspendLayout();
            // 
            // ll_64bit
            // 
            this.ll_64bit.AutoSize = true;
            this.ll_64bit.Location = new System.Drawing.Point(12, 31);
            this.ll_64bit.Name = "ll_64bit";
            this.ll_64bit.Size = new System.Drawing.Size(78, 13);
            this.ll_64bit.TabIndex = 3;
            this.ll_64bit.TabStop = true;
            this.ll_64bit.Text = "Link not found.";
            this.ll_64bit.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.ll_64bit_LinkClicked);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(12, 9);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(145, 13);
            this.label2.TabIndex = 2;
            this.label2.Text = "Download FFmpeg 64bit from";
            // 
            // b_download
            // 
            this.b_download.Enabled = false;
            this.b_download.Location = new System.Drawing.Point(12, 81);
            this.b_download.Name = "b_download";
            this.b_download.Size = new System.Drawing.Size(117, 23);
            this.b_download.TabIndex = 4;
            this.b_download.Text = "Download FFmpeg";
            this.b_download.UseVisualStyleBackColor = true;
            this.b_download.Click += new System.EventHandler(this.b_scarica_Click);
            // 
            // pb_download
            // 
            this.pb_download.Location = new System.Drawing.Point(135, 81);
            this.pb_download.Name = "pb_download";
            this.pb_download.Size = new System.Drawing.Size(274, 23);
            this.pb_download.TabIndex = 5;
            // 
            // l_bytes
            // 
            this.l_bytes.AutoSize = true;
            this.l_bytes.Location = new System.Drawing.Point(415, 86);
            this.l_bytes.Name = "l_bytes";
            this.l_bytes.Size = new System.Drawing.Size(24, 13);
            this.l_bytes.TabIndex = 6;
            this.l_bytes.Text = "0/0";
            // 
            // pb_estr_zip
            // 
            this.pb_estr_zip.Location = new System.Drawing.Point(135, 110);
            this.pb_estr_zip.Name = "pb_estr_zip";
            this.pb_estr_zip.Size = new System.Drawing.Size(274, 23);
            this.pb_estr_zip.TabIndex = 8;
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(15, 115);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(92, 13);
            this.label3.TabIndex = 9;
            this.label3.Text = "Archive extraction";
            // 
            // l_perc_extr
            // 
            this.l_perc_extr.AutoSize = true;
            this.l_perc_extr.Location = new System.Drawing.Point(415, 115);
            this.l_perc_extr.Name = "l_perc_extr";
            this.l_perc_extr.Size = new System.Drawing.Size(21, 13);
            this.l_perc_extr.TabIndex = 10;
            this.l_perc_extr.Text = "0%";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(12, 56);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(52, 13);
            this.label4.TabIndex = 11;
            this.label4.Text = "Builds by:";
            // 
            // ll_zeranoe
            // 
            this.ll_zeranoe.AutoSize = true;
            this.ll_zeranoe.Location = new System.Drawing.Point(61, 56);
            this.ll_zeranoe.Name = "ll_zeranoe";
            this.ll_zeranoe.Size = new System.Drawing.Size(32, 13);
            this.ll_zeranoe.TabIndex = 12;
            this.ll_zeranoe.TabStop = true;
            this.ll_zeranoe.Text = "Gyan";
            this.ll_zeranoe.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.ll_zeranoe_LinkClicked);
            // 
            // DownloadFFMPEG
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(566, 143);
            this.Controls.Add(this.ll_zeranoe);
            this.Controls.Add(this.label4);
            this.Controls.Add(this.l_perc_extr);
            this.Controls.Add(this.label3);
            this.Controls.Add(this.pb_estr_zip);
            this.Controls.Add(this.l_bytes);
            this.Controls.Add(this.pb_download);
            this.Controls.Add(this.b_download);
            this.Controls.Add(this.ll_64bit);
            this.Controls.Add(this.label2);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "DownloadFFMPEG";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "FFmpeg Downloader";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.LinkLabel ll_64bit;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Button b_download;
        private System.Windows.Forms.ProgressBar pb_download;
        private System.Windows.Forms.Label l_bytes;
        private System.Windows.Forms.ProgressBar pb_estr_zip;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Label l_perc_extr;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.LinkLabel ll_zeranoe;
    }
}