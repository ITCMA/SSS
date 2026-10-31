'use strict';

// SSS front end: a dependency-free single page.
// - All URLs are relative ("api/me"), because the app is hosted below a path such as /sss/ in IIS.
// - Text is only ever written with textContent / attributes, never innerHTML (XSS).
// - Which sessions are shown is decided by the server from the Windows identity; this file only renders.

const LANGS = ['en', 'de', 'fr', 'es', 'zh'];
const $ = (id) => document.getElementById(id);
const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* ignore */ } },
};

const state = {
  lang: 'en', dict: {}, fallback: {}, range: '24h', filter: '', csrf: null, data: null, timer: null,
  sort: { live: { key: 'started', dir: -1 }, hist: { key: 'ended', dir: -1 }, hd: { key: 'user', dir: 1 } },
  // view: 'mine' = own sessions, 'hdlist' = helpdesk search/overview, 'hduser' = sessions of the chosen user
  view: 'mine', target: null, helpdesk: false, reasonRequired: false, hdFilter: '', hdLive: null, expert: false, detailsCtx: null, msgTarget: null, limits: { rttWarn: 80, loadWarn: 7000 },
};

const t = (key, vars = {}) => {
  let s = state.dict[key] ?? state.fallback[key] ?? key;
  for (const [k, v] of Object.entries(vars)) s = s.replaceAll(`{${k}}`, v);
  return s;
};

// ---------- i18n ----------
async function loadDict(lang) {
  const r = await fetch(`i18n/${lang}.json`, { credentials: 'same-origin' });
  if (!r.ok) throw new Error('i18n');
  return r.json();
}

async function setLang(lang) {
  if (!LANGS.includes(lang)) lang = 'en';
  state.lang = lang;
  state.dict = lang === 'en' ? state.fallback : await loadDict(lang);
  document.documentElement.lang = lang;
  $('lang').value = lang;
  store.set('sss.lang', lang);
  for (const el of document.querySelectorAll('[data-i18n]')) el.textContent = t(el.dataset.i18n);
  for (const el of document.querySelectorAll('[data-i18n-ph]')) el.placeholder = t(el.dataset.i18nPh);
  buildHeads();
  render();
  renderHd();
}

// ?lang=xx and ?theme=light|dark let the README screenshots be reproduced; they only set the usual preferences.
const QUERY = new URLSearchParams(location.search);

function detectLang() {
  const q = QUERY.get('lang');
  if (q && LANGS.includes(q)) return q;
  const saved = store.get('sss.lang');
  if (saved && LANGS.includes(saved)) return saved;
  const nav = (navigator.language || 'en').slice(0, 2).toLowerCase();
  return LANGS.includes(nav) ? nav : 'en';
}

// ---------- formatting ----------
const fmtDate = (iso) => iso ? new Intl.DateTimeFormat(state.lang, { dateStyle: 'short', timeStyle: 'short' }).format(new Date(iso)) : '–';

function fmtDur(seconds) {
  if (seconds == null || seconds < 0) return '–';
  const d = Math.floor(seconds / 86400), h = Math.floor(seconds % 86400 / 3600), m = Math.floor(seconds % 3600 / 60);
  if (d) return `${d}d ${h}h`;
  if (h) return `${h}h ${m}m`;
  return `${m}m`;
}
const durationOf = (s) => Math.round(((s.endTime ? new Date(s.endTime) : new Date()) - new Date(s.startTime)) / 1000);

// ---------- columns ----------
const LIVE_COLS = [
  { key: 'resource', label: 'colResource', val: (s) => s.resourceName.toLowerCase() },
  { key: 'group', label: 'colGroup', val: (s) => s.deliveryGroup.toLowerCase() },
  { key: 'machine', label: 'colMachine', val: (s) => s.machine.toLowerCase() },
  { key: 'state', label: 'colState', val: (s) => s.state },
  { key: 'started', label: 'colStarted', val: (s) => s.startTime },
  { key: 'duration', label: 'colDuration', val: durationOf },
  { key: 'idle', label: 'colIdle', val: (s) => s.idleSeconds ?? -1 },
  { key: 'client', label: 'colClient', val: (s) => (s.clientName ?? '').toLowerCase() },
  { key: 'quality', label: 'colQuality', val: (s) => worst(s.quality) },
  { key: 'action', label: 'colAction', val: null },
];
const HIST_COLS = [
  { key: 'resource', label: 'colResource', val: (s) => s.resourceName.toLowerCase() },
  { key: 'group', label: 'colGroup', val: (s) => s.deliveryGroup.toLowerCase() },
  { key: 'machine', label: 'colMachine', val: (s) => s.machine.toLowerCase() },
  { key: 'started', label: 'colStarted', val: (s) => s.startTime },
  { key: 'ended', label: 'colEnded', val: (s) => s.endTime },
  { key: 'duration', label: 'colDuration', val: durationOf },
  { key: 'client', label: 'colClient', val: (s) => (s.clientName ?? '').toLowerCase() },
  { key: 'quality', label: 'colQuality', val: (s) => worst(s.quality) },
  { key: 'action', label: 'colAction', val: null, expertOnly: true },
];

