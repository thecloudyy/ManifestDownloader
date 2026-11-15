const invoke = window.__TAURI__.core.invoke;
const listen = window.__TAURI__.event.listen;

const state = {
  steam: null,
  games: [],
  selected: new Set(),
  rows: new Map(),
  running: false,
};

const $ = (id) => document.getElementById(id);
let saveTimer = null;

function addLog(level, text) {
  const line = document.createElement('span');
  line.className = level || 'info';
  const stamp = new Date().toLocaleTimeString([], { hour12: false });
  line.textContent = `[${stamp}] ${text}`;
  const log = $('log');
  log.appendChild(line);
  while (log.childElementCount > 1000) log.removeChild(log.firstChild);
  log.scrollTop = log.scrollHeight;
}

function setStatus(text) {
  $('status').textContent = text;
}

function setRunning(running) {
  state.running = running;
  $('btn-download').disabled = running;
  $('btn-cancel').disabled = !running;
  $('src-github').disabled = running;
  $('src-dex').disabled = running;
  document.querySelectorAll('input[name="api"]').forEach((r) => (r.disabled = running));
  $('apikey').disabled = running;
  $('remember-key').disabled = running;
  $('force').disabled = running;
}

function normalizeSources() {
  return {
    github: $('src-github').checked,
    manifestdex: $('src-dex').checked,
    api: document.querySelector('input[name="api"]:checked').value,
    api_key: $('apikey').value.trim(),
  };
}

function saveSettings() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(async () => {
    try {
      await invoke('save_settings', {
        settings: {
          github: $('src-github').checked,
          manifestdex: $('src-dex').checked,
          api_source: document.querySelector('input[name="api"]:checked').value,
          force: $('force').checked,
          last_app_id: /^\d+$/.test($('appid').value.trim()) ? $('appid').value.trim() : '',
          api_key: $('apikey').value,
          remember_key: $('remember-key').checked,
        },
      });
    } catch (error) {
      addLog('err', `Could not save settings: ${error}`);
    }
  }, 250);
}

function renderGames() {
  const query = $('search').value.trim().toLowerCase();
  const list = $('games');
  list.textContent = '';
  const games = state.games.filter((g) => !query || g.name.toLowerCase().includes(query) || g.app_id.includes(query));
  for (const game of games) {
    const row = document.createElement('div');
    row.className = 'game' + (state.selected.has(game.app_id) ? ' selected' : '');
    row.dataset.appId = game.app_id;

    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = state.selected.has(game.app_id);
    box.addEventListener('change', () => toggleGame(game.app_id, box.checked));

    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = game.name;

    const depots = document.createElement('span');
    depots.className = 'depots';
    depots.textContent = `${game.depots} depots`;

    const id = document.createElement('span');
    id.className = 'depots';
    id.textContent = game.app_id;

    row.append(box, name, depots, id);
    row.addEventListener('click', (event) => {
      if (event.target === box) return;
      toggleGame(game.app_id, !state.selected.has(game.app_id));
    });
    list.appendChild(row);
  }
  $('lib-status').textContent = state.games.length
    ? `${state.games.length} games, ${state.selected.size} selected`
    : 'No luas found in config\\stplug-in';
}

function toggleGame(appId, selected) {
  if (selected) state.selected.add(appId);
  else state.selected.delete(appId);
  $('appid').value = selected && state.selected.size === 1 ? appId : $('appid').value;
  if (selected) $('appid').value = appId;
  renderGames();
}

async function loadLibrary() {
  if (!state.steam) {
    $('lib-status').textContent = 'Steam not found';
    return;
  }
  $('lib-status').textContent = 'Scanning luas...';
  try {
    state.games = await invoke('library', { steamDir: state.steam });
    state.selected.clear();
    renderGames();
  } catch (error) {
    $('lib-status').textContent = `Scan failed: ${error}`;
  }
}

function clearQueue() {
  state.rows.clear();
  document.querySelector('tbody').textContent = '';
  $('queue-count').textContent = '';
  $('bar-fill').style.width = '0%';
}

function statusClass(status) {
  if (status.startsWith('ok')) return 'status-ok';
  if (status === 'failed') return 'status-failed';
  if (status === 'unavailable' || status === 'cancelled') return 'status-warn';
  if (status === 'downloading' || status === 'queued') return 'status-muted';
  return 'status-muted';
}

function applyRow(payload) {
  let entry = state.rows.get(payload.id);
  if (!entry) {
    const tr = document.createElement('tr');
    const appCell = document.createElement('td');
    const depotCell = document.createElement('td');
    const manifestCell = document.createElement('td');
    const statusCell = document.createElement('td');
    const progressCell = document.createElement('td');
    const bar = document.createElement('div');
    bar.className = 'row-bar';
    const fill = document.createElement('div');
    bar.appendChild(fill);
    progressCell.appendChild(bar);

    appCell.textContent = payload.app;
    depotCell.textContent = payload.depot;
    manifestCell.textContent = payload.manifest;
    tr.append(appCell, depotCell, manifestCell, statusCell, progressCell);
    document.querySelector('tbody').appendChild(tr);
    entry = { tr, statusCell, fill };
    state.rows.set(payload.id, entry);
  }
  entry.statusCell.textContent = payload.status;
  entry.statusCell.className = statusClass(payload.status);
  if (payload.progress >= 0) entry.fill.style.width = `${Math.round(payload.progress * 100)}%`;
  updateOverall();
}

