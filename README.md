# netman

Hot reload for **SIPAF** (ASP.NET Web Forms + a VB.NET class library).

Edit a page or a library class and netman refreshes the browser for you:

| You edit... | netman does... | You see... |
|---|---|---|
| `.aspx` / `.ascx` / `.master` / `.js` / `.css` | Bumps a reload stamp (the app domain is **not** recycled) | The same page reloads in place, session intact |
| `.vb` in the library project | Builds the library, deploys the DLL to the site's `Bin`, recycles the site | Automatic re-login, landing on the menu |

netman is made of three parts: a **Host** (.NET Framework, file watcher + build/deploy/recycle), a **browser
script** (Node.js + Playwright, drives Edge with auto-login and reloads), and a **ConfigTool** (Windows Forms GUI
to configure and start/stop everything).

> Spanish quick-start: [`LEEME.md`](LEEME.md) (also included in the ZIP packages). Operational details (cold start, what each button does,
> troubleshooting): [`docs/cold-start.md`](docs/cold-start.md) (Spanish).

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 / 11 | .NET Framework 4.5.1+ is already included |
| Microsoft Edge | Used by netman's browser. For Chrome, set `NETMAN_BROWSER_CHANNEL=chrome` |
| SIPAF running in IIS Express | Start the site **first**, from Visual Studio or with the `iisexpress.exe` command line (equivalent, see [First run](#first-run)); netman does not start it for you |
| Node.js | Optional. The portable package bundles it; otherwise the ConfigTool downloads a portable copy on first start |
| Visual Studio / MSBuild | Only for methods 3 and 4 (building from source) |

No administrator rights are needed for any method.

## Installation

Pick one method. Methods 1 and 2 use a ready-made ZIP from the
[Releases page](https://github.com/Johans1047/netman/releases); methods 3 and 4 build from this repository.

| Method | Best for | Internet needed | Visual Studio needed |
|---|---|---|---|
| [1. Portable ZIP](#1-portable-zip-recommended) | Most people; offline or locked-down machines | No | No |
| [2. Standard ZIP](#2-standard-zip) | A smaller download when you already have Node.js | First start only (if no Node) | No |
| [3. Build your own ZIP](#3-build-your-own-zip) | Handing a package to a team | Yes (NuGet, and Node for `-Portable`) | Yes |
| [4. Run from source](#4-run-from-source) | Developing netman itself | Yes (NuGet, npm) | Yes |

### 1. Portable ZIP (recommended)

`netman-<version>-<date>-portable.zip` (~45 MB) includes the compiled tools, a portable Node.js and Playwright
already installed.

1. Download the `...-portable.zip` asset from the [Releases page](https://github.com/Johans1047/netman/releases).
2. *(Optional but avoids security warnings)* Right-click the ZIP -> **Properties** -> check **Unblock** -> OK.
3. Extract it to a folder you own, for example `C:\Tools\netman`. Do not extract into `Program Files`.
4. Right-click `Install-Netman.ps1` -> **Run with PowerShell**.
   If Windows blocks the script, open PowerShell in that folder and run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-Netman.ps1
   ```
5. Open **netman** from the Desktop shortcut (or run `Abrir-NetmanConfig.cmd`), then follow
   [First run](#first-run).

### 2. Standard ZIP

`netman-<version>-<date>.zip` (< 1 MB) contains only netman's own files.

1. Download the plain `.zip` asset (the one **without** `-portable`) from the
   [Releases page](https://github.com/Johans1047/netman/releases).
2. Extract and run `Install-Netman.ps1` exactly as in steps 2-4 of method 1.
3. The installer looks for Node.js in this order:
   1. A portable copy at `tools\node-portable` (not present in this package).
   2. Node.js on your `PATH`. If found, the installer runs `npm install` to get Playwright.
   3. Neither: nothing is installed yet. The **ConfigTool downloads a portable Node.js and installs Playwright**
      the first time you press *Guardar e iniciar netman*. This needs internet access.

   Browsers are not downloaded: netman uses the Edge (or Chrome) already on your machine.
4. Open **netman** from the Desktop and follow [First run](#first-run).

### 3. Build your own ZIP

Run this on a developer machine with Visual Studio (or the Build Tools). `Build-Package.ps1` restores NuGet
packages, compiles the Host and the ConfigTool in Release, and assembles everything in `dist\`.

```powershell
git clone https://github.com/Johans1047/netman.git
cd netman

# Standard package -> dist\netman-<version>-<date>.zip
.\Build-Package.ps1

# Portable package (downloads the latest Node.js LTS and runs npm install) -> ...-portable.zip
.\Build-Package.ps1 -Portable

# Pin a Node.js version, or keep the folder without zipping
.\Build-Package.ps1 -Portable -NodeVersion 22.11.0
.\Build-Package.ps1 -NoZip
```

The script finds MSBuild through `vswhere`, then falls back to MSBuild 12.0 / 14.0. `<version>` comes from
`git describe --tags --always --dirty`. Give the resulting ZIP to whoever needs it; they install it with
method 1 or 2.

### 4. Run from source

For working on netman itself. The tools are compiled in place and run from the repository folder.

1. Clone the repository and open it in Visual Studio, or use MSBuild from a *Developer Command Prompt*:
   ```powershell
   git clone https://github.com/Johans1047/netman.git
   ```
2. If NuGet does not restore packages on its own (empty `packages\` folder), restore them with
   [`nuget.exe`](https://www.nuget.org/downloads):
   ```powershell
   nuget restore src\Host\packages.config   -SolutionDirectory . -Source https://api.nuget.org/v3/index.json
   nuget restore src\Worker\packages.config -SolutionDirectory . -Source https://api.nuget.org/v3/index.json
   ```
3. Build the two solutions:
   - `HotReloadTool.Product.sln` -> the Host and its libraries (no test projects).
   - `NetmanConfigTool.sln` -> the ConfigTool.

   Build the **Host in Debug**: the scripts, the ConfigTool and the browser script look for it at
   `src\Host\bin\Debug\HotReloadTool.Host.exe`. From the command line:
   ```powershell
   MSBuild src\Host\HotReloadTool.Host.csproj /p:Configuration=Debug
   MSBuild src\ConfigTool\NetmanConfigTool.csproj /p:Configuration=Release
   ```
4. Run the ConfigTool: `src\ConfigTool\bin\Release\NetmanConfigTool.exe` (or `Abrir-NetmanConfig.cmd`, which
   opens that path). On first start it installs Playwright, and a portable Node.js if none is found.
5. Fill in your settings in the GUI; it creates `netman.config.json` for you. To prepare it by hand instead,
   copy `netman.config.example.json` to `netman.config.json` and edit the paths.

#### Command-line alternatives to the GUI

Start the site in IIS Express first (Visual Studio or the `iisexpress.exe` command, see [First run](#first-run)), then use either:

```powershell
# One window: prompts for credentials, starts the Host (own window) and the browser script
.\Start-Netman.ps1
.\Start-Netman.ps1 -Manual -Url "http://localhost:12345/SIPAF/wPerfiles.aspx"   # log in by hand
```

```batch
:: Two windows
bin\netman-sipaf.cmd                 :: terminal 1: watcher (detects changes, builds, deploys, recycles)
node netman-hotreload-browser.js     :: terminal 2: browser with auto-login and reload
```

Credentials are read from `NETMAN_SIPAF_USER` / `NETMAN_SIPAF_PASS`.

#### Running the tests (optional)

`HotReloadTool.sln` includes the xUnit test projects. Restore the extra `packages.config` files of
`tests\Common.Tests`, `tests\Host.Tests`, `tests\Host.Tests\Fixtures` and `tests\Worker.Tests` with the same
`nuget restore ... -Source` command, build the solution, then:

```powershell
packages\xunit.runner.console.2.3.1\tools\net452\xunit.console.exe tests\Common.Tests\bin\Debug\HotReloadTool.Common.Tests.dll
```

## First run

1. **Start the SIPAF site first.** netman needs the site already being served by IIS Express; it does not start
   it for you. There are two ways, and they are **equivalent**: both launch the same IIS Express server for the
   same site (the `SIPAF-Site` binding, `http://localhost:12345/SIPAF`).

   | Way | How |
   |---|---|
   | Visual Studio | Open the SIPAF site project and run it (**Ctrl+F5**). Visual Studio starts IIS Express for you. |
   | Command line | `& 'C:\Program Files\IIS Express\iisexpress.exe' /site:SIPAF-Site` |

   The command line is simply a way to start the site without opening Visual Studio: the result is the same as
   running the site from Visual Studio. Keep that console open while you work (Ctrl+C stops the server). The
   `SIPAF-Site` binding must exist in
   `%USERPROFILE%\Documents\IISExpress\config\applicationhost.config`, which is where Visual Studio keeps it too.
2. Open the ConfigTool and fill in:

   | Field | Example |
   |---|---|
   | SIPAF user | your SIPAF login |
   | SIPAF password | stored in **Windows Credential Manager** (target `netman:sipaf`), never in a file |
   | LibreriaSipaf project (`.vbproj`) | `C:\Proyecto_VS2013\SIPAF\LibreriaSIPAF\LibreriaSIPAF.vbproj` |
   | Site folder | `C:\Proyecto_VS2013\SIPAF\SIPAF` |
   | Base URL | `http://localhost:12345/SIPAF/` (default) |

3. Press **Probar sitio** to check that IIS Express answers, then **Guardar e iniciar netman**.
4. If you start the site from Visual Studio, use **Ctrl+F5** ("Start without debugging"), not F5: stopping a debug
   session can kill netman's processes. Starting it from the command line avoids the issue.

Settings (everything except the password) are saved to `netman.config.json` next to the tool. That file is local
to each machine and is not committed.

## Optional environment variables

| Variable | Purpose |
|---|---|
| `NETMAN_SITE_URL` | Site URL (default comes from `netman.config.json`) |
| `NETMAN_BROWSER_CHANNEL` | `msedge` (default) or `chrome` |
| `NETMAN_DOWNLOADS_DIR` | Download folder for netman's browser |
| `NETMAN_DIALOGS` | `accept` accepts every JS dialog. By default `confirm`/`prompt` are cancelled so a delete is never approved by accident |
| `NETMAN_LIBRARY_PROJECT`, `NETMAN_WEB_DIR` | Override the library project / site folder for `bin\netman-sipaf.cmd` |

## Stopping and uninstalling

- **Stop:** press **Detener** in the ConfigTool (or close its window). The Host, Node and the browser are closed
  with it.
- **Uninstall:** delete the netman folder and the `netman` shortcut on the Desktop. To remove the saved password:
  ```powershell
  cmdkey /delete:netman:sipaf
  ```

## Troubleshooting

- **`ERR_CONNECTION_REFUSED` or "Probar sitio" fails:** IIS Express is not running. Start the site first.
- **PowerShell refuses to run `Install-Netman.ps1`:** use the `-ExecutionPolicy Bypass` command from method 1.
- **No Edge or Chrome found:** install Edge, or set `NETMAN_BROWSER_CHANNEL=chrome` if you use Chrome.
- **Odd behaviour with several watchers:** make sure only one `HotReloadTool.Host` is running (an old one may
  remain from the GUI plus another from the `.cmd`).

More in [`docs/cold-start.md`](docs/cold-start.md).
