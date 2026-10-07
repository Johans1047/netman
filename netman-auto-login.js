// netman-auto-login.js - Helper reutilizable para auto-login en SIPAF
// Uso: node netman-auto-login.js [--url <target>] [--user <usuario>] [--pass <clave>]
//
// Estrategia: navega a _netman-autologin.html (mismo origin que SIPAF) con las
// credenciales en querystring. Ese HTML hace GET a wInicio.aspx, parsea los
// campos Web Forms (__VIEWSTATE, __EVENTVALIDATION, ...) y hace POST con
// ctl00$cphSipaf$txtUsuario / ctl00$cphSipaf$txtPassword. ASP.NET Forms Auth
// responde con 302 y la cookie queda seteada.
const { chromium } = require('playwright');

const BASE = 'http://localhost:12345/SIPAF';
// Credenciales de desarrollo por variable de entorno — este repo tiene .git,
// no hardcodees la clave acá.
//   set NETMAN_SIPAF_USER=<tu-usuario-sipaf>
//   set NETMAN_SIPAF_PASS=<tu-clave-sipaf>
const DEFAULT_USER = process.env.NETMAN_SIPAF_USER || '';
const DEFAULT_PASS = process.env.NETMAN_SIPAF_PASS || '';

function getArg(flag) {
  const i = process.argv.indexOf(flag);
  return i !== -1 ? process.argv[i + 1] : null;
}

// Credenciales al helper SIN ponerlas en la URL (la URL queda en historial,
// logs de IIS Express y Referer): se las dejamos en localStorage del origen
// del helper antes de que cargue su script. Solo en esa pagina.
async function sembrarCredencialesAutologin(context, user, pass) {
  await context.addInitScript(function (cred) {
    if (location.pathname.indexOf('_netman-autologin') === -1) return;
    try { localStorage.setItem('netman.autologin', JSON.stringify({ user: cred.user, pass: cred.pass })); } catch (e) {}
  }, { user: user, pass: pass });
}

(async () => {
  const target = getArg('--url') || `${BASE}/wPerfiles.aspx`;
  const user = getArg('--user') || DEFAULT_USER;
  const pass = getArg('--pass') || DEFAULT_PASS;

  if (!user || !pass) {
    console.error('[netman-login] Faltan credenciales: pasá --user/--pass o seteá NETMAN_SIPAF_USER/PASS.');
    process.exit(1);
  }

  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext();
  const page = await context.newPage();

  // El HTML helper entra solo si encuentra credenciales en su localStorage;
  // las sembramos con addInitScript (nunca en la URL).
  await sembrarCredencialesAutologin(context, user, pass);
  const loginUrl = `${BASE}/_netman-autologin.html?to=${encodeURIComponent(target)}`;

  console.log(`[netman-login] Ejecutando auto-login...`);
  await page.goto(loginUrl);

  // Esperar a que salga del autologin (redirige al destino tras login OK)
  await page.waitForFunction(
    () => !window.location.href.includes('_netman-autologin'),
    { timeout: 15000 }
  ).catch(() => {});

  const finalUrl = page.url();
  const ok = !finalUrl.includes('_netman-autologin');

  if (ok) {
    console.log(`[netman-login] OK → ${finalUrl}`);
  } else {
    // Leer el mensaje de error que dejó el status div
    const errMsg = await page.$eval('#status', el => el.textContent).catch(() => '(sin detalle)');
    console.error(`[netman-login] FALLÓ: ${errMsg}`);
  }

  await browser.close();
  process.exit(ok ? 0 : 1);
})().catch(err => {
  console.error('[netman-login] ERROR:', err.message);
  process.exit(1);
});
