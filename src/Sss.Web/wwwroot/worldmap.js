'use strict';
// World map of the live sessions. The map outlines and a list of places are bundled (geo/world.json: Natural Earth, public domain; cities from GeoNames, CC BY 4.0); the position of each
// session is found here in the browser from the country and city Citrix reports, so no address or name is sent to a map service.

const mapState = { geo: null, idx: null, data: null, places: [], unlocated: [], k: 1, tx: 0, ty: 0, query: '', hits: new Set(), ready: false, drag: null };
const MAP_W = 3600, MAP_TOP = 110, MAP_H = 1390; // 80 degrees north to 59 degrees south, in 0.1 degree units

const geoKey = (s) => (s ?? '').normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
const COUNTRY_ALIAS = { 'united states': 'US', usa: 'US', 'united states of america': 'US', 'russian federation': 'RU', 'korea republic of': 'KR', 'south korea': 'KR',
  'czech republic': 'CZ', czechia: 'CZ', 'great britain': 'GB', uk: 'GB', 'united kingdom': 'GB', turkiye: 'TR', 'viet nam': 'VN', 'the netherlands': 'NL', 'ivory coast': 'CI' };

// Other spellings of cities that the bundled list writes differently.
const CITY_ALIAS = { munchen: 'munich', koln: 'cologne', wien: 'vienna', praha: 'prague', warszawa: 'warsaw', kobenhavn: 'copenhagen', roma: 'rome', milano: 'milan',
  bangalore: 'bengaluru', bombay: 'mumbai', calcutta: 'kolkata', peking: 'beijing', 'new delhi': 'delhi', 'frankfurt am main': 'frankfurt', nurnberg: 'nuremberg',
  hannover: 'hanover', 'ho chi minh city': 'ho chi minh', lisboa: 'lisbon', bruxelles: 'brussels', sevilla: 'seville', moskva: 'moscow' };

async function mapGeo() {
  if (mapState.geo) return mapState.geo;
  const r = await fetch('geo/world.json', { credentials: 'same-origin' });
  if (!r.ok) throw new Error(String(r.status));
  const geo = await r.json();
  const byCountry = new Map(), byName = new Map(), places = new Map(), anyCity = new Map();
  for (const c of geo.countries) {
    for (const n of c.n) byName.set(geoKey(n), c);
    if (c.c && c.c !== '-99') { byCountry.set(c.c, c); byName.set(geoKey(c.c), c); }
    if (c.c3 && c.c3 !== '-99') byName.set(geoKey(c.c3), c);
  }
  for (const [name, ascii, cc, lat, lon, pop] of geo.places) {
    for (const n of new Set([geoKey(name), geoKey(ascii)])) {
      if (!n) continue;
      const key = `${cc}|${n}`, old = places.get(key);
      if (!old || old.pop < pop) places.set(key, { lat, lon, pop });
      const o2 = anyCity.get(n);
      if (!o2 || o2.pop < pop) anyCity.set(n, { lat, lon, pop });
    }
  }
  mapState.geo = geo;
  mapState.idx = { byCountry, byName, places, anyCity, u: geo.u };
  return geo;
}

// Where is this session? The city first (exact place from the bundled list), then the middle of the country, otherwise unknown.
function mapLocate(country, city) {
  const { byName, byCountry, places, anyCity, u } = mapState.idx;
  const ck = geoKey(country), cityKey = geoKey(city);
  const c = byName.get(ck) ?? (COUNTRY_ALIAS[ck] ? byCountry.get(COUNTRY_ALIAS[ck]) : null);
  let lat = null, lon = null, exact = false;
  if (cityKey) {
    const find = (k) => (c && c.c ? places.get(`${c.c}|${k}`) : null) ?? (!c ? anyCity.get(k) : null);
    const p = find(cityKey) ?? (CITY_ALIAS[cityKey] ? find(CITY_ALIAS[cityKey]) : null);
    if (p) { lat = p.lat; lon = p.lon; exact = true; }
  }
  if (lat == null && c) { lat = c.y; lon = c.x; }
  if (lat == null) return null;
  return { x: (lon + 180) * u, y: (90 - lat) * u, exact, country: c ? c.n[0] : country };
}

