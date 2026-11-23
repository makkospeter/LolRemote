import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const root = process.argv[2];
const port = Number(process.argv[3] || 8765);
const CODE = '123456';

const champions = [
  [1, 'Annie'], [22, 'Ashe'], [64, 'Lee Sin'], [81, 'Ezreal'], [99, 'Lux'], [103, 'Ahri'],
  [157, 'Yasuo'], [222, 'Jinx'], [412, 'Thresh'], [17, 'Teemo'], [25, 'Morgana'], [20, 'Nunu & Willump']
].map(([id, name]) => ({ id, name }));
const nameOf = id => champions.find(c => c.id === id)?.name ?? null;
const spells = [[4, 'Flash'], [14, 'Ignite'], [12, 'Teleport'], [7, 'Heal'], [11, 'Smite'], [3, 'Exhaust']].map(([id, name]) => ({ id, name }));
const spellName = id => spells.find(s => s.id === id)?.name ?? `#${id}`;

const commands = [];
const fake = { blockWs: false, firewall: 'missing', host: '192.168.1.23', polled: 0 };
const log = [];
const addLog = (text, color = 'Gray') => log.push({ time: new Date().toTimeString().slice(0, 8), text, color });

const settings = { autoAccept: false, autoApplyTemplate: true };
const templates = {};
const world = {
  phase: 'Lobby',
  readyResponse: 'None',
  cs: null
};

function freshChampSelect() {
  return {
    timerPhase: 'BAN_PICK',
    action: { type: 'ban', championId: 0 },
    myTeam: [
      { cellId: 0, position: 'Top', championId: 0, intentId: 0, isMe: false },
      { cellId: 1, position: 'Jungle', championId: 0, intentId: 64, isMe: false },
      { cellId: 2, position: 'Mid', championId: 0, intentId: 0, isMe: true },
      { cellId: 3, position: 'ADC', championId: 0, intentId: 0, isMe: false },
      { cellId: 4, position: 'Support', championId: 0, intentId: 0, isMe: false }
    ],
    theirTeam: [0, 0, 0, 0, 0],
    myBans: [], theirBans: ['Yasuo'],
    spell1: 4, spell2: 14,
    incoming: null,
    swapOptions: [{ label: 'Pick sorrend csere: Top (még nem pickelt)', isTrade: false, id: 11, verb: 'request' }],
    runePages: [
      { id: 101, name: 'Mágus - Electrocute', summary: 'Mágus - Electrocute (Electrocute, Domination/Sorcery)', current: true },
      { id: 102, name: 'ADC - Lethal Tempo', summary: 'ADC - Lethal Tempo (Lethal Tempo, Precision/Resolve)', current: false }
    ]
  };
}

function snapshot() {
  const cs = world.cs;
  const myChamp = cs ? (cs.myTeam[2].championId || (cs.action?.type === 'pick' ? cs.action.championId : 0) || cs.myTeam[2].intentId) : 0;
  return {
    settings: { ...settings },
    phase: world.phase,
    phaseText: world.phase,
    secondsLeft: world.phase === 'ReadyCheck' ? 10 : cs ? 25 : null,
    readyCheck: world.phase === 'ReadyCheck' ? { response: world.readyResponse } : null,
    spells,
    champSelect: cs && {
      timerPhase: cs.timerPhase,
      myChampionId: myChamp,
      myAction: cs.action && { type: cs.action.type, championId: cs.action.championId, championName: cs.action.championId ? nameOf(cs.action.championId) : null },
      myTeam: cs.myTeam.map(m => ({ ...m, championName: m.championId ? nameOf(m.championId) : null, intentName: m.intentId ? nameOf(m.intentId) : null })),
      theirTeam: cs.theirTeam.map(id => ({ championId: id, championName: id ? nameOf(id) : null })),
      myBans: cs.myBans, theirBans: cs.theirBans,
      spell1: { id: cs.spell1, name: spellName(cs.spell1) },
      spell2: { id: cs.spell2, name: spellName(cs.spell2) },
      incoming: cs.incoming,
      swapOptions: cs.swapOptions,
      runePages: cs.runePages,
      template: myChamp ? { championId: myChamp, championName: nameOf(myChamp), exists: !!templates[myChamp], summary: templates[myChamp] || null } : null
    }
  };
}

