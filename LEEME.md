# netman — hot reload para SIPAF

**Qué hace:** cuando editas una página (`.aspx` / `.ascx` / `.master` / `.js` / `.css`) o una clase de la librería (`.vb`), netman actualiza el navegador solo.

- **Cambios de vista:** recarga en el acto, la sesión sigue viva.
- **Cambios de librería (`.vb`):** compila, despliega la DLL, recicla el sitio y vuelve a iniciar sesión.

> English version and every installation method: [`README.md`](README.md).

## Instalación (una sola vez)

1. Descomprime el ZIP en una carpeta tuya (por ejemplo `C:\Tools\netman`).
2. Clic derecho en `Install-Netman.ps1` → **Ejecutar con PowerShell**.
   Si Windows bloquea el script, abre PowerShell en esa carpeta y ejecuta:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-Netman.ps1
   ```

3. Abre **netman** desde el Escritorio.
4. Completa usuario SIPAF, clave (se guarda en el Administrador de credenciales de Windows, no en un archivo), ruta del proyecto de la librería, ruta del sitio y URL (por defecto `http://localhost:12345/SIPAF/`).
5. Con el sitio corriendo en IIS Express, dale a **Guardar e iniciar netman**.

## Requisitos

- Windows 10/11 (.NET Framework 4.5.1+ ya viene incluido).
- Microsoft Edge (o Chrome con `NETMAN_BROWSER_CHANNEL=chrome`).
- Node.js: opcional. Si no lo tienes, se usa el incluido (versión portable) o el ConfigTool lo descarga la primera vez.

## Variables opcionales

| Variable | Qué hace |
|---|---|
| `NETMAN_SITE_URL` | URL del sitio (por defecto sale de `netman.config.json`) |
| `NETMAN_BROWSER_CHANNEL` | `msedge` (defecto) o `chrome` |
| `NETMAN_DOWNLOADS_DIR` | Carpeta de descargas del navegador de netman |
| `NETMAN_DIALOGS` | `accept` = aceptar todos los diálogos JS (por defecto se cancelan los `confirm`/`prompt` para no aprobar un borrado sin querer) |

## Al terminar

**Detener** en el ConfigTool (o cerrar la ventana) cierra Host, Node y el navegador.
Más detalle en [`docs/cold-start.md`](docs/cold-start.md).
