namespace TnA___Tanoshimi_no_Autohardsubber
{
    partial class TestFFMPEG
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
            System.Windows.Forms.DataGridViewCellStyle dataGridViewCellStyle1 = new System.Windows.Forms.DataGridViewCellStyle();
            this.b_ok = new System.Windows.Forms.Button();
            this.DGV_test = new System.Windows.Forms.DataGridView();
            this.codec = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.supported = new System.Windows.Forms.DataGridViewTextBoxColumn();
            ((System.ComponentModel.ISupportInitialize)(this.DGV_test)).BeginInit();
            this.SuspendLayout();
            // 
            // b_ok
            // 
            this.b_ok.Anchor = System.Windows.Forms.AnchorStyles.Bottom;
            this.b_ok.Location = new System.Drawing.Point(113, 283);
            this.b_ok.Name = "b_ok";
            this.b_ok.Size = new System.Drawing.Size(75, 23);
            this.b_ok.TabIndex = 0;
            this.b_ok.Text = "OK";
            this.b_ok.UseVisualStyleBackColor = true;
            this.b_ok.Click += new System.EventHandler(this.b_ok_Click);
            // 
            // DGV_test
            // 
            this.DGV_test.AllowUserToAddRows = false;
            this.DGV_test.AllowUserToDeleteRows = false;
            this.DGV_test.AllowUserToResizeColumns = false;
            this.DGV_test.AllowUserToResizeRows = false;
            this.DGV_test.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.DGV_test.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.DGV_test.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.codec,
            this.supported});
            dataGridViewCellStyle1.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleCenter;
            dataGridViewCellStyle1.BackColor = System.Drawing.SystemColors.Window;
            dataGridViewCellStyle1.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            dataGridViewCellStyle1.ForeColor = System.Drawing.SystemColors.ControlText;
            dataGridViewCellStyle1.SelectionBackColor = System.Drawing.SystemColors.Highlight;
            dataGridViewCellStyle1.SelectionForeColor = System.Drawing.SystemColors.HighlightText;
            dataGridViewCellStyle1.WrapMode = System.Windows.Forms.DataGridViewTriState.False;
            this.DGV_test.DefaultCellStyle = dataGridViewCellStyle1;
            this.DGV_test.Location = new System.Drawing.Point(12, 12);
            this.DGV_test.Name = "DGV_test";
            this.DGV_test.RowHeadersVisible = false;
            this.DGV_test.RowHeadersWidthSizeMode = System.Windows.Forms.DataGridViewRowHeadersWidthSizeMode.DisableResizing;
            this.DGV_test.ScrollBars = System.Windows.Forms.ScrollBars.None;
            this.DGV_test.Size = new System.Drawing.Size(277, 265);
            this.DGV_test.TabIndex = 1;
            // 
            // codec
            // 
            this.codec.AutoSizeMode = System.Windows.Forms.DataGridViewAutoSizeColumnMode.Fill;
            this.codec.HeaderText = "Codec/Filter";
            this.codec.Name = "codec";
            this.codec.ReadOnly = true;
            this.codec.SortMode = System.Windows.Forms.DataGridViewColumnSortMode.NotSortable;
            // 
            // supported
            // 
            this.supported.AutoSizeMode = System.Windows.Forms.DataGridViewAutoSizeColumnMode.Fill;
            this.supported.HeaderText = "Supported";
            this.supported.Name = "supported";
            this.supported.ReadOnly = true;
            this.supported.SortMode = System.Windows.Forms.DataGridViewColumnSortMode.NotSortable;
            // 
            // TestFFMPEG
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(301, 318);
            this.Controls.Add(this.DGV_test);
            this.Controls.Add(this.b_ok);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "TestFFMPEG";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Test di FFmpeg";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.TestFFMPEG_FormClosing);
            ((System.ComponentModel.ISupportInitialize)(this.DGV_test)).EndInit();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.Button b_ok;
        private System.Windows.Forms.DataGridView DGV_test;
        private System.Windows.Forms.DataGridViewTextBoxColumn codec;
        private System.Windows.Forms.DataGridViewTextBoxColumn supported;
    }
}