// Farm-wide overview for the helpdesk (rows are { session, userName, userSid })
const HD_COLS = [
  { key: 'user', label: 'colUser', val: (h) => h.userName.toLowerCase() },
  { key: 'resource', label: 'colResource', val: (h) => h.session.resourceName.toLowerCase() },
  { key: 'group', label: 'colGroup', val: (h) => h.session.deliveryGroup.toLowerCase() },
  { key: 'machine', label: 'colMachine', val: (h) => h.session.machine.toLowerCase() },
  { key: 'state', label: 'colState', val: (h) => h.session.state },
  { key: 'started', label: 'colStarted', val: (h) => h.session.startTime },
  { key: 'client', label: 'colClient', val: (h) => (h.session.clientName ?? '').toLowerCase() },
  { key: 'action', label: 'colAction', val: null },
];

const RANK = { unknown: 0, good: 1, warning: 2, critical: 3 };
const worst = (q) => Math.max(RANK[q.frontend], RANK[q.backend]);

function el(tag, props = {}, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === 'class') e.className = v; else if (k === 'text') e.textContent = v; else e.setAttribute(k, v);
  }
  for (const k of kids) if (k) e.append(k);
  return e;
}

function buildHeads() {
  for (const [id, cols, key] of [['live', LIVE_COLS, 'live'], ['hist', HIST_COLS, 'hist'], ['hd-live', HD_COLS, 'hd']]) {
    const tr = el('tr');
    for (const c of cols.filter((c) => !c.expertOnly || state.expert)) {
      const th = el('th', { scope: 'col' });
      const so = state.sort[key];
      th.setAttribute('aria-sort', c.val && so.key === c.key ? (so.dir > 0 ? 'ascending' : 'descending') : 'none');
      if (c.val) {
        const b = el('button', { type: 'button', text: t(c.label) });
        b.addEventListener('click', () => {
          so.dir = so.key === c.key ? -so.dir : 1;
          so.key = c.key;
          buildHeads();
          render();
          renderHd();
        });
        th.append(b);
      } else {
        th.textContent = t(c.label);
      }
      tr.append(th);
    }
    const thead = $(id).tHead;
    thead.replaceChildren(tr);
  }
}

// ---------- rendering ----------
const SYMBOL = { good: '✔', warning: '▲', critical: '✖', unknown: '?' };

function qualityCell(q) {
  const line = (labelKey, health, detail) => el('span', { class: `badge h-${health}`, title: detail },
    el('span', { class: 'dot', 'aria-hidden': 'true', text: SYMBOL[health] }),
    el('span', { text: `${t(labelKey)}: ${t('health' + health[0].toUpperCase() + health.slice(1))}` }));
  const fe = [q.icaRttMs != null ? `${t('rtt')} ${q.icaRttMs} ms` : null,
    q.bandwidthKbps != null ? `${t('bandwidth')} ${(q.bandwidthKbps / 1000).toFixed(1)} Mbit/s` : null].filter(Boolean).join(' · ');
  const be = [q.logonSeconds != null ? `${t('logon')} ${q.logonSeconds} s` : null,
    q.loadIndex != null ? `${t('load')} ${q.loadIndex}` : null].filter(Boolean).join(' · ');
  const hints = [];
  if (q.frontend === 'warning' || q.frontend === 'critical') {
    // name the metric that caused the rating, so the hint is specific
    if (q.icaRttMs != null && q.icaRttMs >= (state.limits.rttWarn)) hints.push(t('hintClientRtt'));
    else hints.push(t('hintClientBw'));
  }
  if (q.backend === 'warning' || q.backend === 'critical') hints.push(t(q.loadIndex != null && q.loadIndex >= state.limits.loadWarn ? 'hintBackendLoad' : 'hintBackendLogon'));
  const box = el('div', { class: 'q' }, line('qualityClient', q.frontend, fe), line('qualityBackend', q.backend, be));
  for (const h of hints) box.append(el('small', { class: 'hint', text: h }));
  return box;
}

function cell(label, ...kids) {
  const td = el('td', { 'data-label': label });
  td.append(...kids.filter((k) => k != null));
  return td;
}

