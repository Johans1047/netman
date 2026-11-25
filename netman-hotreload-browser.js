// netman-hotreload-browser.js
// Flujo automatico: editas -> netman avisa -> el browser reacciona.
//   - Cambio de .aspx/.aspx.vb (web-reload.stamp): recarga la MISMA pagina
//     (la sesion sigue viva, no hace falta relogin).
//   - Cambio de libreria .vb (recycle.stamp): el AppDomain se recicla, se
//     pierde la sesion -> reloguea y aterriza en el menu de modulos.
//
// IMPORTANTE sobre el frame de contenido:
// Los modulos de SIPAF (msCompras.aspx, msFinanciera.aspx, msPresupuesto.aspx,
// msPlanificacion.aspx, ...) son paginas "contenedor" con un <iframe
// name="Contenido"> donde vive la pagina real que estas editando (el menu
// clickea y navega ESE iframe, no la pagina superior). page.url() de
// Playwright solo ve el frame TOP-LEVEL (msCompras.aspx), que nunca cambia -
// por eso un page.goto(target) sobre la pagina superior recargaba el
// contenedor entero y el iframe volvia a su src por defecto
// (wBienvenidaCompras.aspx / wBienvenidaFinanzas.aspx / ...), es decir "el
// menu por defecto". El fix: trackear y recargar el frame hijo "Contenido"
// directamente, no la pagina de arriba.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const { siteBaseUrl } = require('./netman-config');
// URL base del sitio: NETMAN_SITE_URL > SiteBaseUrl de netman.config.json > default.
const BASE = siteBaseUrl();
// Credenciales de desarrollo: preferí variables de entorno en vez de
// hardcodearlas acá (este repo tiene .git — no las commitees en texto plano).
//   set NETMAN_SIPAF_USER=<tu-usuario-sipaf>
//   set NETMAN_SIPAF_PASS=<tu-clave-sipaf>
const USER = process.env.NETMAN_SIPAF_USER || '';
const PASS = process.env.NETMAN_SIPAF_PASS || '';
const STAMP_DIR = path.join(__dirname, 'src', 'Host', 'bin', 'Debug');
const WEB_STAMP = path.join(STAMP_DIR, 'web-reload.stamp');
const RECYCLE_STAMP = path.join(STAMP_DIR, 'recycle.stamp');
// Pedido de "abrir esta URL en el navegador de netman": el ConfigTool escribe
// la URL en este archivo cuando haces clic en un enlace de sus logs, y este
// script la abre en una pestaña de ESTE navegador (misma sesion/cookies).
const OPEN_URL_REQUEST = path.join(STAMP_DIR, 'open-url.request');
const CONTENT_FRAME_NAME = 'Contenido';
// DESCARGAS. Playwright NO guarda las descargas como un Chrome normal: las deja
// en una carpeta temporal con nombre GUID y las BORRA cuando se cierra el
// contexto (esto aplica tambien con launch({ downloadsPath }) -- la doc oficial
// lo dice: "the downloads are deleted when the browser context they were
// created in is closed"). Por eso hay que copiar cada archivo con
// download.saveAs() al destino real; ver hookDownloads() mas abajo.
//
// Destino, en orden de prioridad:
//   1) variable de entorno NETMAN_DOWNLOADS_DIR (override explicito)
//   2) la carpeta "Descargas" REAL de Windows (Known Folder), que puede estar
//      redirigida a OneDrive o a la red -- no asumimos que es ~/Downloads
//   3) ~/Downloads
// Ojo: "Descargas" es solo el nombre localizado que muestra el Explorador; la
// ruta fisica casi siempre se llama Downloads.
function resolveDownloadsDir() {
  const fromEnv = (process.env.NETMAN_DOWNLOADS_DIR || '').trim();
  if (fromEnv) return fromEnv;

  if (process.platform === 'win32') {
    try {
      const out = require('child_process').execFileSync(
        'reg',
        ['query', 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders',
         '/v', '{374DE290-123F-4565-9164-39C4925E467B}'],
        { encoding: 'utf8', timeout: 3000, windowsHide: true }
      );
      const m = /REG_(?:EXPAND_)?SZ\s+(.+?)\s*$/m.exec(out);
      if (m) {
        const expanded = m[1].replace(/%([^%]+)%/g, function (all, name) {
          return process.env[name] !== undefined ? process.env[name] : all;
        });
        if (expanded && expanded.indexOf('%') === -1) return expanded;
      }
    } catch (e) { /* sin acceso al registro: caemos al default */ }
  }
  return path.join(require('os').homedir(), 'Downloads');
}
const DOWNLOADS_DIR = resolveDownloadsDir();

