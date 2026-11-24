'use strict';

const STORAGE_PREFIX = 'lolremote.';
const READY_CHECK_TOTAL = 12;
const RING_LENGTH = 2 * Math.PI * 124;

const urlCode = (() => {
  try {
    const k = new URLSearchParams(location.search).get('k');
    return k && /^\d{6}$/.test(k) ? k : null;
  } catch { return null; }
})();
const embedded = (() => {
  try { return new URLSearchParams(location.search).has('embed'); } catch { return false; }
})();
if (embedded) document.body.classList.add('embed');
const CLIENT_KIND = embedded ? 'desktop' : 'phone';

if (urlCode) {
  try { localStorage.setItem('lolremote.code', urlCode); } catch { }
}

const app = {
  ws: null,
  wsOpen: false,
  authed: false,
  authError: null,
  code: storageGet('code'),
  codeInput: '',
  connected: false,
  snapshot: null,
  log: [],
  receivedAt: 0,
  pending: new Map(),
  nextId: 1,
  busy: false,
  sheet: null,
  champions: null,
  search: '',
  reconnectTimer: null,
  lastMainHtml: '',
  lastSheetHtml: '',
  lastPhaseKey: '',
  wakeLock: null,
  counterExpanded: false,
  transport: storageGet('transport') === 'poll' ? 'poll' : 'ws',
  wsFailures: 0,
  pollTimer: null,
  failingSince: 0,
  build: null
};

function keepCodeInUrl(code) {
  if (embedded) return;
  try {
    if (new URLSearchParams(location.search).get('k') !== code) history.replaceState(null, '', `/?k=${code}`);
  } catch { }
}

function storageGet(key) {
  try { return localStorage.getItem(STORAGE_PREFIX + key); } catch { return null; }
}

function storageSet(key, value) {
  try {
    if (value == null) localStorage.removeItem(STORAGE_PREFIX + key);
    else localStorage.setItem(STORAGE_PREFIX + key, value);
  } catch { }
}

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
}

function initials(name) {
  if (!name) return '?';
  const parts = name.replace(/[^\p{L} ]/gu, ' ').trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  return (parts.length > 1 ? parts[0][0] + parts[1][0] : parts[0].slice(0, 2)).toUpperCase();
}

function avatar(id, name, size) {
  if (!id) return `<div class="avatar ${size} empty"></div>`;
  return `<div class="avatar ${size}" data-initials="${esc(initials(name))}"><img src="/icon/${encodeURIComponent(id)}" alt="" loading="lazy" onerror="this.remove()"></div>`;
}

function connect() {
  clearTimeout(app.reconnectTimer);
  if (app.transport === 'poll') {
    startPolling();
    return;
  }
  const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
  let ws;
  try {
    ws = new WebSocket(`${protocol}://${location.host}/ws`);
  } catch {
    scheduleReconnect();
    return;
  }
  app.ws = ws;
  let opened = false;
  const openTimer = setTimeout(() => {
    if (!opened && app.ws === ws) {
      try { ws.close(); } catch { }
    }
  }, 5000);
  ws.onopen = () => {
    opened = true;
    clearTimeout(openTimer);
    app.wsFailures = 0;
    app.failingSince = 0;
    app.wsOpen = true;
    if (app.code) send({ type: 'auth', code: app.code, client: CLIENT_KIND });
    render();
  };
  ws.onmessage = event => {
    let message;
    try { message = JSON.parse(event.data); } catch { return; }
    handleMessage(message);
  };
  ws.onclose = () => {
    clearTimeout(openTimer);
    if (app.ws !== ws) return;
    app.ws = null;
    if (!opened) {
      app.wsFailures++;
      if (!app.failingSince) app.failingSince = Date.now();
      if (app.wsFailures >= 2) {
        app.transport = 'poll';
        render();
        startPolling();
        return;
      }
    }
    app.wsOpen = false;
    app.authed = false;
    failPending('Megszakadt a kapcsolat a PC-vel.');
    render();
    scheduleReconnect();
  };
  ws.onerror = () => { };
}

async function pollOnce() {
  if (!app.code) {
    app.wsOpen = true;
    app.authed = false;
    render();
    return;
  }
  try {
    const response = await fetch('/api/state', {
      cache: 'no-store',
      headers: { 'X-LolRemote-Code': app.code, 'X-LolRemote-Client': CLIENT_KIND }
    });
    if (response.status === 401) {
      handleMessage({ type: 'auth', ok: false });
      return;
    }
    if (!response.ok) throw new Error(String(response.status));
    const state = await response.json();
    const wasAuthed = app.authed;
    app.wsOpen = true;
    app.failingSince = 0;
    if (!wasAuthed) handleMessage({ type: 'auth', ok: true });
    storageSet('transport', 'poll');
    handleMessage(state);
  } catch {
    if (!app.failingSince) app.failingSince = Date.now();
    if (app.wsOpen) {
      app.wsOpen = false;
      app.authed = false;
      render();
    } else {
      render();
    }
  }
}

function startPolling() {
  clearInterval(app.pollTimer);
  pollOnce();
  app.pollTimer = setInterval(pollOnce, 1000);
}

function scheduleReconnect() {
  clearTimeout(app.reconnectTimer);
  app.reconnectTimer = setTimeout(connect, 2000);
}

function send(payload) {
  if (app.ws && app.ws.readyState === WebSocket.OPEN) {
    app.ws.send(JSON.stringify(payload));
    return true;
  }
  return false;
}

function handleMessage(message) {
  switch (message.type) {
    case 'auth':
      if (message.ok) {
        app.authed = true;
        app.authError = null;
        storageSet('code', app.code);
        keepCodeInUrl(app.code);
      } else {
        if (app.code) app.authError = 'Hibás vagy régi kód. Olvasd be újra a QR-kódot a PC-s ablakból.';
        app.authed = false;
        app.code = null;
        storageSet('code', null);
      }
      render();
      break;
    case 'state':
      app.connected = !!message.connected;
      app.snapshot = message.snapshot || null;
      app.log = Array.isArray(message.log) ? message.log : [];
      app.receivedAt = performance.now();
      onStateChanged();
      render();
      break;
    case 'result': {
      const resolve = app.pending.get(message.id);
      if (resolve) {
        app.pending.delete(message.id);
        resolve(message);
      }
      break;
    }
  }
}

function onStateChanged() {
  const snap = app.snapshot;
  const key = [
    snap?.phase,
    snap?.readyCheck?.response,
    snap?.champSelect?.myAction?.type,
    snap?.champSelect?.incoming?.id
  ].join('|');
  if (key !== app.lastPhaseKey) {
    const isReady = snap?.phase === 'ReadyCheck' && snap?.readyCheck?.response === 'None';
    const myTurn = !!snap?.champSelect?.myAction && snap.champSelect.myAction.type !== 'intent';
    if (app.lastPhaseKey && (app.sheet?.type === 'champ' || app.sheet?.type === 'confirm')) closeSheet();
    if (isReady) notify('ready');
    else if (myTurn) notify('turn');
    else if (snap?.champSelect?.incoming) notify('incoming');
    app.lastPhaseKey = key;
  }

  if (!snap?.champSelect) {
    app.buildOpponent = 0;
    app.counterManual = false;
    app.counterExpanded = false;
    app.build = null;
  }
  if ((app.sheet?.type === 'champ' || app.sheet?.type === 'confirm') && !snap?.champSelect?.myAction) closeSheet();
  if (app.sheet && !snap?.champSelect) closeSheet();
}