function matches(s, f) {
  if (!f) return true;
  return [s.resourceName, s.deliveryGroup, s.machine, s.clientName, s.clientAddress, s.resourceType]
    .some((v) => (v ?? '').toLowerCase().includes(f));
}

function sorted(list, cols, so) {
  const col = cols.find((c) => c.key === so.key && c.val);
  if (!col) return list;
  return [...list].sort((a, b) => {
    const x = col.val(a), y = col.val(b);
    return (x < y ? -1 : x > y ? 1 : 0) * so.dir;
  });
}

// Buttons of a row: log off (live sessions), expert details, and (helpdesk) a message to the session.
function actions(s, logoffBtn) {
  const box = el('div', { class: 'rowactions' });
  if (logoffBtn) box.append(logoffBtn);
  if (state.expert && s.sessionKey) {
    const b = el('button', { type: 'button', text: t('details'), 'aria-label': `${t('details')}: ${s.resourceName} (${s.machine})` });
    b.addEventListener('click', () => openDetails(s));
    box.append(b);
  }
  if (state.view === 'hduser' && logoffBtn) {
    const m = el('button', { type: 'button', text: t('hdMessage'), 'aria-label': `${t('hdMessage')}: ${s.resourceName}` });
    m.addEventListener('click', () => openMessage(s));
    box.append(m);
  }
  return box;
}

function resCell(s) {
  const td = el('td', { 'data-label': t('colResource'), class: 'res' });
  td.append(el('strong', { text: s.resourceName }),
    el('small', { text: t(s.resourceType === 'Desktop' ? 'typeDesktop' : 'typeApp') }));
  return td;
}

function render() {
  // headings that depend on the view (setLang resets the data-i18n texts, so set them on every render)
  if (state.view === 'hduser' && state.target) {
    $('live-h').textContent = t('hdSessionsOf', { name: state.target.name });
    $('target-name').textContent = t('hdSessionsOf', { name: state.target.name });
  } else {
    $('live-h').textContent = t('liveTitle');
  }
  if (!state.data) return;
  const f = state.filter.trim().toLowerCase();
  const live = sorted(state.data.live.filter((s) => matches(s, f)), LIVE_COLS, state.sort.live);
  const hist = sorted(state.data.history.filter((s) => matches(s, f)), HIST_COLS, state.sort.hist);

  const lb = $('live').tBodies[0];
  lb.replaceChildren(...live.map((s) => {
    const btn = el('button', { type: 'button', class: 'danger-outline', text: t('logoff'),
      'aria-label': `${t('logoff')}: ${s.resourceName} (${s.machine})` });
    btn.addEventListener('click', () => logoffSelected(s, t('confirmOne', { name: s.resourceName, machine: s.machine })));
    return el('tr', {},
      resCell(s),
      cell(t('colGroup'), s.deliveryGroup),
      cell(t('colMachine'), s.machine),
      cell(t('colState'), el('span', { class: `state-${s.state}`, text: t(s.state === 'active' ? 'stateActive' : 'stateDisconnected') })),
      cell(t('colStarted'), fmtDate(s.startTime)),
      cell(t('colDuration'), fmtDur(durationOf(s))),
      cell(t('colIdle'), fmtDur(s.idleSeconds)),
      cell(t('colClient'), `${s.clientName ?? '–'}${s.clientAddress ? ` (${s.clientAddress})` : ''}`),
      cell(t('colQuality'), qualityCell(s.quality)),
      cell(t('colAction'), actions(s, btn)));
  }));
  $('live-empty').hidden = live.length > 0;
  $('live').hidden = live.length === 0;

  const hb = $('hist').tBodies[0];
  hb.replaceChildren(...hist.map((s) => el('tr', {},
    resCell(s),
    cell(t('colGroup'), s.deliveryGroup),
    cell(t('colMachine'), s.machine),
    cell(t('colStarted'), fmtDate(s.startTime)),
    cell(t('colEnded'), fmtDate(s.endTime)),
    cell(t('colDuration'), fmtDur(durationOf(s))),
    cell(t('colClient'), `${s.clientName ?? '–'}${s.clientAddress ? ` (${s.clientAddress})` : ''}`),
    cell(t('colQuality'), qualityCell(s.quality)),
    ...(state.expert ? [cell(t('colAction'), actions(s))] : []))));
  $('hist-empty').hidden = hist.length > 0;
  $('hist').hidden = hist.length === 0;

  const all = state.data.live;
  $('logoff-disc').hidden = !all.some((s) => s.state === 'disconnected');
  $('logoff-all').hidden = all.length === 0;
  $('updated').textContent = t('lastUpdated', { time: new Intl.DateTimeFormat(state.lang, { timeStyle: 'medium' }).format(new Date(state.data.generatedAt)) });
  $('export').href = `api/sessions.csv?range=${encodeURIComponent(state.range)}`;
  $('export').hidden = state.view !== 'mine';
}

