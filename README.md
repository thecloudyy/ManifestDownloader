# ManifestDownloader

Standalone UI port of the Steam Manifest Downloader script: downloads depot
manifests for SteamTools into Steam's `depotcache` in one click. Separate from
the Element app — own project, own single exe. UI uses the same stack as
Element (WPF + WPF-UI, dark theme, Mica).

## Download

Grab the latest release from [GitHub Releases](https://github.com/thecloudyy/ManifestDownloader/releases/latest):

- `ManifestDownloader-Setup-win-x64.exe` — per-user setup installer, no admin rights (requires .NET 8 Desktop Runtime)
- `ManifestDownloader.exe` — portable single-file build (requires .NET 8 Desktop Runtime)

There are no version checks anywhere: the in-app **Reinstall** button always
downloads the latest release from GitHub, replaces the running exe, and
restarts — no manual re-download needed.

## What it does

1. Shows your game library (from installed luas, with names + covers like
   Element) — multi-select games with Ctrl/Shift-click, All/None buttons,
   Ctrl+A, search-as-you-type, or double-click a game to download it
   immediately. Download fetches manifests for every selected game in one run.
2. Finds Steam (same registry method as Element), reads
   `config\stplug-in\<appid>.lua` and parses `addappid(depot, …, "key")` lines.
3. Resolves each depot's public manifest id via the SteamCMD API — **all
   selected games resolve concurrently**.
4. Downloads `<depot>_<manifest>.manifest` through whichever **sources you
   enable** — each is an independent toggle, tried in a fixed order:
   **GitHub mirror → ManifestDeX → fallback API**. Turn GitHub off for a
   ManifestDeX-only run, turn everything off but the API for an API-only run,
   or combine them. Up to **4 depots download in parallel**, each with its own
   progress bar and live speed.
5. Writes to a `.part` sidecar and renames on success, so a crash or cancel can
   never leave a half-written file behind. **Skips depots that are already
   present**: if `<depot>_<manifest>.manifest` exists in `depotcache` or
   `config\depotcache`, that depot is not downloaded again, so re-runs (and
   multi-game runs) only fetch what is missing. Existing files are left
   untouched; queued rows show it as `exists — skipped`. Tick **Force
   re-download** to bypass that check and fetch every selected depot again
   (overwrites the existing files; the checkbox is remembered between runs).
6. Remembers source toggles, last AppID, **window size**, and the API key
   (DPAPI-encrypted) between runs. Env vars still override on startup.
7. **Reinstall button** always pulls the latest release from GitHub, swaps the
   running exe (portable or installed), and restarts the app.

## UI

- Card-based layout: library panel on the left, source/actions/progress,
  queue, and log on the right, with a status bar (Steam path, source).
- Dark theme throughout (fixed — no toggle).
- Queue table shows game name, depot, manifest, a per-row progress bar, and a
  color-coded status (queued / downloading / ok / exists — skipped / failed /
  unavailable).
- Progress card with overall bar, percent, aggregate status line
  (`n/m done · k active — depot … @ speed`), elapsed timer, and summary;
  a result InfoBar reports success/failure counts.
- Log gets `[HH:mm:ss]` timestamps, color per severity, and **Copy/Clear**
  buttons; queue and log panes resize with a splitter.
- Library rows show cover, name, depot count and AppID, a check badge on
  selection, hover/selection styling, and an empty-state message. Games with
  no CDN art get a generated gradient monogram tile. Header actions (All /
  None / rescan) are slim chip-style buttons.
- Unhandled exceptions are appended to
  `%APPDATA%\ManifestDownloader\crash.log` instead of vanishing.

### Shortcuts

| Key | Action |
| --- | --- |
| `Enter` (in AppID box) | Start download |
| `F5` | Rescan library |
| `Ctrl+F` | Focus library search |
| `Esc` | Cancel running download |
| `Ctrl+A` / double-click | Select all / download one game |

## Requirements

- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- Steam installed

## Build from source

Requires .NET 8 SDK. Produces a single framework-dependent exe (needs the
.NET 8 Desktop Runtime), then packages the per-user setup (paths to
`ISCC.exe` vary by install):

```powershell
dotnet publish ManifestDownloader.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
```

Outputs: `dist\ManifestDownloader.exe` (portable) and
`dist\ManifestDownloader-Setup-win-x64.exe` (installer).

## Run

```powershell
.\dist\ManifestDownloader.exe
```

Optional env vars (same names as the script): `MANIFEST_MODE` accepts a source
list separated by `+` or `,` — presets (`github`, `github+hubcap`,
`github+manifesthub`, `github+manifestdex`, legacy `github+morrenus`) and
combinations like `manifestdex` (ManifestDeX-only), `github,manifestdex` or
`github+manifestdex+hubcap`; `HUBCAP_API_KEY` (legacy `MORRENUS_API_KEY`),
`MH_API_KEY`, `APP_ID` — prefill the matching fields on startup.

## Download sources

Source selection is two checkboxes plus a fallback-API choice, persisted
between runs:

| Source | Toggle | Key needed | Notes |
| --- | --- | --- | --- |
| GitHub mirror | `GitHub mirror` | no | plain `<depot>_<manifest>.manifest` files |
| ManifestDeX | `ManifestDeX (keyless)` | no | [code API](https://manifest.manifestdex.com/) → Steam CDN (`depot/{d}/manifest/{m}/5/{code}`, zip container) |
| Hubcap | Fallback API: `Hubcap` | `smm_…` key | [hubcapmanifest.com](https://hubcapmanifest.com) API (formerly Morrenus) |
| ManifestHub | Fallback API: `ManifestHub` | API key | [manifesthub2.filegear-sg.me](https://manifesthub2.filegear-sg.me) API |

Attempt order is always GitHub → ManifestDeX → fallback API (disabled sources
are skipped; with GitHub off you get ManifestDeX-only). ManifestDeX requests
send `User-Agent: ManifestDeX/1.0`, honour the 60 req/min limit (global 1.1 s
spacing + `Retry-After` on 429) and take the CDN host from the `x-manifest-cdn`
response header.