function command(cmd, args = {}) {
  if (app.transport === 'poll') {
    return fetch('/api/cmd', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-LolRemote-Code': app.code || '', 'X-LolRemote-Client': CLIENT_KIND },
      body: JSON.stringify({ cmd, ...args })
    })
      .then(r => r.status === 401 ? { error: 'Hibás párosító kód.' } : r.json())
      .then(result => { pollOnce(); return result; })
      .catch(() => ({ error: 'Nincs kapcsolat a PC-vel.' }))
      .then(result => new Promise(resolve => setTimeout(() => resolve(result), 0)));
  }
  return new Promise(resolve => {
    const id = app.nextId++;
    app.pending.set(id, resolve);
    if (!send({ type: 'cmd', id, cmd, ...args })) {
      app.pending.delete(id);
      resolve({ error: 'Nincs kapcsolat a PC-vel.' });
      return;
    }
    setTimeout(() => {
      if (app.pending.has(id)) {
        app.pending.delete(id);
        resolve({ error: 'Nem jött válasz a PC-től.' });
      }
    }, 10000);
  });
}

function failPending(error) {
  for (const resolve of app.pending.values()) resolve({ error });
  app.pending.clear();
}

async function run(cmd, args, okMessage) {
  if (app.busy) return null;
  app.busy = true;
  render();
  const result = await command(cmd, args);
  app.busy = false;
  if (result.error) toast(result.error, 'error');
  else if (okMessage) toast(okMessage, 'ok');
  render();
  return result;
}

let toastTimer = null;
function toast(text, kind) {
  const el = document.getElementById('toast');
  el.textContent = text;
  el.className = `toast ${kind || ''}`;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 2800);
}

let audioContext = null;
function unlockAudio() {
  try {
    if (!audioContext) {
      const Ctx = window.AudioContext || window.webkitAudioContext;
      if (!Ctx) return;
      audioContext = new Ctx();
    }
    if (audioContext.state === 'suspended') audioContext.resume();
  } catch { }
}

function soundEnabled() {
  return storageGet('sound') !== '0';
}

function beep(frequencies) {
  if (!audioContext || !soundEnabled()) return;
  try {
    let t = audioContext.currentTime + 0.02;
    for (const f of frequencies) {
      const osc = audioContext.createOscillator();
      const gain = audioContext.createGain();
      osc.type = 'triangle';
      osc.frequency.value = f;
      gain.gain.setValueAtTime(0.0001, t);
      gain.gain.exponentialRampToValueAtTime(0.35, t + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.22);
      osc.connect(gain).connect(audioContext.destination);
      osc.start(t);
      osc.stop(t + 0.25);
      t += 0.24;
    }
  } catch { }
}

let titleTimer = null;
function notify(kind) {
  const patterns = {
    ready: { tones: [660, 880, 660, 880], title: '🔔 MEGVAN A MECCS!', vibrate: [300, 120, 300, 120, 300] },
    turn: { tones: [523, 784], title: '🎯 TE JÖSSZ!', vibrate: [200, 100, 200] },
    incoming: { tones: [440, 554], title: '🔄 Csere kérés', vibrate: [150] }
  };
  const p = patterns[kind];
  if (!p) return;
  beep(p.tones);
  try { navigator.vibrate?.(p.vibrate); } catch { }
  document.body.classList.remove('alert');
  void document.body.offsetWidth;
  document.body.classList.add('alert');
  setTimeout(() => document.body.classList.remove('alert'), 2400);
  clearInterval(titleTimer);
  if (document.visibilityState !== 'visible') {
    let on = false;
    titleTimer = setInterval(() => {
      on = !on;
      document.title = on ? p.title : 'LolRemote';
    }, 800);
  }
}

function secondsLeft() {
  const s = app.snapshot?.secondsLeft;
  if (s == null) return null;
  return Math.max(0, Math.ceil(s - (performance.now() - app.receivedAt) / 1000));
}

function updateTimers() {
  const s = secondsLeft();
  document.querySelectorAll('[data-timer]').forEach(el => {
    el.textContent = s == null ? '' : `${s}`;
    el.classList.toggle('low', s != null && s <= 5);
  });
  const ring = document.querySelector('[data-ring]');
  if (ring) {
    const fraction = s == null ? 0 : Math.min(1, s / READY_CHECK_TOTAL);
    ring.style.strokeDashoffset = `${RING_LENGTH * (1 - fraction)}`;
  }
}

function render() {
  if (app.pointerDown) {
    app.renderPending = true;
    return;
  }
  app.renderPending = false;
  renderConnection();
  const main = document.getElementById('main');
  const html = viewMain();
  if (html !== app.lastMainHtml) {
    const active = document.activeElement;
    const keepCodeFocus = active && active.id === 'code-input';
    main.innerHTML = html;
    app.lastMainHtml = html;
    if (keepCodeFocus) {
      const input = document.getElementById('code-input');
      if (input) {
        input.focus();
        input.setSelectionRange(input.value.length, input.value.length);
      }
    }
  }
  if (app.sheet) renderSheet();
  updateTimers();
}

function renderConnection() {
  const el = document.getElementById('conn');
  let cls = 'conn-offline';
  let text = 'Nincs kapcsolat';
  if (app.wsOpen && !app.authed) { cls = 'conn-warn'; text = 'Párosítás'; }
  else if (app.authed && !app.connected) { cls = 'conn-warn'; text = 'League nem fut'; }
  else if (app.authed) { cls = 'conn-ok'; text = 'Csatlakozva'; }
  el.className = `conn ${cls}`;
  el.innerHTML = `<i></i><span>${esc(text)}</span>`;
}

function viewMain() {
  if (!app.wsOpen) {
    return `<section class="card status">
      <div class="spinner"></div>
      <h1>Kapcsolódás a PC-hez…</h1>
      <p>Ellenőrizd, hogy a LolRemote fut a gépen, és a telefon ugyanazon a WiFi-n van.</p>
      ${app.failingSince && Date.now() - app.failingSince > 8000 ? `<div class="trouble">
        <b>Nem sikerül kapcsolódni? Próbáld ezeket:</b>
        <ul>
          <li>Brave böngészőben: koppints az oroszlán ikonra, és kapcsold ki a Shields-et erre az oldalra, vagy nyisd meg Chrome-ban / Safariban.</li>
          <li>A gépen a LolRemote ablakában nyomd meg a „Tűzfal beállítása” gombot, ha látod.</li>
          <li>A telefonon kapcsold ki a VPN-t, és ne vendég WiFi-n legyél.</li>
          <li>Ha a gép címe megváltozott, olvasd be újra a QR-kódot.</li>
        </ul>
      </div>` : ''}
    </section>`;
  }
  if (!app.authed) return viewPairing();
  if (!app.connected || !app.snapshot) {
    return `<section class="card status">
      <div class="big-icon">🖥️</div>
      <h1>A League kliens nem fut</h1>
      <p>A PC-s program fut, de még nem talál League klienst. Indítsd el a játékot a gépen.</p>
    </section>${viewLog()}`;
  }

  const snap = app.snapshot;
  let body;
  if (snap.phase === 'ReadyCheck' && snap.readyCheck) body = viewReadyCheck(snap.readyCheck);
  else if (snap.champSelect) body = viewChampSelect(snap.champSelect);
  else body = viewIdle(snap);
  return body + viewLog();
}