// Nombre libre dentro de dir: "informe.pdf", "informe (1).pdf", ... para no
// pisar descargas anteriores (el suggestedFilename puede traer caracteres
// invalidos en Windows, los reemplazamos).
function uniqueDownloadPath(dir, suggestedName) {
  const safe = String(suggestedName || '').replace(/[\\/:*?"<>|]/g, '_').trim() || 'descarga';
  const ext = path.extname(safe);
  const base = path.basename(safe, ext);
  let candidate = path.join(dir, safe);
  for (let i = 1; fs.existsSync(candidate); i++) {
    candidate = path.join(dir, base + ' (' + i + ')' + ext);
  }
  return candidate;
}

// Se engancha a CADA pagina del contexto (la principal y las pestañas/popups
// que SIPAF abre con window.open, p.ej. el visor de reportes), porque el
// evento 'download' es por pagina.
function hookDownloads(p) {
  p.on('download', async function (download) {
    try {
      const dest = uniqueDownloadPath(DOWNLOADS_DIR, download.suggestedFilename());
      await download.saveAs(dest);
      log('Descarga guardada: ' + dest);
    } catch (err) {
      log('No se pudo guardar la descarga: ' + err.message);
    }
  });
}

function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }
function mtime(p) { try { return fs.statSync(p).mtimeMs; } catch { return 0; } }

// Timestamp HH:MM:SS.mmm en cada linea de log -- pedido explicito para poder
// cruzar estos logs con los de la pestaña Host&GUI y ver el orden real de
// los eventos al debuguear (p.ej. si el reload del browser corrio ANTES o
// DESPUES de que el Host terminara de bumpear el stamp).
function ts() {
  const d = new Date();
  const pad = (n, w) => String(n).padStart(w || 2, '0');
  return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds()) + '.' + pad(d.getMilliseconds(), 3);
}
function log(msg) { console.log('[' + ts() + '] ' + msg); }
function logErr(msg) { console.error('[' + ts() + '] ' + msg); }

// El Host, ademas de tocar el mtime de web-reload.stamp, escribe en una
// segunda linea la URL directa de la pagina que cambio (cuando pudo
// determinarla sin ambiguedad). Sin esto, este script solo sabia "algo
// cambio" y recargaba lo que fuera que el frame tuviera cargado en ESE
// momento -- correcto si estas editando la pagina que estas viendo, pero mal
// en un flujo de dos paginas (A llama a B): editar B estando parado en A solo
// recargaba A. Linea vacia (varias paginas cambiaron a la vez, o no se pudo
// mapear) => mismo comportamiento de siempre, recargar donde estemos parados.
function readStampTargetUrl(p) {
  try {
    const lines = fs.readFileSync(p, 'utf8').split(/\r?\n/);
    const target = (lines[1] || '').trim();
    return target || null;
  } catch {
    return null;
  }
}
function isAuthPage(u) {
  if (!u) return true;
  const l = u.toLowerCase();
  return l.includes('winicio') || l.includes('wterminarsession') || l.includes('_netman-autologin');
}

// El frame de contenido real, si la pagina top-level es un contenedor de
// modulo (msCompras.aspx y similares). null en paginas sin frameset.
function getContentFrame(page) {
  return page.frames().find(f => f.name() === CONTENT_FRAME_NAME) || null;
}

// Normaliza una URL para compararla (sin query ni hash, minusculas). null si no parsea.
function urlKey(u) {
  try {
    const x = new URL(u);
    return (x.origin + x.pathname).toLowerCase();
  } catch { return null; }
}

