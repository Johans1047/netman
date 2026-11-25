@echo off
REM Lanzador CLI del Host de netman para SIPAF (alternativa al ConfigTool / Start-Netman.ps1).
REM Mismos argumentos que Start-Netman.ps1: vigila la libreria (.vb), compila, despliega la DLL
REM en Bin del sitio y recicla el AppDomain; los cambios web (.aspx/.aspx.vb) no compilan.
REM
REM Rutas: salen de netman.config.json (las escribe el ConfigTool). Para forzar otras:
REM   set NETMAN_LIBRARY_PROJECT=C:\ruta\LibreriaSIPAF\LibreriaSIPAF.vbproj
REM   set NETMAN_WEB_DIR=C:\ruta\SIPAF
REM   set NETMAN_SITE_URL=http://localhost:12345/SIPAF/
setlocal
set "CFG=%~dp0..\netman.config.json"
if not defined NETMAN_LIBRARY_PROJECT for /f "usebackq delims=" %%A in (`powershell -NoProfile -Command "try{(Get-Content -Raw '%CFG%' | ConvertFrom-Json).LibreriaSipafProject}catch{}"`) do set "NETMAN_LIBRARY_PROJECT=%%A"
if not defined NETMAN_WEB_DIR          for /f "usebackq delims=" %%A in (`powershell -NoProfile -Command "try{(Get-Content -Raw '%CFG%' | ConvertFrom-Json).SipafSitePath}catch{}"`) do set "NETMAN_WEB_DIR=%%A"
if not defined NETMAN_SITE_URL         for /f "usebackq delims=" %%A in (`powershell -NoProfile -Command "try{(Get-Content -Raw '%CFG%' | ConvertFrom-Json).SiteBaseUrl}catch{}"`) do set "NETMAN_SITE_URL=%%A"
if not defined NETMAN_SITE_URL set "NETMAN_SITE_URL=http://localhost:12345/SIPAF/"

if not defined NETMAN_LIBRARY_PROJECT (echo [netman] Falta la ruta del proyecto de la libreria. Abri NetmanConfigTool y guarda la configuracion, o define NETMAN_LIBRARY_PROJECT. & exit /b 1)
if not defined NETMAN_WEB_DIR         (echo [netman] Falta la ruta del sitio. Abri NetmanConfigTool y guarda la configuracion, o define NETMAN_WEB_DIR. & exit /b 1)

"%~dp0..\src\Host\bin\Debug\HotReloadTool.Host.exe" start ^
  --mode build-recycle ^
  -p "%NETMAN_LIBRARY_PROJECT%" ^
  --deploy-to "%NETMAN_WEB_DIR%\Bin" ^
  --recycle-target "%NETMAN_WEB_DIR%\web.config" ^
  --web-watch "%NETMAN_WEB_DIR%" ^
  --site-url "%NETMAN_SITE_URL%"