function viewPairing() {
  return `<section class="card pair">
    <h1>Párosítás</h1>
    <p>A legegyszerűbb: olvasd be a telefon kamerájával a PC-s LolRemote ablakban látható QR-kódot. Vagy írd be az ott látható 6 jegyű kódot:</p>
    <form data-form="pair" autocomplete="off">
      <input id="code-input" inputmode="numeric" autocomplete="one-time-code" maxlength="6" pattern="[0-9]*" placeholder="000000" value="${esc(app.codeInput)}">
      <button class="btn gold big" type="submit">Csatlakozás</button>
    </form>
    ${app.authError ? `<p class="error">${esc(app.authError)}</p>` : ''}
  </section>`;
}

function viewIdle(snap) {
  const map = {
    None: ['🏠', 'Főmenü', 'Indítsd el a meccskeresést a PC-n. Ha megvan a meccs, innen fogadhatod el.'],
    Lobby: ['👥', 'Lobby', 'Indítsd el a meccskeresést a PC-n. Ha megvan a meccs, innen fogadhatod el.'],
    Matchmaking: ['🔎', 'Meccskeresés…', 'Nyugodtan menj el a géptől, a telefonon el tudod fogadni a meccset.'],
    GameStart: ['⚔️', 'Indul a játék', 'Jó játékot!'],
    InProgress: ['⚔️', 'Játék folyamatban', 'Jó játékot!'],
    EndOfGame: ['🏁', 'Meccs vége', 'Ha újra keresel meccset, itt fogod látni.']
  };
  const [icon, title, text] = map[snap.phase] || ['ℹ️', snap.phaseText || snap.phase, ''];
  const practiceHint = snap.mode === 'Practice Tool' && (snap.phase === 'Lobby' || snap.phase === 'None')
    ? '<p>Practice Tool: nincs ready check, a „Start” gombot a PC-n nyomd meg.</p>' : '';
  return `${snap.mode ? `<div class="mode-badge">${esc(snap.mode)}</div>` : ''}<section class="card status">
    <div class="big-icon">${icon}</div>
    <h1>${esc(title)}</h1>
    ${text ? `<p>${esc(text)}</p>` : ''}
    ${practiceHint}
  </section>${viewSettings(snap)}`;
}

function toggleRow(label, hint, action, on) {
  return `<button class="toggle-row" data-action="${action}" role="switch" aria-checked="${on ? 'true' : 'false'}">
    <span><b>${esc(label)}</b><small>${esc(hint)}</small></span>
    <span class="switch ${on ? 'on' : ''}"><i></i></span>
  </button>`;
}

function viewSettings(snap) {
  const settings = snap?.settings || {};
  return `<section class="card settings">
    <h3>Beállítások</h3>
    ${toggleRow('Automatikus elfogadás', 'A meccset magától elfogadja (1,5 mp késleltetéssel).', 'toggle-auto-accept', !!settings.autoAccept)}
    ${toggleRow('Sablon automatikusan', 'Lock után beállítja a championhoz mentett rúnát és spellt.', 'toggle-auto-template', !!settings.autoApplyTemplate)}
    ${toggleRow('Hangjelzés', 'Csipog, ha megvan a meccs vagy te jössz.', 'toggle-sound', soundEnabled())}
  </section>`;
}

function viewReadyCheck(readyCheck) {
  if (readyCheck.response === 'Accepted') {
    return `<section class="card status">
      <div class="big-icon">✅</div>
      <h1>Elfogadva</h1>
      <p>Várjuk a többi játékost…</p>
    </section>`;
  }
  if (readyCheck.response === 'Declined') {
    return `<section class="card status"><div class="big-icon">❌</div><h1>Elutasítva</h1></section>`;
  }
  return `<section class="ready">
    <div class="ready-title">MEGVAN A MECCS!</div>
    <button class="accept-btn" data-action="accept" ${app.busy ? 'disabled' : ''}>
      <svg viewBox="0 0 260 260" aria-hidden="true">
        <circle class="ring-bg" cx="130" cy="130" r="124"></circle>
        <circle class="ring" data-ring cx="130" cy="130" r="124" stroke-dasharray="${RING_LENGTH}" stroke-dashoffset="0"></circle>
      </svg>
      <span class="label">ELFOGAD</span>
      <span class="secs"><span data-timer></span> mp</span>
    </button>
    <button class="btn ghost" data-action="decline" ${app.busy ? 'disabled' : ''}>Elutasít</button>
    ${app.snapshot?.settings?.autoAccept ? '<p class="auto-note">Auto-accept bekapcsolva: magától elfogadom.</p>' : ''}
  </section>`;
}