function updateOverall() {
  const rows = [...state.rows.values()];
  if (!rows.length) {
    $('bar-fill').style.width = '0%';
    return;
  }
  const total = rows.reduce((sum, row) => {
    const width = parseFloat(row.fill.style.width) || 0;
    return sum + width / 100;
  }, 0);
  $('bar-fill').style.width = `${Math.round((total / rows.length) * 100)}%`;
  const done = rows.filter((r) => r.statusCell.textContent.startsWith('ok') || r.statusCell.textContent === 'failed').length;
  $('queue-count').textContent = `${done}/${rows.length} finished`;
}

async function startDownload() {
  if (state.running) return;
  const typed = $('appid').value.trim();
  const appIds = state.selected.size ? [...state.selected] : typed ? [typed] : [];
  if (!appIds.length) {
    addLog('err', 'Select a game or enter an AppID.');
    return;
  }
  setRunning(true);
  clearQueue();
  $('summary').textContent = '';
  setStatus('Starting...');
  addLog('info', `Downloading ${appIds.length} app(s): ${appIds.join(', ')}`);
  try {
    await invoke('start_download', {
      appIds,
      sources: normalizeSources(),
      force: $('force').checked,
    });
    saveSettings();
  } catch (error) {
    addLog('err', String(error));
    setRunning(false);
    setStatus('Idle.');
  }
}

async function checkForUpdates(manual) {
  try {
    const info = await invoke('check_update');
    if (!info) {
      if (manual) addLog('info', 'No updates available.');
      return;
    }
    const version = await invoke('app_version');
    $('update-text').textContent = `Version ${info.version} is available (you have ${version}).`;
    $('update-modal').classList.remove('hidden');
    $('btn-update-now').onclick = () => runUpdate(info.url);
  } catch (error) {
    if (manual) addLog('err', `Update check failed: ${error}`);
  }
}

async function runUpdate(url) {
  const button = $('btn-update-now');
  button.disabled = true;
  button.textContent = 'Downloading...';
  addLog('info', 'Downloading update...');
  try {
    await invoke('apply_update', { url });
  } catch (error) {
    addLog('err', `Update failed: ${error}`);
    button.disabled = false;
    button.textContent = 'Update now';
  }
}

function wireEvents() {
  $('btn-download').addEventListener('click', startDownload);
  $('btn-cancel').addEventListener('click', () => {
    invoke('cancel_download');
    setStatus('Cancelling...');
  });
  $('btn-folder').addEventListener('click', () => {
    if (state.steam) invoke('open_folder', { path: `${state.steam}\\depotcache` });
  });
  $('btn-rescan').addEventListener('click', loadLibrary);
  $('btn-all').addEventListener('click', () => {
    state.games.forEach((g) => state.selected.add(g.app_id));
    renderGames();
  });
  $('btn-none').addEventListener('click', () => {
    state.selected.clear();
    renderGames();
  });
  $('search').addEventListener('input', renderGames);
  $('btn-clear').addEventListener('click', () => ($('log').textContent = ''));
  $('btn-check-updates').addEventListener('click', () => checkForUpdates(true));
  $('btn-update-later').addEventListener('click', () => $('update-modal').classList.add('hidden'));
  $('btn-copy').addEventListener('click', () => navigator.clipboard.writeText($('log').textContent));
  $('appid').addEventListener('keydown', (event) => {
    if (event.key === 'Enter') startDownload();
  });
  $('appid').addEventListener('change', saveSettings);
  $('apikey').addEventListener('change', saveSettings);
  $('remember-key').addEventListener('change', saveSettings);
  $('force').addEventListener('change', saveSettings);
  $('src-github').addEventListener('change', saveSettings);
  $('src-dex').addEventListener('change', saveSettings);
  document.querySelectorAll('input[name="api"]').forEach((radio) => radio.addEventListener('change', saveSettings));
}

async function init() {
  wireEvents();

  await listen('row', (event) => applyRow(event.payload));
  await listen('log', (event) => addLog(event.payload.level, event.payload.text));
  await listen('summary', (event) => {
    const s = event.payload;
    $('summary').textContent = `${s.ok} ok, ${s.skipped} skipped, ${s.failed} failed, ${s.seconds}s`;
    setStatus(s.cancelled ? 'Cancelled.' : s.failed > 0 ? 'Finished with failures.' : 'Finished.');
    setRunning(false);
  });
  await listen('finished', () => setRunning(false));

  const settings = await invoke('load_settings');
  $('src-github').checked = settings.github;
  $('src-dex').checked = settings.manifestdex;
  const api = document.querySelector(`input[name="api"][value="${settings.api_source === 'morrenus' ? 'hubcap' : settings.api_source}"]`);
  if (api) api.checked = true;
  $('force').checked = settings.force;
  if (settings.last_app_id) $('appid').value = settings.last_app_id;
  if (settings.api_key) $('apikey').value = settings.api_key;
  $('remember-key').checked = settings.remember_key;

  state.steam = await invoke('steam_dir');
  $('steam-path').textContent = state.steam ? `Steam: ${state.steam}` : 'Steam: not found';
  addLog('info', 'Ready.');
  await loadLibrary();
  await checkForUpdates(false);
}

init().catch((error) => addLog('err', `Startup failed: ${error}`));
