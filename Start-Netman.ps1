<#
.SYNOPSIS
    Prende netman: credenciales + Host (build-recycle) + navegador de hot-reload.
    Cubre los pasos 2 a 5 del arranque en frio. El paso 1 (levantar el sitio
    SIPAF en IIS Express) queda afuera a proposito -- corre eso primero.

.DESCRIPTION
    Por defecto:
      2) Pide (o toma de env var / parametro) usuario y clave de SIPAF.
      3) Levanta HotReloadTool.Host en modo build-recycle, en su propia ventana.
      4) Levanta netman-hotreload-browser.js (Playwright) en esta misma ventana
         -- instala el paquete si hace falta -- y se queda corriendo hasta
         que cierres esta consola (Ctrl+C).

    Con -Manual, en vez del paso 4 corre el paso 5: abre el navegador
    default en la pagina de autologin, para loguearte a mano.

    Las rutas de LibreriaSipaf, del sitio Sipaf, el puerto y demas son
    parametros con el valor de hoy como default -- es el parche manual hasta
    que exista netman.config.json (fase 1 del plan de rollout).

.PARAMETER SipafUser
    Usuario de SIPAF. Si no se pasa, usa $env:NETMAN_SIPAF_USER o pregunta.

.PARAMETER SipafPass
    Clave de SIPAF como SecureString. Si no se pasa, usa $env:NETMAN_SIPAF_PASS
    o la pide de forma oculta (Read-Host -AsSecureString).

.PARAMETER Manual
    En vez de levantar el navegador automatizado (paso 4), abre el navegador
    default en la pagina de autologin (paso 5) para loguearte vos mismo.

.PARAMETER Url
    Solo con -Manual: pagina destino despues del login. Default: wPerfiles.aspx.

.PARAMETER SkipSiteCheck
    No valida que el sitio SIPAF ya este respondiendo en IIS Express antes
    de arrancar.

.EXAMPLE
    .\Start-Netman.ps1
    Arranque normal: pide credenciales si hace falta, levanta Host + browser.

.EXAMPLE
    .\Start-Netman.ps1 -Manual -Url "http://localhost:12345/SIPAF/AdministracionSistema/Seguridad/Usuarios/wUsuarios.aspx"
    Levanta el Host y te deja loguearte a mano, directo en esa pagina.
#>
[CmdletBinding()]
param(
    [string]$SipafUser = $env:NETMAN_SIPAF_USER,
    [System.Security.SecureString]$SipafPass,

    [switch]$Manual,
    [string]$Url,
    [switch]$SkipSiteCheck,

    # Rutas de hoy -- ajustalas si tu entorno es distinto a este.
    [string]$LibreriaSipafProject = 'C:\Proyecto_VS2013\SIPAF\LibreriaSIPAF\LibreriaSIPAF.vbproj',
    [string]$SipafSitePath        = 'C:\Proyecto_VS2013\SIPAF\SIPAF',
    [string]$SiteBaseUrl          = 'http://localhost:12345/SIPAF/'
)

$ErrorActionPreference = 'Stop'
$netmanRoot = $PSScriptRoot

# netman.config.json (el mismo que usa NetmanConfigTool) manda sobre los
# defaults de arriba, salvo que pases el parametro a mano.
$configPath = Join-Path $netmanRoot 'netman.config.json'
if (Test-Path $configPath) {
    try {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        if (-not $PSBoundParameters.ContainsKey('LibreriaSipafProject') -and $cfg.LibreriaSipafProject) { $LibreriaSipafProject = $cfg.LibreriaSipafProject }
        if (-not $PSBoundParameters.ContainsKey('SipafSitePath')        -and $cfg.SipafSitePath)        { $SipafSitePath        = $cfg.SipafSitePath }
        if (-not $PSBoundParameters.ContainsKey('SiteBaseUrl')          -and $cfg.SiteBaseUrl)          { $SiteBaseUrl          = $cfg.SiteBaseUrl }
        if ([string]::IsNullOrWhiteSpace($SipafUser) -and $cfg.SipafUser)                               { $SipafUser            = $cfg.SipafUser }
    } catch {
        Write-Warning "No pude leer $configPath ($($_.Exception.Message)). Uso los defaults del script."
    }
}
$hostExe    = Join-Path $netmanRoot 'src\Host\bin\Debug\HotReloadTool.Host.exe'
$browserJs  = Join-Path $netmanRoot 'netman-hotreload-browser.js'
$deployTo   = Join-Path $SipafSitePath 'Bin'
$recycleTgt = Join-Path $SipafSitePath 'web.config'
$defaultTarget = $SiteBaseUrl.TrimEnd('/') + '/wPerfiles.aspx'

