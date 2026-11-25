<#
.SYNOPSIS
    Arma el paquete de netman listo para entregar (carpeta + ZIP) en dist\.

.DESCRIPTION
    Se ejecuta UNA vez en la maquina del desarrollador (necesita Visual Studio /
    MSBuild). Compila Host y ConfigTool en Release y copia todo a
    dist\netman-<version>\ con el MISMO layout que usan los scripts:

        Start-Netman.ps1, Install-Netman.ps1, Abrir-NetmanConfig.cmd
        netman-*.js, package.json, netman.config.example.json
        docs\, site-files\, bin\netman-sipaf.cmd
        src\Host\bin\Debug\          <- el Host compilado (Release). Se llama
                                        "Debug" a proposito: es la ruta que
                                        esperan los scripts, el ConfigTool y el
                                        JS (carpeta de los stamps).
        src\ConfigTool\bin\Release\  <- NetmanConfigTool.exe

    Despues quien lo recibe solo descomprime y ejecuta Install-Netman.ps1.

.PARAMETER Portable
    Version "sin internet": incluye Node portable (tools\node-portable) y
    node_modules con Playwright ya instalado. Pesa mas, pero no necesita
    instalar nada (solo Edge, que Windows trae).

.PARAMETER NoZip
    Deja solo la carpeta, sin crear el ZIP.

.EXAMPLE
    .\Build-Package.ps1
    .\Build-Package.ps1 -Portable
#>
[CmdletBinding()]
param(
    [switch]$Portable,
    [switch]$NoZip,
    [string]$NodeVersion = ''   # vacio = ultima LTS
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

# --- MSBuild -----------------------------------------------------------------
function Find-MSBuild {
    $candidates = @()
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null
        if ($found) { $candidates += $found }
    }
    $candidates += (Join-Path ${env:ProgramFiles(x86)} 'MSBuild\12.0\Bin\MSBuild.exe')
    $candidates += (Join-Path ${env:ProgramFiles(x86)} 'MSBuild\14.0\Bin\MSBuild.exe')
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    throw 'No encuentro MSBuild. Instala Visual Studio (o Build Tools) o abri esto desde "Developer Command Prompt".'
}
$msbuild = Find-MSBuild
Write-Step "MSBuild: $msbuild"

# --- Version -----------------------------------------------------------------
$version = 'dev'
try {
    $v = (& git -C $root describe --tags --always --dirty 2>$null)
    if ($LASTEXITCODE -eq 0 -and $v) { $version = $v.Trim() }
} catch { }
$stamp = Get-Date -Format 'yyyyMMdd'
$name = if ($Portable) { "netman-$version-$stamp-portable" } else { "netman-$version-$stamp" }
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist $name
Write-Step "Paquete: $name"

# --- Restaurar paquetes NuGet --------------------------------------------------
$nuget = Join-Path $root '.nuget\nuget.exe'
if (-not (Test-Path $nuget)) {
    $nuget = Join-Path $env:TEMP 'nuget.exe'
    if (-not (Test-Path $nuget)) {
        Write-Step 'Descargando nuget.exe...'
        Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nuget -UseBasicParsing
    }
}
Write-Step 'Restaurando paquetes NuGet...'
& $nuget restore (Join-Path $root 'HotReloadTool.sln') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'nuget restore fallo.' }

# --- Compilar ------------------------------------------------------------------
foreach ($proj in @('src\Host\HotReloadTool.Host.csproj', 'src\ConfigTool\NetmanConfigTool.csproj')) {
    Write-Step "Compilando $proj (Release)..."
    & $msbuild (Join-Path $root $proj) /p:Configuration=Release /v:minimal /nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion de $proj." }
}

$hostOut = Join-Path $root 'src\Host\bin\Release'
$cfgOut = Join-Path $root 'src\ConfigTool\bin\Release'
if (-not (Test-Path (Join-Path $hostOut 'HotReloadTool.Host.exe'))) { throw "No esta HotReloadTool.Host.exe en $hostOut" }
if (-not (Test-Path (Join-Path $cfgOut 'NetmanConfigTool.exe'))) { throw "No esta NetmanConfigTool.exe en $cfgOut" }

