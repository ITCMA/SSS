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
  view: 'mine', target: null, helpdesk: false, caps: {}, cfg: null, reasonRequired: false, hdFilter: '', hdLive: null, expert: false, detailsCtx: null, msgTarget: null, limits: { rttWarn: 80, loadWarn: 7000 }, quality: null,
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
  for (const el of document.querySelectorAll('[data-i18n-aria]')) el.setAttribute('aria-label', t(el.dataset.i18nAria));
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
// Times are shown to the second; Citrix timestamps in the expert view even to the millisecond.
const fmtDate = (iso) => iso ? new Intl.DateTimeFormat(state.lang, { dateStyle: 'short', timeStyle: 'medium' }).format(new Date(iso)) : '–';
const fmtDateMs = (iso) => new Intl.DateTimeFormat(state.lang, { year: '2-digit', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 3 }).format(new Date(iso));

function fmtDur(seconds) {
  if (seconds == null || seconds < 0) return '–';
  const d = Math.floor(seconds / 86400), h = Math.floor(seconds % 86400 / 3600), m = Math.floor(seconds % 3600 / 60), sec = Math.floor(seconds % 60);
  if (d) return `${d}d ${h}h`;
  if (h) return `${h}h ${m}m`;
  if (m) return `${m}m ${sec}s`;
  return `${sec}s`;
}

// Milliseconds in a sensible precision: 46 ms, 2.08 s, 10.6 s, 1 min 12 s.
function fmtMs(ms) {
  if (ms == null || !Number.isFinite(ms)) return '–';
  const a = Math.abs(ms), nf = (n, d) => new Intl.NumberFormat(state.lang, { maximumFractionDigits: d }).format(n);
  if (a < 1000) return `${nf(ms, 0)} ms`;
  if (a < 10000) return `${nf(ms / 1000, 2)} s`;
  if (a < 60000) return `${nf(ms / 1000, 1)} s`;
  const total = Math.round(a / 1000);
  return `${Math.floor(total / 60)} min ${total % 60} s`;
}

// Rating of a duration: good / norm(al) / bad, from limits in ms [good below, bad from]. Logon steps use rules of thumb,
// the whole logon uses the thresholds of the traffic light. Unknown steps have no rating.
const STEP_LIMITS = [
  [/^brokering/i, [1000, 3000]], [/^(vmstart|machinestart)/i, [30000, 90000]], [/^hdx/i, [1500, 4000]], [/^authentication/i, [2000, 6000]],
  [/^(gpo|grouppolicy)/i, [5000, 15000]], [/^logonscripts?/i, [5000, 15000]], [/^profileload/i, [5000, 15000]], [/^interactive/i, [8000, 20000]],
];
function stepLimits(name) {
  if (/^(client)?log_?on$/i.test(name) && state.quality) return [state.quality.logonWarnSeconds * 1000, state.quality.logonCritSeconds * 1000];
  return STEP_LIMITS.find(([re]) => re.test(name.replace(/[^A-Za-z]/g, '')))?.[1] ?? null;
}
function rateMs(name, ms) {
  const lim = stepLimits(name);
  return !lim || ms == null ? null : ms >= lim[1] ? 'bad' : ms < lim[0] ? 'good' : 'norm';
}
const RATE_SYMBOL = { good: '✔', norm: '●', bad: '✖' };
const RATE_WORD = { good: 'rateGood', norm: 'rateNorm', bad: 'rateBad' };
const rateEl = (rate, text, cls = '') => rate
  ? el('span', { class: `rt r-${rate} ${cls}`.trim(), title: t(RATE_WORD[rate]) }, el('span', { class: 'dot', 'aria-hidden': 'true', text: RATE_SYMBOL[rate] }), el('span', { text }), el('span', { class: 'sr', text: ` (${t(RATE_WORD[rate])})` }))
  : el('span', { class: cls, text });
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

function qualityCell(q, live = false) {
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
  if (live && q.frontend === 'unknown') hints.push(t('hintClientUnknown'));
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
function actions(s, logoffBtn, live = false) {
  const box = el('div', { class: 'rowactions' });
  if (logoffBtn) box.append(logoffBtn);
  if (state.expert && state.processes && s.state !== 'ended' && s.uid && live && (state.view !== 'hduser' || state.caps.deep)) {
    const p = el('button', { type: 'button', text: t('procButton'), 'aria-label': `${t('procButton')}: ${s.resourceName} (${s.machine})` });
    p.addEventListener('click', () => openProcesses(s));
    box.append(p);
  }
  if (state.expert && s.sessionKey) {
    const b = el('button', { type: 'button', text: t('details'), 'aria-label': `${t('details')}: ${s.resourceName} (${s.machine})` });
    b.addEventListener('click', () => openDetails(s));
    box.append(b);
  }
  if (state.view === 'hduser' && live && state.caps.act) {
    const m = el('button', { type: 'button', text: t('hdMessage'), 'aria-label': `${t('hdMessage')}: ${s.resourceName}` });
    m.addEventListener('click', () => openMessage(s));
    box.append(m);
  }
  return box;
}

// Device and address, the Workspace app version when Citrix delivers it, and a hint when the own client is older than the minimum version.
function clientCell(s) {
  const box = el('span', {}, `${s.clientName ?? '–'}${s.clientAddress ? ` (${s.clientAddress})` : ''}`);
  if (s.clientVersion) box.append(el('small', { class: 'block', text: t('clientVersion', { v: s.clientVersion, p: s.clientPlatform ? ` · ${s.clientPlatform}` : '' }) }));
  if (s.clientOutdated) box.append(el('small', { class: 'block state-disconnected', text: `⚠ ${t('clientOutdated')}` }));
  return box;
}

function resCell(s) {
  const td = el('td', { 'data-label': t('colResource'), class: 'res' });
  td.append(el('strong', { text: s.resourceName }),
    el('small', { text: t(s.resourceType === 'Desktop' ? 'typeDesktop' : 'typeApp') }));
  return td;
}

// "No sessions" and "no hits for the filter" are different things: say which, and offer to reset the filter.
function emptyNote(p, shown, total, filter, reset, emptyKey = 'noLive') {
  p.hidden = shown > 0;
  if (shown > 0) return;
  if (filter && total > 0) {
    const b = el('button', { type: 'button', text: t('filterReset') });
    b.addEventListener('click', reset);
    p.replaceChildren(`${t('noMatch')} `, b);
  } else p.textContent = t(emptyKey);
}

function render() { keepFocus(renderTables); }
function renderTables() {
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
    const canAct = state.view !== 'hduser' || state.caps.act;
    const btn = canAct ? el('button', { type: 'button', class: 'danger-outline', text: t('logoff'),
      'aria-label': `${t('logoff')}: ${s.resourceName} (${s.machine})` }) : null;
    btn?.addEventListener('click', () => logoffSelected(s, t('confirmOne', { name: s.resourceName, machine: s.machine })));
    return el('tr', {},
      resCell(s),
      cell(t('colGroup'), s.deliveryGroup),
      cell(t('colMachine'), s.machine),
      cell(t('colState'), el('span', { class: `state-${s.state}`, text: t(s.state === 'active' ? 'stateActive' : 'stateDisconnected') })),
      cell(t('colStarted'), fmtDate(s.startTime)),
      cell(t('colDuration'), fmtDur(durationOf(s))),
      cell(t('colIdle'), fmtDur(s.idleSeconds)),
      cell(t('colClient'), clientCell(s)),
      cell(t('colQuality'), qualityCell(s.quality, true)),
      cell(t('colAction'), actions(s, btn, true)));
  }));
  emptyNote($('live-empty'), live.length, state.data.live.length, f, () => { state.filter = ''; $('filter').value = ''; render(); });
  $('live').hidden = live.length === 0;

  const hb = $('hist').tBodies[0];
  hb.replaceChildren(...hist.map((s) => el('tr', {},
    resCell(s),
    cell(t('colGroup'), s.deliveryGroup),
    cell(t('colMachine'), s.machine),
    cell(t('colStarted'), fmtDate(s.startTime)),
    cell(t('colEnded'), fmtDate(s.endTime)),
    cell(t('colDuration'), fmtDur(durationOf(s))),
    cell(t('colClient'), clientCell(s)),
    cell(t('colQuality'), qualityCell(s.quality)),
    ...(state.expert ? [cell(t('colAction'), actions(s))] : []))));
  emptyNote($('hist-empty'), hist.length, state.data.history.length, f, () => { state.filter = ''; $('filter').value = ''; render(); }, 'noHistory');
  $('hist').hidden = hist.length === 0;

  const all = state.data.live;
  const mayAct = state.view !== 'hduser' || state.caps.act;
  $('logoff-disc').hidden = !mayAct || !all.some((s) => s.state === 'disconnected');
  $('logoff-all').hidden = !mayAct || all.length === 0;
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
  $('audit').hidden = view !== 'audit';
  $('env').hidden = view !== 'env';
  $('panel').hidden = hdList || view === 'sys' || view === 'audit' || view === 'env';
  $('target').hidden = view !== 'hduser';
  syncTabs();
  $('tab-mine').setAttribute('aria-pressed', String(view === 'mine'));
  $('tab-hd').setAttribute('aria-pressed', String(view === 'hdlist' || view === 'hduser'));
  $('tab-sys').setAttribute('aria-pressed', String(view === 'sys'));
  $('tab-audit').setAttribute('aria-pressed', String(view === 'audit'));
  $('tab-env').setAttribute('aria-pressed', String(view === 'env'));
  setStatus('');
  render();
  load();
}

// Tabs follow the roles of the caller (the server enforces them too).
function syncTabs() {
  $('tab-hd').hidden = !state.helpdesk;
  $('tab-sys').hidden = !state.caps.admin;
  $('tab-audit').hidden = !state.caps.audit;
  $('tab-env').hidden = !state.caps.admin;
  $('tabs').hidden = !(state.helpdesk || state.caps.admin || state.caps.audit);
}

// Ticket number or reason for helpdesk actions. Required when the server says so; the content is not checked.
function needReason(out) {
  const v = $('reason').value.trim();
  if (state.reasonRequired && !v) {
    (out ?? { set textContent(m) { setStatus(m, true); } }).textContent = t('hdReasonRequired');
    return null;
  }
  return v;
}

function openUser(sid, name) {
  state.target = { sid, name };
  const label = document.querySelector('#target label span');
  label.setAttribute('data-i18n', state.reasonRequired ? 'hdReason' : 'hdReasonOptional');
  label.textContent = t(state.reasonRequired ? 'hdReason' : 'hdReasonOptional');
  $('reason').value = '';
  showView('hduser');
}

