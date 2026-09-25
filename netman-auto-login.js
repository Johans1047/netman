// netman-auto-login.js - Helper reutilizable para auto-login en SIPAF
// Uso: node netman-auto-login.js [--url <target>] [--user <usuario>] [--pass <clave>]
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

(async () => {
  const target = getArg('--url') || `${BASE}/wPerfiles.aspx`;
  const user = getArg('--user') || DEFAULT_USER;
  const pass = getArg('--pass') || DEFAULT_PASS;

  if (!pass) {
    console.error('[netman-login] Falta la clave: pasá --pass <clave> o seteá NETMAN_SIPAF_PASS.');
    process.exit(1);
  }

  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext();
  const page = await context.newPage();

  // Paso 1: guardar credenciales (primera vez)
  console.log(`[netman-login] Configurando credenciales para ${user}...`);
  await page.goto(`${BASE}/_netman-autologin.html`);
  await page.fill('#usuario', user);
  await page.fill('#clave', pass);
  await page.check('#recordar');
  await page.click('#btnEntrar');
  await page.waitForTimeout(1500);

  // Paso 2: navegar al destino via autologin
  console.log(`[netman-login] Navegando a ${target}...`);
  await page.goto(`${BASE}/_netman-autologin.html?to=${encodeURIComponent(target)}`);

  // Esperar que salga del login
  await page.waitForFunction(
    () => !window.location.href.includes('_netman-autologin'),
    { timeout: 10000 }
  ).catch(() => {});

  const finalUrl = page.url();
  console.log(`[netman-login] OK → ${finalUrl}`);

  await browser.close();
  return finalUrl;
})().catch(err => {
  console.error('[netman-login] ERROR:', err.message);
  process.exit(1);
});