// ---------- helpdesk ----------
// Which view is visible. The server enforces who may use the helpdesk; this only shows or hides.
function showView(view) {
  state.view = view;
  state.data = null;
  const hdList = view === 'hdlist';
  $('hd').hidden = !hdList;
  $('sys').hidden = view !== 'sys';
  $('panel').hidden = hdList || view === 'sys';
  $('target').hidden = view !== 'hduser';
  $('tabs').hidden = !state.helpdesk;
  $('tab-mine').setAttribute('aria-pressed', String(view === 'mine'));
  $('tab-hd').setAttribute('aria-pressed', String(view === 'hdlist' || view === 'hduser'));
  $('tab-sys').setAttribute('aria-pressed', String(view === 'sys'));
  setStatus('');
  render();
  load();
}

function openUser(sid, name) {
  state.target = { sid, name };
  $('reason').value = '';
  showView('hduser');
}

function renderHd() {
  const d = state.hdLive;
  if (!d) return;
  const f = state.hdFilter.trim().toLowerCase();
  const rows = sorted(d.sessions.filter((h) => !f || [h.userName, h.session.resourceName, h.session.deliveryGroup, h.session.machine, h.session.clientName]
    .some((v) => (v ?? '').toLowerCase().includes(f))), HD_COLS, state.sort.hd);
  $('hd-live').tBodies[0].replaceChildren(...rows.map((h) => {
    const s = h.session;
    const open = el('button', { type: 'button', text: t('hdOpen'), 'aria-label': `${t('hdOpen')}: ${h.userName}` });
    open.addEventListener('click', () => openUser(h.userSid, h.userName));
    return el('tr', {},
      cell(t('colUser'), h.userName),
      resCell(s),
      cell(t('colGroup'), s.deliveryGroup),
      cell(t('colMachine'), s.machine),
      cell(t('colState'), el('span', { class: `state-${s.state}`, text: t(s.state === 'active' ? 'stateActive' : 'stateDisconnected') })),
      cell(t('colStarted'), fmtDate(s.startTime)),
      cell(t('colClient'), `${s.clientName ?? '–'}${s.clientAddress ? ` (${s.clientAddress})` : ''}`),
      cell(t('colAction'), open));
  }));
  $('hd-live').hidden = rows.length === 0;
  $('hd-live-empty').hidden = rows.length > 0;
  $('hd-trunc').hidden = !d.truncated;
}

async function loadHdList() {
  const r = await fetch('api/helpdesk/live', { credentials: 'same-origin' });
  if (r.status === 403) { state.helpdesk = false; showView('mine'); return; }
  if (r.status === 503) { setStatus(t('unavailable'), true); return; }
  if (r.status === 429) { setStatus(t('rateLimited'), true); return; }
  if (!r.ok) throw new Error(String(r.status));
  state.hdLive = await r.json();
  renderHd();
  $('updated').textContent = t('lastUpdated', { time: new Intl.DateTimeFormat(state.lang, { timeStyle: 'medium' }).format(new Date()) });
}

async function searchUsers(q) {
  const list = $('hd-users');
  list.replaceChildren();
  $('hd-nousers').hidden = true;
  try {
    const r = await fetch(`api/helpdesk/users?q=${encodeURIComponent(q)}`, { credentials: 'same-origin' });
    if (r.status === 429) { setStatus(t('rateLimited'), true); return; }
    if (r.status === 503) { setStatus(t('unavailable'), true); return; }
    if (!r.ok) throw new Error(String(r.status));
    const users = await r.json();
    list.replaceChildren(...users.map((u) => {
      const b = el('button', { type: 'button', text: u.name });
      if (u.hasLiveSession) b.append(el('small', { text: t('hdLiveBadge') }));
      b.addEventListener('click', () => openUser(u.sid, u.name));
      return el('li', {}, b);
    }));
    $('hd-nousers').hidden = users.length > 0;
    setStatus('');
  } catch {
    setStatus(t('error'), true);
  }
}

async function logoffHd(scope, uids, question) {
  if (state.reasonRequired && !$('reason').value.trim()) { setStatus(t('hdReasonRequired'), true); $('reason').focus(); return; }
  if (!(await confirmDialog(question))) return;
  setStatus('…');
  try {
    const r = await fetch('api/helpdesk/logoff', {
      method: 'POST', credentials: 'same-origin',
      headers: { 'X-CSRF-TOKEN': await csrf(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ sid: state.target.sid, scope, uids, reason: $('reason').value }),
    });
    if (r.status === 429) { setStatus(t('rateLimited'), true); return; }
    if (!r.ok) throw new Error(String(r.status));
    const res = await r.json();
    setStatus(res.rejected.length ? t('partial', { n: res.loggedOff.length, m: res.rejected.length }) : t('done', { n: res.loggedOff.length }));
  } catch {
    state.csrf = null;
    setStatus(t('error'), true);
  }
  await load();
}

