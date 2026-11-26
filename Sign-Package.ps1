<#
.SYNOPSIS
    Firma los paquetes de netman (firma separada RSA-SHA256, archivo .sig).

.DESCRIPTION
    El actualizador del ConfigTool solo instala un paquete si trae un .sig valido
    para la clave publica embebida en src\ConfigTool\UpdateKey.cs. Asi, aunque
    alguien lograra cambiar los archivos de un release en GitHub, las instalaciones
    existentes lo rechazan: sin la clave privada no se puede producir una firma valida.

    La clave PRIVADA vive solo en tu equipo, fuera del repositorio:
        %USERPROFILE%\.netman\signing-key.xml
    Haz una copia de seguridad (gestor de contrasenas). Si se pierde, las
    instalaciones ya hechas no podran verificar releases nuevos y habria que
    reinstalar a mano con una clave nueva.

.PARAMETER NewKey
    Genera el par de claves (RSA 3072): guarda la privada en %USERPROFILE%\.netman y escribe
    la publica en src\ConfigTool\UpdateKey.cs (se commitea). Se niega a pisar una clave existente.

.PARAMETER Zip
    Paquete .zip a firmar. Crea <paquete>.zip.sig al lado y lo verifica.

.EXAMPLE
    .\Sign-Package.ps1 -NewKey
    .\Sign-Package.ps1 -Zip dist\netman-0.6.2.zip
#>
[CmdletBinding()]
param(
    [string]$Zip,
    [switch]$NewKey,
    [string]$KeyPath = (Join-Path $env:USERPROFILE '.netman\signing-key.xml')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$keySource = Join-Path $root 'src\ConfigTool\UpdateKey.cs'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if ($NewKey) {
    if (Test-Path $KeyPath) { throw "Ya existe una clave en $KeyPath. No se pisa (perderias la capacidad de firmar para las instalaciones actuales). Borrala a mano si de verdad quieres rotarla." }
    New-Item -ItemType Directory -Path (Split-Path $KeyPath) -Force | Out-Null

    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider 3072
    $rsa.PersistKeyInCsp = $false
    try {
        [System.IO.File]::WriteAllText($KeyPath, $rsa.ToXmlString($true), $utf8)
        $public = $rsa.ToXmlString($false)
    } finally { $rsa.Dispose() }

    $cs = @"
namespace NetmanConfigTool
{
    /// <summary>
    /// Clave PUBLICA con la que el actualizador verifica los paquetes (RSA, formato XML de .NET).
    /// La privada nunca esta en el repositorio: ver Sign-Package.ps1.
    /// Generado por: Sign-Package.ps1 -NewKey
    /// </summary>
    internal static class UpdateKey
    {
        public const string PublicXml = "$public";
    }
}
"@
    [System.IO.File]::WriteAllText($keySource, $cs, $utf8)
    Write-Host "Clave privada: $KeyPath  (haz una copia de seguridad; NO la subas a git)" -ForegroundColor Yellow
    Write-Host "Clave publica: $keySource  (commitea este archivo)" -ForegroundColor Green
    return
}

if (-not $Zip) { throw 'Indica -Zip <paquete.zip> o usa -NewKey.' }
if (-not (Test-Path $Zip)) { throw "No existe $Zip" }
if (-not (Test-Path $KeyPath)) { throw "No hay clave privada en $KeyPath. Crea una con: .\Sign-Package.ps1 -NewKey" }

$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
try {
    $rsa.FromXmlString([System.IO.File]::ReadAllText($KeyPath))
    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $Zip))
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $sig = $rsa.SignData($bytes, $sha)

    # Comprobacion inmediata con la clave publica DEL REPOSITORIO (la que lleva el ConfigTool):
    # si no coincide con la privada, se avisa ahora y no cuando alguien intente actualizar.
    $m = [regex]::Match([System.IO.File]::ReadAllText($keySource), 'PublicXml = "([^"]+)"')
    if (-not $m.Success) { throw "No encuentro la clave publica en $keySource" }
    $pub = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    $pub.PersistKeyInCsp = $false
    $pub.FromXmlString($m.Groups[1].Value)
    if (-not $pub.VerifyData($bytes, $sha, $sig)) { throw 'La firma no verifica con la clave publica de UpdateKey.cs: la clave privada no corresponde a la publica del repositorio.' }
} finally { $rsa.Dispose() }

$sigPath = "$Zip.sig"
[System.IO.File]::WriteAllText($sigPath, [Convert]::ToBase64String($sig), (New-Object System.Text.ASCIIEncoding))
Write-Host "Firmado: $sigPath" -ForegroundColor Green
