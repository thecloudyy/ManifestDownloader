#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, LazyLock, Mutex};
use std::time::{Duration, Instant};

use regex::Regex;
use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, Manager, State};
use tokio::io::AsyncWriteExt;
use tokio::sync::Semaphore;

const USER_AGENT: &str = "ManifestDownloader/1.0";
const DEX_INTERVAL: Duration = Duration::from_millis(1100);
const WORKERS: usize = 4;

static ADDAPPID: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?i)addappid\(\s*(\d+)\s*,\s*\d+\s*,\s*"[a-f0-9]+""#).unwrap()
});
static HUBCAP_KEY: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^smm_[0-9a-f]{96}$").unwrap());
static DEX_GATE: Mutex<Option<Instant>> = Mutex::new(None);

struct AppState {
    client: reqwest::Client,
    cancel: Mutex<Option<Arc<AtomicBool>>>,
    running: AtomicBool,
}

#[derive(Clone, Serialize)]
struct LogEvent {
    level: String,
    text: String,
}

#[derive(Clone, Serialize)]
struct RowEvent {
    id: u64,
    app: String,
    depot: String,
    manifest: String,
    status: String,
    progress: f64,
}

#[derive(Clone, Serialize)]
struct SummaryEvent {
    ok: u32,
    skipped: u32,
    failed: u32,
    bytes: u64,
    seconds: u64,
    cancelled: bool,
}

#[derive(Clone)]
struct Job {
    id: u64,
    app_id: String,
    app_name: String,
    depot: String,
    manifest: String,
}

#[derive(Clone, Deserialize)]
struct SourcesArg {
    github: bool,
    manifestdex: bool,
    api: String,
    api_key: String,
}

#[derive(Serialize)]
struct Game {
    app_id: String,
    name: String,
    depots: usize,
}

#[derive(Clone, Serialize, Deserialize)]
struct Stored {
    github: bool,
    manifestdex: bool,
    api_source: String,
    force: bool,
    last_app_id: String,
    protected_key: Option<String>,
}

impl Default for Stored {
    fn default() -> Self {
        Stored {
            github: true,
            manifestdex: true,
            api_source: "none".to_string(),
            force: false,
            last_app_id: String::new(),
            protected_key: None,
        }
    }
}

#[derive(Serialize)]
struct SettingsOut {
    github: bool,
    manifestdex: bool,
    api_source: String,
    force: bool,
    last_app_id: String,
    api_key: String,
    remember_key: bool,
}

#[derive(Deserialize)]
struct SettingsIn {
    github: bool,
    manifestdex: bool,
    api_source: String,
    force: bool,
    last_app_id: String,
    api_key: String,
    remember_key: bool,
}