# --- Armar la carpeta ----------------------------------------------------------
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

$rootFiles = @('Start-Netman.ps1', 'Install-Netman.ps1', 'Abrir-NetmanConfig.cmd', 'netman-hotreload-browser.js',
               'netman-auto-login.js', 'netman-config.js', 'package.json', 'package-lock.json',
               'netman.config.example.json', 'LEEME.txt')
foreach ($f in $rootFiles) {
    $src = Join-Path $root $f
    if (Test-Path $src) { Copy-Item $src $stage } else { Write-Warning "Falta $f (se omite)." }
}
foreach ($d in @('docs', 'site-files')) { Copy-Item (Join-Path $root $d) (Join-Path $stage $d) -Recurse }
New-Item -ItemType Directory -Path (Join-Path $stage 'bin') | Out-Null
Copy-Item (Join-Path $root 'bin\netman-sipaf.cmd') (Join-Path $stage 'bin')

$hostDest = Join-Path $stage 'src\Host\bin\Debug'
$cfgDest = Join-Path $stage 'src\ConfigTool\bin\Release'
New-Item -ItemType Directory -Path $hostDest -Force | Out-Null
New-Item -ItemType Directory -Path $cfgDest -Force | Out-Null
Get-ChildItem $hostOut -File | Where-Object { $_.Extension -notin '.pdb', '.xml' } | Copy-Item -Destination $hostDest
Get-ChildItem $cfgOut -File | Where-Object { $_.Extension -notin '.pdb' -and $_.Name -notlike '*.vshost.*' -and $_.Name -ne 'netman.config.json' } | Copy-Item -Destination $cfgDest

# --- Portable: Node + Playwright -------------------------------------------------
if ($Portable) {
    Write-Step 'Version portable: descargando Node...'
    if (-not $NodeVersion) {
        $index = Invoke-RestMethod -Uri 'https://nodejs.org/dist/index.json' -UseBasicParsing
        $lts = $index | Where-Object { $_.lts } | Select-Object -First 1
        $NodeVersion = $lts.version
    }
    if ($NodeVersion -notmatch '^v') { $NodeVersion = "v$NodeVersion" }
    $zipName = "node-$NodeVersion-win-x64.zip"
    $zipPath = Join-Path $env:TEMP $zipName
    if (-not (Test-Path $zipPath)) {
        Invoke-WebRequest -Uri "https://nodejs.org/dist/$NodeVersion/$zipName" -OutFile $zipPath -UseBasicParsing
    }
    $tools = Join-Path $stage 'tools'
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    Expand-Archive -Path $zipPath -DestinationPath $tools -Force
    $extracted = Join-Path $tools "node-$NodeVersion-win-x64"
    Rename-Item $extracted 'node-portable'
    $nodeHome = Join-Path $tools 'node-portable'

    Write-Step 'Instalando Playwright dentro del paquete (npm install)...'
    Push-Location $stage
    try {
        $env:PATH = "$nodeHome;$env:PATH"
        $env:PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD = '1'   # se usa el Edge instalado
        & (Join-Path $nodeHome 'npm.cmd') install | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'npm install fallo.' }
    } finally { Pop-Location }
}

# --- ZIP ------------------------------------------------------------------------
if (-not $NoZip) {
    $zipOut = Join-Path $dist "$name.zip"
    if (Test-Path $zipOut) { Remove-Item $zipOut -Force }
    Write-Step "Comprimiendo -> $zipOut"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipOut
    Write-Host ""
    Write-Host "Listo: $zipOut" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Listo: $stage" -ForegroundColor Green
}
Write-Host "Quien lo reciba: descomprime y ejecuta Install-Netman.ps1 (ver LEEME.txt)."