function viewChampSelect(cs) {
  const action = cs.myAction;
  let turn;
  if (cs.noPickAction && !action) {
    turn = `<section class="card turn wait">
      <div class="turn-head"><span class="label">VÁLASSZ A PC-N</span><span class="timer" data-timer></span></div>
      <p>Ebben a módban a kliens nem ad pick-lehetőséget a telefonnak. Válassz championt a gépen; a spellt és a rúnát innen is állíthatod.</p>
    </section>`;
  } else if (cs.timerPhase === 'FINALIZATION') {
    turn = `<section class="card turn final">
      <div class="turn-head"><span class="label">MINDEN PICK KÉSZ</span><span class="timer" data-timer></span></div>
      <p>Mindjárt indul a játék. Még cserélhetsz, és állíthatsz spellt, rúnát.</p>
    </section>`;
  } else if (action && action.type === 'intent') {
    turn = `<section class="card turn intent">
      <div class="turn-head"><span class="label">TERVEZÉS · KIVEL JÁTSZANÁL?</span><span class="timer" data-timer></span></div>
      <button class="turn-choice" data-action="open-champs">
        ${avatar(action.championId, action.championName, 'lg')}
        <span>${action.championName ? esc(action.championName) : 'Jelöld ki a szándékod'}</span>
        <span class="chev">›</span>
      </button>
      <p class="turn-note">Ez csak jelzés a csapatnak, nem ban és nem lock. A bannolás a következő körben jön.</p>
    </section>`;
  } else if (action) {
    const isBan = action.type === 'ban';
    turn = `<section class="card turn ${isBan ? 'ban' : 'pick'}">
      <div class="turn-head"><span class="label">TE JÖSSZ · ${isBan ? 'BAN' : 'PICK'}</span><span class="timer" data-timer></span></div>
      <button class="turn-choice" data-action="open-champs">
        ${avatar(action.championId, action.championName, 'lg')}
        <span>${action.championName ? esc(action.championName) : 'Válassz championt'}</span>
        <span class="chev">›</span>
      </button>
      <button class="btn ${isBan ? 'danger' : 'primary'} big" data-action="lock" ${action.championId && !app.busy ? '' : 'disabled'}>${isBan ? 'BAN' : 'LOCK IN'}</button>
    </section>`;
  } else {
    turn = `<section class="card turn wait">
      <div class="turn-head"><span class="label">MÁSOK VÁLASZTANAK…</span><span class="timer" data-timer></span></div>
      <p>Szólunk, amikor te jössz.</p>
    </section>`;
  }

  const incoming = cs.incoming ? `<section class="card incoming">
      <p>🔄 ${esc(cs.incoming.label)}</p>
      <div class="row">
        <button class="btn primary" data-action="swap-accept" ${app.busy ? 'disabled' : ''}>Elfogad</button>
        <button class="btn ghost" data-action="swap-decline" ${app.busy ? 'disabled' : ''}>Elutasít</button>
      </div>
    </section>` : '';

  const ally = m => `<div class="member ${m.isMe ? 'me' : ''}">
      ${avatar(m.championId || m.intentId, m.championName || m.intentName, 'sm')}
      <div><div class="pos">${esc(m.position)}${m.isMe ? ' · TE' : ''}</div>
      <div class="name ${!m.championId && m.intentId ? 'intent' : ''}">${esc(m.championName || (m.intentName ? `${m.intentName}?` : '…'))}</div></div>
    </div>`;
  const enemy = m => `<div class="member">
      ${avatar(m.championId, m.championName, 'sm')}
      <div><div class="name">${esc(m.championName || '…')}</div></div>
    </div>`;

  const enemies = cs.theirTeam || [];
  const teams = `<section class="card teams">
      <div class="team"><h3>Csapatod</h3>${(cs.myTeam || []).map(ally).join('')}</div>
      <div class="team enemy"><h3>Ellenfél</h3>${enemies.length ? enemies.map(enemy).join('') : '<p class="no-enemy">Nincs ellenfél</p>'}</div>
    </section>`;

  const hasBans = (cs.myBans || []).length || (cs.theirBans || []).length;
  const bans = hasBans ? `<div class="bans">Bannok: <b>${esc((cs.myBans || []).join(', ') || '–')}</b> · ellenfél: <b>${esc((cs.theirBans || []).join(', ') || '–')}</b></div>` : '';
  const mode = app.snapshot?.mode ? `<div class="mode-badge">${esc(app.snapshot.mode)}</div>` : '';

  const currentRune = (cs.runePages || []).find(p => p.current);
  const swapCount = (cs.swapOptions || []).length;
  const loadout = `<section class="loadout">
      <button class="tile" data-action="open-spell" data-slot="1"><span class="key">D spell</span><span class="val">${esc(cs.spell1?.name || '–')}</span><span class="chev">›</span></button>
      <button class="tile" data-action="open-spell" data-slot="2"><span class="key">F spell</span><span class="val">${esc(cs.spell2?.name || '–')}</span><span class="chev">›</span></button>
      <button class="tile wide" data-action="open-runes"><span class="key">Rúnaoldal</span><span class="val">${esc(currentRune?.name || '–')}</span><span class="sub">${esc(currentRune?.summary || '')}</span></button>
      ${viewTemplateTile(cs.template)}
      <button class="tile wide" data-action="open-swaps"><span class="key">Csere csapattárssal ${swapCount ? `<span class="badge">${swapCount}</span>` : ''}</span><span class="sub">${swapCount ? 'Champion vagy pick sorrend csere' : 'Most nincs elérhető csere'}</span></button>
    </section>`;

  return mode + turn + incoming + viewBanSuggestions(cs) + teams + bans + viewCounters(cs) + viewQuickBuild(cs) + loadout;
}

function enemyIds(cs) {
  return (cs.theirTeam || []).filter(m => m.championId).map(m => m.championId);
}

function counterTargets(cs) {
  const targets = [];
  const lane = app.build?.data?.opponentId;
  for (const m of cs.theirTeam || []) {
    if (m.championId) targets.push({ id: m.championId, name: m.championName, enemy: true, lane: m.championId === lane });
  }
  if (cs.myChampionId) {
    const me = (cs.myTeam || []).find(m => m.isMe);
    const name = me?.championName || me?.intentName || cs.myAction?.championName || cs.template?.championName || '';
    targets.push({ id: cs.myChampionId, name, enemy: false });
  }
  return targets;
}

function ensureCounterTarget(cs) {
  const targets = counterTargets(cs);
  if (!targets.length) {
    app.counterTarget = null;
    return null;
  }
  const manual = app.counterManual ? targets.find(t => t.id === app.counterTarget?.id) : null;
  const target = manual
    || targets.find(t => t.lane)
    || targets.filter(t => t.enemy).pop()
    || targets[targets.length - 1];
  if (app.counterTarget?.id !== target.id) app.counterExpanded = false;
  app.counterTarget = target;
  return target;
}

async function loadCounters(championId) {
  app.counterCache = app.counterCache || {};
  const entry = app.counterCache[championId];
  if (entry && (entry.loading || entry.data)) return;
  app.counterCache[championId] = { loading: true };
  render();
  const result = await command('counters', { championId });
  app.counterCache[championId] = result.error ? { error: result.error } : { data: result.data };
  render();
}

function buildKey(cs) {
  if (!cs?.myChampionId) return null;
  return `${cs.myChampionId}|${app.buildOpponent || 0}|${enemyIds(cs).join(',')}`;
}

function ensureBuild(cs) {
  const key = buildKey(cs);
  if (!key) {
    app.build = null;
    return;
  }
  if (app.build?.key === key) return;
  app.build = { key, loading: true };
  const opponentId = app.buildOpponent || 0;
  setTimeout(async () => {
    const result = await command('build', { opponentId });
    if (app.build?.key !== key) return;
    app.build = result.error ? { key, error: result.error } : { key, data: result.data };
    render();
  }, 0);
}

function pct(value) {
  return `${String(value).replace('.', ',')}%`;
}

function counterRows(items, canPick, isMe) {
  const limit = 5;
  const shown = app.counterExpanded ? items : items.slice(0, limit);
  const rows = shown.map((c, i) => {
    const clickable = canPick && c.pickable && !isMe;
    const dim = !isMe && canPick && !c.pickable ? ' dim' : '';
    const inner = `<span class="rank">${i + 1}</span>
      ${avatar(c.championId, c.name, 'sm')}
      <span class="counter-name">${esc(c.name)}${dim ? '<small>nincs meg neked</small>' : ''}</span>
      <span class="counter-wr">${pct(c.winRate)}<small>${esc(c.games)} meccs</small></span>
      ${clickable ? '<span class="pick-tag">KIJELÖL ›</span>' : ''}`;
    return clickable
      ? `<button class="counter-row clickable" data-action="counter-pick" data-id="${esc(c.championId)}" data-name="${esc(c.name)}" ${app.busy ? 'disabled' : ''}>${inner}</button>`
      : `<div class="counter-row${dim}">${inner}</div>`;
  }).join('');
  const more = items.length > limit
    ? `<button class="btn ghost expand" data-action="toggle-counters">${app.counterExpanded ? 'Kevesebb ▴' : `Összes mutatása (${items.length}) ▾`}</button>`
    : '';
  return rows + more;
}