fn app_dir() -> PathBuf {
    let base = std::env::var("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from("."));
    base.join("ManifestDownloader")
}

fn settings_path() -> PathBuf {
    app_dir().join("settings.json")
}

fn emit_log(app: &AppHandle, level: &str, text: impl Into<String>) {
    let _ = app.emit(
        "log",
        LogEvent {
            level: level.to_string(),
            text: text.into(),
        },
    );
}

fn emit_row(app: &AppHandle, job: &Job, status: &str, progress: f64) {
    let _ = app.emit(
        "row",
        RowEvent {
            id: job.id,
            app: job.app_id.clone(),
            depot: job.depot.clone(),
            manifest: job.manifest.clone(),
            status: status.to_string(),
            progress,
        },
    );
}

fn emit_status(app: &AppHandle, text: impl Into<String>) {
    let _ = app.emit("status", text.into());
}

fn emit_row_parts(app: &AppHandle, id: u64, app_id: &str, depot: &str, manifest: &str, status: &str, progress: f64) {
    let _ = app.emit(
        "row",
        RowEvent {
            id,
            app: app_id.to_string(),
            depot: depot.to_string(),
            manifest: manifest.to_string(),
            status: status.to_string(),
            progress,
        },
    );
}

fn fmt_size(bytes: u64) -> String {
    if bytes >= 1024 * 1024 {
        format!("{:.2} MB", bytes as f64 / (1024.0 * 1024.0))
    } else if bytes >= 1024 {
        format!("{:.1} KB", bytes as f64 / 1024.0)
    } else {
        format!("{bytes} B")
    }
}

fn urlencode(value: &str) -> String {
    value
        .bytes()
        .map(|b| {
            if b.is_ascii_alphanumeric() || b == b'_' || b == b'-' || b == b'.' {
                (b as char).to_string()
            } else {
                format!("%{b:02X}")
            }
        })
        .collect()
}

#[cfg(windows)]
mod dpapi {
    use base64::Engine;
    use windows_sys::Win32::Foundation::LocalFree;
    use windows_sys::Win32::Security::Cryptography::{
        CryptProtectData, CryptUnprotectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
    };

    fn blob(data: &mut [u8]) -> CRYPT_INTEGER_BLOB {
        CRYPT_INTEGER_BLOB {
            cbData: data.len() as u32,
            pbData: data.as_mut_ptr(),
        }
    }

    pub fn protect(plain: &str) -> Option<String> {
        let mut bytes = plain.as_bytes().to_vec();
        let mut input = blob(&mut bytes);
        let mut out = CRYPT_INTEGER_BLOB {
            cbData: 0,
            pbData: std::ptr::null_mut(),
        };
        let ok = unsafe {
            CryptProtectData(
                &input,
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut out,
            )
        };
        if ok == 0 || out.pbData.is_null() {
            return None;
        }
        let enc = unsafe { std::slice::from_raw_parts(out.pbData, out.cbData as usize).to_vec() };
        unsafe {
            LocalFree(out.pbData as *mut _);
        }
        Some(base64::engine::general_purpose::STANDARD.encode(enc))
    }

    pub fn unprotect(b64: &str) -> Option<String> {
        let mut enc = base64::engine::general_purpose::STANDARD.decode(b64).ok()?;
        let mut input = blob(&mut enc);
        let mut out = CRYPT_INTEGER_BLOB {
            cbData: 0,
            pbData: std::ptr::null_mut(),
        };
        let ok = unsafe {
            CryptUnprotectData(
                &input,
                std::ptr::null_mut(),
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut out,
            )
        };
        if ok == 0 || out.pbData.is_null() {
            return None;
        }
        let dec = unsafe { std::slice::from_raw_parts(out.pbData, out.cbData as usize).to_vec() };
        unsafe {
            LocalFree(out.pbData as *mut _);
        }
        String::from_utf8(dec).ok()
    }
}

fn detect_steam() -> Option<PathBuf> {
    let candidates = [
        (
            winreg::enums::HKEY_CURRENT_USER,
            r"SOFTWARE\Valve\Steam",
            "SteamPath",
        ),
        (
            winreg::enums::HKEY_LOCAL_MACHINE,
            r"SOFTWARE\WOW6432Node\Valve\Steam",
            "InstallPath",
        ),
        (
            winreg::enums::HKEY_LOCAL_MACHINE,
            r"SOFTWARE\Valve\Steam",
            "InstallPath",
        ),
    ];
    for (hive, sub, value) in candidates {
        if let Ok(key) = winreg::RegKey::predef(hive).open_subkey(sub) {
            if let Ok(raw) = key.get_value::<String, _>(value) {
                let path = PathBuf::from(raw.trim().replace('/', "\\"));
                if path.join("steam.exe").exists() {
                    return Some(path);
                }
            }
        }
    }
    None
}

fn read_lua(path: &Path) -> (Option<String>, usize) {
    let text = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(_) => return (None, 0),
    };
    let mut depots: HashSet<String> = HashSet::new();
    for caps in ADDAPPID.captures_iter(&text) {
        if let Some(m) = caps.get(1) {
            depots.insert(m.as_str().to_string());
        }
    }
    let mut lines = text.lines();
    let first = lines.next().unwrap_or("").to_string();
    let second = lines.next().unwrap_or("").to_string();
    let mut name: Option<String> = None;
    const MARKER: &str = "-- app name:";
    let lower = first.to_lowercase();
    if let Some(pos) = lower.find(MARKER) {
        let value = first[pos + MARKER.len()..].trim();
        if !value.is_empty() {
            name = Some(value.to_string());
        }
    }
    if name.is_none() && lower.contains("created by") && second.starts_with("--") {
        let value = second.trim_start_matches(['-', ' ']).trim();
        if !value.is_empty() {
            name = Some(value.to_string());
        }
    }
    (name, depots.len())
}

fn has_manifest(steam: &Path, depot: &str, manifest: &str) -> bool {
    let file = format!("{depot}_{manifest}.manifest");
    for dir in [steam.join("depotcache"), steam.join("config").join("depotcache")] {
        let path = dir.join(&file);
        if let Ok(meta) = std::fs::metadata(&path) {
            if meta.is_file() && meta.len() > 0 {
                return true;
            }
        }
    }
    false
}

async fn fetch_app_info(
    client: &reqwest::Client,
    app_id: &str,
) -> Result<serde_json::Value, String> {
    let mut last = String::from("SteamCMD API failed");
    for attempt in 0..3 {
        let request = client
            .get(format!("https://api.steamcmd.net/v1/info/{app_id}"))
            .send();
        match tokio::time::timeout(Duration::from_secs(30), request).await {
            Err(_) => last = "SteamCMD API timed out".to_string(),
            Ok(Err(e)) => last = format!("SteamCMD API: {e}"),
            Ok(Ok(res)) => {
                let status = res.status();
                if !status.is_success() {
                    last = format!("SteamCMD API {status}");
                } else {
                    match res.text().await {
                        Ok(body) => match serde_json::from_str::<serde_json::Value>(&body) {
                            Ok(value) => {
                                let ok = value
                                    .get("status")
                                    .and_then(|s| s.as_str())
                                    .map(|s| s.eq_ignore_ascii_case("success"))
                                    .unwrap_or(false);
                                if ok {
                                    return Ok(value);
                                }
                                last = "SteamCMD API returned no data".to_string();
                            }
                            Err(e) => last = format!("SteamCMD API response: {e}"),
                        },
                        Err(e) => last = format!("SteamCMD API: {e}"),
                    }
                }
            }
        }
        if attempt < 2 {
            tokio::time::sleep(Duration::from_secs(2)).await;
        }
    }
    Err(last)
}