// Frame anidado (iframe dentro del frame de contenido, o mas adentro) que ya
// tiene cargada exactamente la pagina editada. Si existe, hay que recargar ESE
// frame y no navegar "Contenido" a la pagina editada: eso reemplazaria la
// pagina contenedora (la que tiene el iframe) por la pagina interna sola.
// excludeFrame: el propio "Contenido", que se trata aparte (recarga o navega).
function findFrameByUrl(page, targetUrl, excludeFrame) {
  const key = urlKey(targetUrl);
  if (!key) return null;
  return page.frames().find(f => f !== page.mainFrame() && f !== excludeFrame && urlKey(f.url()) === key) || null;
}

// Ventanas emergentes (window.open) y pestañas nuevas: cualquier pagina del
// contexto que no sea la principal. Playwright las ve como Page propias; el
// frame "Contenido" solo existe en la principal.
function popupPages(context, page) {
  return context.pages().filter(p => p !== page && !p.isClosed());
}

// Frame (el principal de la ventana o uno interno) de `p` con la pagina editada.
function findFrameInPage(p, targetUrl) {
  const key = urlKey(targetUrl);
  if (!key) return null;
  return p.frames().find(f => urlKey(f.url()) === key) || null;
}

// URL "real" en la que esta trabajando el usuario: la del iframe de
// contenido si existe, si no la de la pagina top-level.
function currentWorkUrl(page) {
  const frame = getContentFrame(page);
  if (frame) return frame.url();
  return page.url();
}

// Un solo intento de login. Devuelve true/false en vez de tragarse
// cualquier fallo en silencio -- antes, si el timeout de 15s expiraba (p.ej.
// porque el AppDomain nuevo todavia estaba corriendo Application_Start, que
// hace una consulta real a BD antes de poder servir nada), el codigo
// navegaba de todas formas a wPerfiles.aspx. Eso dejaba al navegador
// "logueado" por la cookie de Forms Auth vieja (que sobrevive al reciclaje)
// pero con Application(usuario) -- y por lo tanto _CadenaConexion -- vacio,
// porque el login real nunca habia terminado de correr. Resultado: el error
// "connectionString" de ArgumentException en la primera pagina que tocara
// una clase de LibreriaSipaf.
// Credenciales al helper SIN ponerlas en la URL (la URL queda en historial,
// logs de IIS Express y Referer): se las dejamos en localStorage del origen
// del helper antes de que cargue su script. Solo en esa pagina.
async function sembrarCredencialesAutologin(context, user, pass) {
  await context.addInitScript(function (cred) {
    if (location.pathname.indexOf('_netman-autologin') === -1) return;
    try { localStorage.setItem('netman.autologin', JSON.stringify({ user: cred.user, pass: cred.pass })); } catch (e) {}
  }, { user: user, pass: pass });
}

async function attemptLogin(page) {
  if (!USER || !PASS) {
    throw new Error('Faltan NETMAN_SIPAF_USER / NETMAN_SIPAF_PASS en el entorno (no hardcodeamos credenciales por seguridad).');
  }
  // _netman-autologin.html es un helper same-origin que hace GET a
  // wInicio.aspx, copia __VIEWSTATE/__EVENTVALIDATION y hace el POST Web
  // Forms por nosotros. Las credenciales NO van en la URL: se siembran en su
  // localStorage con addInitScript (la URL queda en historial y logs).
  await sembrarCredencialesAutologin(page.context(), USER, PASS);
  var loginPage = BASE + '/_netman-autologin.html?to=' + encodeURIComponent(BASE + '/wPerfiles.aspx');
  await page.goto(loginPage);

  // _netman-autologin.html ya distingue exito ("Login OK..." + redirige) de
  // fracaso (deja el status con clase "err" y NO redirige). Esperamos la
  // primera senial real que aparezca, en vez de solo esperar el "exito" y
  // seguir igual si nunca llega.
  const outcome = await Promise.race([
    page.waitForFunction(
      () => !window.location.href.includes('_netman-autologin'),
      { timeout: 15000 }
    ).then(() => 'ok').catch(() => null),
    page.waitForFunction(
      () => {
        var el = document.getElementById('status');
        return !!el && el.className.indexOf('err') !== -1;
      },
      { timeout: 15000 }
    ).then(() => 'err').catch(() => null),
  ]);

  if (outcome === 'ok') {
    // Asegurar que terminamos en el menu, no en una pagina de negocio obsoleta
    if (!page.url().toLowerCase().includes('wperfiles')) {
      await page.goto(BASE + '/wPerfiles.aspx', { waitUntil: 'domcontentloaded' }).catch(() => {});
    }
    return true;
  }

  if (outcome === 'err') {
    const msg = await page.$eval('#status', el => el.textContent).catch(() => '(sin mensaje de status)');
    log('Login fallo: ' + msg);
    return false;
  }

  // Ni "ok" ni "err" llegaron en 15s: probablemente el AppDomain nuevo
  // todavia estaba inicializando. Tratarlo como fallo, no como exito mudo.
  log('Login sin confirmar tras 15s (AppDomain todavia inicializando?).');
  return false;
}

