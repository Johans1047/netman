namespace NetmanConfigTool
{
    /// <summary>
    /// Clave PUBLICA con la que el actualizador verifica los paquetes (RSA, formato XML de .NET).
    /// La privada nunca esta en el repositorio: ver Sign-Package.ps1.
    /// Generado por: Sign-Package.ps1 -NewKey
    /// </summary>
    internal static class UpdateKey
    {
        public const string PublicXml = "<RSAKeyValue><Modulus>s38Eh35P4Mz0lHki4gsyvnH2uIfhLzff27uQEdnMcG0wylhcxe+ds/QQ5w1QJDiyXAo1Yk3ykH/CQ8O0PXSYfTO607Abck8iUyAjA2P3bSqVKBSTexvKExCRfxBgGe9DWVVNChGKrZaTDoE2EKbcm3ggFKFLVkHVX2IQzA9X76bSQPbhGHrcZuxSxt05nheY6LwgSXxryxH1mubDC27tvdIYJilatB1tp32N02Ru39DUX+o2os7Pgq7rkzCaLO904mhAksy7XztUljvpsPGtr+JaWdDAEHkZVz3qdHFa2d2J6MKOq179CUU7OSN6w5VvNK53GOBPCZatKnAYOLEGrUdXeRCwcX9AY42jhiJ9dPoSjB766ymhm+0OizRx361TnX/c8npi0uiDDiK5ZtLx/vfd4l9mqdp0Nve0BkLr9aD0MboY9Gv+lMXY0u4Y+aq/3zPogDDiIYtzgNQnZAk6PydcB72bhpPdJ3uXPaZHba+iULIAXxKhEf+olDvBXy0B</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
    }
}