fn public_manifest(info: &serde_json::Value, app_id: &str, depot: &str) -> Option<String> {
    info.get("data")?
        .get(app_id)?
        .get("depots")?
        .get(depot)?
        .get("manifests")?
        .get("public")?
        .get("gid")?
        .as_str()
        .map(|s| s.to_string())
}

async fn validate_hubcap(client: &reqwest::Client, key: &str) -> (bool, String) {
    let url = format!(
        "https://hubcapmanifest.com/api/v1/user/stats?api_key={}",
        urlencode(key)
    );
    let res = match tokio::time::timeout(Duration::from_secs(15), client.get(&url).send()).await {
        Err(_) => return (false, "Key check timed out - check your connection.".to_string()),
        Ok(Err(e)) => return (false, format!("Failed to validate key: {e}")),
        Ok(Ok(r)) => r,
    };
    let status = res.status();
    let body = res.text().await.unwrap_or_default();
    if !status.is_success() {
        if let Ok(value) = serde_json::from_str::<serde_json::Value>(&body) {
            if let Some(detail) = value.get("detail").and_then(|d| d.as_str()) {
                return (false, detail.to_string());
            }
        }
        return (
            false,
            if matches!(status.as_u16(), 401 | 403 | 404) {
                "API key not found or expired.".to_string()
            } else {
                format!("Key check failed: {status}")
            },
        );
    }
    let value: serde_json::Value = match serde_json::from_str(&body) {
        Ok(v) => v,
        Err(_) => return (false, "Unexpected response from Hubcap.".to_string()),
    };
    if value
        .get("can_make_requests")
        .and_then(|c| c.as_bool())
        .map(|c| !c)
        .unwrap_or(false)
    {
        let usage = value.get("daily_usage").map(|v| v.to_string()).unwrap_or_default();
        let limit = value.get("daily_limit").map(|v| v.to_string()).unwrap_or_default();
        return (false, format!("Key hit its daily limit ({usage}/{limit})."));
    }
    let user = value.get("username").and_then(|u| u.as_str()).unwrap_or("user");
    (true, format!("Hubcap key OK: {user}"))
}

async fn dex_code(
    client: &reqwest::Client,
    manifest: &str,
    cancel: &Arc<AtomicBool>,
) -> Option<(String, Option<String>)> {
    if manifest.parse::<u64>().ok()? == 0 {
        return None;
    }
    for _ in 0..3 {
        if cancel.load(Ordering::Relaxed) {
            return None;
        }
        let wait = {
            let mut gate = DEX_GATE.lock().unwrap();
            let now = Instant::now();
            let wait = gate
                .map(|t| DEX_INTERVAL.saturating_sub(now.saturating_duration_since(t)))
                .unwrap_or_default();
            *gate = Some(now);
            wait
        };
        if !wait.is_zero() {
            tokio::time::sleep(wait).await;
        }
        let request = client
            .get(format!("https://manifest.manifestdex.com/{manifest}"))
            .header("User-Agent", "ManifestDeX/1.0")
            .send();
        let res = match request.await {
            Ok(r) => r,
            Err(_) => {
                tokio::time::sleep(Duration::from_secs(2)).await;
                continue;
            }
        };
        if res.status().is_success() {
            let cdn = res
                .headers()
                .get("x-manifest-cdn")
                .and_then(|v| v.to_str().ok())
                .map(|s| s.trim().trim_end_matches('/').to_string())
                .filter(|s| !s.is_empty());
            let body = res.text().await.ok()?.trim().to_string();
            if body.parse::<u64>().map(|c| c != 0).unwrap_or(false) {
                return Some((body, cdn));
            }
            return None;
        }
        if res.status() == reqwest::StatusCode::TOO_MANY_REQUESTS {
            let secs = res
                .headers()
                .get("retry-after")
                .and_then(|v| v.to_str().ok())
                .and_then(|s| s.parse::<u64>().ok())
                .unwrap_or(10)
                .clamp(1, 30);
            tokio::time::sleep(Duration::from_secs(secs)).await;
            continue;
        }
        if matches!(res.status().as_u16(), 502 | 504) {
            tokio::time::sleep(Duration::from_secs(2)).await;
            continue;
        }
        return None;
    }
    None
}

