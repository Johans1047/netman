using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NetmanConfigTool
{
    /// <summary>
    /// Wrapper minimo sobre el Windows Credential Manager (CredWrite/CredRead/
    /// CredDelete de advapi32.dll) para guardar la clave de SIPAF sin tocar
    /// disco en texto plano y sin pasarla por linea de comandos.
    ///
    /// Se guarda como credencial GENERIC bajo el target fijo "netman:sipaf".
    /// Start-Netman.ps1 no la lee todavia (sigue pidiendola o tomandola de
    /// NETMAN_SIPAF_PASS) -- este GUI v0.5 la deja lista para cuando el
    /// script (o una fase 2 de este mismo tool) la busque aca en vez de
    /// preguntar cada vez.
    /// </summary>
    internal static class CredentialManager
    {
        private const string TargetName = "netman:sipaf";
        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWrite([In] ref CREDENTIAL credential, [In] uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CredFree([In] IntPtr cred);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredDelete(string target, int type, int flags);

        /// <summary>Guarda (o reemplaza) la credencial. La clave viaja como UTF-16 en el blob.</summary>
        public static void Save(string userName, string password)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(password ?? string.Empty);
            IntPtr blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
            try
            {
                if (bytes.Length > 0)
                {
                    Marshal.Copy(bytes, 0, blob, bytes.Length);
                }

                var cred = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = TargetName,
                    CredentialBlobSize = bytes.Length,
                    CredentialBlob = blob,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = userName
                };

                if (!CredWrite(ref cred, 0))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException("CredWrite fallo (codigo de error Win32: " + err + ").");
                }
            }
            finally
            {
                // Pisamos el buffer plano antes de liberarlo.
                Array.Clear(bytes, 0, bytes.Length);
                Marshal.FreeHGlobal(blob);
            }
        }

        /// <summary>Devuelve la clave guardada, o null si todavia no hay ninguna.</summary>
        public static string ReadPassword(out string userName)
        {
            userName = null;
            IntPtr credPtr;
            if (!CredRead(TargetName, CRED_TYPE_GENERIC, 0, out credPtr))
            {
                return null;
            }

            try
            {
                var cred = (CREDENTIAL)Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL));
                userName = cred.UserName;
                if (cred.CredentialBlobSize <= 0 || cred.CredentialBlob == IntPtr.Zero)
                {
                    return string.Empty;
                }
                byte[] bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                CredFree(credPtr);
            }
        }

        public static void Delete()
        {
            CredDelete(TargetName, CRED_TYPE_GENERIC, 0);
        }
    }
}
