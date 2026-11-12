namespace CheckUpdates
{
    partial class Updater
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Updater));
            this.pictureBox1 = new System.Windows.Forms.PictureBox();
            this.l_agg = new System.Windows.Forms.Label();
            this.pb_avanz = new System.Windows.Forms.ProgressBar();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).BeginInit();
            this.SuspendLayout();
            // 
            // pictureBox1
            // 
            this.pictureBox1.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox1.Image")));
            this.pictureBox1.Location = new System.Drawing.Point(12, 12);
            this.pictureBox1.Name = "pictureBox1";
            this.pictureBox1.Size = new System.Drawing.Size(290, 168);
            this.pictureBox1.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.pictureBox1.TabIndex = 0;
            this.pictureBox1.TabStop = false;
            // 
            // l_agg
            // 
            this.l_agg.AutoEllipsis = true;
            this.l_agg.Location = new System.Drawing.Point(12, 183);
            this.l_agg.Name = "l_agg";
            this.l_agg.Size = new System.Drawing.Size(290, 41);
            this.l_agg.TabIndex = 1;
            this.l_agg.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // pb_avanz
            // 
            this.pb_avanz.Location = new System.Drawing.Point(12, 227);
            this.pb_avanz.Name = "pb_avanz";
            this.pb_avanz.Size = new System.Drawing.Size(290, 16);
            this.pb_avanz.TabIndex = 2;
            // 
            // Updater
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(314, 255);
            this.ControlBox = false;
            this.Controls.Add(this.pb_avanz);
            this.Controls.Add(this.l_agg);
            this.Controls.Add(this.pictureBox1);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            this.Name = "Updater";
            this.ShowIcon = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "TnA Updater";
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).EndInit();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.PictureBox pictureBox1;
        private System.Windows.Forms.Label l_agg;
        private System.Windows.Forms.ProgressBar pb_avanz;
    }
}