async fn fetch_to_file(
    app: &AppHandle,
    client: &reqwest::Client,
    url: &str,
    path: &Path,
    job: &Job,
    cancel: &Arc<AtomicBool>,
) -> Result<u64, (bool, String)> {
    let mut res = client
        .get(url)
        .send()
        .await
        .map_err(|e| (false, e.to_string()))?;
    if res.status() == reqwest::StatusCode::NOT_FOUND {
        return Err((true, "Not found (404)".to_string()));
    }
    if !res.status().is_success() {
        return Err((false, format!("HTTP {}", res.status())));
    }
    let total = res.content_length();
    let mut file = tokio::fs::File::create(path)
        .await
        .map_err(|e| (false, e.to_string()))?;
    let mut done: u64 = 0;
    let mut last_emit = Instant::now();
    loop {
        if cancel.load(Ordering::Relaxed) {
            let _ = tokio::fs::remove_file(path).await;
            return Err((false, "Cancelled".to_string()));
        }
        let chunk = match tokio::time::timeout(Duration::from_secs(60), res.chunk()).await {
            Err(_) => return Err((false, "Download stalled: no data for 60 seconds.".to_string())),
            Ok(Err(e)) => return Err((false, e.to_string())),
            Ok(Ok(None)) => break,
            Ok(Ok(Some(chunk))) => chunk,
        };
        file.write_all(&chunk)
            .await
            .map_err(|e| (false, e.to_string()))?;
        done += chunk.len() as u64;
        if last_emit.elapsed() >= Duration::from_millis(150) {
            last_emit = Instant::now();
            let fraction = match total {
                Some(t) if t > 0 => done as f64 / t as f64,
                _ => -1.0,
            };
            emit_row(app, job, &format!("downloading {}", fmt_size(done)), fraction);
        }
    }
    let _ = file.flush().await;
    drop(file);
    if done == 0 {
        return Err((false, "Empty file received".to_string()));
    }
    Ok(done)
}

async fn try_url(
    app: &AppHandle,
    client: &reqwest::Client,
    url: &str,
    out: &Path,
    job: &Job,
    tries: u32,
    cancel: &Arc<AtomicBool>,
) -> Result<u64, String> {
    let tmp = PathBuf::from(format!("{}.part", out.display()));
    let mut last = String::from("Download failed.");
    for attempt in 1..=tries {
        if cancel.load(Ordering::Relaxed) {
            let _ = std::fs::remove_file(&tmp);
            return Err("Cancelled".to_string());
        }
        let _ = std::fs::remove_file(&tmp);
        match fetch_to_file(app, client, url, &tmp, job, cancel).await {
            Ok(size) => {
                std::fs::rename(&tmp, out).map_err(|e| e.to_string())?;
                return Ok(size);
            }
            Err((not_found, message)) => {
                last = message;
                if not_found {
                    break;
                }
                if attempt < tries {
                    tokio::time::sleep(Duration::from_secs(3)).await;
                }
            }
        }
    }
    let _ = std::fs::remove_file(&tmp);
    Err(last)
}

async fn dex_download(
    app: &AppHandle,
    client: &reqwest::Client,
    job: &Job,
    out: &Path,
    cancel: &Arc<AtomicBool>,
) -> Result<u64, String> {
    let (code, cdn) = dex_code(client, &job.manifest, cancel)
        .await
        .ok_or_else(|| "ManifestDeX: could not get a request code".to_string())?;
    let base = cdn.unwrap_or_else(|| "https://cache7-ord1.steamcontent.com".to_string());
    let url = format!(
        "{base}/depot/{}/manifest/{}/5/{code}",
        job.depot, job.manifest
    );
    let zip_path = PathBuf::from(format!("{}.zip", out.display()));
    let _ = std::fs::remove_file(&zip_path);
    if let Err(e) = try_url(app, client, &url, &zip_path, job, 3, cancel).await {
        let _ = std::fs::remove_file(&zip_path);
        return Err(e);
    }
    let part = PathBuf::from(format!("{}.part", out.display()));
    let result = (|| -> Result<u64, String> {
        let file = std::fs::File::open(&zip_path).map_err(|e| e.to_string())?;
        let mut archive = zip::ZipArchive::new(file).map_err(|e| e.to_string())?;
        if archive.is_empty() {
            return Err("ManifestDeX zip has no entries".to_string());
        }
        let mut entry = archive.by_index(0).map_err(|e| e.to_string())?;
        let mut dst = std::fs::File::create(&part).map_err(|e| e.to_string())?;
        std::io::copy(&mut entry, &mut dst).map_err(|e| e.to_string())?;
        drop(dst);
        let size = std::fs::metadata(&part).map(|m| m.len()).unwrap_or(0);
        if size == 0 {
            let _ = std::fs::remove_file(&part);
            return Err("ManifestDeX returned an empty manifest".to_string());
        }
        std::fs::rename(&part, out).map_err(|e| e.to_string())?;
        Ok(size)
    })();
    let _ = std::fs::remove_file(&zip_path);
    result
}

