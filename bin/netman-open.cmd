@echo off
REM Abrir una pagina de SIPAF con auto-login
REM Uso: netman-open.bat <url-ejemplo>
REM   netman-open.bat http://localhost:12345/SIPAF/Finanzas/Contabilidad/ReciboCaja/wProcConsultaReciboFactura.aspx
REM   netman-open.bat wProcConsultaReciboFactura.aspx
REM   netman-open.bat

set BASE=http://localhost:12345/SIPAF
set "TARGET=%~1"

if "%TARGET%"=="" (
    set "TARGET=%BASE%/wPerfiles.aspx"
) else if not "%TARGET:~0,4%"=="http" (
    set "TARGET=%BASE%/%TARGET%"
)

cd /d "%~dp0"
node netman-auto-login.js --url "%TARGET%"
