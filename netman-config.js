// netman-config.js - config compartida por los scripts de Node de netman.
//
// Una sola fuente para la URL base del sitio, asi no queda fija en cada
// script. Orden de prioridad:
//   1. variable de entorno NETMAN_SITE_URL
//   2. "SiteBaseUrl" de netman.config.json (el mismo que escribe NetmanConfigTool
//      y lee Start-Netman.ps1), junto a este archivo
//   3. http://localhost:12345/SIPAF  (default historico)
// Devuelve la URL SIN barra final, p. ej. http://localhost:12345/SIPAF
const fs = require('fs');
const path = require('path');

const DEFAULT_SITE_URL = 'http://localhost:12345/SIPAF';

function readLocalConfig() {
  try {
    const p = path.join(__dirname, 'netman.config.json');
    if (!fs.existsSync(p)) return {};
    // replace(/^﻿/): PowerShell/Notepad guardan UTF-8 con BOM y JSON.parse no lo traga.
    return JSON.parse(fs.readFileSync(p, 'utf8').replace(/^﻿/, '')) || {};
  } catch (e) {
    return {};
  }
}

function siteBaseUrl() {
  const fromEnv = (process.env.NETMAN_SITE_URL || '').trim();
  const fromCfg = String(readLocalConfig().SiteBaseUrl || '').trim();
  return (fromEnv || fromCfg || DEFAULT_SITE_URL).replace(/\/+$/, '');
}

module.exports = { siteBaseUrl, DEFAULT_SITE_URL };