const logoffSelected = (s, question) => state.view === 'hduser'
  ? logoffHd('selected', [s.uid], question)
  : logoff(`api/sessions/${encodeURIComponent(s.uid)}/logoff`, question);

// ---------- status page (helpdesk) ----------
const CHECK_LABEL = { version: 'stVersion', provider: 'stProvider', identity: 'stIdentity', uptime: 'stUptime', audit: 'stAudit',
  helpdesk: 'stHelpdesk', broker: 'stBroker', monitor: 'stMonitor', 'monitor-https': 'stMonitorHttps' };

async function loadSys() {
  const r = await fetch('api/helpdesk/status', { credentials: 'same-origin' });
  if (r.status === 403) { state.helpdesk = false; showView('mine'); return; }
  if (r.status === 429) { setStatus(t('rateLimited'), true); return; }
  if (!r.ok) throw new Error(String(r.status));
  const checks = await r.json();
  const tr = el('tr', {}, el('th', { scope: 'col', text: t('colCheck') }), el('th', { scope: 'col', text: t('colResult') }), el('th', { scope: 'col', text: '' }));
  $('sys-table').tHead.replaceChildren(tr);
  $('sys-table').tBodies[0].replaceChildren(...checks.map((c) => {
    const health = c.ok === true ? 'good' : c.ok === false ? 'critical' : 'warning';
    const word = c.ok === true ? 'statusOk' : c.ok === false ? 'statusFail' : 'statusWarn';
    return el('tr', {},
      cell(t('colCheck'), t(CHECK_LABEL[c.id] ?? c.id)),
      cell(t('colResult'), el('span', { class: `badge h-${health}` },
        el('span', { class: 'dot', 'aria-hidden': 'true', text: SYMBOL[health] }), el('span', { text: t(word) }))),
      cell('', c.detail));
  }));
}

// ---------- expert details ----------
const GROUP_TITLE = { session: 'groupSession', logon: 'groupLogon', client: 'groupClient', network: 'groupNetwork', machine: 'groupMachine', technical: 'groupTechnical' };
const TABLE_TITLE = { connections: 'tableConnections', failures: 'tableFailures', applications: 'tableApplications' };
const SVGNS = 'http://www.w3.org/2000/svg';
const svgEl = (tag, attrs = {}) => { const e = document.createElementNS(SVGNS, tag); for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v); return e; };
const ISO = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/;

// Citrix durations are milliseconds; show seconds as well once they are long enough to be hard to read.
function fmtItem(i) {
  if (ISO.test(i.value)) return fmtDate(i.value);
  if (i.unit === 'ms' || (/Duration$/.test(i.key) && /^\d+$/.test(i.value))) {
    const ms = Number(i.value);
    return ms >= 1000 ? `${ms} ms (${(ms / 1000).toFixed(1)} s)` : `${ms} ms`;
  }
  return i.unit ? `${i.value} ${i.unit}` : i.value;
}

// "IcaRttMS" -> "Ica Rtt MS": Citrix property names are shown as they are, only easier to read.
const fieldLabel = (k) => k.replace(/([a-z0-9])([A-Z])/g, '$1 $2');

function groupBox(g) {
  const dl = el('dl');
  for (const i of g.items) dl.append(el('dt', { text: fieldLabel(i.key) }), el('dd', { text: fmtItem(i) }));
  return el('section', { class: 'group' }, el('h3', { text: t(GROUP_TITLE[g.id] ?? g.id) }), dl);
}

