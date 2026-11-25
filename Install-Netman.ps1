<#
.SYNOPSIS
    Prepara netman en esta maquina (se corre una vez despues de descomprimir).

.DESCRIPTION
    - Desbloquea los archivos descargados (quita la marca "bloqueado" del ZIP).
    - Verifica .NET Framework 4.5.1+ y Edge/Chrome.
    - Usa Node del sistema o el portable (tools\node-portable); si no hay
      ninguno, el ConfigTool lo descarga al iniciar por primera vez.
    - Instala Playwright (npm install) si no vino ya en el paquete.
    - Crea netman.config.json a partir del ejemplo si no existe.
    - Crea un acceso directo en el Escritorio al ConfigTool.

    No pide permisos de administrador ni toca nada fuera de esta carpeta
    (salvo el acceso directo del Escritorio).
#>
[CmdletBinding()]
param([switch]$NoShortcut)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Ok([string]$m)   { Write-Host "  [OK] $m" -ForegroundColor Green }
function Warn([string]$m) { Write-Host "  [!!] $m" -ForegroundColor Yellow }
function Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

Step 'Desbloqueando archivos...'
Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
Ok 'Listo'

Step 'Verificando requisitos...'
$netRelease = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
if ($netRelease -ge 378675) { Ok ".NET Framework 4.5.1 o superior ($netRelease)" }
else { Warn '.NET Framework 4.5.1+ no detectado: Windows 10/11 lo trae; en equipos viejos instalalo.' }

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
$chrome = @("${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe", "$env:ProgramFiles\Google\Chrome\Application\chrome.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($edge) { Ok 'Microsoft Edge encontrado (es el navegador que usa netman)' }
elseif ($chrome) { Warn 'No hay Edge pero si Chrome: defini la variable NETMAN_BROWSER_CHANNEL=chrome.' }
else { Warn 'No encontre Edge ni Chrome: netman necesita uno de los dos.' }

$portableNode = Join-Path $root 'tools\node-portable'
$hasPortable = Test-Path (Join-Path $portableNode 'node.exe')
$sysNode = Get-Command node -ErrorAction SilentlyContinue
if ($hasPortable) { Ok 'Node portable incluido en el paquete' }
elseif ($sysNode) { Ok "Node del sistema: $($sysNode.Source)" }
else { Warn 'No hay Node: el ConfigTool lo descargara (portable) la primera vez que inicies netman.' }

Step 'Playwright...'
if (Test-Path (Join-Path $root 'node_modules\playwright')) {
    Ok 'Ya viene instalado en el paquete'
} elseif ($hasPortable -or $sysNode) {
    $npm = if ($hasPortable) { Join-Path $portableNode 'npm.cmd' } else { 'npm' }
    if ($hasPortable) { $env:PATH = "$portableNode;$env:PATH" }
    $env:PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD = '1'
    Push-Location $root
    try {
        & $npm install
        if ($LASTEXITCODE -eq 0) { Ok 'Playwright instalado' } else { Warn 'npm install fallo; se reintenta al iniciar desde el ConfigTool.' }
    } finally { Pop-Location }
} else {
    Warn 'Se instalara al iniciar netman por primera vez.'
}

Step 'Configuracion...'
$cfg = Join-Path $root 'netman.config.json'
if (-not (Test-Path $cfg)) {
    Copy-Item (Join-Path $root 'netman.config.example.json') $cfg
    Ok 'Cree netman.config.json (completalo desde el ConfigTool)'
} else { Ok 'netman.config.json ya existe, no lo toco' }

$exe = Join-Path $root 'src\ConfigTool\bin\Release\NetmanConfigTool.exe'
if (-not (Test-Path $exe)) { Warn "No encuentro $exe" }
elseif (-not $NoShortcut) {
    try {
        $desktop = [Environment]::GetFolderPath('Desktop')
        $lnk = Join-Path $desktop 'netman.lnk'
        $ws = New-Object -ComObject WScript.Shell
        $sc = $ws.CreateShortcut($lnk)
        $sc.TargetPath = $exe
        $sc.WorkingDirectory = Split-Path $exe
        $sc.Description = 'netman - hot reload para SIPAF'
        $sc.Save()
        Ok "Acceso directo en el Escritorio: $lnk"
    } catch { Warn "No pude crear el acceso directo: $($_.Exception.Message)" }
}

Write-Host ''
Write-Host 'Listo. Abri "netman" desde el Escritorio (o Abrir-NetmanConfig.cmd), completa usuario, clave y rutas, y dale a Iniciar.' -ForegroundColor Green
