using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
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

        public ConfigForm()
        {
            InitializeComponent();

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

            Load += ConfigForm_Load;
            FormClosing += ConfigForm_FormClosing;
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
        }

        private void ConfigForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopProcess(ref _browserProcess, "Browser");
            StopProcess(ref _hostProcess, "Host");
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

            string deployTo = Path.Combine(cfg.SipafSitePath, "Bin");
            string recycleTarget = Path.Combine(cfg.SipafSitePath, "web.config");

            string hostArgs = "start --mode build-recycle -p " + Quote(cfg.LibreriaSipafProject)
                + " --deploy-to " + Quote(deployTo)
                + " --recycle-target " + Quote(recycleTarget)
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

            bool playwrightOk = await Task.Run(() => RunBlocking("node", "-e " + Quote("require.resolve('playwright')"), _netmanRoot, "node", false) == 0);
            if (!playwrightOk)
            {
                AppendLog("GUI", "Playwright no está instalado todavía -- instalando (una sola vez, puede tardar)...");
                bool installedOk = await Task.Run(() =>
                    RunBlocking("cmd.exe", "/c npm install playwright", _netmanRoot, "npm", true) == 0
                    && RunBlocking("cmd.exe", "/c npx playwright install chromium", _netmanRoot, "npx", true) == 0);
                if (!installedOk)
                {
                    AppendLog("GUI", "ERROR: no se pudo instalar Playwright. Revisá el log de arriba.");
                    SetRunningState(false);
                    StopProcess(ref _hostProcess, "Host");
                    return;
                }
            }

            _browserProcess = StartTrackedProcess("node", Quote(_browserJsPath), _netmanRoot, "Browser");
            if (_browserProcess == null)
            {
                SetRunningState(false);
                StopProcess(ref _hostProcess, "Host");
                return;
            }

            AppendLog("GUI", "Navegador de hot-reload lanzado (PID " + _browserProcess.Id + "). Editá y mirá los logs de abajo.");
            SetStatus("Netman corriendo", false);
        }

        private void btnDetener_Click(object sender, EventArgs e)
        {
            AppendLog("GUI", "Deteniendo netman...");
            StopProcess(ref _browserProcess, "Browser");
            StopProcess(ref _hostProcess, "Host");
            SetRunningState(false);
            SetStatus("netman detenido.", false);
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

        private void StopProcess(ref Process process, string tag)
        {
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
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
            lblStatus.ForeColor = isError ? System.Drawing.Color.DarkRed : System.Drawing.Color.DarkGreen;
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
        }

        private void AppendLog(string tag, string line)
        {
            var box = BoxForTag(tag);
            if (box.InvokeRequired)
            {
                try { box.Invoke(new Action<string, string>(AppendLog), tag, line); } catch { }
                return;
            }
            box.AppendText("[" + tag + "] " + line + Environment.NewLine);
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        }
    }
}