function chart(title, series) {
  const W = 420, H = 160, L = 44, R = 8, T = 12, B = 22;
  const pts = series.flatMap((s) => s.points);
  const x0 = Math.min(...pts.map((p) => p[0])), x1 = Math.max(...pts.map((p) => p[0]));
  const y1 = Math.max(1, ...pts.map((p) => p[1]));
  const sx = (x) => L + (x1 === x0 ? 0 : (x - x0) / (x1 - x0)) * (W - L - R);
  const sy = (y) => H - B - (y / y1) * (H - T - B);
  const svg = svgEl('svg', { viewBox: `0 0 ${W} ${H}`, role: 'img', 'aria-label': title });
  svg.append(svgEl('line', { class: 'axis', x1: L, y1: H - B, x2: W - R, y2: H - B }), svgEl('line', { class: 'axis', x1: L, y1: T, x2: L, y2: H - B }));
  const label = (txt, x, y, anchor) => { const e = svgEl('text', { x, y, 'text-anchor': anchor }); e.textContent = txt; return e; };
  svg.append(label(String(Math.round(y1)), L - 4, T + 8, 'end'), label('0', L - 4, H - B, 'end'));
  const tf = new Intl.DateTimeFormat(state.lang, { timeStyle: 'short' });
  svg.append(label(tf.format(x0), L, H - 5, 'start'), label(tf.format(x1), W - R, H - 5, 'end'));
  const legend = el('ul', { class: 'legend' });
  series.forEach((s, n) => {
    svg.append(svgEl('polyline', { class: `line s${n % 4}`, points: s.points.map((p) => `${sx(p[0]).toFixed(1)},${sy(p[1]).toFixed(1)}`).join(' ') }));
    const last = s.points[s.points.length - 1][1];
    legend.append(el('li', { class: `k${n % 4}`, text: `■ ${fieldLabel(s.name)}${s.unit ? ` (${s.unit})` : ''}: ${Math.round(last)}` }));
  });
  return el('figure', { class: 'chart' }, el('h3', { text: title }), svg, legend);
}

function buildCharts(series) {
  const is = (re) => (s) => re.test(s.name);
  const bw = series.filter(is(/Bandwidth/i));
  const dc = series.filter(is(/^(Dc|Server)/i));
  const net = series.filter((s) => /Latency|Rtt/i.test(s.name) && !/^(Dc|Server)/i.test(s.name) && !/Bandwidth/i.test(s.name));
  const used = new Set([...bw, ...dc, ...net]);
  const more = series.filter((s) => !used.has(s)).slice(0, 6);
  const box = el('div', { class: 'charts' });
  for (const [title, list] of [[t('chartNetwork'), net], [t('chartBandwidth'), bw], [t('chartDatacenter'), dc]])
    if (list.length) box.append(chart(title, list.slice(0, 4)));
  for (const s of more) box.append(chart(`${t('chartMore')}: ${fieldLabel(s.name)}`, [s]));
  return box.childElementCount ? box : el('p', { class: 'muted', text: t('chartNoData') });
}

function detailTable(tb) {
  const cols = [...new Set(tb.rows.flatMap((r) => r.map((i) => i.key)))].filter((k) => !/(Id|Key)$/.test(k)).slice(0, 12);
  const table = el('table', { class: 'smalltable' });
  table.append(el('caption', { class: 'sr', text: t(TABLE_TITLE[tb.id] ?? tb.id) }),
    el('thead', {}, el('tr', {}, ...cols.map((c) => el('th', { scope: 'col', text: fieldLabel(c) })))),
    el('tbody', {}, ...tb.rows.map((r) => el('tr', {}, ...cols.map((c) => {
      const i = r.find((x) => x.key === c);
      return el('td', { 'data-label': fieldLabel(c), text: i ? fmtItem(i) : '' });
    })))));
  return el('section', {}, el('h3', { text: t(TABLE_TITLE[tb.id] ?? tb.id) }), el('div', { class: 'table-wrap' }, table));
}

function supportText(s, d) {
  const q = s.quality;
  const lines = [
    `SSS ${new Date().toISOString()}`,
    state.view === 'hduser' && state.target ? `${t('colUser')}: ${state.target.name}` : $('user').textContent,
    `${t('colResource')}: ${s.resourceName} (${s.resourceType})`,
    `${t('colGroup')}: ${s.deliveryGroup}`, `${t('colMachine')}: ${s.machine}`,
    `${t('colState')}: ${s.state}`, `${t('colStarted')}: ${s.startTime}`, s.endTime ? `${t('colEnded')}: ${s.endTime}` : null,
    `${t('colClient')}: ${s.clientName ?? '–'} ${s.clientAddress ?? ''}`.trim(),
    `${t('qualityClient')}: ${q.frontend}${q.icaRttMs != null ? `, RTT ${q.icaRttMs} ms` : ''}${q.bandwidthKbps != null ? `, ${q.bandwidthKbps} kbit/s` : ''}`,
    `${t('qualityBackend')}: ${q.backend}${q.logonSeconds != null ? `, logon ${q.logonSeconds} s` : ''}`,
    `SessionKey: ${s.sessionKey}`,
  ];
  if (d) for (const g of d.groups) if (g.id !== 'technical') for (const i of g.items) lines.push(`${i.key}: ${fmtItem(i)}`);
  return lines.filter(Boolean).join('\n');
}

