namespace TnA___Tanoshimi_no_Autohardsubber
{
    partial class VersionHistory
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
            this.b_ripristina = new System.Windows.Forms.Button();
            this.l_backupcount = new System.Windows.Forms.Label();
            this.lv_versioni = new System.Windows.Forms.ListView();
            this.pb_avanz = new System.Windows.Forms.ProgressBar();
            this.l_avanz = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // b_ripristina
            // 
            this.b_ripristina.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.b_ripristina.BackColor = System.Drawing.Color.YellowGreen;
            this.b_ripristina.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.b_ripristina.Location = new System.Drawing.Point(12, 328);
            this.b_ripristina.Name = "b_ripristina";
            this.b_ripristina.Size = new System.Drawing.Size(450, 47);
            this.b_ripristina.TabIndex = 2;
            this.b_ripristina.Text = "Ripristina la versione selezionata";
            this.b_ripristina.UseVisualStyleBackColor = false;
            this.b_ripristina.Click += new System.EventHandler(this.B_ripristina_Click);
            // 
            // l_backupcount
            // 
            this.l_backupcount.AutoSize = true;
            this.l_backupcount.Location = new System.Drawing.Point(12, 9);
            this.l_backupcount.Name = "l_backupcount";
            this.l_backupcount.Size = new System.Drawing.Size(79, 13);
            this.l_backupcount.TabIndex = 4;
            this.l_backupcount.Text = "Backup trovati:";
            // 
            // lv_versioni
            // 
            this.lv_versioni.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lv_versioni.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lv_versioni.FullRowSelect = true;
            this.lv_versioni.GridLines = true;
            this.lv_versioni.HideSelection = false;
            this.lv_versioni.Location = new System.Drawing.Point(12, 25);
            this.lv_versioni.MaximumSize = new System.Drawing.Size(450, 297);
            this.lv_versioni.MinimumSize = new System.Drawing.Size(450, 297);
            this.lv_versioni.MultiSelect = false;
            this.lv_versioni.Name = "lv_versioni";
            this.lv_versioni.ShowGroups = false;
            this.lv_versioni.Size = new System.Drawing.Size(450, 297);
            this.lv_versioni.TabIndex = 5;
            this.lv_versioni.UseCompatibleStateImageBehavior = false;
            this.lv_versioni.View = System.Windows.Forms.View.List;
            // 
            // pb_avanz
            // 
            this.pb_avanz.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.pb_avanz.Location = new System.Drawing.Point(12, 381);
            this.pb_avanz.Name = "pb_avanz";
            this.pb_avanz.Size = new System.Drawing.Size(351, 23);
            this.pb_avanz.TabIndex = 6;
            // 
            // l_avanz
            // 
            this.l_avanz.AutoSize = true;
            this.l_avanz.Location = new System.Drawing.Point(369, 391);
            this.l_avanz.Name = "l_avanz";
            this.l_avanz.Size = new System.Drawing.Size(49, 13);
            this.l_avanz.TabIndex = 7;
            this.l_avanz.Text = "0 / 0 MB";
            // 
            // CronologiaVersioni
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(474, 415);
            this.Controls.Add(this.l_avanz);
            this.Controls.Add(this.pb_avanz);
            this.Controls.Add(this.lv_versioni);
            this.Controls.Add(this.l_backupcount);
            this.Controls.Add(this.b_ripristina);
            this.MaximizeBox = false;
            this.MaximumSize = new System.Drawing.Size(490, 454);
            this.MinimizeBox = false;
            this.MinimumSize = new System.Drawing.Size(490, 454);
            this.Name = "CronologiaVersioni";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Cronologia versioni";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.Button b_ripristina;
        private System.Windows.Forms.Label l_backupcount;
        private System.Windows.Forms.ListView lv_versioni;
        private System.Windows.Forms.ProgressBar pb_avanz;
        private System.Windows.Forms.Label l_avanz;
    }
}