function viewLaneInfo() {
  const d = app.build?.data;
  if (!d?.opponentName) return '';
  const style = { defensive: 'óvatos', aggressive: 'agresszív', balanced: 'kiegyensúlyozott' }[d.playStyle] || d.playStyle;
  return `<div class="lane-info">
    <div><b>Sávellenfél${d.opponentGuessed ? ' (tipp)' : ''}:</b> ${esc(d.opponentName)}${style ? ` · javasolt stílus: ${esc(style)}` : ''}</div>
    ${d.tip ? `<details class="tip"><summary>Tipp a matchuphoz (angol)</summary><p>${esc(d.tip)}</p></details>` : ''}
  </div>`;
}

function viewCounters(cs) {
  ensureBuild(cs);
  const target = ensureCounterTarget(cs);
  const targets = counterTargets(cs);
  let body;
  if (!target) {
    body = '<div class="counter-empty">Amint az ellenfél pickel, itt látod, kik jók ellene.</div>';
  } else {
    const entry = app.counterCache?.[target.id];
    if (!entry) setTimeout(() => loadCounters(target.id), 0);
    if (!entry || entry.loading) {
      body = '<div class="counter-empty"><span class="mini-spin"></span>Adatok betöltése…</div>';
    } else if (entry.error) {
      body = `<div class="counter-empty">${esc(entry.error)} <button class="linklike" data-action="retry-counters" data-id="${esc(target.id)}">Újra</button></div>`;
    } else {
      const d = entry.data;
      const canPick = cs.myAction?.type === 'pick' || cs.myAction?.type === 'intent';
      const label = d.isMe
        ? `Neked nehéz ellenfelek · ${d.position}`
        : `Jók ${d.championName} ellen · ${d.position}`;
      const hint = d.isMe
        ? 'Az ő nyerési arányuk ellened.'
        : `Az ő nyerési arányuk ${d.championName} ellen.${canPick ? ' Koppints egyre: kijelöli, és rákérdez a lockra.' : ''} A bannolt és foglalt championok nem szerepelnek.`;
      const unavailable = new Set(cs.unavailable || []);
      const items = d.isMe ? (d.items || []) : (d.items || []).filter(c => !unavailable.has(c.championId));
      body = `<div class="counter-label">${esc(label)}</div><div class="counter-hint">${esc(hint)}</div>`
        + (items.length ? counterRows(items, canPick, d.isMe) : '<div class="counter-empty">Nincs elég adat ehhez a matchuphoz.</div>');
    }
  }
  const chips = targets.map(t => `<button class="chip ${t.id === target?.id ? 'on' : ''}" data-action="counter-target" data-id="${esc(t.id)}">${t.enemy ? '' : 'Te: '}${esc(t.name)}${t.lane ? ' <i>sáv</i>' : ''}</button>`).join('');
  return `<section class="card counters">
    <div class="card-head"><h3>Matchup</h3><span class="source">OP.GG</span></div>
    ${chips ? `<div class="chips">${chips}</div>` : ''}
    ${viewLaneInfo()}
    ${body}
  </section>`;
}

function viewQuickBuild(cs) {
  if (!cs.myChampionId) return '';
  const b = app.build;
  if (!b || b.loading) return `<div class="quick muted"><span class="mini-spin"></span>Ajánlott rúna és spell betöltése…</div>`;
  if (b.error || !b.data) return '';
  const spell = b.data.spells?.[0];
  const rune = b.data.runes?.[0];
  if (!spell && !rune) return '';
  const vs = b.data.opponentName ? ` vs ${esc(b.data.opponentName)}` : '';
  return `<button class="quick" data-action="apply-quick" ${app.busy ? 'disabled' : ''}>
    <span class="quick-title">⚡ Ajánlott beállítása${vs}</span>
    <span class="quick-sub">${spell ? esc(spell.names) : ''}${spell && rune ? ' · ' : ''}${rune ? esc(rune.title) : ''}</span>
  </button>`;
}

function orderSpells(ids, cs) {
  const [a, b] = ids;
  if (b === cs?.spell1?.id || a === cs?.spell2?.id) return [b, a];
  return [a, b];
}

async function applyRune(rune, label) {
  return run('rune-apply-recommended', {
    primaryStyleId: rune.primaryStyleId,
    subStyleId: rune.subStyleId,
    perkIds: rune.perkIds,
    label
  }, 'Rúnaoldal beállítva');
}

function viewTemplateTile(template) {
  if (!template) {
    return `<div class="tile wide template muted"><span class="key">Sablon</span><span class="sub">Válassz championt, és elmentheted hozzá a mostani rúnát + spellt.</span></div>`;
  }
  return `<div class="tile wide template ${template.exists ? 'has' : ''}">
    <span class="key">Sablon · ${esc(template.championName)}</span>
    <span class="val">${template.exists ? esc(template.summary) : 'Nincs mentve'}</span>
    <span class="tile-actions">
      ${template.exists ? `<button class="btn primary small" data-action="template-apply" ${app.busy ? 'disabled' : ''}>Alkalmaz</button>` : ''}
      <button class="btn small" data-action="template-save" ${app.busy ? 'disabled' : ''}>${template.exists ? 'Felülír' : 'Mentés'}</button>
    </span>
  </div>`;
}

function viewLog() {
  const items = [...app.log].reverse().slice(0, 12)
    .map(e => `<li class="c-${esc(e.color)}"><time>${esc(e.time)}</time><span>${esc(e.text)}</span></li>`)
    .join('');
  return `<section class="card log">
    <details ${storageGet('logOpen') === '1' ? 'open' : ''} data-log>
      <summary>Események</summary>
      <ul>${items || '<li>Még nincs esemény.</li>'}</ul>
    </details>
    ${embedded ? '' : '<button class="linklike" data-action="unpair">Párosítás törlése</button>'}
  </section>`;
}

let lockedScrollY = 0;
function lockBody() {
  if (document.body.classList.contains('locked')) return;
  lockedScrollY = window.scrollY;
  document.body.style.top = `-${lockedScrollY}px`;
  document.body.classList.add('locked');
}

function unlockBody() {
  if (!document.body.classList.contains('locked')) return;
  document.body.classList.remove('locked');
  document.body.style.top = '';
  window.scrollTo(0, lockedScrollY);
}

function openSheet(sheet) {
  app.sheet = sheet;
  app.lastSheetHtml = '';
  const el = document.getElementById('sheet');
  el.hidden = false;
  lockBody();
  const cs = app.snapshot?.champSelect;
  if (cs) ensureBuild(cs);
  if (sheet.type === 'rune') {
    loadRecommendations(false);
  }
  if (sheet.type === 'champ') {
    app.search = '';
    app.champions = null;
    renderSheet(true);
    loadChampions();
  } else {
    renderSheet(true);
  }
}