function Write-Step {
    param([string]$Text)
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function ConvertTo-PlainText {
    param([System.Security.SecureString]$Secure)
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try {
        return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

# ---------------------------------------------------------------------------
# Paso 0 (chequeo, no bloqueante): el sitio ya tiene que estar arriba.
# ---------------------------------------------------------------------------
if (-not $SkipSiteCheck) {
    Write-Step "Verificando que el sitio SIPAF responda en $SiteBaseUrl..."
    try {
        $probe = Invoke-WebRequest -Uri ($SiteBaseUrl.TrimEnd('/') + '/wInicio.aspx') `
            -UseBasicParsing -TimeoutSec 4 -ErrorAction Stop
        Write-Host "    OK ($($probe.StatusCode))." -ForegroundColor Green
    } catch {
        Write-Warning "No pude alcanzar $SiteBaseUrl. Este script asume que el sitio ya esta corriendo en IIS Express (paso 1: abri SIPAF.sln y Ctrl+F5, o lanza iisexpress.exe directo). Sigo igual, pero el Host va a fallar si el sitio no esta arriba."
    }
}

# ---------------------------------------------------------------------------
# Paso 2 -- credenciales
# ---------------------------------------------------------------------------
Write-Step "Paso 2: credenciales de SIPAF"

if ([string]::IsNullOrWhiteSpace($SipafUser)) {
    $SipafUser = Read-Host "Usuario SIPAF"
}

if (-not $SipafPass) {
    if ($env:NETMAN_SIPAF_PASS) {
        $SipafPassPlain = $env:NETMAN_SIPAF_PASS
    } else {
        $SipafPass = Read-Host "Clave SIPAF" -AsSecureString
        $SipafPassPlain = ConvertTo-PlainText $SipafPass
    }
} else {
    $SipafPassPlain = ConvertTo-PlainText $SipafPass
}

if ([string]::IsNullOrWhiteSpace($SipafPassPlain)) {
    throw "Falta la clave de SIPAF -- no puedo seguir sin ella."
}

# Las dejamos en el entorno de ESTE proceso; Start-Process hereda este
# entorno para los procesos hijos (Host y node), así que no hace falta
# pasarlas por linea de comandos ni escribirlas a disco.
$env:NETMAN_SIPAF_USER = $SipafUser
$env:NETMAN_SIPAF_PASS = $SipafPassPlain
Write-Host "    Credenciales cargadas para esta sesion (usuario: $SipafUser)." -ForegroundColor Green

# ---------------------------------------------------------------------------
# Paso 3 -- Host de netman (build-recycle), en su propia ventana
# ---------------------------------------------------------------------------
Write-Step "Paso 3: iniciando netman (Host, build-recycle)"

if (-not (Test-Path $hostExe)) {
    throw "No encuentro $hostExe. Compila HotReloadTool.sln primero (Visual Studio o msbuild)."
}
if (-not (Test-Path $LibreriaSipafProject)) {
    throw "No encuentro $LibreriaSipafProject. Pasa la ruta correcta con -LibreriaSipafProject."
}
if (-not (Test-Path $SipafSitePath)) {
    throw "No encuentro $SipafSitePath. Pasa la ruta correcta con -SipafSitePath."
}

$hostArgs = @(
    'start'
    '--mode', 'build-recycle'
    '-p', $LibreriaSipafProject
    '--deploy-to', $deployTo
    '--recycle-target', $recycleTgt
    '--web-watch', $SipafSitePath
    '--site-url', $SiteBaseUrl
)

Start-Process -FilePath $hostExe -ArgumentList $hostArgs -WorkingDirectory (Split-Path $hostExe) `
    -WindowStyle Normal
Write-Host "    Host lanzado en su propia ventana. Dejalo abierto." -ForegroundColor Green

# Darle un respiro al Host antes de arrancar el browser (arranque en frio).
Start-Sleep -Seconds 2

# ---------------------------------------------------------------------------
# Paso 4 o Paso 5 -- navegador automatizado, o login manual
# ---------------------------------------------------------------------------
if ($Manual) {
    Write-Step "Paso 5: login manual"
    $target = if ($Url) { $Url } else { $defaultTarget }
    $loginUrl = $SiteBaseUrl.TrimEnd('/') + '/_netman-autologin.html?to=' + [uri]::EscapeDataString($target)
    Start-Process $loginUrl
    Write-Host "    Se abrio el navegador default en la pagina de autologin. Tildá 'Recordar' la primera vez." -ForegroundColor Green
    Write-Host "    (El Host sigue corriendo en su ventana aparte.)" -ForegroundColor DarkGray
    return
}

Write-Step "Paso 4: navegador de hot-reload"

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw "No encuentro 'node' en el PATH. Instala Node.js antes de continuar (o usa -Manual)."
}
if (-not (Test-Path $browserJs)) {
    throw "No encuentro $browserJs."
}

# El chequeo/instalacion de playwright si hace falta lo hacemos aca, en esta
# consola (para ver el progreso de npm), pero el proceso node en si arranca
# en su PROPIA ventana (igual que el Host en el paso 3).
#
# Por que: si "node $browserJs" corre como comando nativo dentro de esta
# misma consola/pipeline de PowerShell, Ctrl+C no llega directo al proceso
# node -- PowerShell primero intercepta la senal para frenar su propio
# pipeline (y correr los "finally" pendientes), y recien en un segundo
# Ctrl+C el evento le pega de lleno al hijo. Dandole su propia ventana
# (su propio grupo de consola), el navegador de hot-reload recibe un solo
# Ctrl+C sin competir con nada mas.
Push-Location $netmanRoot
try {
    $playwrightOk = $true
    node -e "require.resolve('playwright')" *> $null
    if ($LASTEXITCODE -ne 0) { $playwrightOk = $false }

    if (-not $playwrightOk) {
        Write-Host "    Playwright no esta instalado en $netmanRoot todavia -- instalando (una sola vez)..." -ForegroundColor Yellow
        npm install playwright
        if ($LASTEXITCODE -ne 0) { throw "npm install playwright fallo." }
        npx playwright install chromium
        if ($LASTEXITCODE -ne 0) { throw "playwright install chromium fallo." }
    }
} finally {
    Pop-Location
}

Start-Process -FilePath 'node' -ArgumentList @($browserJs) -WorkingDirectory $netmanRoot -WindowStyle Normal
Write-Host "    Navegador de hot-reload lanzado en su propia ventana -- Ctrl+C ahi (una sola vez) lo detiene." -ForegroundColor Green
Write-Host ""
Write-Host "    Todo arriba: Host (paso 3) + navegador (paso 4), cada uno en su ventana." -ForegroundColor Green
Write-Host "    Para apagar netman: Ctrl+C en la ventana del navegador, y cerra la ventana del Host." -ForegroundColor DarkGray