async function openDetails(s) {
  const dlg = $('details');
  const ctx = state.detailsCtx = { s, d: null };   // a slower, older response must not overwrite a newer dialog
  $('details-sub').textContent = `${s.resourceName} · ${s.machine}`;
  $('details-msg').textContent = t('detailsLoading');
  $('details-text').hidden = true;
  $('details-body').replaceChildren();
  if (!dlg.open) dlg.showModal();
  const url = state.view === 'hduser'
    ? `api/helpdesk/sessions/${encodeURIComponent(s.sessionKey)}/details?sid=${encodeURIComponent(state.target.sid)}`
    : `api/sessions/${encodeURIComponent(s.sessionKey)}/details`;
  try {
    const r = await fetch(url, { credentials: 'same-origin' });
    if (r.status === 404) { $('details-msg').textContent = t('detailsNone'); return; }
    if (r.status === 429) { $('details-msg').textContent = t('rateLimited'); return; }
    if (r.status === 503) { $('details-msg').textContent = t('unavailable'); return; }
    if (!r.ok) throw new Error(String(r.status));
    const d = await r.json();
    if (state.detailsCtx !== ctx) return;
    ctx.d = d;
    $('details-msg').textContent = d.unavailable.length
      ? t('detailsPartial', { parts: d.unavailable.map((p) => t(TABLE_TITLE[p] ?? p)).join(', ') }) : '';
    $('details-body').replaceChildren(
      buildCharts(d.series),
      el('div', { class: 'groups' }, ...d.groups.map(groupBox)),
      ...d.tables.map(detailTable));
  } catch {
    $('details-msg').textContent = t('error');
  }
}

async function copySupport() {
  const ctx = state.detailsCtx;
  if (!ctx) return;
  const text = supportText(ctx.s, ctx.d);
  try {
    await navigator.clipboard.writeText(text);
    $('details-msg').textContent = t('copied');
  } catch {
    // no clipboard access (e.g. plain http): show the text for manual copying
    const ta = $('details-text');
    ta.value = text;
    ta.hidden = false;
    ta.select();
    $('details-msg').textContent = t('copyManual');
  }
}

function setExpert(on) {
  state.expert = on;
  store.set('sss.expert', on ? '1' : '0');
  $('expert').setAttribute('aria-pressed', String(on));
  buildHeads();
  render();
}

// ---------- helpdesk: message to a session ----------
function openMessage(s) {
  state.msgTarget = s;
  $('message-target').textContent = `${state.target.name} · ${s.resourceName} (${s.machine})`;
  $('message-text').value = '';
  $('message').showModal();
}

async function sendMessage() {
  const s = state.msgTarget;
  const text = $('message-text').value.trim();
  if (!s || !text) return;
  setStatus('…');
  try {
    const r = await fetch('api/helpdesk/message', {
      method: 'POST', credentials: 'same-origin',
      headers: { 'X-CSRF-TOKEN': await csrf(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ sid: state.target.sid, uids: [s.uid], text }),
    });
    if (r.status === 429) { setStatus(t('rateLimited'), true); return; }
    if (!r.ok) throw new Error(String(r.status));
    const res = await r.json();
    setStatus(t('hdMessageSent', { n: res.loggedOff.length }));
  } catch {
    state.csrf = null;
    setStatus(t('error'), true);
  }
}

// ---------- API ----------
function setStatus(msg, err = false) {
  const s = $('status');
  s.textContent = msg;
  s.className = err ? 'status err' : 'status';
}

async function load() {
  try {
    if (state.view === 'hdlist') { await loadHdList(); return; }
    if (state.view === 'sys') { await loadSys(); return; }
    const url = state.view === 'hduser'
      ? `api/helpdesk/sessions?sid=${encodeURIComponent(state.target.sid)}&range=${encodeURIComponent(state.range)}`
      : `api/sessions?range=${encodeURIComponent(state.range)}`;
    const r = await fetch(url, { credentials: 'same-origin' });
    if (r.status === 503) { setStatus(t('unavailable'), true); return; }
    if (r.status === 429) { setStatus(t('rateLimited'), true); return; }
    if (r.status === 403 && state.view === 'hduser') { state.helpdesk = false; showView('mine'); return; }
    if (!r.ok) throw new Error(String(r.status));
    state.data = await r.json();
    render();
    const w = state.data.warnings || [];
    if (w.includes('history-unavailable')) setStatus(t('warnHistory'), true);
    else if (w.includes('quality-unavailable')) setStatus(t('warnQuality'), true);
    else if ($('status').classList.contains('err')) setStatus('');
  } catch {
    setStatus(t('error'), true);
  }
}