function closeSheet() {
  app.sheet = null;
  app.lastSheetHtml = '';
  const el = document.getElementById('sheet');
  el.hidden = true;
  document.getElementById('sheet-content').innerHTML = '';
  unlockBody();
}

async function loadRecommendations(all) {
  app.recs = { loading: true, all, items: [] };
  if (app.sheet) renderSheet();
  const result = await command('rune-recommendations', { all });
  if (!app.sheet || app.sheet.type !== 'rune') return;
  app.recs = result.error
    ? { loading: false, all, items: [], error: result.error }
    : { loading: false, all: !!result.data?.all, items: result.data?.items || [], championName: result.data?.championName };
  renderSheet();
}

async function loadChampions() {
  const result = await command('champions');
  if (!app.sheet || app.sheet.type !== 'champ') return;
  if (result.error) {
    toast(result.error, 'error');
    app.champions = [];
  } else {
    app.champions = Array.isArray(result.data) ? result.data : [];
  }
  renderChampGrid();
}

function renderSheet(force) {
  const content = document.getElementById('sheet-content');
  const cs = app.snapshot?.champSelect;
  if (app.sheet.type === 'champ') {
    if (force || !content.querySelector('.search')) {
      const kind = cs?.myAction?.type;
      const title = kind === 'ban' ? 'Kit bannolsz?' : kind === 'intent' ? 'Kivel szeretnél játszani?' : 'Kit választasz?';
      content.innerHTML = `<div class="sheet-head"><h2>${title}</h2><button class="close" data-action="close-sheet" aria-label="Bezár">✕</button></div>
        <input class="search" type="search" placeholder="Keresés név szerint…" autocomplete="off" autocorrect="off" spellcheck="false" data-search>
        <div class="grid" data-grid></div>`;
      renderChampGrid();
    }
    return;
  }

  let html = '';
  if (app.sheet.type === 'confirm') {
    const action = cs?.myAction;
    const isBan = action?.type === 'ban';
    const id = action?.championId || app.sheet.championId;
    const name = action?.championName || app.sheet.name;
    html = `<div class="confirm">
      ${avatar(id, name, 'xl')}
      <h2>${esc(name)}</h2>
      <p>${isBan ? 'Kijelölve bannolásra. Bannolod?' : 'Kijelölve. Lockolod?'}</p>
      <button class="btn ${isBan ? 'danger' : 'primary'} big" data-action="confirm-lock" ${app.busy ? 'disabled' : ''}>${isBan ? 'BAN' : 'LOCK IN'}</button>
      <button class="btn ghost" data-action="close-sheet">Még nem, másikat választok</button>
    </div>`;
  } else if (app.sheet.type === 'spell') {
    const slot = app.sheet.slot;
    const current = slot === 1 ? cs?.spell1?.id : cs?.spell2?.id;
    const b = app.build;
    let recHtml = '';
    if (b?.loading) recHtml = '<div class="empty-note small"><span class="mini-spin"></span>Ajánlás betöltése…</div>';
    else if (b?.data?.spells?.length) {
      recHtml = b.data.spells.map((sp, i) => `<button class="option rec" data-action="pick-build-spell" data-index="${i}">
          <span>${esc(sp.names)}<small>${esc(sp.games)} meccs · ${pct(sp.winRate)} győzelem</small></span><span class="tag">MINDKETTŐ</span>
        </button>`).join('');
    }
    const head = b?.data ? `Ajánlott párok · ${esc(b.data.championName)} ${esc(b.data.position)}${b.data.opponentName ? ` vs ${esc(b.data.opponentName)}` : ''}` : 'Ajánlott párok';
    html = `<div class="sheet-head"><h2>${slot === 1 ? 'D' : 'F'} spell</h2><button class="close" data-action="close-sheet" aria-label="Bezár">✕</button></div><div class="list">
      ${recHtml ? `<div class="list-head">${head}</div>${recHtml}<div class="list-head">Csak a ${slot === 1 ? 'D' : 'F'} spell cseréje</div>` : ''}
      ${(app.snapshot?.spells || []).map(s =>
      `<button class="option ${s.id === current ? 'current' : ''}" data-action="pick-spell" data-id="${esc(s.id)}">
        <span>${esc(s.name)}</span>${s.id === current ? '<span class="tag">JELENLEGI</span>' : ''}
      </button>`).join('')}</div>`;
  } else if (app.sheet.type === 'rune') {
    const pages = cs?.runePages || [];
    const b = app.build;
    let recHtml;
    let head = 'Ajánlott rúnák';
    if (b?.data?.runes?.length) {
      head = `OP.GG · ${esc(b.data.championName)} ${esc(b.data.position)}${b.data.opponentName ? ` vs ${esc(b.data.opponentName)}` : ''}`;
      recHtml = b.data.runes.map((r, i) => `<button class="option rec" data-action="pick-build-rune" data-index="${i}">
          <span>${esc(r.title)}<small>${esc(r.detail)}</small><small class="stat">${esc(r.games)} meccs · ${pct(r.winRate)} győzelem</small></span><span class="tag">BEÁLLÍT</span>
        </button>`).join('');
    } else if (b?.loading) {
      recHtml = '<div class="empty-note small"><span class="mini-spin"></span>Ajánlás betöltése…</div>';
    } else {
      const recs = app.recs;
      head = 'A játék ajánlásai';
      if (!recs || recs.loading) recHtml = '<div class="empty-note small"><span class="mini-spin"></span>Betöltés…</div>';
      else if (recs.error) recHtml = `<div class="empty-note small">${esc(recs.error)}</div>`;
      else if (!recs.items.length) recHtml = '<div class="empty-note small">Nincs ajánlás ehhez a championhoz.</div>';
      else recHtml = recs.items.slice(0, 3).map((r, i) => `<button class="option rec" data-action="pick-rec" data-index="${i}">
          <span>${esc(r.title)}<small>${esc(r.detail)}</small></span><span class="tag">BEÁLLÍT</span>
        </button>`).join('');
    }
    html = `<div class="sheet-head"><h2>Rúnaoldal</h2><button class="close" data-action="close-sheet" aria-label="Bezár">✕</button></div><div class="list">
      <div class="list-head">${head}</div>
      ${recHtml}
      <div class="list-head">Saját rúnaoldalak</div>
      ${pages.length ? pages.map(p =>
      `<button class="option ${p.current ? 'current' : ''}" data-action="pick-rune" data-id="${esc(p.id)}">
        <span>${esc(p.name)}<small>${esc(p.summary)}</small></span>${p.current ? '<span class="tag">JELENLEGI</span>' : ''}
      </button>`).join('') : '<div class="empty-note">Nincs rúnaoldal.</div>'}</div>`;
  } else if (app.sheet.type === 'swap') {
    const options = cs?.swapOptions || [];
    html = `<div class="sheet-head"><h2>Csere csapattárssal</h2><button class="close" data-action="close-sheet" aria-label="Bezár">✕</button></div><div class="list">${options.length ? options.map((o, i) =>
      `<button class="option" data-action="do-swap" data-index="${i}">
        <span>${esc(o.label)}</span><span class="tag">${o.verb === 'cancel' ? 'VISSZAVON' : 'KÉR'}</span>
      </button>`).join('') : '<div class="empty-note">Most nincs elérhető csere. Champion cserét akkor kérhetsz, ha már mindketten lockoltatok; pick sorrend cserét a pickek előtt.</div>'}</div>`;
  }
  if (html !== app.lastSheetHtml) {
    const scroller = content.querySelector('.list');
    const top = scroller ? scroller.scrollTop : 0;
    content.innerHTML = html;
    app.lastSheetHtml = html;
    const next = content.querySelector('.list');
    if (next && top) next.scrollTop = top;
  }
}