async fn download_manifest(
    app: &AppHandle,
    client: &reqwest::Client,
    job: &Job,
    out_dir: &Path,
    sources: &SourcesArg,
    cancel: &Arc<AtomicBool>,
) -> Result<u64, String> {
    let out = out_dir.join(format!("{}_{}.manifest", job.depot, job.manifest));
    let mut last = String::from("No download source enabled.");

    if sources.github {
        let url = format!(
            "https://raw.githubusercontent.com/qwe213312/k25FCdfEOoEJ42S6/main/{}_{}.manifest",
            job.depot, job.manifest
        );
        match try_url(app, client, &url, &out, job, 2, cancel).await {
            Ok(size) => return Ok(size),
            Err(e) => last = e,
        }
    }

    if sources.manifestdex {
        match dex_download(app, client, job, &out, cancel).await {
            Ok(size) => return Ok(size),
            Err(e) => last = e,
        }
    }

    if sources.api == "hubcap" || sources.api == "manifesthub" {
        let key = urlencode(&sources.api_key);
        let url = if sources.api == "hubcap" {
            format!(
                "https://hubcapmanifest.com/api/v1/generate/manifest?depot_id={}&manifest_id={}&api_key={key}",
                job.depot, job.manifest
            )
        } else {
            format!(
                "https://api.manifesthub2.filegear-sg.me/manifest?apikey={key}&depotid={}&manifestid={}",
                job.depot, job.manifest
            )
        };
        match try_url(app, client, &url, &out, job, 5, cancel).await {
            Ok(size) => return Ok(size),
            Err(e) => last = e,
        }
    }

    Err(last)
}

fn finish(
    app: &AppHandle,
    ok: u32,
    skipped: u32,
    failed: u32,
    bytes: u64,
    started: Instant,
    cancelled: bool,
) {
    let _ = app.emit(
        "summary",
        SummaryEvent {
            ok,
            skipped,
            failed,
            bytes,
            seconds: started.elapsed().as_secs(),
            cancelled,
        },
    );
    let _ = app.emit("finished", ());
}

