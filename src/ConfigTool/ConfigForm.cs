using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NetmanConfigTool
{
    /// <summary>
    /// Prototipo v0.5 del GUI de configuración de netman.
    ///
    /// Guarda las rutas y la URL en netman.config.json (no secreto) y la
    /// clave de SIPAF en Windows Credential Manager (nunca en disco, nunca
    /// en la linea de comandos). "Iniciar netman" lanza el Host y el
    /// navegador de hot-reload directamente (sin pasar por Start-Netman.ps1
    /// ni por ventanas de consola sueltas) con su stdout/stderr redirigido
    /// al panel de logs de este mismo formulario.
    /// </summary>
    public partial class ConfigForm : Form
    {
        private readonly string _netmanRoot;
        private readonly string _configPath;
        private readonly string _hostExePath;
        private readonly string _browserJsPath;

        private Process _hostProcess;
        private Process _browserProcess;

        // ---- Actualizaciones (releases de GitHub) ---------------------------
        // null => esto es un checkout de desarrollo (no hay version.txt): no se busca nada.
        private readonly string _installedVersion;
        private UpdateInfo _pendingUpdate;
        private bool _updating;

        /// <summary>
        /// null => hay "node" en el PATH del sistema, se usa tal cual.
        /// Si no, esta es la carpeta de una copia portable de Node (sin
        /// instalador) que el propio GUI descargó -- ahí también viven
        /// npm.cmd y npx.cmd, así que no hace falta nada más instalado.
        /// </summary>
        private string _nodeHome;

        // ---- URLs clickeables en los logs ---------------------------------
        // Los logs son RichTextBox sobre fondo negro: el azul por defecto de
        // DetectUrls casi no se lee, así que las URLs se pintan a mano (azul
        // claro + subrayado) y se guarda su rango para abrirlas con un clic.
        private static readonly Regex UrlRegex =
            new Regex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Color LinkColor = Color.FromArgb(135, 206, 250);   // celeste (LightSkyBlue)
        private Font _linkFont;

        private struct LinkSpan
        {
            public int Start;
            public int Length;
            public string Url;
        }

        private readonly Dictionary<RichTextBox, List<LinkSpan>> _links =
            new Dictionary<RichTextBox, List<LinkSpan>>();

        // ---- Paleta tomada del ícono (netman.ico): azul petróleo + rayo blanco ----
        private static readonly Color Brand = Color.FromArgb(0x0B, 0x6E, 0x99);      // fondo del ícono
        private static readonly Color BrandDark = Color.FromArgb(0x09, 0x5A, 0x7D);  // hover
        private static readonly Color BrandDeeper = Color.FromArgb(0x07, 0x45, 0x5F);// presionado
        private static readonly Color Accent = Color.FromArgb(0x3C, 0xB4, 0xE5);     // línea bajo el encabezado
        private static readonly Color FormBack = Color.FromArgb(0xF3, 0xF7, 0xF9);
        private static readonly Color Ink = Color.FromArgb(0x1F, 0x2D, 0x35);
        private static readonly Color InkSoft = Color.FromArgb(0x4A, 0x5B, 0x66);
        private static readonly Color LineGray = Color.FromArgb(0xB8, 0xC7, 0xCF);
        private static readonly Color LogBack = Color.FromArgb(0x0A, 0x2A, 0x3A);    // logs: azul muy oscuro, no negro puro
        private static readonly Color LogFore = Color.FromArgb(0xD6, 0xE4, 0xEC);
        private static readonly Color OkColor = Color.FromArgb(0x0F, 0x7A, 0x5C);
        private static readonly Color ErrColor = Color.FromArgb(0xC0, 0x39, 0x2B);

        private enum BtnKind { Primary, Outline, Neutral }

        /// <summary>
        /// Botón plano con los colores de la marca. Se repinta al habilitar o
        /// deshabilitar: un botón Flat deshabilitado conserva su BackColor y
        /// quedaría texto gris sobre azul.
        /// </summary>
        private static void StyleButton(Button b, BtnKind kind)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            b.FlatAppearance.BorderSize = 1;

            Action apply = delegate
            {
                if (!b.Enabled)
                {
                    b.BackColor = Color.FromArgb(0xE4, 0xE9, 0xEC);
                    b.ForeColor = Color.FromArgb(0x93, 0xA1, 0xAA);
                    b.FlatAppearance.BorderColor = Color.FromArgb(0xD3, 0xDB, 0xE0);
                    b.Cursor = Cursors.Default;
                    return;
                }

                b.Cursor = Cursors.Hand;
                switch (kind)
                {
                    case BtnKind.Primary:
                        b.BackColor = Brand;
                        b.ForeColor = Color.White;
                        b.FlatAppearance.BorderColor = Brand;
                        b.FlatAppearance.MouseOverBackColor = BrandDark;
                        b.FlatAppearance.MouseDownBackColor = BrandDeeper;
                        break;
                    case BtnKind.Outline:
                        b.BackColor = Color.White;
                        b.ForeColor = Brand;
                        b.FlatAppearance.BorderColor = Brand;
                        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xE3, 0xF0, 0xF6);
                        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xCB, 0xE2, 0xEC);
                        break;
                    default:
                        b.BackColor = Color.White;
                        b.ForeColor = Ink;
                        b.FlatAppearance.BorderColor = LineGray;
                        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xEC, 0xF1, 0xF4);
                        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xDD, 0xE5, 0xEA);
                        break;
                }
            };

            apply();
            b.EnabledChanged += delegate { apply(); };
        }

        private void ApplyBrandStyle()
        {
            StyleButton(btnIniciar, BtnKind.Primary);
            StyleButton(btnGuardar, BtnKind.Outline);
            StyleButton(btnProbarSitio, BtnKind.Outline);
            StyleButton(btnBrowseLibreria, BtnKind.Outline);
            StyleButton(btnBrowseSitePath, BtnKind.Outline);
            StyleButton(btnDetener, BtnKind.Neutral);
            StyleButton(btnCerrar, BtnKind.Neutral);
            StyleButton(btnActualizar, BtnKind.Outline);

            foreach (Label l in new Label[] { lblUser, lblPass, lblLibreria, lblSitePath, lblSiteUrl, lblLogs })
            {
                l.ForeColor = Ink;
            }
        }

        // Línea de acento (azul claro) en el borde inferior del encabezado.
        private void pnlHeader_Paint(object sender, PaintEventArgs e)
        {
            int h = Math.Max(2, (int)Math.Round(3 * e.Graphics.DpiY / 96f));
            using (var br = new SolidBrush(Accent))
            {
                e.Graphics.FillRectangle(br, 0, pnlHeader.Height - h, pnlHeader.Width, h);
            }
        }

        public ConfigForm()
        {
            InitializeComponent();
            ApplyBrandStyle();

            try
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // Sin icono no es motivo para no arrancar.
            }

            _netmanRoot = FindNetmanRoot();
            _configPath = NetmanConfig.DefaultPath(_netmanRoot);
            _hostExePath = Path.Combine(_netmanRoot, "src", "Host", "bin", "Debug", "HotReloadTool.Host.exe");
            _browserJsPath = Path.Combine(_netmanRoot, "netman-hotreload-browser.js");

            _installedVersion = Updater.InstalledVersion(_netmanRoot);
            lblTitle.Text = "netman — configuración (" + Updater.Display(_installedVersion) + ")";

            // Ventana por defecto el doble de ancha (sin pasarse de la pantalla): asi cabe
            // el boton "Actualizar a vX.Y.Z" del encabezado sin pisar el titulo.
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(ClientSize.Width * 2, area.Width - 40), ClientSize.Height);

            Load += ConfigForm_Load;
            FormClosing += ConfigForm_FormClosing;

            WireLinks(txtLogHost);
            WireLinks(txtLogBrowser);

            SizeChanged += (s, e) => ApplyResponsiveLayout();
        }

        // ---- Diseño adaptable: la parte de arriba sigue el ancho de la ventana ----
        // Los logs ya crecen con la ventana (Anchor). Esto hace lo mismo con los
        // campos y botones de arriba: el bloque se ensancha hasta un máximo
        // y queda centrado. Trabaja con deltas sobre la posición actual (la que
        // dejó el escalado por DPI), así no depende de medidas fijas.
        private const int MaxContentWidth = 750;   // px a 96 DPI
        private const int ContentMargin = 16;      // px a 96 DPI

        private void ApplyResponsiveLayout()
        {
            if (txtUser == null || WindowState == FormWindowState.Minimized)
            {
                return;
            }

            float dpi = CurrentAutoScaleDimensions.Width / 96f;
            if (dpi <= 0f) dpi = 1f;

            int margin = (int)(ContentMargin * dpi);
            int available = ClientSize.Width - 2 * margin;
            int newWidth = Math.Min(available, (int)(MaxContentWidth * dpi));
            int curWidth = txtUser.Width;
            if (newWidth < 1 || curWidth < 1)
            {
                return;
            }

            int newLeft = (ClientSize.Width - newWidth) / 2;
            int dL = newLeft - txtUser.Left;
            int dW = newWidth - curWidth;
            if (dL == 0 && dW == 0)
            {
                return;
            }

            SuspendLayout();
            try
            {
                // Alineados a la izquierda del bloque: solo se desplazan.
                foreach (Control c in new Control[] {
                    logo, lblTitle, lblUser, lblPass, lblLibreria, lblSitePath, lblSiteUrl,
                    btnGuardar, btnIniciar, btnDetener })
                {
                    c.Left += dL;
                }

                // Ocupan todo el ancho del bloque: se desplazan y se estiran.
                foreach (Control c in new Control[] {
                    lblHint, txtUser, txtPass, txtLibreria, txtSitePath, txtSiteUrl, lblStatus })
                {
                    c.Left += dL;
                    c.Width += dW;
                }

                // Pegados al borde derecho del bloque: se desplazan y siguen el borde.
                foreach (Control c in new Control[] {
                    btnBrowseLibreria, btnBrowseSitePath, btnProbarSitio, btnCerrar, btnActualizar })
                {
                    c.Left += dL + dW;
                }
            }
            finally
            {
                ResumeLayout();
            }
        }

        /// <summary>
        /// Sube desde donde esté el .exe (bin\Debug o bin\Release de
        /// src\ConfigTool, normalmente) buscando Start-Netman.ps1 como marca
        /// de la raiz de netman. Así no importa cuántos niveles de carpeta
        /// haya exactamente -- si en algún momento se empaqueta/mueve el
        /// .exe suelto a otro lado, no lo encuentra y cae a la carpeta del
        /// .exe (el usuario corrige las rutas a mano igual).
        /// </summary>
        private static string FindNetmanRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Start-Netman.ps1")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private void ConfigForm_Load(object sender, EventArgs e)
        {
            ApplyResponsiveLayout();

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
                ? "Config cargada. Hay una clave guardada -- Mantén la casilla de clave en blanco para conservarla."
                : "Config cargada. Todavía no hay clave guardada en Credential Manager.", false);

            if (Debugger.IsAttached)
            {
                AppendLog("GUI", "AVISO: estás corriendo con el debugger de Visual Studio adjunto (F5). "
                    + "Si parás la depuración mientras el Host o el navegador siguen corriendo, VS puede "
                    + "matarlos junto con este proceso. Usá Ctrl+F5 la próxima vez.");
            }

            CheckForUpdateAsync();
        }

        // Pregunta a GitHub por el ultimo release sin bloquear la ventana. Si no hay
        // red o falla, solo deja una linea en el log: nunca molesta con un cuadro de dialogo.
        private async void CheckForUpdateAsync()
        {
            if (_installedVersion == null || Updater.IsDisabled())
            {
                return;
            }

            try
            {
                UpdateInfo info = await Task.Run(() => Updater.CheckForUpdate(_installedVersion));
                if (IsDisposed)
                {
                    return;
                }

                if (info == null)
                {
                    AppendLog("GUI", "netman " + Updater.Display(_installedVersion) + ": estás al día.");
                    return;
                }

                _pendingUpdate = info;
                btnActualizar.Text = "Actualizar a " + Updater.Display(info.Tag);
                btnActualizar.Visible = true;
                AppendLog("GUI", "Hay una versión nueva de netman: " + Updater.Display(info.Tag) + " (instalada: " + Updater.Display(_installedVersion)
                    + "). Botón \"Actualizar\" arriba a la derecha.");
            }
            catch (Exception ex)
            {
                AppendLog("GUI", "No pude buscar actualizaciones: " + ex.GetBaseException().Message);
            }
        }

        private async void btnActualizar_Click(object sender, EventArgs e)
        {
            UpdateInfo info = _pendingUpdate;
            if (info == null || _updating)
            {
                return;
            }

            if (_hostProcess != null || _browserProcess != null)
            {
                MessageBox.Show(this, "netman está corriendo. Pulsa Detener antes de actualizar.",
                    "Actualizar netman", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Las notas del release (lo que cambió), recortadas para que el cuadro no se desborde.
            string notes = (info.Notes ?? string.Empty).Trim();
            if (notes.Length > 500)
            {
                notes = notes.Substring(0, 500) + "...";
            }

            DialogResult answer = MessageBox.Show(this,
                "Se va a instalar " + Updater.Display(info.Tag) + " en:\n" + _netmanRoot + "\n\n"
                + (notes.Length > 0 ? "Novedades:\n" + notes + "\n\n" : string.Empty)
                + "Tu configuración (netman.config.json), la clave guardada y Node/Playwright no se tocan. "
                + "netman se cierra y se vuelve a abrir solo.\n\n¿Continuar?",
                "Actualizar netman", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            _updating = true;
            btnActualizar.Enabled = false;
            btnIniciar.Enabled = false;
            SetStatus("Descargando " + Updater.Display(info.Tag) + " ...", false);
            AppendLog("GUI", "Descargando " + info.AssetName + " ...");

            try
            {
                string staging = await Task.Run(() => Updater.Download(info, pct =>
                {
                    try { BeginInvoke(new Action(() => SetStatus("Descargando " + Updater.Display(info.Tag) + " ... " + pct + "%", false))); }
                    catch { }
                }));

                AppendLog("GUI", "Paquete verificado. Cerrando para aplicar la actualización...");
                Updater.LaunchApply(staging, _netmanRoot, Application.ExecutablePath, info);
                _closingAfterStop = true;
                Close();
            }
            catch (Exception ex)
            {
                string msg = ex.GetBaseException().Message;
                AppendLog("GUI", "ERROR actualizando: " + msg);
                SetStatus("No se pudo actualizar: " + msg, true);
                _updating = false;
                btnActualizar.Enabled = true;
                btnIniciar.Enabled = _hostProcess == null && _browserProcess == null;
            }
        }

        // Verdadero cuando ya detuvimos los procesos y el cierre es el definitivo.
        private bool _closingAfterStop;

        private async void ConfigForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_stopping && e.CloseReason == CloseReason.UserClosing)
            {
                // Ya estamos deteniendo (Detener o un cierre previo): no cerrar
                // todavia o los hilos de fondo mueren a medio taskkill y dejan
                // procesos huerfanos. Se cierra solo cuando termina.
                e.Cancel = true;
                return;
            }

            if (_closingAfterStop || (_hostProcess == null && _browserProcess == null))
            {
                return;
            }

            if (e.CloseReason != CloseReason.UserClosing)
            {
                // Apagado de Windows u otro cierre que no se puede cancelar ni
                // esperar: limpieza sincrona y rapida (solo el Browser, que es
                // el que tiene Chrome colgando, necesita matar el arbol).
                StopProcess(ref _browserProcess, "Browser", true);
                StopProcess(ref _hostProcess, "Host", false);
                return;
            }

            // Cierre normal: NO bloquear el hilo de UI mientras taskkill mata el
            // arbol de procesos (con Chrome de por medio puede tardar varios
            // segundos y la ventana se quedaba "colgada"). Cancelamos este cierre,
            // paramos todo en segundo plano y volvemos a cerrar cuando termine.
            e.Cancel = true;
            Enabled = false;
            SetStatus("Deteniendo netman antes de cerrar...", false);
            await StopAllAsync();
            _closingAfterStop = true;
            Close();
        }

        private void btnCerrar_Click(object sender, EventArgs e)
        {
            Close();
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
            string baseUrl = txtSiteUrl.Text;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                SetStatus("Falta la URL base del sitio.", true);
                return;
            }

            btnProbarSitio.Enabled = false;
            SetStatus("Probando " + baseUrl + " ...", false);
            Application.DoEvents();

            string message;
            bool ok = ProbeSite(baseUrl, out message);
            SetStatus(message, !ok);
            btnProbarSitio.Enabled = true;
        }

        /// <summary>
        /// GET a wInicio.aspx: cualquier respuesta HTTP (incluso un 302/401)
        /// ya dice que IIS Express está arriba. Mismo chequeo que el Paso 0
        /// de Start-Netman.ps1.
        /// </summary>
        private static bool ProbeSite(string baseUrl, out string message)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/wInicio.aspx");
                request.Timeout = 4000;
                request.Method = "GET";
                // wInicio.aspx hace Request.UserAgent.ToLower() sin null-check:
                // sin User-Agent el sitio tira 500. Un browser real siempre lo manda.
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) netman-probe/1.0";
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    message = "OK: el sitio respondió (" + (int)response.StatusCode + ").";
                    return true;
                }
            }
            catch (WebException wex)
            {
                var resp = wex.Response as HttpWebResponse;
                if (resp != null)
                {
                    message = "El sitio respondió (" + (int)resp.StatusCode + ").";
                    return true;
                }
                message = "No pude alcanzar el sitio: " + wex.Message
                    + " (¿está levantado IIS Express? Paso 1 del arranque en frío.)";
                return false;
            }
            catch (Exception ex)
            {
                message = "No pude alcanzar el sitio: " + ex.Message;
                return false;
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

        private async void btnIniciar_Click(object sender, EventArgs e)
        {
            if (_hostProcess != null || _browserProcess != null)
            {
                SetStatus("netman ya está corriendo -- usá \"Detener\" primero.", true);
                return;
            }

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

            if (!File.Exists(_hostExePath))
            {
                SetStatus("No encuentro " + _hostExePath + " -- compilá HotReloadTool.sln primero.", true);
                return;
            }
            if (!File.Exists(_browserJsPath))
            {
                SetStatus("No encuentro " + _browserJsPath, true);
                return;
            }

            SetRunningState(true);
            ClearLog();
            SetStatus("Iniciando netman...", false);

            // Las dejamos en el entorno de ESTE proceso; StartTrackedProcess
            // las propaga a los hijos (Host y node) -- nunca por linea de
            // comandos ni a disco.
            Environment.SetEnvironmentVariable("NETMAN_SIPAF_USER", cfg.SipafUser);
            Environment.SetEnvironmentVariable("NETMAN_SIPAF_PASS", password);

            AppendLog("GUI", "Verificando que el sitio responda en " + cfg.SiteBaseUrl + " ...");
            string probeMessage = null;
            await Task.Run(() => ProbeSite(cfg.SiteBaseUrl, out probeMessage));
            AppendLog("GUI", probeMessage);

            // Node primero: si no está en el PATH, bajamos una copia portable
            // (sin instalador, no toca el sistema) antes de arrancar nada.
            bool nodeOk = await EnsureNodeAsync();
            if (!nodeOk)
            {
                AppendLog("GUI", "ERROR: no pude conseguir Node. Revisá el log de arriba (¿hay salida a internet hacia nodejs.org?).");
                SetRunningState(false);
                return;
            }

            string deployTo = Path.Combine(cfg.SipafSitePath, "Bin");
            string recycleTarget = Path.Combine(cfg.SipafSitePath, "web.config");

            string hostArgs = "start --mode build-recycle -p " + Quote(cfg.LibreriaSipafProject)
                + " --deploy-to " + Quote(deployTo)
                + " --recycle-target " + Quote(recycleTarget)
                + " --extensions \"*.vb,*.config\""
                + " --web-watch " + Quote(cfg.SipafSitePath)
                + " --site-url " + Quote(cfg.SiteBaseUrl);

            _hostProcess = StartTrackedProcess(_hostExePath, hostArgs, Path.GetDirectoryName(_hostExePath), "Host");
            if (_hostProcess == null)
            {
                SetRunningState(false);
                return;
            }
            AppendLog("GUI", "Host lanzado (PID " + _hostProcess.Id + "). Esperando unos segundos antes del navegador...");

            await Task.Delay(2000);

            bool playwrightOk = await Task.Run(() => RunBlocking(NodeExePath(), "-e " + Quote("require.resolve('playwright')"), _netmanRoot, "node", false) == 0);
            if (!playwrightOk)
            {
                AppendLog("GUI", "Playwright no está instalado todavía -- instalando (una sola vez, puede tardar)...");
                string npmInvoke = _nodeHome == null ? "npm install playwright" : Quote(NpmCmdPath()) + " install playwright";
                // Sin 'playwright install chromium': se usa el Edge instalado (channel msedge).
                bool installedOk = await Task.Run(() =>
                    RunBlocking("cmd.exe", "/c " + npmInvoke, _netmanRoot, "npm", true) == 0);
                if (!installedOk)
                {
                    AppendLog("GUI", "ERROR: no se pudo instalar Playwright. Revisá el log de arriba.");
                    SetRunningState(false);
                    StopProcess(ref _hostProcess, "Host", false);
                    return;
                }
            }

            _browserProcess = StartTrackedProcess(NodeExePath(), Quote(_browserJsPath), _netmanRoot, "Browser");
            if (_browserProcess == null)
            {
                SetRunningState(false);
                StopProcess(ref _hostProcess, "Host", false);
                return;
            }

            AppendLog("GUI", "Navegador de hot-reload lanzado (PID " + _browserProcess.Id + "). Editá y mirá los logs de abajo.");
            SetStatus("Netman corriendo", false);
        }

        /// <summary>
        /// Se fija si hay "node" en el PATH; si no, baja y descomprime una
        /// copia portable oficial (sin instalador, no pide admin, no toca
        /// el sistema) en netman\tools\node-portable\. La segunda vez que
        /// se corre esto en la misma máquina no vuelve a descargar nada --
        /// ya la encuentra ahí.
        /// </summary>
        private async Task<bool> EnsureNodeAsync()
        {
            if (RunBlocking("node", "--version", _netmanRoot, "node-check", false) == 0)
            {
                _nodeHome = null;
                AppendLog("GUI", "Node encontrado en el PATH del sistema.");
                return true;
            }

            string portableDir = Path.Combine(_netmanRoot, "tools", "node-portable");
            if (File.Exists(Path.Combine(portableDir, "node.exe")))
            {
                _nodeHome = portableDir;
                AppendLog("GUI", "Usando la copia portable de Node ya descargada en " + portableDir);
                return true;
            }

            AppendLog("GUI", "No encontré Node en el PATH -- descargando una copia portable (una sola vez, sin instalador)...");
            bool ok = await Task.Run(() => DownloadPortableNode(portableDir));
            if (ok)
            {
                _nodeHome = portableDir;
            }
            return ok;
        }

        private bool DownloadPortableNode(string destDir)
        {
            string tempZip = null;
            string extractRoot = null;
            try
            {
                AppendLog("GUI", "Consultando la última versión LTS en nodejs.org...");
                string indexJson;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "netman-config-tool");
                    indexJson = wc.DownloadString("https://nodejs.org/dist/index.json");
                }

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var entries = serializer.Deserialize<List<Dictionary<string, object>>>(indexJson);

                string version = null;
                foreach (var entry in entries)
                {
                    object ltsVal;
                    entry.TryGetValue("lts", out ltsVal);
                    // El campo "lts" es `false` (bool) para versiones no-LTS,
                    // o el nombre del release (string) para las que sí lo son.
                    if (ltsVal is bool && !(bool)ltsVal)
                    {
                        continue;
                    }

                    object versionVal;
                    entry.TryGetValue("version", out versionVal);
                    version = versionVal as string;
                    if (!string.IsNullOrEmpty(version))
                    {
                        break;
                    }
                }

                if (string.IsNullOrEmpty(version))
                {
                    AppendLog("GUI", "No pude determinar la última versión LTS de Node en el índice de nodejs.org.");
                    return false;
                }

                string zipName = "node-" + version + "-win-x64.zip";
                string url = "https://nodejs.org/dist/" + version + "/" + zipName;
                tempZip = Path.Combine(Path.GetTempPath(), zipName);

                AppendLog("GUI", "Descargando Node " + version + " (portable, win-x64)...");
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "netman-config-tool");
                    wc.DownloadFile(url, tempZip);
                }

                AppendLog("GUI", "Descomprimiendo...");
                extractRoot = Path.Combine(Path.GetTempPath(), "netman-node-extract-" + Guid.NewGuid().ToString("N"));
                ZipFile.ExtractToDirectory(tempZip, extractRoot);

                string innerFolder = Directory.GetDirectories(extractRoot).FirstOrDefault();
                if (innerFolder == null)
                {
                    AppendLog("GUI", "El .zip descargado de Node no tiene la estructura esperada.");
                    return false;
                }

                if (Directory.Exists(destDir))
                {
                    Directory.Delete(destDir, true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destDir));
                Directory.Move(innerFolder, destDir);

                AppendLog("GUI", "Node " + version + " portable listo en " + destDir);
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("GUI", "ERROR descargando Node portable: " + ex.Message);
                return false;
            }
            finally
            {
                try { if (tempZip != null) File.Delete(tempZip); } catch { }
                try { if (extractRoot != null) Directory.Delete(extractRoot, true); } catch { }
            }
        }

        private string NodeExePath()
        {
            return _nodeHome == null ? "node" : Path.Combine(_nodeHome, "node.exe");
        }

        private string NpmCmdPath()
        {
            return _nodeHome == null ? "npm" : Path.Combine(_nodeHome, "npm.cmd");
        }

        private string NpxCmdPath()
        {
            return _nodeHome == null ? "npx" : Path.Combine(_nodeHome, "npx.cmd");
        }

        private async void btnDetener_Click(object sender, EventArgs e)
        {
            AppendLog("GUI", "Deteniendo netman...");
            btnDetener.Enabled = false;
            SetStatus("Deteniendo netman...", false);
            // taskkill /T tarda (el Browser trae un arbol de Chrome): va en
            // segundo plano para que la ventana siga respondiendo.
            await StopAllAsync();
            SetRunningState(false);
            SetStatus("netman detenido.", false);
            // El log tambien tiene que decirlo: si la ultima linea es "Deteniendo netman...",
            // parece que sigue esperando aunque ya termino.
            AppendLog("GUI", "netman detenido.");
        }

        /// <summary>
        /// Detiene Browser y Host sin bloquear el hilo de UI. Toma los procesos
        /// y limpia los campos YA (en el hilo de UI), asi un segundo clic o el
        /// cierre del formulario no intentan matarlos otra vez; los dos arboles
        /// se matan en paralelo en hilos de fondo.
        /// </summary>
        private async Task StopAllAsync()
        {
            Process browser = _browserProcess;
            Process host = _hostProcess;
            _browserProcess = null;
            _hostProcess = null;
            _stopping = true;

            try
            {
                var work = new List<Task>();
                if (browser != null)
                {
                    work.Add(Task.Run(() => StopDetached(browser, "Browser")));
                }
                if (host != null)
                {
                    work.Add(Task.Run(() => StopDetached(host, "Host")));
                }
                await Task.WhenAll(work);
            }
            finally
            {
                _stopping = false;
            }
        }

        // Verdadero mientras StopAllAsync esta matando procesos en segundo plano.
        private bool _stopping;

        /// <summary>Corre en un hilo de fondo: mata el arbol del proceso y lo libera.</summary>
        private void StopDetached(Process process, string tag)
        {
            try
            {
                if (!process.HasExited)
                {
                    KillProcessTree(process.Id, tag);
                }
            }
            catch (Exception ex)
            {
                AppendLog("GUI", "No pude detener " + tag + ": " + ex.Message);
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        /// <summary>Lanza un proceso hijo con stdout/stderr redirigidos al panel de logs.</summary>
        private Process StartTrackedProcess(string fileName, string arguments, string workingDirectory, string tag)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.EnvironmentVariables["NETMAN_SIPAF_USER"] = Environment.GetEnvironmentVariable("NETMAN_SIPAF_USER");
                psi.EnvironmentVariables["NETMAN_SIPAF_PASS"] = Environment.GetEnvironmentVariable("NETMAN_SIPAF_PASS");

                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (s, ev) => { if (ev.Data != null) AppendLog(tag, ev.Data); };
                process.ErrorDataReceived += (s, ev) => { if (ev.Data != null) AppendLog(tag + "!", ev.Data); };
                process.Exited += (s, ev) => AppendLog("GUI", tag + " terminó.");
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return process;
            }
            catch (Exception ex)
            {
                AppendLog("GUI", "ERROR lanzando " + tag + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Corre un proceso corto y espera a que termine (chequeo/instalación de Playwright).</summary>
        private int RunBlocking(string fileName, string arguments, string workingDirectory, string tag, bool showLog)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = new Process { StartInfo = psi })
                {
                    if (showLog)
                    {
                        p.OutputDataReceived += (s, ev) => { if (ev.Data != null) AppendLog(tag, ev.Data); };
                        p.ErrorDataReceived += (s, ev) => { if (ev.Data != null) AppendLog(tag + "!", ev.Data); };
                    }
                    p.Start();
                    if (showLog)
                    {
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                    }
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                AppendLog("GUI", "ERROR ejecutando " + fileName + " " + arguments + ": " + ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// Version SINCRONA (bloquea el hilo de UI): solo para rutas de error de
        /// arranque y cierres que no se pueden esperar. Para el boton Detener y
        /// el cierre normal usar <see cref="StopAllAsync"/>.
        /// </summary>
        /// <param name="killTree">
        /// true = taskkill /T (mata tambien a los hijos, p.ej. el Chrome que
        /// Playwright lanza desde el node.exe del Browser; tarda mas);
        /// false = Process.Kill() rapido, solo ese PID.
        /// </param>
        private void StopProcess(ref Process process, string tag, bool killTree)
        {
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    // process.Kill() solo mata ESTE PID, no es un "mata el
                    // arbol". El Browser lanza Chrome via Playwright como
                    // proceso hijo -- matar solo el node.exe lo deja huerfano,
                    // vivo de fondo sin que el GUI se entere (asi se nos quedo
                    // un Chrome corriendo horas con el Host ya muerto). Usar
                    // taskkill /T mata el PID y todos sus descendientes de una.
                    if (killTree)
                    {
                        KillProcessTree(process.Id, tag);
                    }
                    else
                    {
                        process.Kill();
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("GUI", "No pude detener " + tag + ": " + ex.Message);
            }
            finally
            {
                try { process.Dispose(); } catch { }
                process = null;
            }
        }

        /// <summary>
        /// Mata un proceso y todo su arbol de descendientes via
        /// <c>taskkill /T /F</c>. Si taskkill mismo no se puede lanzar por
        /// alguna razon, cae de vuelta a un Process.Kill() simple (mata solo
        /// ese PID) -- mejor una limpieza parcial que ninguna.
        /// </summary>
        private void KillProcessTree(int pid, string tag)
        {
            try
            {
                using (var taskkill = new Process())
                {
                    taskkill.StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/PID " + pid + " /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    taskkill.Start();
                    taskkill.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                AppendLog("GUI", "taskkill fallo para " + tag + " (PID " + pid + "), probando Kill() simple: " + ex.Message);
                try
                {
                    using (var p = Process.GetProcessById(pid))
                    {
                        if (!p.HasExited) p.Kill();
                    }
                }
                catch
                {
                    // Ya habra muerto solo, o no se pudo de ninguna forma --
                    // no hay mas nada razonable que intentar aca.
                }
            }
        }

        private void SetRunningState(bool running)
        {
            btnIniciar.Enabled = !running;
            btnDetener.Enabled = running;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private void SetStatus(string text, bool isError)
        {
            lblStatus.Text = text;
            // Alto contraste: rojo fuerte para error, verde fuerte para OK/neutro.
            lblStatus.ForeColor = isError ? ErrColor : OkColor;
        }

        private System.Windows.Forms.RichTextBox BoxForTag(string tag)
        {
            // "Browser" y "Browser!" van a su pestaña; GUI/Host/Host! a la otra.
            return (tag != null && tag.StartsWith("Browser", StringComparison.Ordinal))
                ? txtLogBrowser
                : txtLogHost;
        }

        private void ClearLog()
        {
            if (tabsLogs.InvokeRequired)
            {
                tabsLogs.Invoke(new Action(ClearLog));
                return;
            }
            txtLogHost.Clear();
            txtLogBrowser.Clear();
            _links[txtLogHost].Clear();
            _links[txtLogBrowser].Clear();
        }

        private void AppendLog(string tag, string line)
        {
            var box = BoxForTag(tag);
            if (box.InvokeRequired)
            {
                try { box.Invoke(new Action<string, string>(AppendLog), tag, line); } catch { }
                return;
            }
            int start = box.TextLength;
            string text = "[" + tag + "] " + line + Environment.NewLine;
            box.AppendText(text);
            MarkLinks(box, start, text);
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        }

        /// <summary>Quita la puntuación que suele pegarse al final de una URL en una frase.</summary>
        private static string TrimUrl(string url)
        {
            return url.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"');
        }

        /// <summary>Pinta como enlace cada URL de la línea recién agregada y guarda su rango.</summary>
        private void MarkLinks(RichTextBox box, int lineStart, string text)
        {
            var matches = UrlRegex.Matches(text);
            if (matches.Count == 0)
            {
                return;
            }

            if (_linkFont == null)
            {
                _linkFont = new Font(box.Font, FontStyle.Bold | FontStyle.Underline);
            }

            foreach (Match m in matches)
            {
                string url = TrimUrl(m.Value);
                if (url.Length <= "https://".Length)
                {
                    continue;
                }

                int start = lineStart + m.Index;
                box.Select(start, url.Length);
                box.SelectionColor = LinkColor;
                box.SelectionFont = _linkFont;
                _links[box].Add(new LinkSpan { Start = start, Length = url.Length, Url = url });
            }

            // Lo que se agregue después vuelve al estilo normal.
            box.Select(box.TextLength, 0);
            box.SelectionColor = box.ForeColor;
            box.SelectionFont = box.Font;
        }

        private void WireLinks(RichTextBox box)
        {
            _links[box] = new List<LinkSpan>();
            box.MouseMove += LogBox_MouseMove;
            box.MouseClick += LogBox_MouseClick;
        }

        /// <summary>Devuelve la URL bajo el puntero, o null si no hay ninguna.</summary>
        private string UrlAt(RichTextBox box, Point location)
        {
            int index = box.GetCharIndexFromPosition(location);
            List<LinkSpan> spans = _links[box];
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                LinkSpan span = spans[i];
                if (index >= span.Start && index < span.Start + span.Length)
                {
                    // GetCharIndexFromPosition devuelve el carácter más cercano:
                    // si el puntero está pasado el final de la URL, no cuenta.
                    Point end = box.GetPositionFromCharIndex(span.Start + span.Length - 1);
                    if (location.Y >= end.Y && location.Y <= end.Y + box.Font.Height * 2
                        && (index < span.Start + span.Length - 1 || location.X <= end.X + (int)box.Font.Size * 2))
                    {
                        return span.Url;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Le pide al navegador de netman que abra la URL: escribe
        /// open-url.request junto al Host y el script de Node (que revisa ese
        /// archivo cada ~1 s) la abre en una pestaña del mismo navegador.
        /// Devuelve false si ese navegador no está corriendo.
        /// </summary>
        private bool OpenInNetmanBrowser(string url)
        {
            try
            {
                if (_browserProcess == null || _browserProcess.HasExited)
                {
                    return false;
                }

                string file = Path.Combine(Path.GetDirectoryName(_hostExePath), "open-url.request");
                File.WriteAllText(file, url + Environment.NewLine);
                AppendLog("GUI", "Abriendo en el navegador de netman: " + url);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void LogBox_MouseMove(object sender, MouseEventArgs e)
        {
            var box = (RichTextBox)sender;
            box.Cursor = UrlAt(box, e.Location) != null ? Cursors.Hand : Cursors.IBeam;
        }

        private void LogBox_MouseClick(object sender, MouseEventArgs e)
        {
            var box = (RichTextBox)sender;
            if (e.Button != MouseButtons.Left || box.SelectionLength > 0)
            {
                return; // arrastrar para seleccionar/copiar texto no abre nada
            }

            string url = UrlAt(box, e.Location);
            if (url == null)
            {
                return;
            }

            // Con netman corriendo, el enlace se abre en SU navegador (el de
            // Playwright, que ya tiene la sesión de SIPAF). Si no está
            // corriendo, cae al navegador predeterminado de Windows.
            if (OpenInNetmanBrowser(url))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog("GUI", "No pude abrir " + url + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// El rayo del ícono (netman.ico) dibujado con vectores: queda nítido a
    /// cualquier DPI y no depende de extraer un tamaño de ícono concreto.
    /// Cuadrado redondeado translúcido + rayo blanco, para el encabezado azul.
    /// </summary>
    internal sealed class BoltLogo : Control
    {
        // Rayo sobre una grilla de 256x256 (medido del ícono original).
        private static readonly PointF[] Bolt =
        {
            new PointF(150, 20), new PointF(70, 123), new PointF(124, 125),
            new PointF(103, 236), new PointF(190, 110), new PointF(138, 112)
        };

        public BoltLogo()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            float s = Math.Min(Width, Height);
            float r = s * 0.22f;
            float d = r * 2f;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(s - d - 1, 0, d, d, 270, 90);
                path.AddArc(s - d - 1, s - d - 1, d, d, 0, 90);
                path.AddArc(0, s - d - 1, d, d, 90, 90);
                path.CloseFigure();
                using (var br = new SolidBrush(Color.FromArgb(46, 255, 255, 255)))
                {
                    g.FillPath(br, path);
                }
            }

            var pts = new PointF[Bolt.Length];
            for (int i = 0; i < Bolt.Length; i++)
            {
                pts[i] = new PointF(Bolt[i].X * s / 256f, Bolt[i].Y * s / 256f);
            }
            g.FillPolygon(Brushes.White, pts);
        }
    }
}