function renderChampGrid() {
  const grid = document.querySelector('[data-grid]');
  if (!grid) return;
  if (app.champions == null) {
    grid.innerHTML = '<div class="empty-note">Betöltés…</div>';
    return;
  }
  const term = app.search.trim().toLowerCase();
  const list = app.champions.filter(c => !term || c.name.toLowerCase().includes(term));
  list.sort((a, b) => {
    const aStarts = a.name.toLowerCase().startsWith(term) ? 0 : 1;
    const bStarts = b.name.toLowerCase().startsWith(term) ? 0 : 1;
    return aStarts - bStarts || a.name.localeCompare(b.name);
  });
  const selected = app.snapshot?.champSelect?.myAction?.championId;
  grid.innerHTML = list.length
    ? list.map(c => `<button class="champ ${c.id === selected ? 'selected' : ''}" data-action="pick-champ" data-id="${esc(c.id)}">
        ${avatar(c.id, c.name, 'md')}<span>${esc(c.name)}</span>
      </button>`).join('')
    : '<div class="empty-note">Nincs találat.</div>';
}

async function hoverAndConfirm(id, name, cs) {
  const intent = cs?.myAction?.type === 'intent';
  const result = await run('hover', { championId: id }, intent ? `Szándék: ${name}` : null);
  if (result && !result.error && !intent) openSheet({ type: 'confirm', championId: id, name });
}

function banSuggestKey(cs) {
  const showing = cs.timerPhase === 'PLANNING' || cs.myAction?.type === 'ban';
  if (!showing) return null;
  return `${cs.myIntentId || 0}|${cs.myPosition || ''}|${(cs.unavailable || []).length}|${(cs.allyHovers || []).slice().sort().join(',')}`;
}

async function loadBanSuggestions(key) {
  app.banSuggest = { key, loading: true };
  render();
  const result = await command('ban-suggestions');
  if (app.banSuggest?.key !== key) return;
  app.banSuggest = result.error ? { key, error: result.error } : { key, data: result.data };
  render();
}

function viewBanSuggestions(cs) {
  const key = banSuggestKey(cs);
  if (!key) return '';
  const current = app.banSuggest;
  if (!current || (current.key !== key && !current.loading)) setTimeout(() => loadBanSuggestions(key), 0);
  const canBan = cs.myAction?.type === 'ban';
  const head = `<div class="card-head"><h3>Mit bannolj?</h3><span class="source">OP.GG</span></div>`;
  if (!current || current.loading || (current.key !== key && !current.data)) {
    return `<section class="card bansug">${head}<div class="counter-empty"><span class="mini-spin"></span>Javaslat betöltése…</div></section>`;
  }
  if (current.error) {
    return `<section class="card bansug">${head}<div class="counter-empty">${esc(current.error)} <button class="linklike" data-action="retry-bans">Újra</button></div></section>`;
  }
  const d = current.data;
  const unavailable = new Set([...(cs.unavailable || []), ...(cs.allyHovers || [])]);
  const row = (c, i) => {
    if (unavailable.has(c.championId)) return '';
    const inner = `<span class="rank">${i + 1}</span>
      ${avatar(c.championId, c.name, 'sm')}
      <span class="counter-name">${esc(c.name)}${c.both ? ' <i class="both">★</i>' : ''}<small>${esc(c.detail)}</small></span>
      ${canBan ? '<span class="pick-tag ban">BAN ›</span>' : ''}`;
    return canBan
      ? `<button class="counter-row clickable" data-action="ban-suggest" data-id="${esc(c.championId)}" data-name="${esc(c.name)}" ${app.busy ? 'disabled' : ''}>${inner}</button>`
      : `<div class="counter-row">${inner}</div>`;
  };
  const best = d.best && !unavailable.has(d.best.championId)
    ? `<div class="ban-best">Legjobb ban most: <b>${esc(d.best.name)}</b>${canBan ? ` <button class="btn danger small" data-action="ban-suggest" data-id="${esc(d.best.championId)}" data-name="${esc(d.best.name)}" ${app.busy ? 'disabled' : ''}>Kijelöl</button>` : ''}</div>`
    : '';
  const threats = d.intentName
    ? `<div class="counter-label">Ellened (${esc(d.intentName)}, ${esc(d.position)}) a legerősebbek</div>`
      + (d.threats?.length ? d.threats.map(row).join('') : `<div class="counter-empty">${esc(d.threatError || 'Nincs elég adat.')}</div>`)
    : '<div class="counter-hint">Jelöld ki, kivel játszanál, és megmutatom, ki a legveszélyesebb ellene.</div>';
  const meta = `<div class="counter-label">Meta a te rolodban (${esc(d.position)})</div>`
    + (d.meta?.length ? d.meta.map(row).join('') : `<div class="counter-empty">${esc(d.metaError || 'Nincs adat.')}</div>`);
  const note = `<div class="counter-hint">${canBan ? 'Koppints egyre: kijelöli bannolásra, és rákérdez.' : 'A ban körben ezekre koppintva tudsz bannolni.'} ★ = erős a metában és ellened is. A csapattársaid kijelölt championjai és a már bannoltak nem szerepelnek.</div>`;
  return `<section class="card bansug">${head}${best}${threats}${meta}${note}</section>`;
}

async function requestWakeLock() {
  try {
    if ('wakeLock' in navigator && !app.wakeLock) {
      app.wakeLock = await navigator.wakeLock.request('screen');
      app.wakeLock.addEventListener('release', () => { app.wakeLock = null; });
    }
  } catch { }
}

let pointerTimer = null;
function releasePointer(delay) {
  clearTimeout(pointerTimer);
  pointerTimer = setTimeout(() => {
    app.pointerDown = false;
    if (app.renderPending) render();
  }, delay);
}
document.addEventListener('pointerdown', event => {
  if (!event.target.closest('[data-action]')) return;
  app.pointerDown = true;
  releasePointer(1500);
}, true);
document.addEventListener('pointerup', () => { if (app.pointerDown) releasePointer(120); }, true);
document.addEventListener('pointercancel', () => { if (app.pointerDown) releasePointer(0); }, true);