function mapBuild(data) {
  const groups = new Map(), unlocated = [];
  for (const p of data.points) {
    const loc = mapLocate(p.country, p.city);
    if (!loc) { unlocated.push(p); continue; }
    const city = (p.city ?? '').trim();
    // a known place is one dot however it is spelled; a city missing from the bundled list still gets its own dot (named after the city), not the country's
    const key = loc.exact ? `${Math.round(loc.x)}|${Math.round(loc.y)}` : `c|${loc.country}|${geoKey(city)}`;
    if (!groups.has(key)) groups.set(key, { x: loc.x, y: loc.y, exact: loc.exact, country: loc.country, city, sessions: [] });
    groups.get(key).sessions.push(p);
  }
  // cities that are not in the list all sit in the middle of their country: spread them in a small ring so each keeps its own dot
  const shared = new Map();
  for (const g of groups.values()) if (!g.exact) { const k = `${Math.round(g.x)}|${Math.round(g.y)}`; (shared.get(k) ?? shared.set(k, []).get(k)).push(g); }
  for (const list of shared.values()) if (list.length > 1) list.forEach((g, i) => { const a = i * 2.39996, d = 16 * Math.sqrt(i); g.x += Math.cos(a) * d; g.y += Math.sin(a) * d; });
  mapState.places = [...groups.values()].sort((a, b) => b.sessions.length - a.sessions.length);
  mapState.placeOf = new Map(mapState.places.flatMap((pl) => pl.sessions.map((x) => [x, pl])));
  mapState.unlocated = unlocated;
}

const mapEls = {};
function mapInit() {
  if (mapState.ready) return;
  mapState.ready = true;
  const svg = $('map-svg');
  // soft land colour as one gradient over the whole map, and a faint grid of latitudes and longitudes under the land
  const defs = svgEl('defs'), lg = svgEl('linearGradient', { id: 'map-landg', gradientUnits: 'userSpaceOnUse', x1: 0, y1: MAP_TOP, x2: 0, y2: MAP_TOP + MAP_H });
  lg.append(svgEl('stop', { offset: 0, class: 'map-s0' }), svgEl('stop', { offset: 1, class: 'map-s1' }));
  defs.append(lg);
  svg.append(defs);
  mapEls.world = svgEl('g', { id: 'map-world' });
  const grid = svgEl('g', { class: 'map-grid' });
  for (let lon = -180; lon <= 180; lon += 30) grid.append(svgEl('line', { x1: (lon + 180) * 10, x2: (lon + 180) * 10, y1: MAP_TOP, y2: MAP_TOP + MAP_H }));
  for (let lat = -60; lat <= 80; lat += 30) grid.append(svgEl('line', { x1: 0, x2: MAP_W, y1: (90 - lat) * 10, y2: (90 - lat) * 10 }));
  mapEls.world.append(grid);
  mapEls.land = svgEl('g', { class: 'map-land' });
  mapEls.dots = svgEl('g', { class: 'map-dots' });
  mapEls.world.append(mapEls.land, mapEls.dots);
  svg.append(mapEls.world);
  for (const c of mapState.geo.countries) mapEls.land.append(svgEl('path', { d: c.d, class: 'map-country' }));
  svg.addEventListener('wheel', (e) => { e.preventDefault(); cancelAnimationFrame(mapAnim); mapZoomAt(e.deltaY < 0 ? 1.4 : 1 / 1.4, e.clientX, e.clientY); }, { passive: false });
  svg.addEventListener('pointerdown', (e) => {
    if (e.target.closest?.('.map-dot')) return;
    cancelAnimationFrame(mapAnim);
    mapState.drag = { x: e.clientX, y: e.clientY, tx: mapState.tx, ty: mapState.ty };
    svg.setPointerCapture(e.pointerId);
  });
  svg.addEventListener('pointermove', (e) => {
    const d = mapState.drag;
    if (!d) return;
    const px = mapPx();
    mapState.tx = d.tx + (e.clientX - d.x) / px;
    mapState.ty = d.ty + (e.clientY - d.y) / px;
    mapApply(false);
  });
  const stop = () => { if (mapState.drag) { mapState.drag = null; mapDraw(); } };
  svg.addEventListener('pointerup', stop);
  svg.addEventListener('pointercancel', stop);
  svg.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { mapHome(); return; }
    if (e.target.closest?.('.map-dot')) return;
    const step = 80 / mapPx(), key = e.key;
    if (key === '+' || key === '=') { mapZoomAt(1.4); e.preventDefault(); return; }
    if (key === '-') { mapZoomAt(1 / 1.4); e.preventDefault(); return; }
    if (key === 'ArrowLeft') mapState.tx += step; else if (key === 'ArrowRight') mapState.tx -= step;
    else if (key === 'ArrowUp') mapState.ty += step; else if (key === 'ArrowDown') mapState.ty -= step; else return;
    e.preventDefault();
    mapApply(true);
  });
  $('map-zoom-in').addEventListener('click', () => mapZoomAt(1.6));
  $('map-zoom-out').addEventListener('click', () => mapZoomAt(1 / 1.6));
  $('map-reset').addEventListener('click', mapHome);
  $('map-q').addEventListener('input', () => { mapState.query = $('map-q').value.trim(); mapSearch(false); });
  $('map-search').addEventListener('submit', (e) => { e.preventDefault(); mapState.query = $('map-q').value.trim(); mapSearch(true); });
  $('map-refresh').addEventListener('click', () => loadMap().catch(() => setStatus(t('error'), true)));
  window.addEventListener('resize', () => { if (!$('map').hidden) mapApply(true); });
}

