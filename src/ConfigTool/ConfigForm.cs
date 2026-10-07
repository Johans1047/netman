using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
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

        /// <summary>
        /// null => hay "node" en el PATH del sistema, se usa tal cual.
        /// Si no, esta es la carpeta de una copia portable de Node (sin
        /// instalador) que el propio GUI descargó -- ahí también viven
        /// npm.cmd y npx.cmd, así que no hace falta nada más instalado.
        /// </summary>
        private string _nodeHome;

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
                string npxInvoke = _nodeHome == null ? "npx playwright install chromium" : Quote(NpxCmdPath()) + " playwright install chromium";
                bool installedOk = await Task.Run(() =>
                    RunBlocking("cmd.exe", "/c " + npmInvoke, _netmanRoot, "npm", true) == 0
                    && RunBlocking("cmd.exe", "/c " + npxInvoke, _netmanRoot, "npx", true) == 0);
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
