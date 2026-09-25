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

function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }
function mtime(p) { try { return fs.statSync(p).mtimeMs; } catch { return 0; } }
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

async function establishLogin(page) {
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
  await page.waitForFunction(
    () => !window.location.href.includes('_netman-autologin'),
    { timeout: 15000 }
  ).catch(() => {});
  // Asegurar que terminamos en el menu, no en una pagina de negocio obsoleta
  if (!page.url().toLowerCase().includes('wperfiles')) {
    await page.goto(BASE + '/wPerfiles.aspx', { waitUntil: 'domcontentloaded' }).catch(() => {});
  }
}

(async () => {
  console.log('[hotreload-browser] Iniciando Chrome visible...');
  const browser = await chromium.launch({ headless: false, args: ['--start-maximized'] });
  const context = await browser.newContext({ viewport: null });
  const page = await context.newPage();

  console.log('[hotreload-browser] Estableciendo sesion inicial...');
  await establishLogin(page);
  const cookie = (await context.cookies()).find(c => c.name === 'AutForm');
  console.log(cookie ? '[hotreload-browser] Sesion establecida.' : '[hotreload-browser] AVISO: sin cookie de auth.');

  // Pagina "de trabajo": la ultima real que el usuario navego (no login).
  // Si hay un iframe "Contenido" (msCompras.aspx y demas contenedores de
  // modulo), esto es la URL de ESE frame, no la del contenedor de arriba.
  let workPage = null;

  function snapshotWorkPage() {
    const u = currentWorkUrl(page);
    if (!isAuthPage(u)) workPage = u;
  }
  await page.goto(BASE + '/wPerfiles.aspx', { waitUntil: 'domcontentloaded' }).catch(() => {});
  console.log('[hotreload-browser] Menu listo. Editando... (Ctrl+C para salir)');
  console.log('[hotreload-browser] Sinal de recarga: ' + WEB_STAMP);

  let lastWeb = mtime(WEB_STAMP);
  let lastRec = mtime(RECYCLE_STAMP);

  while (true) {
    await sleep(800);
    snapshotWorkPage();

    const w = mtime(WEB_STAMP);
    const r = mtime(RECYCLE_STAMP);

    if (r !== lastRec) {           // reciclaje de libreria: sesion perdida
      lastRec = r; lastWeb = w;
      console.log('[hotreload-browser] RECICLAJE (libreria). Re-logueando -> menu...');
      await establishLogin(page);
      await page.goto(BASE + '/wPerfiles.aspx', { waitUntil: 'domcontentloaded' }).catch(() => {});
      console.log('[hotreload-browser] Listo en menu: ' + page.url());
      continue;
    }

    if (w !== lastWeb) {           // cambio web: recargar la pagina de trabajo real
      lastWeb = w;
      if (isAuthPage(workPage)) continue;   // si estamos en login, no tocar

      const frame = getContentFrame(page);

      if (frame) {
        // Contenedor de modulo (msCompras.aspx y similares): recargar el
        // IFRAME de contenido in-place, sin tocar la pagina/menu de arriba.
        console.log('[hotreload-browser] Cambio web. Recargando frame "' + CONTENT_FRAME_NAME + '": ' + frame.url());
        await frame.evaluate(() => window.location.reload()).catch(async () => {
          // Fallback si evaluate falla (frame recien reemplazado, etc.):
          // reasignar location.href desde la pagina padre.
          await page.evaluate((name) => {
            const f = window.frames[name];
            if (f) f.location.reload();
          }, CONTENT_FRAME_NAME).catch(() => {});
        });

        // Esperar a que el DOM del frame vuelva a estar listo y confirmar
        // que no nos tiro al login (sesion expirada).
        await sleep(300);
        const refreshedFrame = getContentFrame(page);
        const postUrl = refreshedFrame ? refreshedFrame.url() : page.url();
        if (isAuthPage(postUrl)) {
          console.log('[hotreload-browser] Caduco la sesion, relogueando...');
          await establishLogin(page);
        } else {
          console.log('[hotreload-browser] Frame recargado: ' + postUrl);
        }
      } else {
        // Pagina sin frameset (ej. wInicio.aspx sola): recargar top-level.
        const target = workPage || page.url();
        console.log('[hotreload-browser] Cambio web. Recargando la misma pagina: ' + target);
        await page.goto(target, { waitUntil: 'domcontentloaded' }).catch(() => {});
        if (isAuthPage(page.url())) {
          console.log('[hotreload-browser] Caduco la sesion, relogueando...');
          await establishLogin(page);
          await page.goto(target, { waitUntil: 'domcontentloaded' }).catch(() => {});
        }
        console.log('[hotreload-browser] Recargada: ' + page.url());
      }
    }
  }
})().catch(err => { console.error('[hotreload-browser] ERROR:', err.message); process.exit(1); });