// Reintenta attemptLogin() con backoff en vez de aceptar el primer intento
// fallido y seguir navegando a ciegas. Si se agotan los intentos, tira el
// error hacia arriba (el loop principal ya sale del proceso con
// process.exit(1) en ese caso) -- mejor frenar fuerte y que se note, que
// seguir trabajando con una sesion a medio loguear.
async function establishLogin(page) {
  const MAX_INTENTOS = 4;
  const ESPERA_BASE_MS = 2000; // backoff: 2s, 4s, 6s entre intentos

  for (let intento = 1; intento <= MAX_INTENTOS; intento++) {
    const ok = await attemptLogin(page).catch(err => {
      log('Error en intento de login: ' + err.message);
      return false;
    });
    if (ok) {
      if (intento > 1) log('Login OK en el intento ' + intento + '.');
      return;
    }
    if (intento < MAX_INTENTOS) {
      const espera = ESPERA_BASE_MS * intento;
      log('Reintentando login en ' + (espera / 1000) + 's (intento ' + (intento + 1) + '/' + MAX_INTENTOS + ')...');
      await sleep(espera);
    }
  }

  throw new Error('No se pudo re-loguear tras ' + MAX_INTENTOS + ' intentos. Revisá el sitio a mano antes de seguir editando.');
}

// EXTENSIONES DE CHROME (config SOLO local). Playwright no puede instalar nada
// desde la Chrome Web Store: hay que bajar la carpeta de la extension a mano y
// apuntarla con --load-extension. Y solo funciona sobre un contexto
// persistente (launchPersistentContext) -- chromium.launch() normal no lo
// soporta de forma util, porque el profile se tira al cerrar.
//
// Se declara en netman.extensions.json, un archivo aparte de
// netman.config.json y tambien en .gitignore (config local de cada maquina,
// nunca se commitea):
//   { "Extensions": [ "C:\\ruta\\a\\mi-extension" ] }
// Va aparte A PROPOSITO: netman.config.json lo reescribe el ConfigTool desde
// su propio modelo, que ignora toda clave que el no conoce -- si pusieramos
// "Extensions" ahi, desapareceria cada vez que alguien apreta Guardar.
// Sin ese campo, o si el archivo no existe, arranca EXACTAMENTE igual que
// antes (launch + newContext): cero cambio para quien no lo use.
function loadLocalExtensions() {
  try {
    const cfgPath = path.join(__dirname, 'netman.extensions.json');
    if (!fs.existsSync(cfgPath)) return [];
    // replace(/^\uFEFF/): PowerShell/Notepad guardan UTF-8 con BOM y JSON.parse no lo traga.
    const cfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8').replace(/^\uFEFF/, ''));
    const list = Array.isArray(cfg.Extensions) ? cfg.Extensions : [];
    // Las rutas relativas se resuelven contra la carpeta del config, NO contra
    // el cwd -- este script lo lanzan el .ps1, el ConfigTool y VS, cada uno con
    // un cwd distinto, y ahi una relativa pasaria a ser impredecible.
    const cfgDir = path.dirname(cfgPath);
    const found = [];
    for (const raw of list) {
      // Ojo: path.resolve('') devuelve el cwd, no una cadena vacia -- hay que
      // filtrar ANTES de resolver, si no una entrada "" o null se colaria como
      // si fuera una extension valida.
      const rel = String(raw == null ? '' : raw).trim();
      if (!rel) continue;
      const p = path.resolve(cfgDir, rel);
      if (fs.existsSync(p)) found.push(p);
      else log('AVISO: no existe la carpeta de la extension, la salteo: ' + p);
    }
    return found;
  } catch (err) {
    log('AVISO: no pude leer Extensions de netman.extensions.json: ' + err.message);
    return [];
  }
}

