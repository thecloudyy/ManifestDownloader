# ManifestDownloader

Downloads Steam depot manifests into Steam's `depotcache` in one click.

Desktop app built with [Tauri](https://tauri.app/) — a Rust backend and a plain
HTML/CSS front end in the Windows WebView2 runtime. No .NET runtime required.

## Download

Grab the latest release from [GitHub Releases](https://github.com/thecloudyy/ManifestDownloader/releases/latest):

- `ManifestDownloader-Setup-win-x64.exe` — per-user setup installer, no admin rights
- `ManifestDownloader.exe` — portable single-file build

Both need the WebView2 runtime (preinstalled on Windows 11 and current Windows 10) and Steam.

## What it does

1. Lists your installed luas from `config\stplug-in` and shows depot counts.
2. Reads each lua's `addappid(depot, ..., "key")` lines and resolves the public
   manifest id for every depot through the SteamCMD API.
3. Downloads `<depot>_<manifest>.manifest` from the sources you enable, in
   attempt order: **GitHub mirror → ManifestDeX → fallback API**
   (Hubcap or ManifestHub). Up to 4 depots download in parallel.
4. **Skips depots that are already present**: if the manifest exists in
   `depotcache` or `config\depotcache`, that depot is not fetched again. Tick
   **Force re-download** to overwrite existing manifests instead.
5. Writes to a `.part` sidecar and renames on success, so a crash or cancel
   cannot leave a half-written manifest behind.
6. Remembers source selection, force mode, last AppID and the API key
   (DPAPI-encrypted for the current Windows user) between runs.
7. On startup it asks GitHub for the latest release and, when a newer version
   exists, shows an update dialog — **Update now** downloads the new exe, swaps
   it in and restarts the app. **Check updates** in the header re-runs the check.

## UI

- Dark, flat layout: library on the left, sources/actions/queue/log on the right.
- Multi-select games in the library, filter by name or AppID, or type an AppID directly.
- Queue table with per-depot status (queued / downloading / ok / exists - skipped /
  failed / unavailable) and a progress bar per row.
- Log with severity colours, Copy and Clear.

## Requirements

- Windows 10/11 x64
- WebView2 runtime
- Steam installed

## Sources

| Source | Toggle | Key needed | Notes |
| --- | --- | --- | --- |
| GitHub mirror | `GitHub mirror` | no | plain `<depot>_<manifest>.manifest` files |
| ManifestDeX | `ManifestDeX (no key)` | no | code API → Steam CDN, zip container |
| Hubcap | Fallback API: `Hubcap` | `smm_…` key | hubcapmanifest.com API |
| ManifestHub | Fallback API: `ManifestHub` | API key | manifesthub2.filegear-sg.me API |

## Build from source

Requires the Rust toolchain. The front end is static files in `ui/`, embedded
into the executable at build time.

```powershell
cargo build --release --manifest-path src-tauri\Cargo.toml
copy src-tauri\target\release\manifest-downloader.exe dist\ManifestDownloader.exe
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer.iss
```

Outputs: `dist\ManifestDownloader.exe` (portable) and
`dist\ManifestDownloader-Setup-win-x64.exe` (installer).

## Run

```powershell
.\dist\ManifestDownloader.exe
```

## Layout

- `src-tauri/` — Rust backend: Steam detection, lua parsing, app info lookup,
  source downloads, depotcache checks, settings, and Tauri commands.
- `ui/` — `index.html`, `style.css`, `app.js`: the window content.
- `installer.iss` — Inno Setup script for the per-user setup.
