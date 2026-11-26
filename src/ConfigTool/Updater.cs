using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NetmanConfigTool
{
    /// <summary>Datos de un release de GitHub que se puede instalar.</summary>
    public sealed class UpdateInfo
    {
        public string Tag;          // p. ej. v0.5-27-gabc1234
        public string Notes;        // cuerpo del release (puede estar vacio)
        public string AssetName;    // netman-<version>-<fecha>.zip (el estandar, sin portable)
        public string AssetUrl;
        public string Sha256;       // null si GitHub no lo informa
        public long Size;
    }

    /// <summary>
    /// Busca y aplica actualizaciones de netman usando los releases de GitHub
    /// como "endpoint": no hay servidor propio.
    ///
    /// Version instalada: archivo version.txt en la raiz de netman (lo escribe
    /// Build-Package.ps1; en un checkout de desarrollo no existe y no se busca
    /// nada). Se compara con el tag del ultimo release: version X.Y.Z con una "v"
    /// delante (v0.6.0). Tambien se entiende el formato viejo de git describe
    /// (v0.5-27-gabc1234).
    ///
    /// Aplicar: se baja el ZIP estandar (no el portable: Node y node_modules
    /// no cambian), se verifica el SHA-256 si GitHub lo informa, se extrae a
    /// una carpeta temporal y un script .cmd externo espera a que este proceso
    /// cierre, copia los archivos encima (sin borrar nada: netman.config.json,
    /// tools\ y node_modules\ quedan como estaban) y reabre el ConfigTool.
    /// </summary>
    internal static class Updater
    {
        private const string DefaultApi = "https://api.github.com/repos/Johans1047/netman/releases/latest";
        private const string AllowedDownloadPrefix = "https://github.com/Johans1047/netman/releases/download/";

        // Para pruebas: apunta a otro "endpoint" (por ejemplo un servidor local).
        // Solo con esta variable se aceptan descargas fuera de github.com.
        private const string ApiOverrideVar = "NETMAN_UPDATE_API";
        public const string DisableVar = "NETMAN_NO_UPDATE_CHECK";

        private static readonly Regex VersionRegex =
            new Regex(@"^v?(?<base>\d+(?:\.\d+)*)(?:-(?<n>\d+)-g[0-9a-fA-F]+)?(?<dirty>-dirty)?$", RegexOptions.Compiled);

        public static string VersionFile(string netmanRoot)
        {
            return Path.Combine(netmanRoot, "version.txt");
        }

        /// <summary>Version instalada, o null si esto no es un paquete (checkout de desarrollo).</summary>
        public static string InstalledVersion(string netmanRoot)
        {
            try
            {
                string path = VersionFile(netmanRoot);
                if (!File.Exists(path)) return null;
                string v = File.ReadAllText(path).Trim();
                return v.Length == 0 ? null : v;
            }
            catch { return null; }
        }

        /// <summary>Version para mostrar: siempre con "v" delante (0.6.0 -> v0.6.0).</summary>
        public static string Display(string version)
        {
            if (string.IsNullOrEmpty(version)) return "dev";
            return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : "v" + version;
        }

        public static bool IsDisabled()
        {
            string v = Environment.GetEnvironmentVariable(DisableVar);
            return !string.IsNullOrEmpty(v) && v != "0";
        }

        /// <summary>true si "candidate" es mas nueva que "current". Las que no se pueden leer nunca ganan.</summary>
        public static bool IsNewer(string candidate, string current)
        {
            int[] cBase, kBase;
            int cN, kN;
            if (!Parse(candidate, out cBase, out cN) || !Parse(current, out kBase, out kN)) return false;

            int len = Math.Max(cBase.Length, kBase.Length);
            for (int i = 0; i < len; i++)
            {
                int a = i < cBase.Length ? cBase[i] : 0;
                int b = i < kBase.Length ? kBase[i] : 0;
                if (a != b) return a > b;
            }
            return cN > kN;
        }

        private static bool Parse(string v, out int[] baseParts, out int commits)
        {
            baseParts = null;
            commits = 0;
            if (string.IsNullOrEmpty(v)) return false;
            Match m = VersionRegex.Match(v.Trim());
            if (!m.Success) return false;

            string[] parts = m.Groups["base"].Value.Split('.');
            baseParts = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out baseParts[i])) return false;
            }
            if (m.Groups["n"].Success) int.TryParse(m.Groups["n"].Value, out commits);
            return true;
        }

        private static void EnableTls()
        {
            // .NET 4.5.1 usa por defecto SSL3/TLS 1.0; GitHub exige TLS 1.2.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        }

        private static WebClient NewClient()
        {
            EnableTls();
            var wc = new WebClient();
            wc.Headers.Add("User-Agent", "netman-config-tool");
            wc.Headers.Add("Accept", "application/vnd.github+json");
            return wc;
        }

        /// <summary>
        /// Consulta el ultimo release. Devuelve null si ya esta al dia.
        /// Lanza excepcion si no hay red, la API falla o el release no trae el ZIP esperado.
        /// </summary>
        public static UpdateInfo CheckForUpdate(string installedVersion)
        {
            string api = Environment.GetEnvironmentVariable(ApiOverrideVar);
            if (string.IsNullOrWhiteSpace(api)) api = DefaultApi;

            string json;
            using (WebClient wc = NewClient())
            {
                json = wc.DownloadString(api);
            }

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var rel = ser.Deserialize<Dictionary<string, object>>(json);

            string tag = Str(rel, "tag_name");
            if (string.IsNullOrEmpty(tag)) throw new InvalidDataException("La respuesta no trae tag_name.");
            if (!IsNewer(tag, installedVersion)) return null;

            var assets = rel.ContainsKey("assets") ? rel["assets"] as System.Collections.IEnumerable : null;
            if (assets != null)
            {
                foreach (object o in assets)
                {
                    var a = o as Dictionary<string, object>;
                    if (a == null) continue;
                    string name = Str(a, "name");
                    // El paquete estandar: .zip sin "portable" en el nombre.
                    if (name == null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        || name.IndexOf("portable", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    var info = new UpdateInfo
                    {
                        Tag = tag,
                        Notes = Str(rel, "body"),
                        AssetName = name,
                        AssetUrl = Str(a, "browser_download_url")
                    };
                    object size;
                    if (a.TryGetValue("size", out size) && size != null) info.Size = Convert.ToInt64(size);

                    string digest = Str(a, "digest");   // "sha256:<hex>" en releases recientes
                    if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        info.Sha256 = digest.Substring(7).Trim().ToLowerInvariant();

                    return info;
                }
            }
            throw new InvalidDataException("El release " + tag + " no trae el paquete estandar (.zip sin \"portable\").");
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? v.ToString() : null;
        }

        /// <summary>
        /// Baja y prepara la actualizacion. Devuelve la carpeta con los archivos nuevos ya extraidos.
        /// El progreso (0-100) se informa por el callback; puede venir de otro hilo.
        /// </summary>
        public static string Download(UpdateInfo info, Action<int> progress)
        {
            bool overridden = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiOverrideVar));
            if (string.IsNullOrEmpty(info.AssetUrl)
                || (!overridden && !info.AssetUrl.StartsWith(AllowedDownloadPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("La URL de descarga no es de github.com/Johans1047/netman; no se descarga.");
            }

            string work = Path.Combine(Path.GetTempPath(), "netman-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string zipPath = Path.Combine(work, "paquete.zip");
            string staging = Path.Combine(work, "nuevo");

            try
            {
                using (WebClient wc = NewClient())
                {
                    wc.DownloadProgressChanged += (s, e) => { if (progress != null) progress(e.ProgressPercentage); };
                    wc.DownloadFileTaskAsync(new Uri(info.AssetUrl), zipPath).Wait();
                }

                if (info.Sha256 != null)
                {
                    string actual = Sha256Of(zipPath);
                    if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("El SHA-256 del paquete no coincide con el que informa GitHub. Se descarta.");
                }

                ExtractSafely(zipPath, staging);

                if (!File.Exists(Path.Combine(staging, "src", "ConfigTool", "bin", "Release", "NetmanConfigTool.exe")))
                    throw new InvalidDataException("El paquete no tiene la estructura esperada (falta NetmanConfigTool.exe).");

                File.Delete(zipPath);
                return staging;
            }
            catch
            {
                try { Directory.Delete(work, true); } catch { }
                throw;
            }
        }

        private static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder();
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // Extrae el ZIP comprobando que ninguna entrada se salga de la carpeta destino (zip-slip).
        private static void ExtractSafely(string zipPath, string destDir)
        {
            string root = Path.GetFullPath(destDir).TrimEnd('\\') + "\\";
            Directory.CreateDirectory(destDir);
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(destDir, entry.FullName.Replace('/', '\\')));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Entrada sospechosa en el ZIP: " + entry.FullName);

                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\") || entry.Name.Length == 0)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }

        /// <summary>
        /// Lanza el script que espera a que este proceso termine, copia los archivos
        /// nuevos sobre netmanRoot y vuelve a abrir el ConfigTool. Despues de llamarlo,
        /// el llamador debe cerrar la aplicacion.
        /// </summary>
        public static void LaunchApply(string stagingDir, string netmanRoot, string exeToRestart, UpdateInfo info)
        {
            string work = Path.GetDirectoryName(stagingDir);
            string script = Path.Combine(work, "aplicar-actualizacion.cmd");
            int pid = Process.GetCurrentProcess().Id;

            var sb = new StringBuilder();
            string log = Path.Combine(Path.GetTempPath(), "netman-update.log");
            sb.AppendLine("@echo off");
            sb.AppendLine("title Actualizando netman a " + Display(info.Tag));
            sb.AppendLine("echo Actualizando netman a " + Display(info.Tag) + " ... no cierres esta ventana.");
            sb.AppendLine("echo [%date% %time%] actualizando a " + info.Tag + " > \"" + log + "\"");
            // Sin bloques entre parentesis: una ruta con "(" o ")" (p. ej. "Program Files (x86)")
            // dentro de un "if ( ... )" rompe el script. Se usan etiquetas y goto.
            sb.AppendLine(":esperar");
            sb.AppendLine("tasklist /FI \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul");
            sb.AppendLine("if errorlevel 1 goto copiar");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("goto esperar");
            sb.AppendLine(":copiar");
            // /E subcarpetas, /Y sin preguntar, /I destino es carpeta. No borra nada del destino.
            sb.AppendLine("xcopy \"" + stagingDir + "\\*\" \"" + netmanRoot.TrimEnd('\\') + "\\\" /E /Y /Q /I >> \"" + log + "\" 2>&1");
            sb.AppendLine("echo [%date% %time%] xcopy terminado, errorlevel=%errorlevel% >> \"" + log + "\"");
            sb.AppendLine("if errorlevel 1 goto fallo");
            sb.AppendLine("start \"\" \"" + exeToRestart + "\"");
            sb.AppendLine("echo [%date% %time%] reiniciado >> \"" + log + "\"");
            // (goto) 2>nul hace que cmd deje de leer este script antes de borrar la carpeta donde vive.
            sb.AppendLine("(goto) 2>nul & rd /s /q \"" + work + "\"");
            sb.AppendLine(":fallo");
            sb.AppendLine("echo.");
            sb.AppendLine("echo ERROR: no se pudieron copiar los archivos. Cierra todo lo que use netman y vuelve a intentar.");
            sb.AppendLine("echo Los archivos nuevos quedaron en la carpeta nuevo de: " + work);
            sb.AppendLine("pause");
            sb.AppendLine("exit /b 1");
            File.WriteAllText(script, sb.ToString(), Encoding.Default);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + script + "\"",
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false
            });
        }
    }
}
