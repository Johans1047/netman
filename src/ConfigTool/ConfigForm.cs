
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace NetmanConfigTool
{
    /// <summary>
    /// Prototipo v0.5 del GUI de configuración de netman.
    ///
    /// Reemplaza la parte de "pegarle a los parametros por defecto de
    /// Start-Netman.ps1 a mano" por un formulario: guarda las rutas y la URL
    /// en netman.config.json (no secreto) y la clave de SIPAF en Windows
    /// Credential Manager (nunca en disco, nunca en la linea de comandos).
    /// "Guardar e iniciar netman" lanza Start-Netman.ps1 con esos valores,
    /// pasando la clave por variable de entorno del proceso hijo -- mismo
    /// mecanismo que ya usa el propio script para el Host y el browser.
    /// </summary>
    public partial class ConfigForm : Form
    {
        private readonly string _netmanRoot;
        private readonly string _configPath;
        private readonly string _startScriptPath;

        public ConfigForm()
        {
            InitializeComponent();

            _netmanRoot = FindNetmanRoot();
            _configPath = NetmanConfig.DefaultPath(_netmanRoot);
            _startScriptPath = Path.Combine(_netmanRoot, "Start-Netman.ps1");

            Load += ConfigForm_Load;
        }

        /// <summary>
        /// Sube desde bin\Debug (o bin\Release) de src\ConfigTool hasta la raiz
        /// de netman buscando un marcador (Start-Netman.ps1, o HotReloadTool.sln
        /// como respaldo). Esto cubre Debug y Release y cualquier profundidad sin
        /// depender de contar niveles a mano. Si el .exe se movio/empaqueto suelto
        /// en otro lado (sin marcador arriba), cae a la carpeta del .exe -- el
        /// usuario puede corregir las rutas a mano igual.
        /// </summary>
        private static string FindNetmanRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            // Subimos un numero acotado de niveles buscando el marcador; 5 cubre
            // Debug/Release y margenes razonables.
            for (int i = 0; i < 5 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Start-Netman.ps1"))
                    || File.Exists(Path.Combine(dir.FullName, "HotReloadTool.sln")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private void ConfigForm_Load(object sender, EventArgs e)
        {
            var cfg = NetmanConfig.Load(_configPath);

            txtUser.Text = cfg.SipafUser;
            txtLibreria.Text = string.IsNullOrWhiteSpace(cfg.LibreriaSipafProject)
                ? @"C:\LibreriaSipaf\LibreriaSIPAF.vbproj"
                : cfg.LibreriaSipafProject;
            txtSitePath.Text = string.IsNullOrWhiteSpace(cfg.SipafSitePath)
                ? @"C:\Proyecto_VS2013\Sipaf"
                : cfg.SipafSitePath;
            txtSiteUrl.Text = string.IsNullOrWhiteSpace(cfg.SiteBaseUrl)
                ? "http://localhost:12345/SIPAF/"
                : cfg.SiteBaseUrl;

            string savedUser;
            string savedPass = CredentialManager.ReadPassword(out savedUser);
            if (!string.IsNullOrEmpty(savedUser) && string.IsNullOrWhiteSpace(txtUser.Text))
            {
                txtUser.Text = savedUser;
            }

            SetStatus(savedPass != null
                ? "Config cargada. Hay una clave guardada -- dejá la casilla de clave en blanco para conservarla."
                : "Config cargada. Todavía no hay clave guardada en Credential Manager.", false);
        }

        private void btnBrowseLibreria_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(txtLibreria.Text) && File.Exists(txtLibreria.Text))
            {
                dlgLibreria.InitialDirectory = Path.GetDirectoryName(txtLibreria.Text);
            }
            if (dlgLibreria.ShowDialog(this) == DialogResult.OK)
            {
                txtLibreria.Text = dlgLibreria.FileName;
            }
        }

        private void btnBrowseSitePath_Click(object sender, EventArgs e)
        {
            if (Directory.Exists(txtSitePath.Text))
            {
                dlgSitePath.SelectedPath = txtSitePath.Text;
            }
            if (dlgSitePath.ShowDialog(this) == DialogResult.OK)
            {
                txtSitePath.Text = dlgSitePath.SelectedPath;
            }
        }

        private void btnProbarSitio_Click(object sender, EventArgs e)
        {
            string baseUrl = txtSiteUrl.Text.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                SetStatus("Falta la URL base del sitio.", true);
                return;
            }

            btnProbarSitio.Enabled = false;
            SetStatus("Probando " + baseUrl + " ...", false);
            Application.DoEvents();

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(baseUrl + "/wInicio.aspx");
                request.Timeout = 4000;
                request.Method = "GET";
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    SetStatus("OK: el sitio respondió (" + (int)response.StatusCode + ").", false);
                }
            }
            catch (WebException wex)
            {
                var resp = wex.Response as HttpWebResponse;
                if (resp != null)
                {
                    // Un 302/401 a wInicio.aspx tambien cuenta como "el sitio esta arriba".
                    SetStatus("El sitio respondió (" + (int)resp.StatusCode + ").", false);
                }
                else
                {
                    SetStatus("No pude alcanzar el sitio: " + wex.Message
                        + " (¿está levantado IIS Express? Paso 1 del arranque en frío.)", true);
                }
            }
            catch (Exception ex)
            {
                SetStatus("No pude alcanzar el sitio: " + ex.Message, true);
            }
            finally
            {
                btnProbarSitio.Enabled = true;
            }
        }

        private bool TrySaveConfig(out NetmanConfig cfg)
        {
            cfg = null;

            if (string.IsNullOrWhiteSpace(txtUser.Text))
            {
                SetStatus("Falta el usuario de SIPAF.", true);
                return false;
            }
            if (string.IsNullOrWhiteSpace(txtLibreria.Text) || !File.Exists(txtLibreria.Text))
            {
                SetStatus("No encuentro el .vbproj de LibreriaSipaf en esa ruta.", true);
                return false;
            }
            if (string.IsNullOrWhiteSpace(txtSitePath.Text) || !Directory.Exists(txtSitePath.Text))
            {
                SetStatus("No encuentro la carpeta del sitio Sipaf en esa ruta.", true);
                return false;
            }
            if (string.IsNullOrWhiteSpace(txtSiteUrl.Text))
            {
                SetStatus("Falta la URL base del sitio.", true);
                return false;
            }

            cfg = new NetmanConfig
            {
                SipafUser = txtUser.Text.Trim(),
                LibreriaSipafProject = txtLibreria.Text.Trim(),
                SipafSitePath = txtSitePath.Text.Trim(),
                SiteBaseUrl = txtSiteUrl.Text.Trim()
            };

            try
            {
                cfg.Save(_configPath);
            }
            catch (Exception ex)
            {
                SetStatus("No pude guardar " + _configPath + ": " + ex.Message, true);
                return false;
            }

            // Clave: solo la tocamos si el usuario escribió algo. En blanco == "dejala como está".
            if (!string.IsNullOrEmpty(txtPass.Text))
            {
                try
                {
                    CredentialManager.Save(cfg.SipafUser, txtPass.Text);
                    txtPass.Clear();
                }
                catch (Exception ex)
                {
                    SetStatus("Config guardada, pero falló guardar la clave en Credential Manager: " + ex.Message, true);
                    return false;
                }
            }

            return true;
        }

        private void btnGuardar_Click(object sender, EventArgs e)
        {
            NetmanConfig cfg;
            if (TrySaveConfig(out cfg))
            {
                SetStatus("Guardado: " + _configPath, false);
            }
        }

        private void btnIniciar_Click(object sender, EventArgs e)
        {
            NetmanConfig cfg;
            if (!TrySaveConfig(out cfg))
            {
                return;
            }

            string unusedUser;
            string password = CredentialManager.ReadPassword(out unusedUser);
            if (string.IsNullOrEmpty(password))
            {
                SetStatus("No hay clave guardada -- escribila una vez y guardá antes de iniciar.", true);
                return;
            }

            if (!File.Exists(_startScriptPath))
            {
                SetStatus("No encuentro Start-Netman.ps1 en " + _netmanRoot, true);
                return;
            }

            var args = new StringBuilder();
            args.Append("-NoExit -ExecutionPolicy Bypass -File ")
                .Append(Quote(_startScriptPath))
                .Append(" -SipafUser ").Append(Quote(cfg.SipafUser))
                .Append(" -LibreriaSipafProject ").Append(Quote(cfg.LibreriaSipafProject))
                .Append(" -SipafSitePath ").Append(Quote(cfg.SipafSitePath))
                .Append(" -SiteBaseUrl ").Append(Quote(cfg.SiteBaseUrl));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = args.ToString(),
                WorkingDirectory = _netmanRoot,
                UseShellExecute = false
            };
            // La clave viaja por variable de entorno del proceso hijo, no por
            // linea de comandos (esa queda visible en el Administrador de
            // tareas) ni escrita a disco. Start-Netman.ps1 ya sabe leer
            // NETMAN_SIPAF_PASS de ahí si está presente.
            psi.EnvironmentVariables["NETMAN_SIPAF_PASS"] = password;

            try
            {
                Process.Start(psi);
                SetStatus("netman iniciado -- revisá las ventanas del Host y del navegador de hot-reload.", false);
            }
            catch (Exception ex)
            {
                SetStatus("No pude iniciar Start-Netman.ps1: " + ex.Message, true);
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "`\"") + "\"";
        }

        private void SetStatus(string text, bool isError)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = isError ? System.Drawing.Color.Firebrick : System.Drawing.Color.DimGray;
        }
    }
}