async function csrf() {
  if (state.csrf) return state.csrf;
  const r = await fetch('api/csrf', { credentials: 'same-origin' });
  state.csrf = (await r.json()).token;
  return state.csrf;
}

function confirmDialog(text) {
  const d = $('confirm');
  $('confirm-text').textContent = text;
  return new Promise((resolve) => {
    d.addEventListener('close', () => resolve(d.returnValue === 'ok'), { once: true });
    d.returnValue = 'cancel';
    d.showModal();
  });
}

async function logoff(url, question) {
  if (!(await confirmDialog(question))) return;
  setStatus('…');
  try {
    const r = await fetch(url, { method: 'POST', credentials: 'same-origin', headers: { 'X-CSRF-TOKEN': await csrf() } });
    if (r.status === 429) { setStatus(t('rateLimited'), true); return; }
    if (!r.ok) throw new Error(String(r.status));
    const res = await r.json();
    setStatus(res.rejected.length ? t('partial', { n: res.loggedOff.length, m: res.rejected.length }) : t('done', { n: res.loggedOff.length }));
  } catch {
    state.csrf = null;
    setStatus(t('error'), true);
  }
  await load();
}

// ---------- init ----------
function applyTheme(v) {
  if (v === 'light' || v === 'dark') document.documentElement.dataset.theme = v;
  else delete document.documentElement.dataset.theme;
  $('theme').value = v;
  store.set('sss.theme', v);
}

async function init() {
  state.fallback = await loadDict('en');
  applyTheme(['light', 'dark', 'auto'].includes(QUERY.get('theme')) ? QUERY.get('theme') : (store.get('sss.theme') || 'auto'));
  $('theme').addEventListener('change', (e) => applyTheme(e.target.value));
  $('lang').addEventListener('change', (e) => setLang(e.target.value));
  $('range').addEventListener('change', (e) => { state.range = e.target.value; load(); });
  $('filter').addEventListener('input', (e) => { state.filter = e.target.value; render(); });
  $('refresh').addEventListener('click', load);
  $('logoff-disc').addEventListener('click', () => state.view === 'hduser'
    ? logoffHd('disconnected', [], t('confirmDisconnected'))
    : logoff('api/sessions/logoff-disconnected', t('confirmDisconnected')));
  $('logoff-all').addEventListener('click', () => state.view === 'hduser'
    ? logoffHd('all', [], t('confirmAll'))
    : logoff('api/sessions/logoff-all', t('confirmAll')));
  $('expert').addEventListener('click', () => setExpert(!state.expert));
  $('details-close').addEventListener('click', () => $('details').close());
  $('details-copy').addEventListener('click', copySupport);
  $('sys-refresh').addEventListener('click', load);
  $('tab-sys').addEventListener('click', () => showView('sys'));
  $('message').addEventListener('close', () => { if ($('message').returnValue === 'ok') sendMessage(); });
  state.expert = store.get('sss.expert') === '1';
  $('expert').setAttribute('aria-pressed', String(state.expert));
  $('tab-mine').addEventListener('click', () => showView('mine'));
  $('tab-hd').addEventListener('click', () => showView('hdlist'));
  $('target-back').addEventListener('click', () => showView('hdlist'));
  $('hd-search').addEventListener('submit', (e) => { e.preventDefault(); const q = $('hd-q').value.trim(); if (q.length >= 2) searchUsers(q); });
  $('hd-filter').addEventListener('input', (e) => { state.hdFilter = e.target.value; renderHd(); });

  await setLang(detectLang());

  let refreshSeconds = 30;
  try {
    const r = await fetch('api/me', { credentials: 'same-origin' });
    if (!r.ok) throw new Error(String(r.status));
    const me = await r.json();
    $('user').textContent = t('signedInAs', { name: me.name });
    $('demo').hidden = !me.demo;
    state.limits = { rttWarn: me.rttWarnMs ?? 80, loadWarn: me.loadWarn ?? 7000 };
    if (me.storeFrontUrl) { $('storefront').href = me.storeFrontUrl; $('storefront').hidden = false; }
    state.helpdesk = !!me.helpdesk;
    state.reasonRequired = !!me.reasonRequired;
    $('tabs').hidden = !state.helpdesk;
    refreshSeconds = me.refreshSeconds || 30;
  } catch {
    setStatus(t('error'), true);
  }
  await load();
  // demo mode only: ?shot=details opens the expert view of the first session (README screenshots)
  if (!$('demo').hidden && QUERY.get('shot') === 'details' && state.data?.live[0]) { setExpert(true); await openDetails(state.data.live[0]); }
  state.timer = setInterval(() => { if (!document.hidden && !$('confirm').open) load(); }, refreshSeconds * 1000);
}

init();
