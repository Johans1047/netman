namespace NetmanConfigTool
{
    partial class ConfigForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private System.Windows.Forms.Label lblTitle;
        private System.Windows.Forms.Label lblUser;
        private System.Windows.Forms.TextBox txtUser;
        private System.Windows.Forms.Label lblPass;
        private System.Windows.Forms.TextBox txtPass;
        private System.Windows.Forms.Label lblLibreria;
        private System.Windows.Forms.TextBox txtLibreria;
        private System.Windows.Forms.Button btnBrowseLibreria;
        private System.Windows.Forms.Label lblSitePath;
        private System.Windows.Forms.TextBox txtSitePath;
        private System.Windows.Forms.Button btnBrowseSitePath;
        private System.Windows.Forms.Label lblSiteUrl;
        private System.Windows.Forms.TextBox txtSiteUrl;
        private System.Windows.Forms.Button btnProbarSitio;
        private System.Windows.Forms.Button btnGuardar;
        private System.Windows.Forms.Button btnIniciar;
        private System.Windows.Forms.Button btnCerrar;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.OpenFileDialog dlgLibreria;
        private System.Windows.Forms.FolderBrowserDialog dlgSitePath;
        private System.Windows.Forms.ToolTip toolTip;

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.lblTitle = new System.Windows.Forms.Label();
            this.lblUser = new System.Windows.Forms.Label();
            this.txtUser = new System.Windows.Forms.TextBox();
            this.lblPass = new System.Windows.Forms.Label();
            this.txtPass = new System.Windows.Forms.TextBox();
            this.lblLibreria = new System.Windows.Forms.Label();
            this.txtLibreria = new System.Windows.Forms.TextBox();
            this.btnBrowseLibreria = new System.Windows.Forms.Button();
            this.lblSitePath = new System.Windows.Forms.Label();
            this.txtSitePath = new System.Windows.Forms.TextBox();
            this.btnBrowseSitePath = new System.Windows.Forms.Button();
            this.lblSiteUrl = new System.Windows.Forms.Label();
            this.txtSiteUrl = new System.Windows.Forms.TextBox();
            this.btnProbarSitio = new System.Windows.Forms.Button();
            this.btnGuardar = new System.Windows.Forms.Button();
            this.btnIniciar = new System.Windows.Forms.Button();
            this.btnCerrar = new System.Windows.Forms.Button();
            this.lblStatus = new System.Windows.Forms.Label();
            this.dlgLibreria = new System.Windows.Forms.OpenFileDialog();
            this.dlgSitePath = new System.Windows.Forms.FolderBrowserDialog();
            this.toolTip = new System.Windows.Forms.ToolTip(this.components);
            this.SuspendLayout();

            const int left = 16;
            const int fieldWidth = 380;
            const int rowH = 48;
            int y = 44;

            // lblTitle
            this.lblTitle.AutoSize = true;
            this.lblTitle.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Bold);
            this.lblTitle.Location = new System.Drawing.Point(left, 12);
            this.lblTitle.Text = "netman — configuración (v0.5)";

            // lblUser
            this.lblUser.AutoSize = true;
            this.lblUser.Location = new System.Drawing.Point(left, y);
            this.lblUser.Text = "Usuario SIPAF:";
            y += 18;
            // txtUser
            this.txtUser.Location = new System.Drawing.Point(left, y);
            this.txtUser.Size = new System.Drawing.Size(fieldWidth, 23);
            y += rowH;

            // lblPass
            this.lblPass.AutoSize = true;
            this.lblPass.Location = new System.Drawing.Point(left, y);
            this.lblPass.Text = "Clave SIPAF (se guarda en Windows Credential Manager, no en disco):";
            y += 18;
            // txtPass
            this.txtPass.Location = new System.Drawing.Point(left, y);
            this.txtPass.Size = new System.Drawing.Size(fieldWidth, 23);
            this.txtPass.PasswordChar = '●';
            this.toolTip.SetToolTip(this.txtPass, "Dejar en blanco para conservar la clave ya guardada sin cambiarla.");
            y += rowH;

            // lblLibreria
            this.lblLibreria.AutoSize = true;
            this.lblLibreria.Location = new System.Drawing.Point(left, y);
            this.lblLibreria.Text = "Proyecto LibreriaSipaf (.vbproj):";
            y += 18;
            // txtLibreria
            this.txtLibreria.Location = new System.Drawing.Point(left, y);
            this.txtLibreria.Size = new System.Drawing.Size(fieldWidth - 34, 23);
            // btnBrowseLibreria
            this.btnBrowseLibreria.Location = new System.Drawing.Point(left + fieldWidth - 30, y - 1);
            this.btnBrowseLibreria.Size = new System.Drawing.Size(30, 25);
            this.btnBrowseLibreria.Text = "...";
            this.btnBrowseLibreria.Click += new System.EventHandler(this.btnBrowseLibreria_Click);
            y += rowH;

            // lblSitePath
            this.lblSitePath.AutoSize = true;
            this.lblSitePath.Location = new System.Drawing.Point(left, y);
            this.lblSitePath.Text = "Carpeta del sitio Sipaf:";
            y += 18;
            // txtSitePath
            this.txtSitePath.Location = new System.Drawing.Point(left, y);
            this.txtSitePath.Size = new System.Drawing.Size(fieldWidth - 34, 23);
            // btnBrowseSitePath
            this.btnBrowseSitePath.Location = new System.Drawing.Point(left + fieldWidth - 30, y - 1);
            this.btnBrowseSitePath.Size = new System.Drawing.Size(30, 25);
            this.btnBrowseSitePath.Text = "...";
            this.btnBrowseSitePath.Click += new System.EventHandler(this.btnBrowseSitePath_Click);
            y += rowH;

            // lblSiteUrl
            this.lblSiteUrl.AutoSize = true;
            this.lblSiteUrl.Location = new System.Drawing.Point(left, y);
            this.lblSiteUrl.Text = "URL base del sitio (IIS Express):";
            y += 18;
            // txtSiteUrl
            this.txtSiteUrl.Location = new System.Drawing.Point(left, y);
            this.txtSiteUrl.Size = new System.Drawing.Size(fieldWidth - 96, 23);
            // btnProbarSitio
            this.btnProbarSitio.Location = new System.Drawing.Point(left + fieldWidth - 92, y - 1);
            this.btnProbarSitio.Size = new System.Drawing.Size(92, 25);
            this.btnProbarSitio.Text = "Probar sitio";
            this.btnProbarSitio.Click += new System.EventHandler(this.btnProbarSitio_Click);
            y += rowH + 4;

            // lblStatus
            this.lblStatus.AutoSize = false;
            this.lblStatus.Location = new System.Drawing.Point(left, y);
            this.lblStatus.Size = new System.Drawing.Size(fieldWidth, 40);
            this.lblStatus.Text = "";
            this.lblStatus.ForeColor = System.Drawing.Color.DimGray;
            y += 46;

            // btnGuardar
            this.btnGuardar.Location = new System.Drawing.Point(left, y);
            this.btnGuardar.Size = new System.Drawing.Size(110, 32);
            this.btnGuardar.Text = "Guardar";
            this.btnGuardar.Click += new System.EventHandler(this.btnGuardar_Click);

            // btnIniciar
            this.btnIniciar.Location = new System.Drawing.Point(left + 118, y);
            this.btnIniciar.Size = new System.Drawing.Size(160, 32);
            this.btnIniciar.Text = "Guardar e iniciar netman";
            this.btnIniciar.Click += new System.EventHandler(this.btnIniciar_Click);

            // btnCerrar
            this.btnCerrar.Location = new System.Drawing.Point(left + fieldWidth - 90, y);
            this.btnCerrar.Size = new System.Drawing.Size(90, 32);
            this.btnCerrar.Text = "Cerrar";
            this.btnCerrar.Click += new System.EventHandler((s, e) => this.Close());

            // dlgLibreria
            this.dlgLibreria.Filter = "Proyecto VB.NET (*.vbproj)|*.vbproj|Todos los archivos (*.*)|*.*";
            this.dlgLibreria.Title = "Ubicar LibreriaSIPAF.vbproj";

            // dlgSitePath
            this.dlgSitePath.Description = "Ubicar la carpeta del sitio Sipaf (Proyecto_VS2013\\Sipaf)";

            // ConfigForm
            this.AutoScaleDimensions = new System.Drawing.SizeF(96F, 96F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.ClientSize = new System.Drawing.Size(left * 2 + fieldWidth, y + 48);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "netman config";
            this.Controls.Add(this.lblTitle);
            this.Controls.Add(this.lblUser);
            this.Controls.Add(this.txtUser);
            this.Controls.Add(this.lblPass);
            this.Controls.Add(this.txtPass);
            this.Controls.Add(this.lblLibreria);
            this.Controls.Add(this.txtLibreria);
            this.Controls.Add(this.btnBrowseLibreria);
            this.Controls.Add(this.lblSitePath);
            this.Controls.Add(this.txtSitePath);
            this.Controls.Add(this.btnBrowseSitePath);
            this.Controls.Add(this.lblSiteUrl);
            this.Controls.Add(this.txtSiteUrl);
            this.Controls.Add(this.btnProbarSitio);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.btnGuardar);
            this.Controls.Add(this.btnIniciar);
            this.Controls.Add(this.btnCerrar);
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