async fn run_download(
    app: AppHandle,
    client: reqwest::Client,
    steam: PathBuf,
    app_ids: Vec<String>,
    sources: SourcesArg,
    force: bool,
    cancel: Arc<AtomicBool>,
) {
    let started = Instant::now();

    if !sources.github && !sources.manifestdex && sources.api == "none" {
        emit_log(
            &app,
            "err",
            "No source enabled. Turn on GitHub, ManifestDeX, or a fallback API.",
        );
        finish(&app, 0, 0, 0, 0, started, false);
        return;
    }

    let needs_key = sources.api == "hubcap" || sources.api == "manifesthub";
    if needs_key && sources.api_key.trim().is_empty() {
        emit_log(&app, "err", "This source needs an API key.");
        finish(&app, 0, 0, 0, 0, started, false);
        return;
    }

    if sources.api == "hubcap" {
        if !HUBCAP_KEY.is_match(sources.api_key.trim()) {
            emit_log(
                &app,
                "err",
                "Invalid Hubcap key format: expected smm_ followed by 96 hex characters.",
            );
            finish(&app, 0, 0, 0, 0, started, false);
            return;
        }
        emit_log(&app, "info", "Checking Hubcap key...");
        let (ok, message) = validate_hubcap(&client, sources.api_key.trim()).await;
        emit_log(&app, if ok { "ok" } else { "err" }, message);
        if !ok {
            finish(&app, 0, 0, 0, 0, started, false);
            return;
        }
    }

    emit_log(&app, "info", format!("Steam: {}", steam.display()));
    emit_status(&app, "Resolving depots...");
    if force {
        emit_log(
            &app,
            "warn",
            "Force re-download: manifests already in depotcache will be replaced.",
        );
    }
    let depot_cache = steam.join("depotcache");
    let _ = std::fs::create_dir_all(&depot_cache);

    let multi = app_ids.len() > 1;
    let mut queued: Vec<Job> = Vec::new();
    let mut skipped: u32 = 0;
    let mut next_id: u64 = 0;

    for app_id in &app_ids {
        if cancel.load(Ordering::Relaxed) {
            break;
        }
        let tag = if multi {
            format!("[{app_id}] ")
        } else {
            String::new()
        };
        let lua_path = steam
            .join("config")
            .join("stplug-in")
            .join(format!("{app_id}.lua"));
        if !lua_path.exists() {
            emit_log(&app, "err", format!("{tag}No lua for AppID {app_id}."));
            continue;
        }
        let depots = {
            let text = std::fs::read_to_string(&lua_path).unwrap_or_default();
            let mut seen = HashSet::new();
            let mut list = Vec::new();
            for caps in ADDAPPID.captures_iter(&text) {
                if let Some(m) = caps.get(1) {
                    if seen.insert(m.as_str().to_string()) {
                        list.push(m.as_str().to_string());
                    }
                }
            }
            list
        };
        if depots.is_empty() {
            emit_log(&app, "err", format!("{tag}No depot IDs in lua."));
            continue;
        }

        let app_name = read_lua(&lua_path).0.unwrap_or_else(|| format!("App {app_id}"));

        emit_status(&app, format!("{tag}Fetching app info..."));
        emit_log(&app, "info", format!("{tag}Fetching app info..."));
        let info = match fetch_app_info(&client, app_id).await {
            Ok(value) => value,
            Err(e) => {
                emit_log(&app, "err", format!("{tag}AppID {app_id}: {e}"));
                continue;
            }
        };

        let mut missing = Vec::new();
        for depot in &depots {
            if cancel.load(Ordering::Relaxed) {
                break;
            }
            let manifest = match public_manifest(&info, app_id, depot) {
                Some(gid) => gid,
                None => {
                    missing.push(depot.clone());
                    continue;
                }
            };
            if !force && has_manifest(&steam, depot, &manifest) {
                skipped += 1;
                emit_row_parts(
                    &app,
                    next_id,
                    app_id,
                    depot,
                    &manifest,
                    "exists - skipped",
                    1.0,
                );
                emit_log(
                    &app,
                    "ok",
                    format!("{tag}Depot {depot} already present: {depot}_{manifest}.manifest"),
                );
                next_id += 1;
                continue;
            }
            emit_row_parts(&app, next_id, app_id, depot, &manifest, "queued", 0.0);
            queued.push(Job {
                id: next_id,
                app_id: app_id.clone(),
                app_name: app_name.clone(),
                depot: depot.clone(),
                manifest,
            });
            next_id += 1;
        }
        if !missing.is_empty() {
            emit_log(
                &app,
                "warn",
                format!(
                    "{tag}{} depot(s) unavailable (no public manifest): {}",
                    missing.len(),
                    missing.join(", ")
                ),
            );
            for depot in missing {
                emit_row_parts(&app, next_id, app_id, &depot, "-", "unavailable", 0.0);
                next_id += 1;
            }
        }
    }

    if skipped > 0 {
        emit_log(&app, "ok", format!("{skipped} depot(s) already in depotcache, skipped."));
    }

    if queued.is_empty() {
        if skipped > 0 {
            emit_log(
                &app,
                "ok",
                format!("Nothing to download - all {skipped} depot(s) are already present."),
            );
        } else {
            emit_log(&app, "warn", "Nothing to download.");
        }
        finish(&app, 0, skipped, 0, 0, started, cancel.load(Ordering::Relaxed));
        return;
    }

    emit_log(
        &app,
        "info",
        format!("Downloading {} depot(s) to {}", queued.len(), depot_cache.display()),
    );
    let queue_total = queued.len();
    emit_status(&app, format!("Downloading {queue_total} depot(s)..."));

    let semaphore = Arc::new(Semaphore::new(WORKERS));
    let ok_count = Arc::new(AtomicU32::new(0));
    let fail_count = Arc::new(AtomicU32::new(0));
    let byte_count = Arc::new(AtomicU64::new(0));
    let mut handles = Vec::new();

    for job in queued {
        let app = app.clone();
        let client = client.clone();
        let depot_cache = depot_cache.clone();
        let sources = sources.clone();
        let cancel = cancel.clone();
        let semaphore = semaphore.clone();
        let ok_count = ok_count.clone();
        let fail_count = fail_count.clone();
        let byte_count = byte_count.clone();
        handles.push(tauri::async_runtime::spawn(async move {
            let permit = semaphore.clone().acquire_owned().await;
            if permit.is_err() {
                return;
            }
            if cancel.load(Ordering::Relaxed) {
                emit_row(&app, &job, "cancelled", 0.0);
                return;
            }
            emit_row(&app, &job, "downloading", 0.0);
            match download_manifest(&app, &client, &job, &depot_cache, &sources, &cancel).await {
                Ok(size) => {
                    ok_count.fetch_add(1, Ordering::Relaxed);
                    byte_count.fetch_add(size, Ordering::Relaxed);
                    emit_row(&app, &job, &format!("ok ({})", fmt_size(size)), 1.0);
                    let done = ok_count.load(Ordering::Relaxed) + fail_count.load(Ordering::Relaxed);
                    emit_status(&app, format!("Downloading {done}/{queue_total} depot(s)..."));
                }
                Err(error) => {
                    if cancel.load(Ordering::Relaxed) {
                        emit_row(&app, &job, "cancelled", 0.0);
                    } else {
                        fail_count.fetch_add(1, Ordering::Relaxed);
                        emit_row(&app, &job, "failed", 0.0);
                        let done = ok_count.load(Ordering::Relaxed) + fail_count.load(Ordering::Relaxed);
                        emit_status(&app, format!("Downloading {done}/{queue_total} depot(s)..."));
                        emit_log(
                            &app,
                            "err",
                            format!(
                                "{}Depot {} failed: {error}",
                                if multi {
                                    format!("[{}] ", job.app_id)
                                } else {
                                    String::new()
                                },
                                job.depot
                            ),
                        );
                    }
                }
            }
        }));
    }

    for handle in handles {
        let _ = handle.await;
    }

    let ok = ok_count.load(Ordering::Relaxed);
    let failed = fail_count.load(Ordering::Relaxed);
    let bytes = byte_count.load(Ordering::Relaxed);
    let cancelled = cancel.load(Ordering::Relaxed);
    let summary = if failed > 0 {
        format!(
            "Done: {ok} ok, {skipped} skipped, {failed} failed ({}).",
            fmt_size(bytes)
        )
    } else {
        format!("Done: {ok} ok, {skipped} skipped ({}).", fmt_size(bytes))
    };
    emit_log(&app, if failed > 0 { "err" } else { "ok" }, summary);
    finish(&app, ok, skipped, failed, bytes, started, cancelled);
}

#[tauri::command]
fn steam_dir() -> Option<String> {
    detect_steam().map(|p| p.display().to_string())
}