function renderHd() { keepFocus(renderHdTable); }
function renderHdTable() {
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
      cell(t('colClient'), clientCell(s)),
      cell(t('colAction'), open));
  }));
  $('hd-live').hidden = rows.length === 0;
  emptyNote($('hd-live-empty'), rows.length, d.sessions.length, f, () => { state.hdFilter = ''; $('hd-filter').value = ''; renderHd(); });
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
  if (needReason() === null) { $('reason').focus(); return; }
  if (!(await confirmDialog(question))) return;
  setStatus(t('loggingOff'));
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
const CHECK_LABEL = { version: 'stVersion', provider: 'stProvider', identity: 'stIdentity', uptime: 'stUptime', audit: 'stAudit', config: 'stConfig',
  helpdesk: 'stHelpdesk', broker: 'stBroker', monitor: 'stMonitor', 'monitor-https': 'stMonitorHttps',
  controllers: 'stControllers', controller: 'stController', sink: 'stSink', instances: 'stInstances', instance: 'stInstance' };

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
      cell(t('colCheck'), t(CHECK_LABEL[c.id] ?? c.id) + (c.label ? `: ${c.label}` : '')),
      cell(t('colResult'), el('span', { class: `badge h-${health}` },
        el('span', { class: 'dot', 'aria-hidden': 'true', text: SYMBOL[health] }), el('span', { text: t(word) }))),
      cell('', c.detail));
  }));
}


// ---------- effective configuration (admin, read only) ----------
async function loadCfg() {
  if (state.cfg) { renderCfg(); return; }
  const r = await fetch('api/helpdesk/config', { credentials: 'same-origin' });
  if (!r.ok) { setStatus(t(r.status === 429 ? 'rateLimited' : 'error'), true); return; }
  state.cfg = await r.json();
  renderCfg();
}

const cfgSourceName = (src) => src === 'default' ? t('cfgDefault') : src === 'EnvironmentVariables' ? t('cfgEnv') : src;

function renderCfg() {
  // the source list: every file the values come from, kept apart (sss.config.json, sss.local.json, appsettings.json = shipped defaults ...)
  const sel = $('cfg-source'), keep = sel.value;
  const sources = [...new Set((state.cfg ?? []).map((c) => c.source))];
  sel.replaceChildren(el('option', { value: '', text: t('cfgSourceAll') }), ...sources.map((s) => el('option', { value: s, text: cfgSourceName(s) })));
  sel.value = sources.includes(keep) ? keep : '';
  const f = $('cfg-filter').value.trim().toLowerCase(), onlyChanged = $('cfg-changed').checked && !sel.value;
  const rows = (state.cfg ?? []).filter((c) => (!sel.value || c.source === sel.value) && (!onlyChanged || c.explicit) && (!f || `${c.path} ${c.value} ${c.source}`.toLowerCase().includes(f)));
  $('cfg-table').tHead.replaceChildren(el('tr', {}, ...['colSetting', 'colValue', 'colSource'].map((k) => el('th', { scope: 'col', text: t(k) }))));
  $('cfg-table').tBodies[0].replaceChildren(...rows.map((c) => el('tr', {},
    cell(t('colSetting'), c.path), cell(t('colValue'), c.value),
    cell(t('colSource'), cfgSourceName(c.source)))));
  for (const td of $('cfg-table').querySelectorAll('td')) td.classList.add('wrap');
  $('cfg-table').hidden = rows.length === 0;
  $('cfg-none').hidden = rows.length > 0;
}

// ---------- environment (admin): client versions and locations ----------
// A horizontal bar for one row; the width is the share of the largest value.
function shareBar(value, max, bad = false) {
  const W = 120, svg = svgEl('svg', { viewBox: `0 0 ${W} 12`, width: String(W), height: '12', 'aria-hidden': 'true' });
  svg.append(svgEl('rect', { class: bad ? 'bar barbad' : 'bar', x: '0', y: '1', width: String(Math.max(2, W * value / Math.max(1, max))), height: '10', rx: '2' }));
  return svg;
}

async function loadEnv() {
  const r = await fetch('api/helpdesk/environment', { credentials: 'same-origin' });
  if (r.status === 403) { state.caps.admin = false; syncTabs(); showView('mine'); return; }
  if (r.status === 429) { setStatus(t('rateLimited'), true); return; }
  if (r.status === 503) { setStatus(t('unavailable'), true); return; }
  if (!r.ok) throw new Error(String(r.status));
  renderEnv(await r.json());
}

// ---------- small charts for the Environment tab ----------
const CAT = ['cat0', 'cat1', 'cat2', 'cat3', 'cat4', 'cat5'];
function envLegend(parts) {
  return el('ul', { class: 'legend envlegend' }, ...parts.map((p) => el('li', {}, el('span', { class: `swatch ${p.cls}`, 'aria-hidden': 'true' }), `${p.label}: `, el('strong', { text: String(p.value) }))));
}
function donutChart(title, parts) {
  parts = parts.filter((p) => p.value > 0);
  const total = parts.reduce((a, p) => a + p.value, 0);
  if (!total) return null;
  const R = 52, r = 32, svg = svgEl('svg', { viewBox: '0 0 120 120', width: '120', height: '120', role: 'img', 'aria-label': title });
  let a0 = -Math.PI / 2;
  for (const p of parts) {
    const a1 = a0 + (p.value / total) * Math.PI * 2 - (parts.length > 1 ? 0.012 : 0), big = a1 - a0 > Math.PI ? 1 : 0;
    const pt = (rad, a) => `${60 + rad * Math.cos(a)} ${60 + rad * Math.sin(a)}`;
    const path = svgEl('path', { class: `slice ${p.cls}`, d: parts.length === 1
      ? `M60 ${60 - R} A${R} ${R} 0 1 1 59.9 ${60 - R} L59.9 ${60 - r} A${r} ${r} 0 1 0 60 ${60 - r} Z`
      : `M${pt(R, a0)} A${R} ${R} 0 ${big} 1 ${pt(R, a1)} L${pt(r, a1)} A${r} ${r} 0 ${big} 0 ${pt(r, a0)} Z` });
    const tip = svgEl('title'); tip.textContent = `${p.label}: ${p.value} (${Math.round(p.value / total * 100)} %)`; path.append(tip);
    svg.append(path);
    a0 += (p.value / total) * Math.PI * 2;
  }
  const c = svgEl('text', { x: '60', y: '65', 'text-anchor': 'middle', class: 'donut-total' }); c.textContent = String(total); svg.append(c);
  return el('figure', { class: 'envchart' }, el('h3', { text: title }), el('div', { class: 'donutrow' }, svg, envLegend(parts)));
}
function stackBar(title, parts) {
  parts = parts.filter((p) => p.value > 0);
  const total = parts.reduce((a, p) => a + p.value, 0);
  if (!total) return null;
  const W = 300, svg = svgEl('svg', { viewBox: `0 0 ${W} 26`, class: 'stack', role: 'img', 'aria-label': title });
  let x = 0;
  for (const p of parts) {
    const w = (p.value / total) * W, rect = svgEl('rect', { class: `slice ${p.cls}`, x, y: 2, width: Math.max(1, w - 1), height: 22 });
    const tip = svgEl('title'); tip.textContent = `${p.label}: ${p.value}`; rect.append(tip); svg.append(rect); x += w;
  }
  return el('figure', { class: 'envchart' }, el('h3', { text: title }), svg, envLegend(parts));
}
function barChart(title, rows) {
  if (!rows.length) return null;
  const max = Math.max(1, ...rows.map((r) => r.value)), L = 150, W = 420, rowH = 22;
  const svg = svgEl('svg', { viewBox: `0 0 ${W} ${rows.length * rowH + 4}`, class: 'hbars', role: 'img', 'aria-label': title });
  rows.forEach((r, i) => {
    const y = 2 + i * rowH;
    const name = svgEl('text', { x: L - 6, y: y + 14, 'text-anchor': 'end' }); name.textContent = r.label.length > 22 ? `${r.label.slice(0, 21)}…` : r.label;
    const bar = svgEl('rect', { class: 'slice cat0', x: L, y: y + 2, width: Math.max(2, (r.value / max) * (W - L - 40)), height: rowH - 8, rx: 3 });
    const val = svgEl('text', { x: L + Math.max(2, (r.value / max) * (W - L - 40)) + 5, y: y + 14 }); val.textContent = String(r.value);
    const tip = svgEl('title'); tip.textContent = `${r.label}: ${r.value}`; bar.append(tip);
    svg.append(name, bar, val);
  });
  return el('figure', { class: 'envchart wide' }, el('h3', { text: title }), svg);
}
function renderEnvCharts(d) {
  const top = (list, n, other) => {
    const s = [...list].sort((a, b) => b.value - a.value), head = s.slice(0, n), rest = s.slice(n).reduce((a, x) => a + x.value, 0);
    return (rest ? [...head, { label: other, value: rest }] : head).map((p, i) => ({ ...p, cls: CAT[i % CAT.length] }));
  };
  const byPlatform = new Map();
  for (const v of d.versions) byPlatform.set(v.platform === '?' ? t('envUnknown') : v.platform, (byPlatform.get(v.platform === '?' ? t('envUnknown') : v.platform) ?? 0) + v.sessions);
  const loc = (d.citrixLocations ?? []).map((l) => ({ label: [l.city, l.country].filter(Boolean).join(', ') || t('envNoCitrixLocation'), value: l.sessions }));
  const ml = d.machineList ?? [];
  const reg = ml.filter((m) => m.registration === 1 && !m.maintenance).length, maint = ml.filter((m) => m.maintenance).length, notReg = ml.filter((m) => m.registration !== 1 && !m.maintenance).length;
  const apps = (d.apps ?? []).slice(0, 8).map((a) => ({ label: a.name, value: a.launches }));
  const charts = [
    donutChart(t('envChartPlatform'), top([...byPlatform].map(([label, value]) => ({ label, value })), 5, t('envOther'))),
    donutChart(t('envChartLocation'), top(loc, 5, t('envOther'))),
    stackBar(t('envChartMachines'), [{ label: t('regRegistered'), value: reg, cls: 'cat1' }, { label: t('colMaintenance'), value: maint, cls: 'cat3' }, { label: t('envNotRegistered'), value: notReg, cls: 'cat4' }]),
    barChart(t('envChartApps'), apps),
  ].filter(Boolean);
  $('env-charts').replaceChildren(...charts);
}