// Screen pixels per map unit at zoom 1.
const mapPx = () => Math.max(0.05, $('map-svg').getBoundingClientRect().width / MAP_W);

// Zooms around a point of the screen (the middle of the map when none is given).
function mapZoomAt(f, cx, cy) {
  const r = $('map-svg').getBoundingClientRect(), px = r.width / MAP_W;
  const sx = ((cx ?? r.left + r.width / 2) - r.left) / px, sy = ((cy ?? r.top + r.height / 2) - r.top) / px + MAP_TOP;
  const k = Math.min(80, Math.max(1, mapState.k * f)), g = k / mapState.k;
  mapState.tx = sx - (sx - mapState.tx) * g;
  mapState.ty = sy - (sy - mapState.ty) * g;
  mapState.k = k;
  mapApply(true);
}

// Flies to the place in about a second, like a map service: the point in the middle of the screen glides to the place while the zoom goes out a little on a long
// trip and then in. A user action (click, search), so it is animated even when the system asks for less motion; any touch of the map stops it.
// Back to the whole world with nothing selected: the place box is closed and the search is cleared.
function mapHome() {
  $('map-place').hidden = true;
  $('map-q').value = '';
  mapState.query = '';
  mapSearch(false);
  mapFocus({ x: MAP_W / 2, y: MAP_TOP + MAP_H / 2 }, 1);
}

let mapAnim = 0;
function mapFocus(place, k) {
  const r = $('map-svg').getBoundingClientRect(), px = r.width / MAP_W, h = r.height / px, kEnd = Math.min(80, Math.max(1, k));
  const from = { k: mapState.k }, c0x = (MAP_W / 2 - mapState.tx) / from.k, c0y = (MAP_TOP + h / 2 - mapState.ty) / from.k;
  const dist = Math.hypot(place.x - c0x, place.y - c0y), dip = Math.log(1 + Math.min(3, (dist / (MAP_W / Math.min(from.k, kEnd))) * 2));
  cancelAnimationFrame(mapAnim);
  const t0 = performance.now(), ms = 1000, ease = (x) => (x < 0.5 ? 4 * x ** 3 : 1 - (-2 * x + 2) ** 3 / 2);
  const step = (now) => {
    const f = Math.min(1, (now - t0) / ms), e = ease(f);
    const kk = Math.max(1, Math.exp(Math.log(from.k) + (Math.log(kEnd) - Math.log(from.k)) * e - dip * Math.sin(Math.PI * f)));
    mapState.k = kk;
    mapState.tx = MAP_W / 2 - (c0x + (place.x - c0x) * e) * kk;
    mapState.ty = MAP_TOP + h / 2 - (c0y + (place.y - c0y) * e) * kk;
    mapApply(true);
    if (f < 1) mapAnim = requestAnimationFrame(step);
  };
  mapAnim = requestAnimationFrame(step);
}

