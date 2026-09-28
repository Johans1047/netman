# Cold Start — netman + SIPAF (arranque en frío)

> Guía para dejar el hot-reload de SIPAF corriendo desde cero, en una máquina nueva o
> después de reiniciar. Ordén esto **en este orden**: IIS primero, netman después.

## Requisitos previos (una sola vez)

- Visual Studio con el workload ASP.NET (.NET Framework) instalado (para IIS Express).
- Los dos proyectos de SIPAF en su lugar:
  - Librería: `C:\LibreriaSipaf\LibreriaSIPAF.vbproj`
  - Sitio web: `C:\Proyecto_VS2013\Sipaf`
- El binding `SIPAF-Site` en `%USERPROFILE%\Documents\IISExpress\config\applicationhost.config`
  (`http://localhost:12345/SIPAF`).
- netman construido: `HotReloadTool.sln` y `NetmanConfigTool.sln` (Build → Release).

## Paso 1 — Levantar IIS Express (obligatorio primero)

El hot-reload **no funciona si el sitio no está sirviéndose**. Opciones:

- **Desde VS:** abrì el proyecto del sitio (Sipaf) y presioná **F5** (deja IIS Express corriendo;
  no hace falta debug, con que quede el server arriba alcanza).
- **Sin VS (línea de comandos):**
  ```powershell
  & 'C:\Program Files\IIS Express\iisexpress.exe' /site:SIPAF-Site
  ```
  Dejá esa consola abierta mientras laburás (Ctrl+C apaga el server).

Verificar que responde (Chromium manda `User-Agent`; un cliente sin UA recibe 500 por un null-check
ausente en `wInicio.aspx.vb:21`):
```powershell
$req = [System.Net.WebRequest]::Create('http://localhost:12345/SIPAF/wInicio.aspx')
$req.UserAgent = 'Mozilla/5.0 test'
$req.GetResponse().StatusCode   # => OK (200)
```

## Paso 2 — Iniciar netman

**Recomendado (GUI):** `bin\Release\NetmanConfigTool.exe` → completá usuario, clave, rutas y URL →
**Guardar e iniciar netman**. El "Probar sitio" debe decir OK (si no, volvés al Paso 1).

**Alternativa (CLI):** dos ventanas:
```batch
:: Terminal 1: watcher (detecta cambios, compila, deploya, recicla)
C:\Users\jonathan.salazar1\Tools\netman\bin\netman-sipaf.cmd

:: Terminal 2: navegador con auto-login y recarga del iframe Contenido
cd C:\Users\jonathan.salazar1\Tools\netman
node netman-hotreload-browser.js
```
Las credenciales se leen de `NETMAN_SIPAF_USER` / `NETMAN_SIPAF_PASS` (el GUI las guarda en Windows
Credential Manager; el browser las toma del entorno).

## Paso 3 — Editar y recargar

| Editás... | Qué hace netman | Qué ves en el browser |
|-----------|-----------------|------------------------|
| `.aspx` / `.aspx.vb` (web) | bump `web-reload.stamp` (sin reciclar AppDomain) | recarga **la misma página** en su iframe `Contenido`, sesión intacta |
| `.vb` (LibreriaSipaf) | build + deploy `LibreriaSIPAF.dll`+pdb + recicla AppDomain | re-login y aterriza en el **menú** (`wPerfiles`) |

El contexto por módulo/conexión de SIPAF vive en `Application`/`Session` (in-proc) y **se pierde en
cada reciclaje**; por eso tras un cambio de librería hay que volver a elegir módulo desde el menú.

## Qué hace cada botón del GUI

(Los `...` al lado de las rutas solo abren el explorador de archivos.)

| Botón | Qué hace |
|-------|----------|
| **Probar sitio** | GET a `wInicio.aspx` (con User-Agent) para verificar que IIS Express está arriba. Solo informa en el estado/logs; no cambia nada. |
| **Guardar** | Valida los campos y escribe usuario/rutas/URL en `netman.config.json`. La clave va a Windows Credential Manager **solo si escribiste una** (en blanco = conserva la existente). No lanza nada. |
| **Iniciar netman** | Primero hace "Guardar"; luego lanza el **Host** y el **browser de hot-reload** como procesos hijos y muestra sus logs en el panel. Requiere clave guardada. |
| **Detener** | Detiene los dos procesos (primero Browser, después Host) sin cerrar la ventana. |
| **Cerrar** | Cierra el formulario; al cerrarse, el `FormClosing` detiene Browser + Host (no quedan procesos huérfanos). |

## Troubleshooting

- `net::ERR_CONNECTION_REFUSED` o "probe" en rojo → **IIS Express está caído**. Volvé al Paso 1.
- `wInicio.aspx` da 500 para requests sin header → es el null-check ausente en `wInicio.aspx.vb:21`
  (bug de la app, no del tool). Un browser real siempre manda User-Agent.
- El browser cae en `wTerminarSession`/menú por defecto → no recargaba el iframe `Contenido`; el
  script actual recarga el frame hijo in-place. Si ves el contenedor pero el frame vuelve a la
  bienvenida, es un cambio de librería (reciclo), esperado.
- Comportamiento raro con varios watchers → asegurate de tener **una sola** instancia de
  `HotReloadTool.Host` (puede quedar una vieja del GUI + otra del `.cmd`).