function execute(msg) {
  commands.push(msg);
  const cs = world.cs;
  switch (msg.cmd) {
    case 'accept':
      if (world.phase !== 'ReadyCheck') return { error: 'Nincs elfogadható meccs.' };
      world.readyResponse = 'Accepted';
      addLog('Elfogadtad a meccset.', 'Cyan');
      setTimeout(() => { world.phase = 'ChampSelect'; world.cs = freshChampSelect(); addLog('>>> TE JÖSSZ: BAN', 'Green'); broadcast(); }, 400);
      return {};
    case 'decline':
      world.phase = 'Lobby';
      return {};
    case 'champions':
      return { data: champions };
    case 'hover':
      if (!cs?.action) return { error: 'Most nem te jössz.' };
      cs.action.championId = msg.championId;
      if (cs.action.type === 'pick') cs.myTeam[2].intentId = msg.championId;
      addLog(`Kijelölve: ${nameOf(msg.championId)}`, 'Cyan');
      return {};
    case 'lock':
      if (!cs?.action?.championId) return { error: 'Előbb jelölj ki egy championt.' };
      if (cs.action.type === 'ban') {
        cs.myBans.push(nameOf(cs.action.championId));
        addLog(`Te bannoltad: ${nameOf(cs.action.championId)}`, 'Cyan');
        cs.action = { type: 'pick', championId: 0 };
      } else {
        cs.myTeam[2].championId = cs.action.championId;
        cs.myTeam[2].intentId = 0;
        addLog(`Te lockoltad: ${nameOf(cs.action.championId)}`, 'Cyan');
        cs.action = null;
        cs.timerPhase = 'FINALIZATION';
        cs.myTeam[3].championId = 222;
        cs.theirTeam = [17, 1, 99, 81, 412];
        cs.incoming = { label: 'ADC (Jinx) champion cserét kér', isTrade: true, id: 4 };
        cs.swapOptions = [{ label: `Champion csere: ${nameOf(cs.myTeam[2].championId)} <-> ADC (Jinx)`, isTrade: true, id: 4, verb: 'request' }];
      }
      return {};
    case 'spells':
      if (msg.spell1 === msg.spell2) return { error: 'A két spell nem lehet ugyanaz.' };
      cs.spell1 = msg.spell1;
      cs.spell2 = msg.spell2;
      return {};
    case 'rune':
      if (!cs) return { error: 'Rúnaoldalt csak champ selectben lehet választani.' };
      cs.runePages.forEach(p => { p.current = p.id === msg.pageId; });
      return {};
    case 'swap':
      if (msg.verb === 'accept' && cs.incoming) {
        const mine = cs.myTeam[2].championId;
        cs.myTeam[2].championId = cs.myTeam[3].championId;
        cs.myTeam[3].championId = mine;
        cs.incoming = null;
      } else if (msg.verb === 'decline') {
        cs.incoming = null;
      }
      return {};
    case 'setting':
      if (!(msg.name in settings)) return { error: 'Ismeretlen beállítás.' };
      settings[msg.name] = !!msg.value;
      return {};
    case 'template-save': {
      const champ = cs?.myTeam[2].championId;
      if (!champ) return { error: 'Előbb válassz championt.' };
      const rune = cs.runePages.find(p => p.current);
      templates[champ] = `${rune.name} · ${spellName(cs.spell1)} + ${spellName(cs.spell2)}`;
      return {};
    }
    case 'counters': {
      const name = nameOf(msg.championId) || `#${msg.championId}`;
      const me = cs ? (cs.myTeam[2].championId || cs.action?.championId || cs.myTeam[2].intentId) : 0;
      const pool = [22, 99, 25, 1, 17, 157, 81, 412, 64, 222, 20, 103].filter(id => id !== msg.championId);
      const items = pool.map((id, i) => ({ championId: id, name: nameOf(id), winRate: 56 - i * 0.7, games: 900 - i * 60, pickable: i % 3 !== 2 }));
      return { data: { championId: msg.championId, championName: name, isMe: msg.championId === me, position: 'mid', items, source: 'OP.GG' } };
    }
    case 'build': {
      const opponentId = msg.opponentId || 99;
      return { data: {
        championId: 103, championName: 'Ahri', position: 'mid',
        opponentId, opponentName: nameOf(opponentId), opponentGuessed: !msg.opponentId,
        tip: 'Do not use Charm until the enemy closes in.', playStyle: 'defensive',
        spells: [
          { ids: [4, 14], names: 'Flash + Ignite', games: 4146, winRate: 52.7 },
          { ids: [4, 12], names: 'Flash + Teleport', games: 1114, winRate: 53.1 }
        ],
        runes: ['Electrocute', 'Deathfire Touch', 'Arcane Comet'].map((title, i) => ({
          primaryStyleId: 8100, subStyleId: 8200, perkIds: [8112, 8139, 8140, 8106, 8210, 8226, 5005, 5008, 5001],
          title, detail: 'Domination / Sorcery · Taste of Blood, Grisly Mementos', games: 2439 - i * 900, winRate: 50.1 + i
        }))
      } };
    }
    case 'rune-recommendations': {
      const positions = msg.all ? ['MIDDLE', 'TOP', 'JUNGLE', 'BOTTOM', 'UTILITY'] : ['MIDDLE'];
      const items = positions.flatMap(position => [0, 1, 2].map(i => ({
        position, positionText: position[0] + position.slice(1).toLowerCase(), isMine: position === 'MIDDLE',
        title: ['Electrocute', 'Arcane Comet', 'Conqueror'][i], detail: 'Domination / Sorcery · ...',
        primaryStyleId: 8100, subStyleId: 8200, perkIds: [8112, 8139, 8138, 8106, 8226, 8237, 5008, 5008, 5011]
      })));
      return { data: { championName: 'Ahri', myPosition: 'MIDDLE', all: !!msg.all, items } };
    }
    case 'rune-apply-recommended':
      if (!Array.isArray(msg.perkIds) || msg.perkIds.length !== 9) return { error: 'Érvénytelen rúnaoldal.' };
      cs.runePages.forEach(p => { p.current = false; });
      cs.runePages.push({ id: 900, name: `LolRemote: Ahri ${msg.label}`, summary: msg.label, current: true });
      return {};
    case 'template-apply':
      if (!templates[cs?.myTeam[2].championId]) return { error: 'Ehhez a championhoz nincs mentett sablon.' };
      return {};
    default:
      return { error: 'Ismeretlen parancs.' };
  }
}