function mapApply(redraw) {
  const k = mapState.k, r = $('map-svg').getBoundingClientRect(), h = r.height / (r.width / MAP_W);
  mapState.tx = Math.min(0, Math.max(MAP_W - MAP_W * k, mapState.tx));
  mapState.ty = Math.min(MAP_TOP * (1 - k), Math.max(MAP_TOP + h - (MAP_TOP + MAP_H) * k, mapState.ty));
  mapEls.world.setAttribute('transform', `translate(${mapState.tx} ${mapState.ty}) scale(${k})`);
  if (redraw) mapDraw();
}

// One circle per place with the number of sessions; zoomed in far enough, every session is its own dot around the place.
function mapDraw() {
  if (!mapEls.dots) return;
  const k = mapState.k, px = mapPx() * k, rad = (n) => n / px; // n is in screen pixels
  const g = mapEls.dots, q = mapState.query;
  g.replaceChildren();
  for (const place of mapState.places) {
    const n = place.sessions.length, hit = place.sessions.some((s) => mapState.hits.has(s));
    if (k >= 14 && n > 1) {
      const head = svgEl('text', { x: place.x, y: place.y - rad(8.5) * Math.sqrt(n + 1) - rad(12), 'font-size': rad(14), 'text-anchor': 'middle', class: 'map-label place' });
      head.textContent = `${place.city || place.country} (${n})`;
      g.append(head);
      place.sessions.forEach((s, i) => {
        const a = i * 2.39996, d = rad(8.5) * Math.sqrt(i + 1), x = place.x + Math.cos(a) * d, y = place.y + Math.sin(a) * d;
        const isHit = mapState.hits.has(s);
        g.append(mapDot(x, y, rad(4), '', place, s, isHit ? 'hit' : q ? 'dim' : ''));
        if (isHit) {
          const tx = svgEl('text', { x: x + rad(9), y: y + rad(4), class: 'map-label', 'font-size': rad(11) });
          tx.textContent = s.user || s.device || '';
          g.append(tx);
        }
      });
    } else {
      const r = rad(Math.min(9 + Math.sqrt(n) * 3.2, 26));
      g.append(mapDot(place.x, place.y, r, n > 1 ? String(n) : '', place, null, hit ? 'hit' : q ? 'dim' : '', place.exact ? '' : 'approx'));
      // the name of the place once, however many sessions come out there (many devices behind one provider address are one place)
      if (k >= 2.5 || hit) {
        const tx = svgEl('text', { x: place.x, y: place.y + r + rad(14), 'font-size': rad(12), 'text-anchor': 'middle', class: 'map-label' });
        tx.textContent = place.city || place.country;
        g.append(tx);
      }
    }
  }
}

function mapDot(x, y, r, label, place, session, state, extra) {
  const a = svgEl('g', { class: `map-dot ${state} ${extra ?? ''}`.trim(), tabindex: '0', role: 'button' });
  const who = session ? session.user || session.device || '' : `${place.sessions.length} ${t('mapSessions')}`;
  a.setAttribute('aria-label', `${place.city || place.country}: ${who}`);
  if (!session) a.append(svgEl('circle', { cx: x, cy: y, r: r * 1.6, class: 'map-halo' }), svgEl('circle', { cx: x, cy: y, r, class: 'map-pulse' }));
  a.append(svgEl('circle', { cx: x, cy: y, r, class: 'map-core' }));
  if (label) {
    const tx = svgEl('text', { x, y: y + r * 0.34, 'font-size': r * 0.95, 'text-anchor': 'middle' });
    tx.textContent = label;
    a.append(tx);
  }
  const open = () => {
    mapShowPlace(place, session);
    if (!session && place.sessions.length > 1 && mapState.k < 14) { mapTip(null); mapFocus(place, 40); }
  };
  a.addEventListener('click', open);
  a.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } });
  a.addEventListener('mouseenter', () => mapTip(a, place, session));
  a.addEventListener('focus', () => mapTip(a, place, session));
  a.addEventListener('mouseleave', () => mapTip(null));
  a.addEventListener('blur', () => mapTip(null));
  return a;
}