function renderEnv(d) {
  renderEnvCharts(d);
  const tile = (n, label, bad = false) => el('div', {}, el('h3', { text: label }), el('p', { class: bad && n > 0 ? 'state-disconnected' : '', text: String(n) }));
  $('env-tiles').replaceChildren(tile(d.sessions, t('envSessions')), tile(d.devices, t('envDevices')), tile(d.users, t('envUsers')), tile(d.outdated, t('envOutdated'), true));

  const vNote = $('env-versions-note');
  vNote.hidden = false;
  vNote.textContent = !d.versionAvailable ? t('envNoVersionField') : d.withoutVersion > 0 ? t('envNoVersion', { n: d.withoutVersion }) : t('envAsOf', { time: fmtDate(d.at) });
  const maxV = Math.max(1, ...d.versions.map((v) => v.devices));
  $('env-versions').tHead.replaceChildren(el('tr', {}, ...['colVersion', 'colPlatform', 'colDevices', 'colSessions', 'colShare'].map((k) => el('th', { scope: 'col', text: t(k) }))));
  $('env-versions').tBodies[0].replaceChildren(...d.versions.map((v) => el('tr', {},
    cell(t('colVersion'), el('span', {}, v.version || t('envUnknown'), v.outdated ? el('span', { class: 'state-disconnected', text: ` ✖ ${t('envOutdatedMark')}` }) : '')),
    cell(t('colPlatform'), v.platform), cell(t('colDevices'), String(v.devices)), cell(t('colSessions'), String(v.sessions)),
    cell(t('colShare'), shareBar(v.devices, maxV, v.outdated)))));
  $('env-versions').hidden = d.versions.length === 0;

  const lNote = $('env-locations-note');
  lNote.hidden = d.locationsConfigured;
  lNote.textContent = t('envNoLocations');
  const maxL = Math.max(1, ...d.locations.map((l) => l.sessions));
  $('env-locations').tHead.replaceChildren(el('tr', {}, ...['colLocation', 'colSessions', 'colDevices', 'colUsers', 'colShare'].map((k) => el('th', { scope: 'col', text: t(k) }))));
  $('env-locations').tBodies[0].replaceChildren(...d.locations.map((l) => el('tr', {},
    cell(t('colLocation'), l.name || t('envUnassigned')), cell(t('colSessions'), String(l.sessions)), cell(t('colDevices'), String(l.devices)),
    cell(t('colUsers'), String(l.users)), cell(t('colShare'), shareBar(l.sessions, maxL)))));
  $('env-locations').hidden = d.locations.length === 0;

  const cl = d.citrixLocations ?? [], maxC = Math.max(1, ...cl.map((l) => l.sessions));
  $('env-citrix-h').hidden = cl.length === 0;
  $('env-citrix').hidden = cl.length === 0;
  $('env-citrix').tHead.replaceChildren(el('tr', {}, ...['colCountry', 'colCity', 'colSessions', 'colDevices', 'colUsers', 'colShare'].map((k) => el('th', { scope: 'col', text: t(k) }))));
  $('env-citrix').tBodies[0].replaceChildren(...cl.map((l) => el('tr', {},
    cell(t('colCountry'), l.country || (l.city ? t('envUnknown') : t('envNoCitrixLocation'))), cell(t('colCity'), l.city || (l.country ? t('envUnknown') : '')), cell(t('colSessions'), String(l.sessions)),
    cell(t('colDevices'), String(l.devices)), cell(t('colUsers'), String(l.users)), cell(t('colShare'), shareBar(l.sessions, maxC)))));

  const ms = d.machines ?? [], maxM = Math.max(1, ...ms.map((m) => m.machines));
  $('env-machines-h').hidden = ms.length === 0;
  $('env-machines').hidden = ms.length === 0;
  $('env-machines').tHead.replaceChildren(el('tr', {}, ...['colAgent', 'colOs', 'colMachines', 'colMaintenance', 'colUnregistered', 'colShare'].map((k) => el('th', { scope: 'col', text: t(k) }))));
  $('env-machines').tBodies[0].replaceChildren(...ms.map((m) => el('tr', {},
    cell(t('colAgent'), m.agentVersion || t('envNoAgent')), cell(t('colOs'), m.agentVersion ? m.os : t('envNoAgentGroup', { g: m.deliveryGroup || '–' })), cell(t('colMachines'), `${m.machines}${m.poweredOff ? ` (${t('envOffCount', { n: m.poweredOff })})` : ''}`), cell(t('colMaintenance'), String(m.inMaintenance)),
    cell(t('colUnregistered'), el('span', { class: m.unregistered > 0 ? 'state-disconnected' : '', text: String(m.unregistered) })), cell(t('colShare'), shareBar(m.machines, maxM)))));

  const ml = [...(d.machineList ?? [])].sort((a, b) => (a.registration === 1 ? 0 : 1) - (b.registration === 1 ? 0 : 1) || (a.group ?? '').localeCompare(b.group ?? '') || a.name.localeCompare(b.name));
  $('env-machlist-h').hidden = ml.length === 0;
  $('env-machlist').hidden = ml.length === 0;
  const REG = (m) => m.registration == null ? '' : m.registration === 1 ? t('regRegistered') : t('regNot', { n: m.registration });
  const POW = { 2: 'powerOff', 3: 'powerOn', 4: 'powerSuspended' };
  $('env-machlist').tHead.replaceChildren(el('tr', {}, ...['colMachine', 'colGroup', 'colAgent', 'colOs', 'colRegistration', 'colPower', 'colSessions', 'colMaintenance', 'colChanged'].map((k) => el('th', { scope: 'col', text: t(k) }))));
  $('env-machlist').tBodies[0].replaceChildren(...ml.map((m) => el('tr', {},
    cell(t('colMachine'), m.name), cell(t('colGroup'), m.group), cell(t('colAgent'), m.agentVersion), cell(t('colOs'), m.os),
    cell(t('colRegistration'), el('span', { class: m.registration != null && m.registration !== 1 ? 'state-disconnected' : '', text: REG(m) })),
    cell(t('colPower'), POW[m.power] ? t(POW[m.power]) : ''), cell(t('colSessions'), m.sessions == null ? '' : String(m.sessions)),
    cell(t('colMaintenance'), m.maintenance ? t('yes') : ''), cell(t('colChanged'), m.registrationChanged ? fmtDate(m.registrationChanged) : ''))));
  const lhc = d.lhc ?? [];
  $('env-lhc-h').hidden = lhc.length === 0;
  $('env-lhc-note').hidden = lhc.length === 0;
  $('env-lhc').hidden = lhc.length === 0;
  const lcols = [...new Set(lhc.flatMap((r) => r.map((i) => i.key)))].filter((k) => !/(Id|Key)$/.test(k)).slice(0, 10);
  $('env-lhc').tHead.replaceChildren(el('tr', {}, ...lcols.map((c) => el('th', { scope: 'col', text: fieldLabel(c) }))));
  $('env-lhc').tBodies[0].replaceChildren(...lhc.map((r) => el('tr', {}, ...lcols.map((c) => { const i = r.find((x) => x.key === c); return cell(fieldLabel(c), i ? fmtItem(i) : ''); }))));

  const apps = d.apps ?? [], maxA = Math.max(1, ...apps.map((a) => a.launches));
  $('env-apps-note').textContent = apps.length ? t('envAppsNote') : t('envNoApps');
  $('env-apps').hidden = apps.length === 0;
  $('env-apps').tHead.replaceChildren(el('tr', {}, ...['colApp', 'colLaunches', 'colPeak', 'colUsersDay', 'colMachines', 'colShare'].map((k) => el('th', { scope: 'col', text: t(k) }))));
  $('env-apps').tBodies[0].replaceChildren(...apps.map((a) => el('tr', {},
    cell(t('colApp'), el('span', {}, a.name, a.process ? el('small', { class: 'block muted', text: a.process }) : '')), cell(t('colLaunches'), String(a.launches)), cell(t('colPeak'), String(a.peakConcurrent)),
    cell(t('colUsersDay'), String(a.usersPerDay)), cell(t('colMachines'), String(a.machines)), cell(t('colShare'), shareBar(a.launches, maxA)))));
}

// ---------- audit trail (auditor role) ----------
const auditQuery = () => {
  const p = new URLSearchParams({ days: $('a-days').value });
  for (const [id, key] of [['a-actor', 'actor'], ['a-target', 'target'], ['a-action', 'action'], ['a-result', 'result'], ['a-server', 'server'], ['a-q', 'q'], ['a-stream', 'stream']]) {
    const v = $(id).value.trim();
    if (v) p.set(key, v);
  }
  return p.toString();
};

// A short list whose entries fill a filter field when clicked.
function topList(title, items, field) {
  const ul = el('ul');
  for (const i of items) {
    const b = el('button', { type: 'button', text: i.key });
    b.addEventListener('click', () => { $(field).value = i.key; loadAudit(); });
    ul.append(el('li', {}, b, el('span', { text: String(i.n) })));
  }
  return el('div', {}, el('h3', { text: title }), items.length ? ul : el('p', { class: 'muted', text: '–' }));
}

function dayChart(days) {
  const W = 360, H = 120, pad = 18, max = Math.max(1, ...days.map((d) => d.n));
  const svg = svgEl('svg', { viewBox: `0 0 ${W} ${H}`, role: 'img', 'aria-label': t('auditByDay') });
  const bw = days.length ? Math.min(40, (W - pad) / days.length - 4) : 0;
  days.forEach((d, i) => {
    const h = Math.max(1, (H - 2 * pad) * d.n / max), x = pad + i * ((W - pad) / days.length);
    const r = svgEl('rect', { class: 'bar', x: String(x), y: String(H - pad - h), width: String(bw), height: String(h) });
    r.append(Object.assign(svgEl('title'), { textContent: `${d.key}: ${d.n}` }));
    svg.append(r);
    if (days.length <= 10 || i % Math.ceil(days.length / 8) === 0) {
      const tx = svgEl('text', { x: String(x), y: String(H - 4) });
      tx.textContent = d.key.slice(5);
      svg.append(tx);
    }
  });
  return el('div', {}, el('h3', { text: t('auditByDay') }), svg);
}

async function loadAudit() {
  const r = await fetch(`api/audit?${auditQuery()}&limit=300`, { credentials: 'same-origin' });
  if (r.status === 403) { state.caps.audit = false; syncTabs(); showView('mine'); return; }
  if (r.status === 429) { setStatus(t('rateLimited'), true); return; }
  if (!r.ok) throw new Error(String(r.status));
  const d = await r.json();
  $('a-export').href = `api/audit/export.csv?${auditQuery()}`;
  $('a-summary').textContent = t('auditSummary', { n: d.matching, p: d.summary.problems, s: d.sources.length })
    + (d.truncated ? ' ' + t('auditTruncated') : '') + (d.matching > d.events.length ? ' ' + t('auditShown', { n: d.events.length }) : '');
  $('a-charts').replaceChildren(dayChart(d.summary.byDay), topList(t('auditByActor'), d.summary.byActor, 'a-actor'),
    topList(t('auditByAction'), d.summary.byAction, 'a-action'), topList(t('auditByResult'), d.summary.byResult, 'a-result'),
    topList(t('auditByServer'), d.summary.byServer, 'a-server'));
  $('a-events').tHead.replaceChildren(el('tr', {}, ...['colTime', 'colActor', 'colRole', 'auditAction', 'colUser', 'colSession', 'auditResult', 'colTicket', 'colDetail', 'auditServer']
    .map((k) => el('th', { scope: 'col', text: t(k) }))));
  $('a-events').tBodies[0].replaceChildren(...d.events.map((e) => {
    const bad = /^(fail|rejected|denied|error|warning)/i.test(e.result) || e.stream === 'ops' || e.action.startsWith('alert:');
    return el('tr', {},
      cell(t('colTime'), fmtDateMs(e.time)), cell(t('colActor'), e.actor ?? ''), cell(t('colRole'), e.role ?? ''), cell(t('auditAction'), e.action),
      cell(t('colUser'), e.user ?? ''), cell(t('colSession'), e.session ?? ''),
      cell(t('auditResult'), el('span', { class: bad ? 'state-disconnected' : '', text: (bad ? '✖ ' : '') + e.result })),
      cell(t('colTicket'), e.reason ?? ''), cell(t('colDetail'), e.detail ?? ''), cell(t('auditServer'), e.server ?? ''));
  }));
  for (const td of $('a-events').querySelectorAll('td')) td.classList.add('wrap');
  $('a-events').hidden = d.events.length === 0;
  $('a-none').hidden = d.events.length > 0;
  await loadSinks();
}