const clients = new Set();

function frame(text) {
  const payload = Buffer.from(text);
  let header;
  if (payload.length < 126) header = Buffer.from([0x81, payload.length]);
  else if (payload.length < 65536) { header = Buffer.alloc(4); header[0] = 0x81; header[1] = 126; header.writeUInt16BE(payload.length, 2); }
  else { header = Buffer.alloc(10); header[0] = 0x81; header[1] = 127; header.writeBigUInt64BE(BigInt(payload.length), 2); }
  return Buffer.concat([header, payload]);
}

function sendTo(client, obj) { client.socket.write(frame(JSON.stringify(obj))); }

function broadcast() {
  for (const c of clients) if (c.authed) sendTo(c, { type: 'state', connected: true, snapshot: snapshot(), log: log.slice(-15) });
}

function parseFrames(client, chunk) {
  client.buffer = Buffer.concat([client.buffer, chunk]);
  while (client.buffer.length >= 2) {
    const b = client.buffer;
    const opcode = b[0] & 0x0f;
    let len = b[1] & 0x7f;
    let offset = 2;
    if (len === 126) { if (b.length < 4) return; len = b.readUInt16BE(2); offset = 4; }
    else if (len === 127) { if (b.length < 10) return; len = Number(b.readBigUInt64BE(2)); offset = 10; }
    const masked = (b[1] & 0x80) !== 0;
    const maskOffset = offset;
    if (masked) offset += 4;
    if (b.length < offset + len) return;
    const data = Buffer.from(b.subarray(offset, offset + len));
    if (masked) for (let i = 0; i < data.length; i++) data[i] ^= b[maskOffset + (i % 4)];
    client.buffer = b.subarray(offset + len);
    if (opcode === 8) { client.socket.end(); return; }
    if (opcode === 1) onMessage(client, data.toString('utf8'));
  }
}