// The information box at a dot (same look as the one in the day chart on Insights): the place, how many sessions, and who is there.
function mapTip(dot, place, session) {
  let tip = mapEls.tip;
  if (!tip) { tip = mapEls.tip = el('div', { class: 'daytip maptip', role: 'status', hidden: '' }); $('map-svg').parentElement.append(tip); }
  if (!dot) { tip.hidden = true; return; }
  const list = session ? [session] : place.sessions, where = [place.city, place.country].filter(Boolean).join(', ') || t('mapUnknown');
  const rows = list.slice(0, 8).map((s) => el('div', { text: `${s.user || '–'} · ${s.device || ''}${session ? ` · ${(s.machine ?? '').split('.')[0]} · ${s.group ?? ''}` : ''}` }));
  tip.replaceChildren(el('strong', { text: where }), el('div', { class: 'muted', text: place.exact || place.city === '' ? `${list.length} ${t('mapSessions')}` : `${list.length} ${t('mapSessions')} · ${t('mapApprox')}` }), ...rows,
    list.length > 8 ? el('div', { class: 'muted', text: `+${list.length - 8}` }) : '', !session && list.length > 1 ? el('div', { class: 'muted', text: t('mapClickZoom') }) : '');
  tip.hidden = false;
  const host = tip.parentElement.getBoundingClientRect(), r = dot.getBoundingClientRect();
  tip.style.left = `${Math.min(host.width - tip.offsetWidth - 4, Math.max(4, r.left - host.left + r.width / 2 - tip.offsetWidth / 2))}px`;
  const above = r.top - host.top - tip.offsetHeight - 8;
  tip.style.top = `${above >= 4 ? above : r.bottom - host.top + 8}px`;
}

// The sessions of one place as a list below the map; every user can be opened from it.
function mapShowPlace(place, only) {
  const box = $('map-place'), list = only ? [only] : place.sessions;
  box.hidden = false;
  box.replaceChildren();
  const close = el('button', { type: 'button', class: 'mapclose', text: '×', title: t('mapClose'), 'aria-label': t('mapClose') });
  close.addEventListener('click', mapHome);
  box.append(close, el('h3', { text: `${place.city ? `${place.city}, ` : ''}${place.country} (${list.length})` }));
  if (!place.exact) box.append(el('p', { class: 'muted', text: t(place.city ? 'mapApproxCity' : 'mapApproxNone') }));
  const zoom = el('button', { type: 'button', text: t('mapZoomHere') });
  zoom.addEventListener('click', () => mapFocus(place, 40));
  box.append(zoom, mapTable(list));
}

function mapTable(list) {
  const tbl = el('table', { class: 'smalltable' }), head = el('tr');
  for (const k of ['mapUser', 'mapDevice', 'mapPlace', 'mapMachine', 'mapGroup', '']) head.append(el('th', { scope: 'col', text: k ? t(k) : '' }));
  tbl.append(el('thead', {}, head));
  const body = el('tbody');
  for (const s of list) {
    const tr = el('tr'), td = el('td', { class: 'rowbtns' }), pl = mapState.placeOf?.get(s);
    tr.append(el('td', { text: s.user || '–' }), el('td', { text: s.device || '' }), el('td', { text: [s.city, s.country].filter(Boolean).join(', ') }),
      el('td', { text: s.machine ? s.machine.split('.')[0] : '' }), el('td', { text: s.group || '' }));
    if (pl) {
      const g = el('button', { type: 'button', text: t('mapShowOnMap'), 'aria-label': `${t('mapShowOnMap')}: ${s.user || s.device}` });
      g.addEventListener('click', () => { $('map-svg').scrollIntoView({ behavior: 'smooth', block: 'center' }); mapFocus(pl, 40); mapShowPlace(pl, null); });
      td.append(g);
    }
    if (s.sid) {
      const b = el('button', { type: 'button', text: t('insOpenUser'), 'aria-label': `${t('insOpenUser')}: ${s.user}` });
      b.addEventListener('click', () => openUser(s.sid, s.user));
      td.append(b);
    }
    tr.append(td);
    body.append(tr);
  }
  tbl.append(body);
  return el('div', { class: 'table-wrap' }, tbl);
}