document.addEventListener('click', async event => {
  requestWakeLock();
  unlockAudio();
  const target = event.target.closest('[data-action]');
  if (!target) return;
  clearTimeout(pointerTimer);
  app.pointerDown = false;
  if (app.renderPending) render();
  const action = target.dataset.action;
  const cs = app.snapshot?.champSelect;

  switch (action) {
    case 'accept':
      await run('accept', {}, 'Elfogadva!');
      break;
    case 'decline':
      await run('decline');
      break;
    case 'open-champs':
      openSheet({ type: 'champ' });
      break;
    case 'pick-champ': {
      const id = Number(target.dataset.id);
      const name = (app.champions || []).find(c => c.id === id)?.name || '';
      closeSheet();
      await hoverAndConfirm(id, name, cs);
      break;
    }
    case 'counter-pick':
    case 'ban-suggest': {
      await hoverAndConfirm(Number(target.dataset.id), target.dataset.name || '', cs);
      break;
    }
    case 'retry-bans':
      app.banSuggest = null;
      render();
      break;
    case 'confirm-lock': {
      closeSheet();
      const isBan = cs?.myAction?.type === 'ban';
      await run('lock', {}, isBan ? 'Bannolva!' : 'Lockolva!');
      break;
    }
    case 'lock':
      await run('lock', {}, cs?.myAction?.type === 'ban' ? 'Bannolva!' : 'Lockolva!');
      break;
    case 'open-spell':
      openSheet({ type: 'spell', slot: Number(target.dataset.slot) });
      break;
    case 'pick-spell': {
      const id = Number(target.dataset.id);
      const slot = app.sheet?.slot;
      const current1 = cs?.spell1?.id;
      const current2 = cs?.spell2?.id;
      closeSheet();
      if (current1 == null || current2 == null) return;
      let spell1 = current1;
      let spell2 = current2;
      if (slot === 1) {
        spell1 = id;
        if (id === current2) spell2 = current1;
      } else {
        spell2 = id;
        if (id === current1) spell1 = current2;
      }
      if (spell1 === current1 && spell2 === current2) return;
      await run('spells', { spell1, spell2 });
      break;
    }
    case 'open-runes':
      openSheet({ type: 'rune' });
      break;
    case 'pick-rune': {
      const id = Number(target.dataset.id);
      closeSheet();
      await run('rune', { pageId: id });
      break;
    }
    case 'counter-target': {
      const id = Number(target.dataset.id);
      const cs2 = app.snapshot?.champSelect;
      const t = cs2 ? counterTargets(cs2).find(x => x.id === id) : null;
      if (t) {
        app.counterTarget = t;
        app.counterManual = true;
        app.counterExpanded = false;
        if (t.enemy) app.buildOpponent = t.id;
        render();
      }
      break;
    }
    case 'toggle-counters':
      app.counterExpanded = !app.counterExpanded;
      render();
      break;
    case 'retry-counters':
      if (app.counterCache) delete app.counterCache[Number(target.dataset.id)];
      render();
      break;
    case 'apply-quick': {
      const d = app.build?.data;
      if (!d) break;
      const spell = d.spells?.[0];
      const rune = d.runes?.[0];
      if (spell) {
        const [spell1, spell2] = orderSpells(spell.ids, cs);
        if (spell1 !== cs?.spell1?.id || spell2 !== cs?.spell2?.id) await run('spells', { spell1, spell2 });
      }
      if (rune) await applyRune(rune, `OP.GG${d.opponentName ? ` vs ${d.opponentName}` : ''}`);
      break;
    }
    case 'pick-build-spell': {
      const sp = app.build?.data?.spells?.[Number(target.dataset.index)];
      closeSheet();
      if (sp) {
        const [spell1, spell2] = orderSpells(sp.ids, cs);
        await run('spells', { spell1, spell2 }, 'Spellek beállítva');
      }
      break;
    }
    case 'pick-build-rune': {
      const d = app.build?.data;
      const r = d?.runes?.[Number(target.dataset.index)];
      closeSheet();
      if (r) await applyRune(r, `OP.GG${d.opponentName ? ` vs ${d.opponentName}` : ''}`);
      break;
    }
    case 'hover-counter':
      await run('hover', { championId: Number(target.dataset.id) });
      break;
    case 'pick-rec': {
      const rec = app.recs?.items?.[Number(target.dataset.index)];
      closeSheet();
      if (rec) {
        await run('rune-apply-recommended', {
          primaryStyleId: rec.primaryStyleId,
          subStyleId: rec.subStyleId,
          perkIds: rec.perkIds,
          label: `ajánlott – ${rec.title}`
        }, 'Ajánlott rúnaoldal beállítva');
      }
      break;
    }
    case 'open-swaps':
      openSheet({ type: 'swap' });
      break;
    case 'do-swap': {
      const option = (cs?.swapOptions || [])[Number(target.dataset.index)];
      closeSheet();
      if (option) await run('swap', { isTrade: option.isTrade, swapId: option.id, verb: option.verb });
      break;
    }
    case 'swap-accept':
      if (cs?.incoming) await run('swap', { isTrade: cs.incoming.isTrade, swapId: cs.incoming.id, verb: 'accept' });
      break;
    case 'swap-decline':
      if (cs?.incoming) await run('swap', { isTrade: cs.incoming.isTrade, swapId: cs.incoming.id, verb: 'decline' });
      break;
    case 'toggle-auto-accept':
      await run('setting', { name: 'autoAccept', value: !app.snapshot?.settings?.autoAccept });
      break;
    case 'toggle-auto-template':
      await run('setting', { name: 'autoApplyTemplate', value: !app.snapshot?.settings?.autoApplyTemplate });
      break;
    case 'toggle-sound':
      storageSet('sound', soundEnabled() ? '0' : '1');
      if (soundEnabled()) beep([660, 880]);
      app.lastMainHtml = '';
      render();
      break;
    case 'template-save':
      await run('template-save', {}, 'Sablon mentve');
      break;
    case 'template-apply':
      await run('template-apply', {}, 'Sablon alkalmazva');
      break;
    case 'close-sheet':
      closeSheet();
      break;
    case 'unpair':
      app.code = null;
      app.authed = false;
      app.authError = null;
      storageSet('code', null);
      if (app.ws) app.ws.close();
      break;
  }
});

document.addEventListener('submit', event => {
  const form = event.target.closest('[data-form="pair"]');
  if (!form) return;
  event.preventDefault();
  const value = (document.getElementById('code-input')?.value || '').replace(/\D/g, '');
  if (value.length !== 6) {
    app.authError = 'A kód 6 számjegyből áll.';
    render();
    return;
  }
  app.code = value;
  app.authError = null;
  if (app.transport === 'poll') pollOnce();
  else if (!send({ type: 'auth', code: value, client: CLIENT_KIND })) connect();
});

document.addEventListener('input', event => {
  if (event.target.id === 'code-input') {
    event.target.value = event.target.value.replace(/\D/g, '').slice(0, 6);
    app.codeInput = event.target.value;
  } else if (event.target.matches('[data-search]')) {
    app.search = event.target.value;
    renderChampGrid();
  }
});

document.addEventListener('toggle', event => {
  if (event.target.matches?.('[data-log]')) storageSet('logOpen', event.target.open ? '1' : '0');
}, true);

document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible') {
    clearInterval(titleTimer);
    document.title = 'LolRemote';
    app.wakeLock = null;
    if (app.transport === 'poll') startPolling();
    else if (!app.ws) connect();
  }
});

setInterval(updateTimers, 250);
render();
connect();
