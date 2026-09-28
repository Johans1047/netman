using System;
using System.IO;
using System.Web.Script.Serialization;

namespace NetmanConfigTool
{
    /// <summary>
    /// Config NO secreta de netman (todo lo que hoy son parametros
    /// hardcodeados/defaults en Start-Netman.ps1). La clave de SIPAF nunca
    /// pasa por aca -- vive solo en Windows Credential Manager via
    /// CredentialManager.
    ///
    /// v0.5: este .json todavia no lo lee Start-Netman.ps1 (eso es fase 1
    /// del plan de rollout). Por ahora el GUI es la fuente de verdad para
    /// quien lo use, y el "Iniciar netman" del formulario pasa estos valores
    /// como parametros al script directamente.
    /// </summary>
    [Serializable]
    public class NetmanConfig
    {
        public string SipafUser { get; set; }
        public string LibreriaSipafProject { get; set; }
        public string SipafSitePath { get; set; }
        public string SiteBaseUrl { get; set; }

        public static string DefaultPath(string netmanRoot)
        {
            return Path.Combine(netmanRoot, "netman.config.json");
        }

        public static NetmanConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                return new NetmanConfig();
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new NetmanConfig();
            }

            var serializer = new JavaScriptSerializer();
            return serializer.Deserialize<NetmanConfig>(json) ?? new NetmanConfig();
        }

        public void Save(string path)
        {
            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(this);
            File.WriteAllText(path, json);
        }
    }
}