// Live filter: every letter typed narrows the dots (highlighted), and both lists below the map. The lists open while a search is active.
const mapMatches = (s, q) => [s.user, s.device, s.machine, s.group, s.city, s.country].some((v) => geoKey(v).includes(q));
function mapSearch(go) {
  const q = geoKey(mapState.query), res = $('map-results');
  mapState.hits = new Set();
  res.replaceChildren();
  if (q) {
    for (const s of [...mapState.places.flatMap((pl) => pl.sessions), ...mapState.unlocated]) if (mapMatches(s, q)) mapState.hits.add(s);
    res.append(el('p', { class: 'muted', text: mapState.hits.size ? t('mapFound', { n: mapState.hits.size }) : t('mapNoneFound') }));
    const first = [...mapState.hits].map((s) => mapState.placeOf.get(s)).find(Boolean);
    if (go && first) { mapFocus(first, mapState.hits.size === 1 ? 18 : 6); mapShowPlace(first, null); }
  }
  mapLists();
  mapDraw();
}

// The two lists under the map, closed by default: sessions with a known location and sessions without a usable one.
function mapLists() {
  const q = geoKey(mapState.query), open = (mapState.open ??= { located: false, unlocated: false });
  const make = (kind, all, key, hint) => {
    const host = $(`map-${kind}`), list = q ? all.filter((s) => mapMatches(s, q)) : all;
    host.hidden = all.length === 0;
    if (all.length === 0) { host.replaceChildren(); return; }
    const d = el('details', {}, el('summary', { text: t(key, { n: list.length }) }), hint ? el('p', { class: 'muted', text: hint }) : '', list.length ? mapTable(list.slice(0, 300)) : el('p', { class: 'muted', text: t('mapNoneFound') }));
    d.open = q ? true : open[kind];
    d.addEventListener('toggle', () => { if (!mapState.query) open[kind] = d.open; });
    host.replaceChildren(d);
  };
  make('located', mapState.places.flatMap((pl) => pl.sessions), 'mapLocated', '');
  make('unlocated', mapState.unlocated, 'mapUnlocated', t('mapUnlocatedHint'));
}

function mapRender() {
  const d = mapState.data;
  const nameless = d.points.filter((x) => !x.user).length;
  $('map-total').textContent = t('mapTotal', { n: d.points.length, places: mapState.places.length }) + (nameless ? ` · ${t('mapNoName', { n: nameless, a: d.noUserId ?? 0, b: d.unresolved ?? 0 })}` : '');
  $('map-demo').hidden = true;
  const note = $('map-nonelocated');
  if (note) { note.hidden = !(d.points.length > 0 && mapState.places.length === 0); note.textContent = t('mapNoneLocated'); }
  mapSearch(false);
}

async function loadMap() {
  const r = await fetch('api/helpdesk/insights/map', { credentials: 'same-origin' });
  if (r.status === 403) { showView('mine'); return; }
  if (r.status === 429) { setStatus(t('rateLimited'), true); return; }
  if (r.status === 503) { setStatus(t('unavailable'), true); return; }
  if (!r.ok) throw new Error(String(r.status));
  mapState.data = await r.json();
  await mapGeo();
  mapInit();
  mapBuild(mapState.data);
  $('map-place').hidden = true;
  mapRender();
  mapApply(true);
}