async function loadSinks() {
  const r = await fetch('api/audit/sinks', { credentials: 'same-origin' });
  if (!r.ok) return;
  const sinks = await r.json();
  $('a-nosinks').hidden = sinks.length > 0;
  $('a-sinks').hidden = sinks.length === 0;
  $('a-sinks').tHead.replaceChildren(el('tr', {}, ...['colName', 'colTarget', 'colResult', 'sinkSent', 'sinkPending'].map((k) => el('th', { scope: 'col', text: t(k) }))));
  $('a-sinks').tBodies[0].replaceChildren(...sinks.map((s) => {
    const failing = s.error && (!s.lastSuccess || new Date(s.lastError) > new Date(s.lastSuccess));
    return el('tr', {},
      cell(t('colName'), `${s.name} (${s.kind})`), cell(t('colTarget'), s.target),
      cell(t('colResult'), el('span', { class: failing ? 'state-disconnected' : '', text: failing ? `✖ ${s.error}` : (s.lastSuccess ? `✔ ${fmtDate(s.lastSuccess)}` : '–') })),
      cell(t('sinkSent'), String(s.sent)), cell(t('sinkPending'), `${Math.round((s.pendingBytes || 0) / 1024)} KB`));
  }));
}

async function verifyAudit() {
  $('a-integrity').textContent = '…';
  const r = await fetch('api/audit/verify?days=30', { credentials: 'same-origin' });
  if (!r.ok) { $('a-integrity').textContent = t('error'); return; }
  const files = await r.json();
  const bad = files.find((f) => !f.ok);
  $('a-integrity').textContent = bad ? `✖ ${t('auditIntegrityBad', { file: bad.file, n: bad.firstBad })}`
    : `✔ ${t('auditIntegrityOk', { n: files.reduce((a, f) => a + f.lines, 0), f: files.length })}`;
}

// ---------- expert details ----------
const GROUP_TITLE = { session: 'groupSession', logon: 'groupLogon', client: 'groupClient', network: 'groupNetwork', machine: 'groupMachine', technical: 'groupTechnical' };
const TABLE_TITLE = { connections: 'tableConnections', failures: 'tableFailures', applications: 'tableApplications', launches: 'tableLaunches', faults: 'tableFaults', gpo: 'tableGpo', appx: 'tableAppx', reconnects: 'tableReconnects' };
const SVGNS = 'http://www.w3.org/2000/svg';
const svgEl = (tag, attrs = {}) => { const e = document.createElementNS(SVGNS, tag); for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v); return e; };
const ISO = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/;

// Citrix durations are milliseconds; show seconds as well once they are long enough to be hard to read.
function fmtItem(i) {
  if (ISO.test(i.value)) return fmtDateMs(i.value);
  if (i.unit === 'ms' || (/Duration$/.test(i.key) && /^\d+$/.test(i.value))) {
    const ms = Number(i.value);
    return ms >= 1000 ? `${ms} ms (${fmtMs(ms)})` : `${ms} ms`;
  }
  return i.unit ? `${i.value} ${i.unit}` : i.value;
}

const isDurationItem = (i) => /Duration$/.test(i.key) && /^\d+(\.\d+)?$/.test(i.value) && i.unit !== 'kbit/s';

// A value cell: durations are colored by rating, and behind the end of a Start/End pair the difference is shown.
function valueNode(i, byKey) {
  const text = fmtItem(i);
  if (isDurationItem(i)) return rateEl(rateMs(i.key.replace(/Duration$/, ''), Number(i.value)), text);
  const m = /^(.*)EndDate$/.exec(i.key), start = m && byKey.get(`${m[1]}StartDate`);
  if (m && start && ISO.test(i.value) && ISO.test(start.value)) {
    const ms = new Date(i.value) - new Date(start.value);
    if (ms >= 0) return el('span', {}, text, ' ', rateEl(m[1] ? rateMs(m[1], ms) : null, `Δ ${fmtMs(ms)}`, 'delta'));
  }
  // every other timestamp of the group: how long after the start of the logon (or of the session) it happened
  if (ISO.test(i.value) && /Date$/.test(i.key) && !/^(LogOnStart|Start|Created|Modified)Date$/.test(i.key)) {
    const anchor = byKey.get('LogOnStartDate') ?? byKey.get('StartDate');
    const ms = anchor && ISO.test(anchor.value) ? new Date(i.value) - new Date(anchor.value) : -1;
    if (ms >= 0 && ms < 86400000) return el('span', {}, text, ' ', el('span', { class: 'delta', title: t(byKey.has('LogOnStartDate') ? 'deltaSinceLogon' : 'deltaSinceStart'), text: `+${fmtMs(ms)}` }));
  }
  return text;
}

// "IcaRttMS" -> "Ica Rtt MS": Citrix property names are shown as they are, only easier to read.
const fieldLabel = (k) => k.replace(/([a-z0-9])([A-Z])/g, '$1 $2');

function groupBox(g) {
  const dl = el('dl'), byKey = new Map(g.items.map((i) => [i.key, i]));
  for (const i of g.items) dl.append(el('dt', { text: fieldLabel(i.key) }), el('dd', {}, valueNode(i, byKey)));
  return el('section', { class: 'group' }, el('h3', { text: t(GROUP_TITLE[g.id] ?? g.id) }), dl);
}

// Axis steps that read well: 1, 2, 5 times a power of ten.
function niceMax(v) {
  const p = 10 ** Math.floor(Math.log10(v)), f = v / p;
  return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * p;
}

// Splits a series where the samples are further apart than 3 typical intervals (at least 5 minutes).
function segments(points) {
  const d = points.slice(1).map((p, i) => p[0] - points[i][0]).sort((a, b) => a - b);
  const gap = Math.max(5 * 60000, 3 * (d[Math.floor(d.length / 2)] ?? 0));
  const out = [[]];
  for (const p of points) {
    const cur = out[out.length - 1];
    if (cur.length && p[0] - cur[cur.length - 1][0] > gap) out.push([p]); else cur.push(p);
  }
  return out;
}

const TIME_STEPS_MIN = [1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440, 2880, 10080];

// Time ticks on round local times (full hours, quarter hours, midnight) so values can be read at a glance.
function timeTicks(x0, x1, maxTicks) {
  const span = (x1 - x0) / 60000;
  const step = (TIME_STEPS_MIN.find((m) => span / m <= maxTicks) ?? 10080) * 60000;
  const tzMs = new Date(x0).getTimezoneOffset() * 60000;
  const ticks = [];
  for (let x = Math.ceil((x0 - tzMs) / step) * step + tzMs; x <= x1; x += step) ticks.push(x);
  return ticks;
}