#[tauri::command]
async fn library(state: State<'_, AppState>, steam_dir: String) -> Result<Vec<Game>, String> {
    let dir = Path::new(&steam_dir).join("config").join("stplug-in");
    let mut entries: Vec<(String, Option<String>, usize)> = Vec::new();
    if let Ok(read) = std::fs::read_dir(dir) {
        for entry in read.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("lua") {
                continue;
            }
            let app_id = match path.file_stem().and_then(|s| s.to_str()) {
                Some(stem) if stem.chars().all(|c| c.is_ascii_digit()) => stem.to_string(),
                _ => continue,
            };
            let (name, depots) = read_lua(&path);
            entries.push((app_id, name, depots));
        }
    }

    // Lua headers often lack a name; the SteamCMD app info has one.
    let missing: Vec<String> = entries
        .iter()
        .filter(|(_, name, _)| name.is_none())
        .map(|(id, _, _)| id.clone())
        .collect();
    let mut names: HashMap<String, String> = HashMap::new();
    if !missing.is_empty() {
        let semaphore = Arc::new(Semaphore::new(6));
        let mut handles = Vec::new();
        for app_id in missing {
            let client = state.client.clone();
            let semaphore = semaphore.clone();
            handles.push(tauri::async_runtime::spawn(async move {
                let _permit = semaphore.acquire_owned().await;
                let name = app_info_name(&client, &app_id).await;
                (app_id, name)
            }));
        }
        for handle in handles {
            if let Ok((app_id, Some(name))) = handle.await {
                names.insert(app_id, name);
            }
        }
    }

    let mut games: Vec<Game> = entries
        .into_iter()
        .map(|(app_id, lua_name, depots)| {
            let name = names
                .get(&app_id)
                .cloned()
                .or(lua_name)
                .unwrap_or_else(|| format!("App {app_id}"));
            Game {
                app_id,
                name,
                depots,
            }
        })
        .collect();
    games.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
    Ok(games)
}

async fn app_info_name(client: &reqwest::Client, app_id: &str) -> Option<String> {
    let info = fetch_app_info(client, app_id).await.ok()?;
    info.get("data")?
        .get(app_id)?
        .get("common")?
        .get("name")?
        .as_str()
        .map(|s| s.to_string())
}

#[tauri::command]
fn open_folder(path: String) -> Result<(), String> {
    std::process::Command::new("explorer")
        .arg(path)
        .spawn()
        .map(|_| ())
        .map_err(|e| e.to_string())
}

#[tauri::command]
fn load_settings() -> SettingsOut {
    let stored: Stored = std::fs::read_to_string(settings_path())
        .ok()
        .and_then(|text| serde_json::from_str(&text).ok())
        .unwrap_or_default();
    let has_key = stored.protected_key.is_some();
    let api_key = stored
        .protected_key
        .as_deref()
        .and_then(|k| {
            #[cfg(windows)]
            {
                dpapi::unprotect(k)
            }
            #[cfg(not(windows))]
            {
                Some(k.to_string())
            }
        })
        .unwrap_or_default();
    SettingsOut {
        github: stored.github,
        manifestdex: stored.manifestdex,
        api_source: stored.api_source,
        force: stored.force,
        last_app_id: stored.last_app_id,
        api_key,
        remember_key: has_key,
    }
}

#[tauri::command]
fn save_settings(settings: SettingsIn) -> Result<(), String> {
    let protected = if settings.remember_key && !settings.api_key.trim().is_empty() {
        #[cfg(windows)]
        {
            dpapi::protect(settings.api_key.trim())
        }
        #[cfg(not(windows))]
        {
            Some(settings.api_key.trim().to_string())
        }
    } else {
        None
    };
    let stored = Stored {
        github: settings.github,
        manifestdex: settings.manifestdex,
        api_source: settings.api_source,
        force: settings.force,
        last_app_id: settings.last_app_id,
        protected_key: protected,
    };
    let dir = app_dir();
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let tmp = dir.join("settings.json.tmp");
    let text = serde_json::to_string(&stored).map_err(|e| e.to_string())?;
    std::fs::write(&tmp, text).map_err(|e| e.to_string())?;
    std::fs::rename(&tmp, settings_path()).map_err(|e| e.to_string())?;
    Ok(())
}

#[tauri::command]
async fn start_download(
    app: AppHandle,
    state: State<'_, AppState>,
    app_ids: Vec<String>,
    sources: SourcesArg,
    force: bool,
) -> Result<(), String> {
    let ids: Vec<String> = app_ids
        .into_iter()
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
        .collect();
    if ids.is_empty() {
        return Err("Enter at least one AppID.".to_string());
    }
    let steam = detect_steam().ok_or_else(|| "Could not find Steam.".to_string())?;
    if state.running.swap(true, Ordering::SeqCst) {
        return Err("A download is already running.".to_string());
    }
    let cancel = Arc::new(AtomicBool::new(false));
    *state.cancel.lock().unwrap() = Some(cancel.clone());
    let client = state.client.clone();
    let reset = app.clone();
    tauri::async_runtime::spawn(async move {
        run_download(app, client, steam, ids, sources, force, cancel).await;
        // Clear the in-progress flag so the next run is accepted.
        if let Some(state) = reset.try_state::<AppState>() {
            state.running.store(false, Ordering::SeqCst);
        }
    });
    Ok(())
}

