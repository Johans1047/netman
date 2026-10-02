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

const BASE = 'http://localhost:12345/SIPAF';
// Credenciales de desarrollo: preferí variables de entorno en vez de
// hardcodearlas acá (este repo tiene .git — no las commitees en texto plano).
//   set NETMAN_SIPAF_USER=<tu-usuario-sipaf>
//   set NETMAN_SIPAF_PASS=<tu-clave-sipaf>
const USER = process.env.NETMAN_SIPAF_USER || '';
const PASS = process.env.NETMAN_SIPAF_PASS || '';
const STAMP_DIR = path.join(__dirname, 'src', 'Host', 'bin', 'Debug');
const WEB_STAMP = path.join(STAMP_DIR, 'web-reload.stamp');
const RECYCLE_STAMP = path.join(STAMP_DIR, 'recycle.stamp');
const CONTENT_FRAME_NAME = 'Contenido';
// Por defecto Playwright guarda las descargas en una carpeta temporal propia
// que no es accesible desde el Explorador -- si estas probando un boton de
// "Descargar" dentro del Chrome de netman, el archivo "desaparecia". Fijando
// downloadsPath, Chromium las guarda directo en tu carpeta de Descargas de
// siempre, como cualquier otro navegador.
const DOWNLOADS_DIR = path.join(require('os').homedir(), 'Downloads');

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
async function attemptLogin(page) {
  if (!USER || !PASS) {
    throw new Error('Faltan NETMAN_SIPAF_USER / NETMAN_SIPAF_PASS en el entorno (no hardcodeamos credenciales por seguridad).');
  }
  await page.goto(BASE + '/_netman-autologin.html');
  await page.waitForSelector('#usuario', { timeout: 10000 });
  await page.fill('#usuario', USER);
  await page.fill('#clave', PASS);
  // Forzar destino al menu para no heredar un target viejo de localStorage
  await page.fill('#destino', BASE + '/wPerfiles.aspx');
  await page.check('#recordar');
  await page.click('#btnEntrar');

  // _netman-autologin.html ya distingue exito ("Login OK..." + redirige) de
  // fracaso (deja el status con clase "err" y NO redirige). Esperamos la
  // primera señal real que aparezca, en vez de solo esperar el "exito" y
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
  log('Login sin confirmar tras 15s (¿AppDomain todavia inicializando?).');
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

(async () => {
  log('Iniciando Chrome visible...');
  try { fs.mkdirSync(DOWNLOADS_DIR, { recursive: true }); } catch {}
  const browser = await chromium.launch({ headless: false, args: ['--start-maximized'], downloadsPath: DOWNLOADS_DIR });
  const context = await browser.newContext({ viewport: null, acceptDownloads: true });
  const page = await context.newPage();
  // Red de seguridad: si algun dialog nativo (confirm/alert/prompt) llegara
  // a aparecer igual -- por ejemplo un "Confirmar reenvio de formulario" que
  // el fix de frame.goto no cubra por algun camino no previsto -- aceptarlo
  // en vez de dejar que Playwright lo cancele solo (su default), que es lo
  // que dejaba la pagina "trabada" en el contenido viejo hasta un F5 manual.
  page.on('dialog', dialog => dialog.accept().catch(() => {}));

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

  while (true) {
    await sleep(800);
    snapshotWorkPage();

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
        const dest = stampTarget || frame.url();
        if (stampTarget && stampTarget !== frame.url()) {
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
        await frame.goto(dest, { waitUntil: 'domcontentloaded' }).catch(async () => {
          // Fallback si goto falla (frame recien reemplazado, etc.):
          // reasignar location.href desde la pagina padre (asignar .href es
          // una navegacion GET nueva, no un reload).
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
    }
  }
})().catch(err => { logErr('ERROR: ' + err.message); process.exit(1); });