// Profile del contexto persistente. Fuera del repo y fuera de %TEMP% (Windows
// lo limpia y perdes la sesion de la extension entre corridas).
const BROWSER_PROFILE_DIR = process.env.NETMAN_BROWSER_PROFILE
  || path.join(process.env.LOCALAPPDATA || require('os').homedir(), 'netman', 'browser-profile');

// Usa el Edge/Chrome ya instalado (no descarga el Chromium de Playwright, que
// la red puede bloquear). NETMAN_BROWSER_CHANNEL=chrome para usar Chrome.
const BROWSER_CHANNEL = process.env.NETMAN_BROWSER_CHANNEL || 'msedge';

(async () => {
  log('Iniciando ' + BROWSER_CHANNEL + ' visible...');
  try { fs.mkdirSync(DOWNLOADS_DIR, { recursive: true }); } catch {}
  log('Descargas -> ' + DOWNLOADS_DIR);
  // OJO: sin downloadsPath a proposito. Con downloadsPath Playwright dejaria
  // aca archivos con nombre GUID (que ademas borra al cerrar). Los archivos
  // buenos los copia hookDownloads() con su nombre real.
  const extensions = loadLocalExtensions();
  let context;
  if (extensions.length > 0) {
    try { fs.mkdirSync(BROWSER_PROFILE_DIR, { recursive: true }); } catch {}
    // Las dos cosas van SIEMPRE en un solo flag con las rutas separadas por
    // coma. Chromium sobreescribe los switches repetidos, asi que un
    // --load-extension por extension terminaria cargando solo la ultima
    // (medido: repetido con 2 ext -> 1 worker; comma-joined -> 2 workers).
    // --disable-extensions-except es obligatorio junto a --load-extension:
    // sin el primero Chromium ignora el segundo.
    const args = [
      '--start-maximized',
      '--disable-extensions-except=' + extensions.join(','),
      '--load-extension=' + extensions.join(','),
    ];
    log('Extensiones: ' + extensions.length + ' | profile: ' + BROWSER_PROFILE_DIR);
    context = await chromium.launchPersistentContext(BROWSER_PROFILE_DIR, {
      channel: BROWSER_CHANNEL,
      headless: false,      // las extensiones NO corren en headless
      viewport: null,
      acceptDownloads: true,
      args: args,
    });
  } else {
    const browser = await chromium.launch({ channel: BROWSER_CHANNEL, headless: false, args: ['--start-maximized'] });
    context = await browser.newContext({ viewport: null, acceptDownloads: true });
  }
  // Registrado ANTES de newPage() para que tambien cubra la pagina principal
  // y cualquier pestaña/popup que se abra despues.
  context.on('page', hookDownloads);
  // launch() normal no tiene ninguna pestaña todavia; el contexto persistente
  // ya arranca con una (la ultima sesion o about:blank). Reusamos la que haya
  // para no dejar una pestaña huerfana, y enganchamos descargas tambien en la
  // que ya existe -- el listener de 'page' de arriba solo cubre las futuras.
  for (const existing of context.pages()) hookDownloads(existing);
  const page = context.pages()[0] || await context.newPage();
  // Dialogs nativos (alert/confirm/prompt/beforeunload). Playwright, si hay un
  // listener registrado, espera que alguien los resuelva. Politica por defecto
  // ("safe"): alert y beforeunload se aceptan (son informativos y sin ellos la
  // pagina queda trabada en el contenido viejo hasta un F5); confirm y prompt
  // se CANCELAN y se loguea el texto, para no aprobar sin querer una
  // confirmacion de borrado/guardado. NETMAN_DIALOGS=accept devuelve el
  // comportamiento anterior (aceptar todo).
  const DIALOG_POLICY = (process.env.NETMAN_DIALOGS || 'safe').toLowerCase();
  page.on('dialog', async dialog => {
    const type = dialog.type();
    const informativo = type === 'alert' || type === 'beforeunload';
    const aceptar = DIALOG_POLICY === 'accept' || informativo;
    if (!informativo) {
      log('Dialogo ' + type + ' ' + (aceptar ? 'aceptado' : 'CANCELADO (NETMAN_DIALOGS=accept para aceptarlos)') + ': ' + dialog.message());
    }
    try { await (aceptar ? dialog.accept() : dialog.dismiss()); } catch (e) {}
  });

  log('Estableciendo sesion inicial...');
  await establishLogin(page);
  const cookie = (await context.cookies()).find(c => c.name === 'AutForm');
  log(cookie ? 'Sesion establecida.' : 'AVISO: sin cookie de auth.');

  // Pagina "de trabajo": la ultima real que el usuario navego (no login).
  // Si hay un iframe "Contenido" (msCompras.aspx y demas contenedores de
  // modulo), esto es la URL de ESE frame, no la del contenedor de arriba.
  let workPage = null;

  function snapshotWorkPage() {
    const u = currentWorkUrl(page);
    if (!isAuthPage(u)) workPage = u;
  }
  await page.goto(BASE + '/wPerfiles.aspx', { waitUntil: 'domcontentloaded' }).catch(() => {});
  log('Menu listo. Editando... (Ctrl+C para salir)');
  log('Sinal de recarga: ' + WEB_STAMP);

  let lastWeb = mtime(WEB_STAMP);
  let lastRec = mtime(RECYCLE_STAMP);
  let lastOpen = mtime(OPEN_URL_REQUEST);   // un pedido viejo de antes de arrancar no se repite

  // Pestaña reutilizable para los enlaces que se abren desde el ConfigTool.
  let linkPage = null;
  async function openRequestedUrl() {
    let url = '';
    try { url = fs.readFileSync(OPEN_URL_REQUEST, 'utf8').replace(/^\uFEFF/, '').trim(); } catch {}
    if (!/^https?:\/\//i.test(url)) {
      log('Pedido de abrir URL ignorado (no es http/https): ' + url);
      return;
    }
    try {
      if (!linkPage || linkPage.isClosed()) linkPage = await context.newPage();
      log('Abriendo enlace del log en este navegador: ' + url);
      await linkPage.goto(url, { waitUntil: 'domcontentloaded' }).catch(() => {});
      await linkPage.bringToFront().catch(() => {});
    } catch (e) {
      log('No se pudo abrir ' + url + ': ' + e.message);
    }
  }

  while (true) {
    await sleep(800);
    snapshotWorkPage();

    const o = mtime(OPEN_URL_REQUEST);
    if (o !== lastOpen) {
      lastOpen = o;
      await openRequestedUrl();
    }

    const w = mtime(WEB_STAMP);
    const r = mtime(RECYCLE_STAMP);

    if (r !== lastRec) {           // reciclaje de libreria: sesion perdida
      lastRec = r; lastWeb = w;
      log('RECICLAJE (libreria). Re-logueando -> menu...');
      await establishLogin(page);
      await page.goto(BASE + '/wPerfiles.aspx', { waitUntil: 'domcontentloaded' }).catch(() => {});
      log('Listo en menu: ' + page.url());
      continue;
    }

    if (w !== lastWeb) {           // cambio web: recargar/navegar a la pagina real
      lastWeb = w;
      if (isAuthPage(workPage)) continue;   // si estamos en login, no tocar

      const stampTarget = readStampTargetUrl(WEB_STAMP);

      // Ventana emergente con la pagina editada: se recarga SOLO esa (si no, el
      // codigo de abajo navegaria "Contenido" a una pagina que el usuario tiene
      // abierta en otra ventana y le taparia lo que estaba haciendo).
      if (stampTarget) {
        let handledInPopup = false;
        for (const popup of popupPages(context, page)) {
          const pf = findFrameInPage(popup, stampTarget);
          if (!pf) continue;
          log('Cambio web. La pagina editada esta en una ventana emergente -- recargando: ' + pf.url());
          await pf.goto(pf.url(), { waitUntil: 'domcontentloaded' }).catch(() => {});
          handledInPopup = true;
        }
        if (handledInPopup) continue;
      }

      const frame = getContentFrame(page);

      if (frame) {
        // Contenedor de modulo (msCompras.aspx y similares): recargar/navegar
        // el IFRAME de contenido in-place, sin tocar la pagina/menu de arriba.
        //
        // dest: si el Host nos dio la URL exacta de la pagina que cambio Y es
        // distinta de donde esta parado el frame ahora mismo, navegamos ahi
        // directo (caso "pagina A llama a pagina B", editaste B estando en
        // A). Si no, nos quedamos con el comportamiento de siempre: recargar
        // la URL actual del frame.
        // Si la pagina editada ya esta cargada en un iframe anidado (p. ej. un
        // detalle dentro de la pagina que muestra "Contenido"), se recarga ese
        // frame. Si no, se sigue como siempre sobre "Contenido".
        const nestedFrame = stampTarget ? findFrameByUrl(page, stampTarget, frame) : null;
        const reloadFrame = nestedFrame || frame;
        const dest = nestedFrame ? nestedFrame.url() : (stampTarget || frame.url());
        if (nestedFrame) {
          log('Cambio web. La pagina editada esta en un iframe anidado -- recargando ese frame: ' + dest);
        } else if (stampTarget && stampTarget !== frame.url()) {
          log('Cambio web. Pagina editada distinta de donde estas parado -- navegando frame "' + CONTENT_FRAME_NAME + '" a: ' + dest);
        } else {
          log('Cambio web. Recargando frame "' + CONTENT_FRAME_NAME + '": ' + dest);
        }

        // OJO: antes esto hacia window.location.reload() dentro del frame.
        // Web Forms postea al mismo .aspx en cada click de boton (Buscar,
        // Limpiar, etc.), asi que si el documento actual del frame llego ahi
        // por un POST, reload() repite ESE POST -> el navegador dispara el
        // dialog nativo "Confirmar reenvio de formulario", que Playwright
        // cancela solo por default -- el reload quedaba cancelado en
        // silencio y la pagina se veia "vieja" hasta un F5 manual. Fix:
        // frame.goto(dest) hace un GET limpio en vez de repetir la ultima
        // request tal cual, asi nunca dispara ese dialog, haya habido
        // postback o no -- y de paso sirve igual para navegar a una URL
        // distinta cuando dest viene del stamp.
        await reloadFrame.goto(dest, { waitUntil: 'domcontentloaded' }).catch(async () => {
          // Fallback si goto falla (frame recien reemplazado, etc.):
          // reasignar location.href desde la pagina padre (asignar .href es
          // una navegacion GET nueva, no un reload).
          if (reloadFrame !== frame) return;   // el fallback por nombre solo aplica a "Contenido"
          await page.evaluate((name, url) => {
            const f = window.frames[name];
            if (f) f.location.href = url;
          }, CONTENT_FRAME_NAME, dest).catch(() => {});
        });

        // Esperar a que el DOM del frame vuelva a estar listo y confirmar
        // que no nos tiro al login (sesion expirada).
        await sleep(300);
        const refreshedFrame = getContentFrame(page);
        const postUrl = refreshedFrame ? refreshedFrame.url() : page.url();
        if (isAuthPage(postUrl)) {
          log('Caduco la sesion, relogueando...');
          await establishLogin(page);
        } else {
          log('Frame recargado: ' + postUrl);
        }
      } else {
        // Pagina sin frameset (ej. wInicio.aspx sola): recargar/navegar top-level.
        const target = stampTarget || workPage || page.url();
        log('Cambio web. Recargando la misma pagina: ' + target);
        await page.goto(target, { waitUntil: 'domcontentloaded' }).catch(() => {});
        if (isAuthPage(page.url())) {
          log('Caduco la sesion, relogueando...');
          await establishLogin(page);
          await page.goto(target, { waitUntil: 'domcontentloaded' }).catch(() => {});
        }
        log('Recargada: ' + page.url());
      }

      // Cambio sin pagina concreta (.ascx, .master, .js, .css o varias paginas):
      // puede afectar tambien a lo que haya en ventanas emergentes abiertas.
      if (!stampTarget) {
        for (const popup of popupPages(context, page)) {
          log('Cambio web. Recargando ventana emergente: ' + popup.url());
          await popup.goto(popup.url(), { waitUntil: 'domcontentloaded' }).catch(() => {});
        }
      }
    }
  }
})().catch(err => { logErr('ERROR: ' + err.message); process.exit(1); });