#[derive(Serialize)]
struct UpdateInfo {
    version: String,
    url: String,
    page: String,
}

fn parse_version(text: &str) -> Option<(u64, u64, u64)> {
    let cleaned = text.trim().trim_start_matches(['v', 'V']);
    let core = cleaned.split(['-', '+']).next().unwrap_or(cleaned);
    let mut parts = core.split('.');
    let major = parts.next()?.parse().ok()?;
    let minor = parts.next().unwrap_or("0").parse().ok()?;
    let patch = parts.next().unwrap_or("0").parse().ok()?;
    Some((major, minor, patch))
}

#[tauri::command]
fn app_version() -> String {
    env!("CARGO_PKG_VERSION").to_string()
}

async fn fetch_update(client: &reqwest::Client) -> Option<UpdateInfo> {
    let request = client
        .get("https://api.github.com/repos/thecloudyy/ManifestDownloader/releases/latest")
        .header("Accept", "application/vnd.github+json")
        .send();
    let res = tokio::time::timeout(Duration::from_secs(15), request)
        .await
        .ok()?
        .ok()?;
    if !res.status().is_success() {
        return None; // offline or rate limited: stay quiet
    }
    let value: serde_json::Value = serde_json::from_str(&res.text().await.ok()?).ok()?;
    let tag = value.get("tag_name").and_then(|v| v.as_str())?;
    let latest = parse_version(tag)?;
    let current = parse_version(env!("CARGO_PKG_VERSION"))?;
    if latest <= current {
        return None;
    }
    let page = value
        .get("html_url")
        .and_then(|v| v.as_str())
        .unwrap_or("https://github.com/thecloudyy/ManifestDownloader/releases/latest")
        .to_string();
    let url = value
        .get("assets")
        .and_then(|a| a.as_array())
        .and_then(|items| {
            items
                .iter()
                .find(|a| {
                    a.get("name").and_then(|n| n.as_str()) == Some("ManifestDownloader.exe")
                })
                .and_then(|a| a.get("browser_download_url"))
                .and_then(|u| u.as_str())
        })
        .unwrap_or("https://github.com/thecloudyy/ManifestDownloader/releases/latest/download/ManifestDownloader.exe")
        .to_string();
    Some(UpdateInfo {
        version: tag.trim_start_matches(['v', 'V']).to_string(),
        url,
        page,
    })
}

#[tauri::command]
async fn check_update(state: State<'_, AppState>) -> Result<Option<UpdateInfo>, String> {
    Ok(fetch_update(&state.client).await)
}

#[tauri::command]
async fn apply_update(
    app: AppHandle,
    state: State<'_, AppState>,
    url: String,
) -> Result<(), String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let temp_exe = std::env::temp_dir().join("ManifestDownloader-update.exe");
    let res = state
        .client
        .get(&url)
        .send()
        .await
        .map_err(|e| e.to_string())?;
    if !res.status().is_success() {
        return Err(format!("Download failed: HTTP {}", res.status()));
    }
    let bytes = res.bytes().await.map_err(|e| e.to_string())?;
    if bytes.len() < 100_000 {
        return Err("The update download looks incomplete.".to_string());
    }
    std::fs::write(&temp_exe, &bytes).map_err(|e| e.to_string())?;

    // The running exe cannot be overwritten, so hand the swap to a detached
    // script that waits for this process to exit, copies the new build over
    // the old one and starts it again.
    let script = std::env::temp_dir().join("manifestdownloader-update.cmd");
    let body = format!(
        "@echo off\r\nping 127.0.0.1 -n 3 >nul\r\ncopy /y \"{}\" \"{}\" >nul\r\nstart \"\" \"{}\"\r\ndel \"%~f0\"\r\n",
        temp_exe.display(),
        exe.display(),
        exe.display()
    );
    std::fs::write(&script, body).map_err(|e| e.to_string())?;

    let mut command = std::process::Command::new("cmd");
    command.args(["/C", &script.to_string_lossy()]);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x0800_0000);
    }
    command.spawn().map_err(|e| e.to_string())?;

    app.exit(0);
    Ok(())
}

#[tauri::command]
fn cancel_download(state: State<'_, AppState>) {
    if let Some(flag) = state.cancel.lock().unwrap().as_ref() {
        flag.store(true, Ordering::Relaxed);
    }
}

fn main() {
    let client = reqwest::Client::builder()
        .user_agent(USER_AGENT)
        .timeout(Duration::from_secs(130))
        .build()
        .expect("http client");

    tauri::Builder::default()
        .manage(AppState {
            client,
            cancel: Mutex::new(None),
            running: AtomicBool::new(false),
        })
        .invoke_handler(tauri::generate_handler![
            steam_dir,
            library,
            open_folder,
            load_settings,
            save_settings,
            start_download,
            cancel_download,
            app_version,
            check_update,
            apply_update
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
