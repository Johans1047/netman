using System;
using System.IO;
using System.Reflection;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Deja <c>_netman-autologin.html</c> en la raiz del sitio web que netman
    /// vigila. La pagina tiene que vivir DENTRO del sitio (mismo origin que
    /// SIPAF, para compartir cookies de Forms Auth), asi que la instalacion
    /// plug-and-play no puede depender de que alguien la copie a mano: el
    /// contenido va embebido en el .exe (fuente: site-files\_netman-autologin.html)
    /// y se escribe si falta o si cambio.
    /// </summary>
    internal static class AutologinPageInstaller
    {
        public const string FileName = "_netman-autologin.html";

        /// <returns>true si escribio/actualizo el archivo, false si ya estaba al dia.</returns>
        public static bool EnsureInstalled(string siteDirectory)
        {
            if (string.IsNullOrWhiteSpace(siteDirectory))
                throw new ArgumentException("siteDirectory is required.", "siteDirectory");
            if (!Directory.Exists(siteDirectory))
                throw new DirectoryNotFoundException("Site directory not found: " + siteDirectory);

            string embedded = ReadEmbedded();
            string target = Path.Combine(siteDirectory, FileName);

            if (File.Exists(target) && File.ReadAllText(target) == embedded)
                return false;

            File.WriteAllText(target, embedded, new System.Text.UTF8Encoding(false));
            return true;
        }

        private static string ReadEmbedded()
        {
            Assembly asm = typeof(AutologinPageInstaller).Assembly;
            using (Stream s = asm.GetManifestResourceStream(FileName))
            {
                if (s == null)
                    throw new InvalidOperationException("Embedded resource '" + FileName + "' not found in " + asm.GetName().Name + ".");
                using (var r = new StreamReader(s, System.Text.Encoding.UTF8))
                    return r.ReadToEnd();
            }
        }
    }
}