function onMessage(client, text) {
  const msg = JSON.parse(text);
  if (msg.type === 'auth') {
    client.authed = msg.code === CODE;
    client.kind = msg.client;
    sendTo(client, { type: 'auth', ok: client.authed });
    if (client.authed) broadcast();
    return;
  }
  if (!client.authed) return sendTo(client, { type: 'auth', ok: false });
  if (msg.type === 'cmd') {
    const result = execute(msg);
    sendTo(client, { type: 'result', id: msg.id, error: result.error ?? null, data: result.data ?? null });
    broadcast();
  }
}

const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.webmanifest': 'application/manifest+json' };

const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname === '/__test/phase') {
    world.phase = url.searchParams.get('name');
    world.readyResponse = 'None';
    if (world.phase !== 'ChampSelect') world.cs = null;
    if (world.phase === 'ReadyCheck') addLog('MEGVAN A MECCS! Fogadd el!', 'Yellow');
    broadcast();
    res.end('ok');
    return;
  }
  if (url.pathname === '/__test/commands') {
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify(commands));
    return;
  }
  if (url.pathname === '/setup-info') {
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ pairUrl: `http://192.168.1.23/?k=${CODE}`, baseUrl: 'http://192.168.1.23', shortUrl: 'http://lolremote.local', code: CODE,
      phones: [...clients].filter(c => c.authed && c.kind !== 'desktop').length + (Date.now() - fake.polled < 5000 ? 1 : 0), league: true,
      firewall: fake.firewall, hosts: ['192.168.1.23', '10.8.0.2'].map(a => ({ address: a, name: a.startsWith('10.') ? 'virtuális / VPN · Radmin' : 'Ethernet · Ethernet', selected: a === fake.host })), phaseText: world.phase, mode: 'Ranked Solo/Duo', version: '1.0.0' }));
    return;
  }
  if (url.pathname === '/__test/blockws') { fake.blockWs = url.searchParams.get('on') === '1'; res.end('ok'); return; }
  if (url.pathname === '/setup-firewall') { fake.firewall = 'ok'; res.setHeader('content-type', 'application/json'); res.end(JSON.stringify({ error: null, firewall: 'ok' })); return; }
  if (url.pathname === '/setup-host') { fake.host = url.searchParams.get('address'); res.end('ok'); return; }
  if (url.pathname === '/api/state' || url.pathname === '/api/cmd') {
    if (req.headers['x-lolremote-code'] !== CODE) { res.statusCode = 401; res.end(); return; }
    if (req.headers['x-lolremote-client'] !== 'desktop') fake.polled = Date.now();
    res.setHeader('content-type', 'application/json');
    if (url.pathname === '/api/state') { res.end(JSON.stringify({ type: 'state', connected: true, snapshot: snapshot(), log: log.slice(-15) })); return; }
    let body = '';
    req.on('data', c => { body += c; });
    req.on('end', () => {
      const result = execute(JSON.parse(body));
      res.end(JSON.stringify({ error: result.error ?? null, data: result.data ?? null }));
      broadcast();
    });
    return;
  }
  if (url.pathname === '/setup-qr.png') { res.setHeader('content-type', 'image/png'); res.end(fs.readFileSync(path.join(root, 'icon-512.png'))); return; }
  if (url.pathname.startsWith('/icon/')) { res.statusCode = 404; res.end(); return; }
  const file = path.join(root, url.pathname === '/' ? 'index.html' : url.pathname);
  if (!file.startsWith(root) || !fs.existsSync(file)) { res.statusCode = 404; res.end(); return; }
  res.setHeader('content-type', types[path.extname(file)] || 'application/octet-stream');
  res.end(fs.readFileSync(file));
});

server.on('upgrade', (req, socket) => {
  if (fake.blockWs) { socket.destroy(); return; }
  const key = req.headers['sec-websocket-key'];
  const accept = crypto.createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
  const client = { socket, buffer: Buffer.alloc(0), authed: false };
  clients.add(client);
  socket.on('data', chunk => parseFrames(client, chunk));
  socket.on('close', () => clients.delete(client));
  socket.on('error', () => clients.delete(client));
});

server.listen(port, () => console.log(`fake companion on ${port}`));
