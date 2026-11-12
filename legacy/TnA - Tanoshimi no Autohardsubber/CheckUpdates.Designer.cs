namespace Updater
{
    partial class TnA_Updater
    {
        /// <summary>
        /// Variabile di progettazione necessaria.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Pulire le risorse in uso.
        /// </summary>
        /// <param name="disposing">ha valore true se le risorse gestite devono essere eliminate, false in caso contrario.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Codice generato da Progettazione Windows Form

        /// <summary>
        /// Metodo necessario per il supporto della finestra di progettazione. Non modificare
        /// il contenuto del metodo con l'editor di codice.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(TnA_Updater));
            this.b_check = new System.Windows.Forms.Button();
            this.b_update = new System.Windows.Forms.Button();
            this.l_installed_version = new System.Windows.Forms.Label();
            this.pb_perc_down = new System.Windows.Forms.ProgressBar();
            this.l_new_version = new System.Windows.Forms.Label();
            this.l_dim_down = new System.Windows.Forms.Label();
            this.b_cancel = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // b_check
            // 
            this.b_check.Location = new System.Drawing.Point(307, 12);
            this.b_check.Name = "b_check";
            this.b_check.Size = new System.Drawing.Size(294, 23);
            this.b_check.TabIndex = 1;
            this.b_check.Text = "CHECK AGAIN";
            this.b_check.UseVisualStyleBackColor = true;
            this.b_check.Click += new System.EventHandler(this.b_verifica_Click);
            // 
            // b_update
            // 
            this.b_update.Location = new System.Drawing.Point(307, 52);
            this.b_update.Name = "b_update";
            this.b_update.Size = new System.Drawing.Size(294, 23);
            this.b_update.TabIndex = 2;
            this.b_update.Text = "DOWNLOAD UPDATE";
            this.b_update.UseVisualStyleBackColor = true;
            this.b_update.Click += new System.EventHandler(this.b_agg_Click);
            // 
            // l_installed_version
            // 
            this.l_installed_version.AutoSize = true;
            this.l_installed_version.Location = new System.Drawing.Point(12, 18);
            this.l_installed_version.Name = "l_installed_version";
            this.l_installed_version.Size = new System.Drawing.Size(86, 13);
            this.l_installed_version.TabIndex = 3;
            this.l_installed_version.Text = "Installed version:";
            // 
            // pb_perc_down
            // 
            this.pb_perc_down.Enabled = false;
            this.pb_perc_down.Location = new System.Drawing.Point(15, 92);
            this.pb_perc_down.Name = "pb_perc_down";
            this.pb_perc_down.Size = new System.Drawing.Size(277, 23);
            this.pb_perc_down.TabIndex = 4;
            // 
            // l_new_version
            // 
            this.l_new_version.AutoSize = true;
            this.l_new_version.Location = new System.Drawing.Point(12, 43);
            this.l_new_version.Name = "l_new_version";
            this.l_new_version.Size = new System.Drawing.Size(69, 13);
            this.l_new_version.TabIndex = 5;
            this.l_new_version.Text = "New version:";
            // 
            // l_dim_down
            // 
            this.l_dim_down.AutoSize = true;
            this.l_dim_down.Enabled = false;
            this.l_dim_down.Location = new System.Drawing.Point(12, 76);
            this.l_dim_down.Name = "l_dim_down";
            this.l_dim_down.Size = new System.Drawing.Size(104, 13);
            this.l_dim_down.TabIndex = 8;
            this.l_dim_down.Text = "0/0 MB downloaded";
            // 
            // b_cancel
            // 
            this.b_cancel.Location = new System.Drawing.Point(307, 92);
            this.b_cancel.Name = "b_cancel";
            this.b_cancel.Size = new System.Drawing.Size(294, 23);
            this.b_cancel.TabIndex = 9;
            this.b_cancel.Text = "DON\'T UPDATE";
            this.b_cancel.UseVisualStyleBackColor = true;
            this.b_cancel.Click += new System.EventHandler(this.b_cancel_Click);
            // 
            // TnA_Updater
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(613, 126);
            this.ControlBox = false;
            this.Controls.Add(this.b_cancel);
            this.Controls.Add(this.l_dim_down);
            this.Controls.Add(this.l_new_version);
            this.Controls.Add(this.pb_perc_down);
            this.Controls.Add(this.l_installed_version);
            this.Controls.Add(this.b_update);
            this.Controls.Add(this.b_check);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Fixed3D;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.MaximumSize = new System.Drawing.Size(633, 169);
            this.MinimumSize = new System.Drawing.Size(633, 169);
            this.Name = "TnA_Updater";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "TnA Updater";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.Button b_check;
        private System.Windows.Forms.Button b_update;
        private System.Windows.Forms.Label l_installed_version;
        private System.Windows.Forms.ProgressBar pb_perc_down;
        private System.Windows.Forms.Label l_new_version;
        private System.Windows.Forms.Label l_dim_down;
        private System.Windows.Forms.Button b_cancel;
    }
}