// opts.lines: horizontal reference lines [{ y, cls }] (e.g. the warning and critical limit of the traffic light).
function chart(title, series, opts = {}) {
  const W = 420, H = 180, L = 46, R = 10, T = 12, B = 24;
  const pts = series.flatMap((s) => s.points);
  const x0 = Math.min(...pts.map((p) => p[0])), x1 = Math.max(...pts.map((p) => p[0]));
  const refs = opts.lines ?? [];
  const yMax = niceMax(Math.max(1, ...pts.map((p) => p[1]), ...refs.map((r) => r.y)));
  const sx = (x) => L + (x1 === x0 ? 0 : (x - x0) / (x1 - x0)) * (W - L - R);
  const sy = (y) => H - B - (y / yMax) * (H - T - B);
  const svg = svgEl('svg', { viewBox: `0 0 ${W} ${H}`, role: 'img', 'aria-label': title });
  const label = (txt, x, y, anchor) => { const e = svgEl('text', { x, y, 'text-anchor': anchor }); e.textContent = txt; return e; };
  const num = new Intl.NumberFormat(state.lang, { notation: 'compact', maximumFractionDigits: 1 });
  const multiDay = x1 - x0 > 20 * 3600000;
  const tf = new Intl.DateTimeFormat(state.lang, multiDay ? { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' } : { hour: '2-digit', minute: '2-digit' });
  const full = new Intl.DateTimeFormat(state.lang, { dateStyle: 'short', timeStyle: 'medium' });
  const val = new Intl.NumberFormat(state.lang, { maximumFractionDigits: 1 });

  for (let i = 0; i <= 4; i++) {
    const y = sy((yMax / 4) * i);
    svg.append(svgEl('line', { class: i === 0 ? 'axis' : 'grid', x1: L, y1: y, x2: W - R, y2: y }), label(num.format((yMax / 4) * i), L - 4, y + 4, 'end'));
  }
  const ticks = timeTicks(x0, x1, 5);
  ticks.forEach((x, i) => {
    svg.append(svgEl('line', { class: 'grid', x1: sx(x), y1: T, x2: sx(x), y2: H - B }));
    const anchor = sx(x) < L + 22 ? 'start' : sx(x) > W - R - 22 ? 'end' : 'middle';
    svg.append(label(tf.format(x), sx(x), H - 7, anchor));
  });
  if (!ticks.length) svg.append(label(tf.format(x0), L, H - 7, 'start'), label(tf.format(x1), W - R, H - 7, 'end'));

  for (const r of refs) {
    svg.append(svgEl('line', { class: `ref ${r.cls}`, x1: L, y1: sy(r.y), x2: W - R, y2: sy(r.y) }));
    svg.append(label(r.label, W - R - 3, sy(r.y) - 3, 'end'));
    svg.lastChild.setAttribute('class', `reflabel ${r.cls}`);
  }

  const legend = el('ul', { class: 'legend' });
  series.forEach((s, n) => {
    // a gap in the samples (value not measured) breaks the line instead of drawing a false connection
    for (const seg of segments(s.points)) {
      if (seg.length === 1) svg.append(svgEl('circle', { class: `pt s${n % 4}`, cx: sx(seg[0][0]), cy: sy(seg[0][1]), r: 2 }));
      else svg.append(svgEl('polyline', { class: `line s${n % 4}`, points: seg.map((p) => `${sx(p[0]).toFixed(1)},${sy(p[1]).toFixed(1)}`).join(' ') }));
    }
    const last = s.points[s.points.length - 1][1];
    legend.append(el('li', { class: `k${n % 4}`, text: `■ ${fieldLabel(s.name)}${s.unit ? ` (${s.unit})` : ''}: ${val.format(last)}` }));
  });

  // Hover (mouse, pen and touch): vertical line, a dot per series and a box with the time and the values.
  const cross = svgEl('line', { class: 'cross', y1: T, y2: H - B, visibility: 'hidden' });
  const dots = series.map((_, n) => svgEl('circle', { class: `dot s${n % 4}`, r: 3.5, visibility: 'hidden' }));
  const hit = svgEl('rect', { class: 'hit', x: L, y: T, width: W - L - R, height: H - T - B });
  svg.append(cross, ...dots, hit);
  const tip = el('div', { class: 'tip', hidden: '' });
  const stat = series.length === 1 ? series[0].points.map((p) => p[1]) : null;
  const stats = stat && el('p', { class: 'muted stats', text: t('chartStats', { min: val.format(Math.min(...stat)), avg: val.format(stat.reduce((a, b) => a + b, 0) / stat.length), max: val.format(Math.max(...stat)), n: stat.length }) });
  const fig = el('figure', { class: 'chart' }, el('h3', { text: title }), svg, legend, stats, tip);
  const hide = () => { cross.setAttribute('visibility', 'hidden'); dots.forEach((d) => d.setAttribute('visibility', 'hidden')); tip.hidden = true; };
  const show = (ev) => {
    const box = svg.getBoundingClientRect();
    if (!box.width) return;
    const x = x0 + ((ev.clientX - box.left) / box.width * W - L) / (W - L - R) * (x1 - x0);
    const near = series.map((s) => s.points.reduce((best, p) => (Math.abs(p[0] - x) < Math.abs(best[0] - x) ? p : best), s.points[0]));
    const at = near.reduce((best, p) => (Math.abs(p[0] - x) < Math.abs(best[0] - x) ? p : best), near[0]);
    cross.setAttribute('x1', sx(at[0])); cross.setAttribute('x2', sx(at[0])); cross.setAttribute('visibility', 'visible');
    near.forEach((p, n) => { dots[n].setAttribute('cx', sx(p[0])); dots[n].setAttribute('cy', sy(p[1])); dots[n].setAttribute('visibility', 'visible'); });
    tip.replaceChildren(el('strong', { text: full.format(at[0]) }),
      ...near.map((p, n) => el('div', { class: `k${n % 4}`, text: `■ ${fieldLabel(series[n].name)}: ${val.format(p[1])}${series[n].unit ? ` ${series[n].unit}` : ''}` })));
    tip.hidden = false;
    const fb = fig.getBoundingClientRect();
    const px = box.left - fb.left + (sx(at[0]) / W) * box.width;
    tip.style.left = `${Math.max(0, Math.min(px + 12, fb.width - tip.offsetWidth))}px`;
    tip.style.top = `${box.top - fb.top + 8}px`;
  };
  for (const type of ['pointermove', 'pointerdown']) hit.addEventListener(type, show);
  hit.addEventListener('pointerleave', hide);
  return fig;
}

// Logon waterfall: one bar per step on a common time axis, so the step that eats the time stands out.
// Bars are colored by rating (green good, blue normal, red slow); the longest measured step gets an outline.
function buildWaterfall(w) {
  if (!w || !w.steps.length) return el('div');
  const stepName = (id) => t(`step${id}`) === `step${id}` ? fieldLabel(id) : t(`step${id}`);
  const R = 190, rowH = 24, T = 8, B = 26, W = 980;
  const indent = 12;   // sub-steps sit a little to the right of their parent
  const L = Math.min(280, Math.max(120, Math.ceil(Math.max(...w.steps.map((s) => stepName(s.id).length + (s.parent ? 2 : 0))) * 6.6) + 16));
  const H = T + w.steps.length * rowH + B;
  const unit = w.totalMs >= 40000 ? 10000 : w.totalMs >= 20000 ? 5000 : w.totalMs >= 8000 ? 2000 : w.totalMs >= 3000 ? 1000 : 500;
  const sx = (ms) => L + (ms / w.totalMs) * (W - L - R);
  const svg = svgEl('svg', { viewBox: `0 0 ${W} ${H}`, role: 'img', 'aria-label': t('waterfallTitle') });
  const text = (txt, x, y, anchor, cls) => { const e = svgEl('text', { x, y, 'text-anchor': anchor, ...(cls ? { class: cls } : {}) }); e.textContent = txt; return e; };
  for (let ms = 0; ms <= w.totalMs; ms += unit) {
    svg.append(svgEl('line', { class: 'grid', x1: sx(ms), y1: T, x2: sx(ms), y2: H - B }), text(fmtMs(ms), sx(ms), H - 8, ms === 0 ? 'start' : 'middle'));
  }
  const measured = w.steps.filter((s) => !s.estimated && s.id !== 'Unaccounted');
  const slowest = (measured.length ? measured : w.steps).reduce((a, b) => (b.durationMs > a.durationMs ? b : a));
  w.steps.forEach((s, i) => {
    const y = T + i * rowH, name = stepName(s.id), rate = s.id === 'Unaccounted' ? null : rateMs(s.id, s.durationMs);
    const pct = Math.round((s.durationMs / w.totalMs) * 100);
    const bar = svgEl('rect', { class: `wbar${rate ? ` r-${rate}` : ''}${s.estimated ? ' est' : ''}${s.id === 'Unaccounted' ? ' gap' : ''}${s === slowest ? ' slow' : ''}`, x: sx(s.startMs), y: y + 4, width: Math.max(2, sx(s.durationMs) - sx(0)), height: rowH - 10, rx: 3 });
    const when = s.startAt ? `\n${fmtDateMs(s.startAt)}${s.endAt ? ` – ${fmtDateMs(s.endAt)}` : ''}` : '';
    const avg = s.avgUserMs ? `\n${t('waterfallAvgUser', { s: fmtMs(s.avgUserMs) })}` : '';
    const tip = svgEl('title'); tip.textContent = `${name}: ${fmtMs(s.durationMs)} (${pct} %), ${t('waterfallAt', { s: fmtMs(s.startMs) })}${rate ? ` – ${t(RATE_WORD[rate])}` : ''}${s.estimated ? ` – ${t('waterfallEstimated')}` : ''}${when}${avg}`;
    bar.append(tip);
    // the comparison with the user's earlier logons: slower or faster by more than a third
    const cmp = s.avgUserMs ? (s.durationMs > s.avgUserMs * 1.33 ? ` ▲ Ø ${fmtMs(s.avgUserMs)}` : s.durationMs < s.avgUserMs * 0.75 ? ` ▼ Ø ${fmtMs(s.avgUserMs)}` : ` ≈ Ø ${fmtMs(s.avgUserMs)}`) : '';
    svg.append(text((s.parent ? '↳ ' : '') + name, L - 8, y + rowH / 2 + 4, 'end', s.parent ? 'wsub' : ''), bar, text(fmtMs(s.durationMs) + cmp, sx(s.startMs + s.durationMs) + 5, y + rowH / 2 + 4, 'start', 'wval'));
  });
  const legend = el('ul', { class: 'legend rates' }, ...['good', 'norm', 'bad'].map((r) => el('li', {}, rateEl(r, t(RATE_WORD[r])))));
  return el('figure', { class: 'chart waterfall' }, el('h3', { text: t('waterfallTitle') }), svg, legend,
    el('p', { class: 'muted', text: t('waterfallSlowest', { name: stepName(slowest.id), s: fmtMs(slowest.durationMs), p: Math.round((slowest.durationMs / w.totalMs) * 100) }) }),
    el('p', { class: 'muted', text: t('waterfallNoRating') }),
    w.steps.some((s) => s.estimated) ? el('p', { class: 'muted', text: t('waterfallEstimated') }) : el('span'));
}

// Endpoint -> gateway -> VDA with the latencies in between, from the connection record and the latest metrics (like the session topology of Director).
// Without a gateway the connection goes straight to the VDA and says so.
function buildTopology(d, s) {
  const all = new Map(d.groups.flatMap((g) => g.items.map((i) => [i.key, i])));
  const v = (k) => all.get(k)?.value;
  const last = (name) => { const sr = d.series.find((x) => x.name === name); return sr ? sr.points[sr.points.length - 1][1] : null; };
  const node = (title, ...lines) => el('div', { class: 'node' }, el('h4', { text: title }), ...lines.filter(Boolean).map((l) => el('small', { text: l })));
  const ms = (x) => `${Math.round(x)} ms`;
  const link = (head, ...parts) => el('div', { class: 'link' }, ...(head ? [el('strong', { text: head })] : []), ...parts.filter(([, x]) => x != null).map(([label, x]) => el('div', { text: `${label}: ${ms(x)}` })));
  // without a gateway Citrix records the client itself as the "connected via" address: that is a direct connection
  const same = (a, b) => a && b && a.toLowerCase().split('.')[0] === b.toLowerCase().split('.')[0];
  const viaHost = v('ConnectedViaHostName'), viaIp = v('ConnectedViaIPAddress');
  const direct = (viaHost || viaIp) && !v('GatewayPopName') && (same(viaHost, v('ClientName')) || same(viaIp, v('ClientAddress')) || same(viaIp, v('ClientPublicIP')));
  const via = direct ? null : viaHost ?? viaIp;
  const popName = v('GatewayPopName');
  const rtt = last('IcaRttMS'), wan = last('WanLatency'), dc = last('DcLatency'), net = last('IcaLatency');
  const endpoint = [v('ClientName'), v('ClientAddress'), [v('ClientPlatform'), v('ClientVersion')].filter(Boolean).join(' '), [v('ClientLocationCity'), v('ClientLocationCountry')].filter(Boolean).join(', '),
    /\D/.test(v('ClientNetworkInterfaceType') ?? '0') && `${t('topoNetworkType')}: ${v('ClientNetworkInterfaceType')}`, v('ClientISP')];
  if (!endpoint.some(Boolean) && !via) return el('div');
  const nodes = [node(t('topoClient'), ...endpoint)];
  if (via) nodes.push(link(null, [t('topoWan'), wan], [t('topoNetwork'), net]), node(t('topoGateway'), v('ConnectedViaHostName'), v('ConnectedViaIPAddress'), popName && `${t('topoPop')}: ${popName}`), link(null, [t('topoDc'), dc]));
  else nodes.push(link(t('topoDirect'), [t('topoNetwork'), net ?? wan], [t('topoDc'), dc]), node(t('topoGateway'), t('topoNoGateway')));
  nodes.push(node(t('topoVda'), s.machine, v('DesktopGroup.Name')));
  const hdx = v('HdxProtocolName') ? `${v('HdxProtocolName')}${v('EdtMtu') ? ` (MTU ${v('EdtMtu')})` : ''}` : v('Protocol');
  const facts = [rtt != null && `ICARTT ${ms(rtt)}`, hdx && `${t('topoProtocol')}: ${hdx}`, all.get('IsSecureIca')?.value === 'true' && t('topoSecure'), /\D/.test(v('WorkspaceType') ?? '0') && v('WorkspaceType'),
    v('LaunchedViaHostName') && `${t('topoLaunched')}: ${v('LaunchedViaHostName')}`].filter(Boolean).join(' · ');
  return el('section', { class: 'group' }, el('h3', { text: t('topoTitle') }), el('div', { class: 'topo' }, ...nodes),
    el('p', { class: 'muted', text: via ? facts : [t('topoDirectNote'), facts].filter(Boolean).join(' · ') }));
}
const ctxMachine = (s) => s.machine;

function buildCharts(series) {
  const is = (re) => (s) => re.test(s.name);
  const bw = series.filter(is(/Bandwidth/i));
  const dc = series.filter(is(/^(Dc|Server)/i));
  const net = series.filter((s) => /Latency|Rtt/i.test(s.name) && !/^(Dc|Server)/i.test(s.name) && !/Bandwidth/i.test(s.name));
  const ep = series.filter(is(/^Endpoint\./)), vda = series.filter(is(/^Vda\./));
  const strip = (list, re) => list.map((x) => ({ ...x, name: x.name.replace(re, '') }));
  const used = new Set([...bw, ...dc, ...net, ...ep, ...vda]);
  const more = series.filter((s) => !used.has(s));
  const box = el('div', { class: 'charts' });
  const bwAvail = bw.filter(is(/Available/i)), bwUsed = bw.filter((x) => !bwAvail.includes(x));
  const Q = state.quality;
  // the limits of the traffic light as reference lines, so a curve can be judged at a glance
  const rttLines = Q && net.some(is(/Rtt/i)) ? [{ y: Q.rttWarnMs, cls: 'warn', label: `${t('healthWarning')} ${Q.rttWarnMs} ms` }, { y: Q.rttCritMs, cls: 'crit', label: `${t('healthCritical')} ${Q.rttCritMs} ms` }] : [];
  const epCpu = strip(ep.filter(is(/Percent/i)), /^Endpoint\./), epNet = strip(ep.filter(is(/Throughput|Wifi/i)), /^Endpoint\./);
  const vdaLoad = strip(vda.filter(is(/Percent/i)), /^Vda\./), vdaSessions = strip(vda.filter(is(/SessionCount/i)), /^Vda\./);
  for (const [title, list, lines] of [[t('chartNetwork'), net, rttLines], [t('chartBandwidth'), bwUsed], [t('chartBandwidthAvail'), bwAvail], [t('chartDatacenter'), dc],
    [t('chartEndpointLoad'), epCpu], [t('chartEndpointNet'), epNet], [t('chartVdaLoad'), vdaLoad], [t('chartVdaSessions'), vdaSessions]])
    if (list.length) box.append(chart(title, list.slice(0, 4), { lines }));
  const out = [box.childElementCount ? box : el('p', { class: 'muted', text: t('chartNoData') })];
  if (more.length) {
    const rest = el('div', { class: 'charts' });
    for (const s of more.slice(0, 12)) rest.append(chart(fieldLabel(s.name), [s]));
    out.push(el('details', { class: 'morecharts' }, el('summary', { text: `${t('chartMore')} (${Math.min(more.length, 12)})` }), rest));
  }
  return el('div', {}, ...out);
}

// ---------- why a traffic light is what it is ----------
const rateValue = (v, warn, crit, higher) => v == null ? 'unknown' : higher ? (v >= crit ? 'critical' : v >= warn ? 'warning' : 'good') : (v <= crit ? 'critical' : v <= warn ? 'warning' : 'good');
const healthWord = (h) => t('health' + h[0].toUpperCase() + h.slice(1));

// Counts of connections, connection failures and automatic reconnects of the session: how stable the connection was.
function buildStability(d) {
  const n = (id) => d.tables.find((x) => x.id === id)?.rows.length;
  const parts = [['stabConnections', n('connections')], ['stabFailures', n('failures')], ['stabReconnects', n('reconnects')]];
  if (parts.every(([, c]) => c == null)) return el('div');
  const bad = (n('failures') ?? 0) + (n('reconnects') ?? 0) > 0;
  return el('section', { class: 'group' }, el('h3', { text: t('stabTitle') }),
    el('ul', { class: 'legend' }, ...parts.map(([k, c]) => el('li', {}, `${t(k)}: `, el('strong', { text: String(c ?? 0) })))),
    el('p', { class: bad ? 'state-disconnected' : 'muted', text: t(bad ? 'stabUnstable' : 'stabStable') }));
}

function buildQuality(s) {
  const q = s.quality, Q = state.quality;
  if (!Q) return el('div');
  const num = new Intl.NumberFormat(state.lang, { maximumFractionDigits: 1 });
  const sides = [
    ['qualityClient', q.frontend, 'client', [
      { label: t('metricRtt'), v: q.icaRttMs, text: (v) => `${num.format(v)} ms`, health: rateValue(q.icaRttMs, Q.rttWarnMs, Q.rttCritMs, true),
        rule: t('ruleHigher', { warn: `${Q.rttWarnMs} ms`, crit: `${Q.rttCritMs} ms` }) },
      { label: t('metricBandwidth'), v: q.bandwidthKbps, text: (v) => `${num.format(v / 1000)} Mbit/s`, health: rateValue(q.bandwidthKbps, Q.bandwidthWarnKbps, Q.bandwidthCritKbps, false),
        rule: t('ruleLower', { warn: `${num.format(Q.bandwidthWarnKbps / 1000)} Mbit/s`, crit: `${num.format(Q.bandwidthCritKbps / 1000)} Mbit/s` }) },
    ]],
    ['qualityBackend', q.backend, 'backend', [
      { label: t('metricLogon'), v: q.logonSeconds, text: (v) => fmtMs(v * 1000), health: rateValue(q.logonSeconds, Q.logonWarnSeconds, Q.logonCritSeconds, true),
        rule: t('ruleHigher', { warn: fmtMs(Q.logonWarnSeconds * 1000), crit: fmtMs(Q.logonCritSeconds * 1000) }) },
      ...(q.loadIndex != null ? [{ label: t('metricLoad'), v: q.loadIndex, text: (v) => num.format(v), health: rateValue(q.loadIndex, Q.loadWarn, Q.loadCrit, true),
        rule: t('ruleHigher', { warn: String(Q.loadWarn), crit: String(Q.loadCrit) }) }] : []),
    ]],
  ];
  const box = el('section', { class: 'group quality' }, el('h3', { text: t('qTitle') }), el('p', { class: 'muted', text: t('qIntro') }));
  for (const [titleKey, health, kind, rows] of sides) {
    const bad = rows.filter((r) => r.health === 'warning' || r.health === 'critical');
    let why;
    if (health === 'unknown') why = t(s.state === 'ended' && kind === 'client' ? 'qUnknownEnded' : kind === 'client' ? 'qUnknownClient' : 'qUnknownBackend');
    else if (bad.length) why = t('qCause', { list: bad.map((r) => `${r.label} ${r.text(r.v)} (${healthWord(r.health)})`).join('; ') });
    else why = t('qAllGood');
    const hint = [];
    if (kind === 'client' && (health === 'warning' || health === 'critical')) hint.push(t(q.icaRttMs != null && q.icaRttMs >= Q.rttWarnMs ? 'hintClientRtt' : 'hintClientBw'));
    if (kind === 'backend' && (health === 'warning' || health === 'critical')) hint.push(t(q.loadIndex != null && q.loadIndex >= Q.loadWarn ? 'hintBackendLoad' : 'hintBackendLogon'));
    const table = el('table', { class: 'smalltable qtable' },
      el('thead', {}, el('tr', {}, ...['qMetric', 'qValue', 'qRating', 'qRule'].map((k) => el('th', { scope: 'col', text: t(k) })))),
      el('tbody', {}, ...rows.map((r) => el('tr', {},
        el('td', { 'data-label': t('qMetric'), text: r.label }),
        el('td', { 'data-label': t('qValue'), text: r.v != null ? r.text(r.v) : t('qNotMeasured') }),
        el('td', { 'data-label': t('qRating') }, el('span', { class: `badge h-${r.health}` }, el('span', { class: 'dot', 'aria-hidden': 'true', text: SYMBOL[r.health] }), el('span', { text: healthWord(r.health) }))),
        el('td', { 'data-label': t('qRule'), class: 'muted', text: r.rule })))));
    const measured = kind === 'client' && q.measuredAt
      ? el('p', { class: 'muted', text: t('qMeasuredAt', { time: fmtDate(q.measuredAt), age: fmtDur(Math.max(0, Math.round((Date.now() - new Date(q.measuredAt)) / 1000))) }) }) : null;
    box.append(el('div', { class: 'qside' },
      el('h4', {}, el('span', { class: `badge h-${health}` }, el('span', { class: 'dot', 'aria-hidden': 'true', text: SYMBOL[health] }), el('span', { text: `${t(titleKey)}: ${healthWord(health)}` }))),
      el('p', { text: why }), ...hint.map((h) => el('p', { class: 'muted', text: h })), table, measured));
  }
  box.append(el('p', { class: 'muted', text: t('qNote') }));
  return box;
}

// Tabs of the details dialog: the page stays short and a section is one click away.
function buildTabs(defs, first) {
  const nav = el('div', { class: 'tabbar', role: 'tablist' }), body = el('div');
  const buttons = [];
  const select = (id, focus = false) => defs.forEach((d, n) => {
    const on = d.id === id;
    buttons[n].setAttribute('aria-selected', String(on));
    buttons[n].tabIndex = on ? 0 : -1;
    d.panel.hidden = !on;
    if (on && focus) buttons[n].focus();
  });
  defs.forEach((d, n) => {
    d.panel = el('div', { role: 'tabpanel', id: `dp-${d.id}`, 'aria-labelledby': `dt-${d.id}`, tabindex: '0' }, d.node);
    const b = el('button', { type: 'button', role: 'tab', id: `dt-${d.id}`, 'aria-controls': `dp-${d.id}`, text: d.label });
    b.addEventListener('click', () => select(d.id));
    b.addEventListener('keydown', (e) => {
      const step = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
      if (step) { e.preventDefault(); select(defs[(n + step + defs.length) % defs.length].id, true); }
    });
    buttons.push(b);
    nav.append(b);
    body.append(d.panel);
  });
  select(defs.some((d) => d.id === first) ? first : defs[0].id);
  return [nav, body];
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

// ---------- profile type and size (read on the VDA, on demand) ----------
const fmtMb = (mb) => mb == null ? '–' : mb >= 1024 ? `${new Intl.NumberFormat(state.lang, { maximumFractionDigits: 1 }).format(mb / 1024)} GB` : `${mb} MB`;
const PROFILE_TYPE = { Local: 'profileTypeLocal', Roaming: 'profileTypeRoaming', 'Citrix UPM': 'profileTypeUpm', FSLogix: 'profileTypeFslogix' };

function profileRows(p) {
  const rows = [[t('profileType'), t(PROFILE_TYPE[p.type] ?? 'profileTypeLocal')], [t('profileLoaded'), t(p.loaded ? 'yes' : 'no')]];
  if (p.localPath) rows.push([t('profilePath'), p.localPath]);
  if (p.storePath) rows.push([t('profileStore'), p.storePath]);
  if (p.container) rows.push([t('profileContainer'), p.container]);
  if (p.containerMb != null) rows.push([t('profileContainerSize'), p.containerMaxMb ? `${fmtMb(p.containerMb)} / ${fmtMb(p.containerMaxMb)}` : fmtMb(p.containerMb)]);
  if (p.sizeMb != null) rows.push([t('profileSize'), `${fmtMb(p.sizeMb)}${p.sizePartial ? ` ${t('profilePartial')}` : ''}`]);
  const top = p.tree.filter((n) => !n.path.includes('\\')).slice(0, 5);
  if (top.length) rows.push([t('profileTop'), top.map((f) => `${f.path} ${fmtMb(f.mb)}`).join(', ')]);
  if (p.redirected.length) rows.push([t('profileRedirected'), p.redirected.map((r) => r.name).join(', ')]);
  else if (p.type !== 'Local') rows.push([t('profileRedirected'), t('profileNoneFound')]);
  const flags = [p.temporary && t('profileTemporary'), p.mandatory && t('profileMandatory')].filter(Boolean);
  if (flags.length) rows.push([t('profileNotes'), flags.join('; ')]);
  return rows;
}

// Folder tree like a disk analyzer: nested, largest first, with a bar for the share of the whole profile.
function profileTree(p) {
  const total = Math.max(1, p.sizeMb || 1);
  const set = new Map(p.tree.map((n) => [n.path.toLowerCase(), { ...n, kids: [] }]));
  const roots = [];
  for (const n of set.values()) {
    const parts = n.path.split('\\');
    let parent = null;
    for (let i = parts.length - 1; i > 0 && !parent; i--) parent = set.get(parts.slice(0, i).join('\\').toLowerCase());
    (parent ? parent.kids : roots).push(n);
  }
  const row = (n) => {
    const line = el('span', { class: 'tnode' }, el('span', { class: 'tname', text: n.path.split('\\').pop() }),
      el('progress', { value: n.mb, max: total, 'aria-label': `${n.path} ${fmtMb(n.mb)}` }), el('span', { class: 'tsize', text: fmtMb(n.mb) }));
    if (!n.kids.length) return el('div', { class: 'tleaf' }, line);
    n.kids.sort((a, b) => b.mb - a.mb);
    return el('details', {}, el('summary', {}, line), ...n.kids.map(row));
  };
  roots.sort((a, b) => b.mb - a.mb);
  return roots.map(row);
}

function profileHints(p, d) {
  const hints = [...p.hints];
  const step = d?.waterfall?.steps.find((s) => s.id === 'ProfileLoad' && !s.estimated);
  if (step && step.durationMs >= 20000)
    hints.push({ id: 'profileLoadSlow', level: 'warn', args: { s: Math.round(step.durationMs / 1000), p: Math.round((step.durationMs / d.waterfall.totalMs) * 100) } });
  const order = { crit: 0, warn: 1, info: 2 };
  return hints.sort((a, b) => order[a.level] - order[b.level]).map((h) => el('li', { class: `finding ${h.level}` }, el('span', { class: 'dot', 'aria-hidden': 'true', text: h.level === 'info' ? 'ℹ' : h.level === 'warn' ? '▲' : '✖' }),
    el('span', { text: t(`hint_${h.id}`, h.args ?? {}) })));
}

function profileConfig(p) {
  const parts = [];
  if (p.settings.length) parts.push(el('h4', { text: t('profileSettings') }), el('dl', {}, ...p.settings.flatMap((s) => [el('dt', { text: `${s.source}: ${s.name}` }), el('dd', { text: s.value })])));
  if (p.exclusions.length) parts.push(el('h4', { text: t('profileExclusions') }), el('ul', {}, ...p.exclusions.map((x) => el('li', { text: `${x.item}  (${x.source})` }))));
  if (p.redirected.length) parts.push(el('h4', { text: t('profileRedirected') }), el('dl', {}, ...p.redirected.flatMap((r) => [el('dt', { text: r.name }), el('dd', { text: r.target })])));
  return parts.length ? el('details', { class: 'pconfig' }, el('summary', { text: t('profileConfig') }), ...parts) : null;
}

function renderProfile(ctx, out) {
  const p = ctx.profile;
  const dl = el('dl');
  for (const [k, v] of profileRows(p)) dl.append(el('dt', { text: k }), el('dd', { text: v }));
  const hints = profileHints(p, ctx.d);
  out.replaceChildren(dl,
    hints.length ? el('ul', { class: 'hints' }, ...hints) : null,
    p.tree.length ? el('div', { class: 'ptree' }, el('h4', { text: t('profileTree') }), ...profileTree(p)) : null,
    p.files.length ? el('div', { class: 'pfiles' }, el('h4', { text: t('profileFiles') }),
      ...p.files.map((f) => el('div', { class: 'tleaf' }, el('span', { class: 'tnode' }, el('span', { class: 'tname', text: f.path.split('\\').pop(), title: f.path }),
        el('span', { class: 'tpath', text: f.path }), el('span', { class: 'tsize', text: fmtMb(f.mb) }))))) : null,
    profileConfig(p));
}

function buildProfileSection(ctx) {
  const out = el('div'), btn = el('button', { type: 'button', text: t('profileScan') });
  btn.addEventListener('click', async () => {
    if (state.view === 'hduser' && state.reasonRequired && !$('reason').value.trim()) { out.replaceChildren(el('p', { class: 'muted', text: t('hdReasonFirst') })); return; }
    btn.disabled = true;
    out.replaceChildren(el('p', { class: 'muted', text: t('profileScanning') }));
    try {
      const hd = state.view === 'hduser';
      const r = await fetch(hd ? `api/helpdesk/sessions/${encodeURIComponent(ctx.s.uid)}/profile?sid=${encodeURIComponent(state.target.sid)}&reason=${encodeURIComponent($('reason').value.trim())}` : `api/sessions/${encodeURIComponent(ctx.s.uid)}/profile`,
        { credentials: 'same-origin' });
      if (state.detailsCtx !== ctx) return;
      if (r.status === 429) { out.replaceChildren(el('p', { class: 'muted', text: t('rateLimited') })); return; }
      if (!r.ok) { out.replaceChildren(el('p', { class: 'muted', text: t(r.status === 503 ? 'profileUnavailable' : r.status === 403 ? 'noRight' : 'error') })); return; }
      ctx.profile = await r.json();
      renderProfile(ctx, out);
    } catch { out.replaceChildren(el('p', { class: 'muted', text: t('error') })); }
    finally { btn.disabled = false; }
  });
  return el('section', { class: 'group profile' }, el('h3', { text: t('profileTitle') }), el('p', { class: 'muted', text: t('profileHint') }), btn, out);
}

// Citrix policy values in force in the session (read on the VDA, helpdesk level 3).
function buildPolicySection(ctx) {
  const out = el('div'), btn = el('button', { type: 'button', text: t('policiesRead') });
  btn.addEventListener('click', async () => {
    if (state.reasonRequired && !$('reason').value.trim()) { out.replaceChildren(el('p', { class: 'muted', text: t('hdReasonFirst') })); return; }
    btn.disabled = true;
    out.replaceChildren(el('p', { class: 'muted', text: t('profileScanning') }));
    try {
      const r = await fetch(`api/helpdesk/sessions/${encodeURIComponent(ctx.s.uid)}/policies?sid=${encodeURIComponent(state.target.sid)}&reason=${encodeURIComponent($('reason').value.trim())}`, { credentials: 'same-origin' });
      if (state.detailsCtx !== ctx) return;
      if (!r.ok) { out.replaceChildren(el('p', { class: 'muted', text: t(r.status === 503 ? 'profileUnavailable' : r.status === 403 ? 'noRight' : 'error') })); return; }
      const rows = (await r.json()).entries;
      const filter = el('input', { type: 'search', autocomplete: 'off', placeholder: t('filterPlaceholder'), 'aria-label': t('filter') });
      const body = el('tbody');
      const draw = () => {
        const f = filter.value.trim().toLowerCase();
        body.replaceChildren(...rows.filter((e) => !f || `${e.path} ${e.name} ${e.value}`.toLowerCase().includes(f)).map((e) => el('tr', {},
          cell(t('colPath'), e.path || '–'), cell(t('colName'), e.name), cell(t('colValue'), e.value))));
      };
      filter.addEventListener('input', draw);
      draw();
      out.replaceChildren(rows.length ? el('div', {}, el('p', { class: 'muted', text: t('policiesNote') }), filter,
        el('div', { class: 'table-wrap' }, el('table', { class: 'smalltable' }, el('thead', {}, el('tr', {}, ...['colPath', 'colName', 'colValue'].map((k) => el('th', { scope: 'col', text: t(k) })))), body)))
        : el('p', { class: 'muted', text: t('policiesNone') }));
    } catch { out.replaceChildren(el('p', { class: 'muted', text: t('error') })); }
    finally { btn.disabled = false; }
  });
  return el('section', { class: 'group profile' }, el('h3', { text: t('policiesTitle') }), el('p', { class: 'muted', text: t('policiesHint') }), btn, out);
}

function supportText(s, d, profile) {
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
  if (d?.waterfall) lines.push(`${t('waterfallTitle')}: ${d.waterfall.steps.map((x) => `${x.id} ${Math.round(x.durationMs)} ms${x.estimated ? '*' : ''}`).join(', ')}`);
  if (profile) for (const [k, v] of profileRows(profile)) lines.push(`${k}: ${v}`);
  return lines.filter(Boolean).join('\n');
}

// ---------- applications and processes of a session ----------
const PROC_RESULT = { killed: 'procKilled', closed: 'procKilled', protected: 'procProtected', 'not-owned': 'procGone', 'not-found': 'procGone' };

async function openProcesses(s) {
  if (state.view === 'hduser' && needReason() === null) { $('reason').focus(); return; }
  const dlg = $('procs');
  const ctx = state.procsCtx = { s, items: [] };
  $('procs-sub').textContent = `${s.resourceName} · ${s.machine}`;
  $('procs-msg').textContent = t('detailsLoading');
  $('procs-table').replaceChildren();
  if (!dlg.open) dlg.showModal();
  await loadProcesses(ctx);
}

const procsBase = (ctx) => state.view === 'hduser'
  ? `api/helpdesk/sessions/${encodeURIComponent(ctx.s.uid)}/processes`
  : `api/sessions/${encodeURIComponent(ctx.s.uid)}/processes`;

async function loadProcesses(ctx) {
  try {
    const url = state.view === 'hduser' ? `${procsBase(ctx)}?sid=${encodeURIComponent(state.target.sid)}&reason=${encodeURIComponent($('reason').value.trim())}` : procsBase(ctx);
    const r = await fetch(url, { credentials: 'same-origin' });
    if (state.procsCtx !== ctx) return;
    if (r.status === 403) { $('procs-msg').textContent = t('noRight'); return; }
    if (r.status === 404) { $('procs-msg').textContent = t('detailsNone'); return; }
    if (r.status === 429) { $('procs-msg').textContent = t('rateLimited'); return; }
    if (r.status === 503) { $('procs-msg').textContent = t('procUnavailable'); return; }
    if (!r.ok) throw new Error(String(r.status));
    ctx.items = (await r.json()).processes;
    renderProcesses(ctx);
  } catch {
    $('procs-msg').textContent = t('error');
  }
}

// Which processes are programs the user started (like the "Apps" of the task manager). The endpoint on the VDA runs in session 0 and sees
// no windows of other sessions, so this is a judgement from name and program path. It errs on the side of showing too much: a process
// is hidden only when it is a known Windows or Citrix system process or its program lives in the Windows, Citrix or FSLogix folders.
const APP_KNOWN = new Set(['explorer', 'notepad', 'mspaint', 'mstsc', 'wordpad', 'snippingtool', 'taskmgr', 'cmd', 'powershell', 'powershell_ise', 'regedit', 'mmc', 'charmap', 'osk', 'magnify']);
const SYS_NOISE = new Set(['svchost', 'conhost', 'sihost', 'taskhostw', 'taskhost', 'taskhostex', 'ctfmon', 'runtimebroker', 'searchui', 'searchapp', 'searchindexer', 'shellexperiencehost', 'startmenuexperiencehost',
  'smartscreen', 'dllhost', 'fontdrvhost', 'dwm', 'csrss', 'winlogon', 'wininit', 'services', 'lsass', 'lsm', 'smss', 'spoolsv', 'sipahost', 'vdaredirector', 'websocketagent', 'wfshell', 'ctxgfx', 'cmstart',
  'rdpclip', 'rdpinput', 'wudfhost', 'backgroundtaskhost', 'applicationframehost', 'textinputhost', 'registry', 'system', 'idle', 'audiodg', 'dashost', 'wmiprvse', 'msdtc', 'sppsvc', 'userinit', 'logonui',
  'unsecapp', 'wsmprovhost', 'msmpeng', 'tabtip', 'tabtip32', 'vmtoolsd', 'vmwaretray', 'vmwareuser', 'secure system', 'memory compression']);

function looksLikeApp(p) {
  if (p.window || p.isApp) return true;
  const name = p.name.toLowerCase(), path = (p.path ?? '').toLowerCase();
  if (APP_KNOWN.has(name) || name.includes('calc')) return true;   // the calculator has a different file name per Windows version
  if (SYS_NOISE.has(name) || /^(ctx|citrix)/.test(name)) return false;
  if (path) return !(/^[a-z]:\\windows\\/.test(path) || /\\(citrix|fslogix)\\/.test(path));
  return true;   // program path unknown and not a known system process: most likely something the user started
}

function renderProcesses(ctx) {
  const showAll = $('procs-all').checked;
  let rows;
  if (showAll) {
    rows = [...ctx.items].sort((a, b) => (looksLikeApp(b) ? 1 : 0) - (looksLikeApp(a) ? 1 : 0) || a.name.localeCompare(b.name));
  } else {
    // one row per program, like the task manager: the oldest process of a program is its main process (Chrome starts a dozen helpers)
    const groups = new Map();
    for (const p of ctx.items.filter(looksLikeApp).sort((a, b) => new Date(a.started ?? 0) - new Date(b.started ?? 0) || a.id - b.id)) {
      const key = `${p.name.toLowerCase()}|${(p.path ?? '').toLowerCase()}`;
      const g = groups.get(key);
      if (g) { g.more += 1; g.memoryMb += p.memoryMb; } else groups.set(key, { ...p, more: 0 });
    }
    rows = [...groups.values()].sort((a, b) => a.name.localeCompare(b.name));
  }
  $('procs-msg').textContent = rows.length ? '' : t(showAll ? 'procNone' : 'procNoApps');
  const head = el('tr', {}, ...['colProcess', 'colProgram', 'colPid', 'colStarted', 'colMemory', 'colAction'].map((k) => el('th', { scope: 'col', text: t(k) })));
  $('procs-table').replaceChildren(el('thead', {}, head), el('tbody', {}, ...rows.map((p) => {
    const kill = el('button', { type: 'button', class: 'danger-outline', text: t('procKill') });
    kill.addEventListener('click', () => endProcess(ctx, p));
    const name = el('td', { 'data-label': t('colProcess'), text: p.name });
    if (p.description) name.append(el('small', { class: 'block muted', text: p.description }));
    if (p.more) name.append(el('small', { class: 'block muted', text: t('procMore', { n: p.more }) }));
    return el('tr', {},
      name, el('td', { 'data-label': t('colProgram'), class: 'tpath', title: p.path ?? '', text: p.path ?? '' }),
      el('td', { 'data-label': t('colPid'), text: String(p.id) }), el('td', { 'data-label': t('colStarted'), text: fmtDate(p.started) }),
      el('td', { 'data-label': t('colMemory'), text: `${p.memoryMb} MB` }),
      el('td', { 'data-label': t('colAction') }, el('div', { class: 'rowactions' }, kill)));
  })));
}

async function endProcess(ctx, p) {
  if (!(await confirmDialog(t('procConfirmKill', { name: p.name }), 'procKill'))) return;
  $('procs-msg').textContent = '…';
  try {
    const hd = state.view === 'hduser';
    const r = await fetch(`${procsBase(ctx)}/${p.id}/end`, {
      method: 'POST', credentials: 'same-origin',
      headers: { 'X-CSRF-TOKEN': await csrf(), 'Content-Type': 'application/json' },
      body: JSON.stringify(hd ? { sid: state.target.sid, force: true, reason: $('reason').value.trim() } : { force: true }),
    });
    if (r.status === 429) { $('procs-msg').textContent = t('rateLimited'); return; }
    if (r.status === 503) { $('procs-msg').textContent = t('procUnavailable'); return; }
    if (!r.ok) throw new Error(String(r.status));
    const res = (await r.json()).result;
    await loadProcesses(ctx);
    if (state.procsCtx === ctx) $('procs-msg').textContent = t(PROC_RESULT[res] ?? 'error', { name: p.name });
  } catch {
    state.csrf = null;
    $('procs-msg').textContent = t('error');
  }
}

async function openDetails(s, tab = 'summary') {
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
    const defs = [
      { id: 'summary', label: t('tabSummary'), node: el('div', {}, el('div', { class: 'summary' }, buildQuality(ctx.s), buildTopology(d, ctx.s)), buildStability(d), buildWaterfall(d.waterfall)) },
      { id: 'charts', label: t('tabCharts'), node: buildCharts(d.series) },
      { id: 'props', label: t('tabProps'), node: el('div', {}, el('div', { class: 'groups' }, ...d.groups.map(groupBox)), ...d.tables.map(detailTable)) },
      ...(state.profile && ctx.s.uid && ctx.s.state !== 'ended' && state.view === 'hduser' && state.caps.deep ? [{ id: 'policies', label: t('tabPolicies'), node: buildPolicySection(ctx) }] : []),
      ...(state.profile && ctx.s.uid && ctx.s.state !== 'ended' && (state.view !== 'hduser' || state.caps.deep) ? [{ id: 'profile', label: t('tabProfile'), node: buildProfileSection(ctx) }] : []),
    ];
    $('details-body').replaceChildren(...buildTabs(defs, tab));
  } catch {
    $('details-msg').textContent = t('error');
  }
}

async function copySupport() {
  const ctx = state.detailsCtx;
  if (!ctx) return;
  const text = supportText(ctx.s, ctx.d, ctx.profile);
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
  if (needReason() === null) { $('reason').focus(); return; }
  state.msgTarget = s;
  $('message-target').textContent = `${state.target.name} · ${s.resourceName} (${s.machine})`;
  $('message-text').value = '';
  $('message').showModal();
}

async function sendMessage() {
  const s = state.msgTarget;
  const text = $('message-text').value.trim();
  if (!s || !text) return;
  setStatus(t('sendingMessage'));
  try {
    const r = await fetch('api/helpdesk/message', {
      method: 'POST', credentials: 'same-origin',
      headers: { 'X-CSRF-TOKEN': await csrf(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ sid: state.target.sid, uids: [s.uid], text, reason: $('reason').value.trim() }),
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
// The tables are rebuilt on every refresh: keep the keyboard focus on the same button afterwards.
function keepFocus(fn) {
  const a = document.activeElement, label = a?.getAttribute?.('aria-label'), table = a?.closest?.('table')?.id;
  fn();
  if (label && table && !a.isConnected) [...document.querySelectorAll(`#${table} button`)].find((b) => b.getAttribute('aria-label') === label)?.focus();
}

// While a log-off is on its way the row actions are locked, so a second click cannot repeat it.
function setStatus(msg, err = false) {
  document.body.classList.toggle('busy', msg === t('loggingOff'));
  const s = $('status');
  s.textContent = msg;
  s.className = err ? 'status err' : 'status';
}

async function load() {
  try {
    if (state.view === 'hdlist') { await loadHdList(); return; }
    if (state.view === 'sys') { await loadSys(); return; }
    if (state.view === 'audit') { await loadAudit(); return; }
    if (state.view === 'env') { await loadEnv(); return; }
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

function confirmDialog(text, okKey = 'confirm') {
  const d = $('confirm');
  $('confirm-text').textContent = text;
  $('confirm-ok').textContent = t(okKey);
  return new Promise((resolve) => {
    d.addEventListener('close', () => resolve(d.returnValue === 'ok'), { once: true });
    d.returnValue = 'cancel';
    d.showModal();
  });
}

async function logoff(url, question) {
  if (!(await confirmDialog(question))) return;
  setStatus(t('loggingOff'));
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
  $('procs-close').addEventListener('click', () => $('procs').close());
  $('procs-reload').addEventListener('click', () => state.procsCtx && loadProcesses(state.procsCtx));
  $('procs-all').addEventListener('change', () => state.procsCtx && renderProcesses(state.procsCtx));
  $('details-copy').addEventListener('click', copySupport);
  $('sys-refresh').addEventListener('click', load);
  $('tab-sys').addEventListener('click', () => showView('sys'));
  $('message').addEventListener('close', () => { if ($('message').returnValue === 'ok') sendMessage(); });
  state.expert = store.get('sss.expert') === '1';
  $('expert').setAttribute('aria-pressed', String(state.expert));
  $('tab-mine').addEventListener('click', () => showView('mine'));
  $('tab-hd').addEventListener('click', () => showView('hdlist'));
  $('tab-audit').addEventListener('click', () => showView('audit'));
  $('tab-env').addEventListener('click', () => showView('env'));
  $('env-refresh').addEventListener('click', () => loadEnv().catch(() => setStatus(t('error'), true)));
  $('cfg').addEventListener('toggle', () => { if ($('cfg').open) loadCfg().catch(() => setStatus(t('error'), true)); });
  $('cfg-filter').addEventListener('input', renderCfg);
  $('cfg-changed').addEventListener('change', renderCfg);
  $('cfg-source').addEventListener('change', renderCfg);
  $('audit-form').addEventListener('submit', (e) => { e.preventDefault(); loadAudit().catch(() => setStatus(t('error'), true)); });
  $('a-verify').addEventListener('click', verifyAudit);
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
    state.quality = me.quality ?? null;
    if (me.storeFrontUrl) { $('storefront').href = me.storeFrontUrl; $('storefront').hidden = false; }
    state.helpdesk = !!me.helpdesk;
    state.caps = me.caps ?? {};
    state.reasonRequired = !!me.reasonRequired;
    state.processes = !!me.processes;
    state.profile = !!me.profile;
    syncTabs();
    refreshSeconds = me.refreshSeconds || 30;
  } catch {
    setStatus(t('error'), true);
  }
  await load();
  // demo mode only (README screenshots): ?shot=details opens the expert view of the first session, ?shot=profile also reads its
  // profile, ?shot=processes opens its process list, ?shot=env the Environment tab
  const shot = QUERY.get('shot');
  if (!$('demo').hidden && state.data?.live[0] && ['details', 'profile', 'processes'].includes(shot)) {
    setExpert(true);
    if (shot === 'processes') await openProcesses(state.data.live[0]);
    else { await openDetails(state.data.live[0], shot === 'profile' ? 'profile' : 'summary'); if (shot === 'profile') document.querySelector('.profile button')?.click(); }
  }
  if (!$('demo').hidden && shot === 'env' && state.caps.admin) { showView('env'); await loadEnv(); }
  state.timer = setInterval(() => { if (!document.hidden && !$('confirm').open && state.view !== 'audit' && state.view !== 'env') load(); }, refreshSeconds * 1000);
}

init();
