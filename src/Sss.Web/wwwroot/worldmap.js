'use strict';
// World map of the live sessions. The map outlines and a list of places are bundled (geo/world.json, Natural Earth, public domain); the position of each
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
    const key = `${Math.round(loc.x)}|${Math.round(loc.y)}`;
    if (!groups.has(key)) groups.set(key, { x: loc.x, y: loc.y, exact: loc.exact, country: loc.country, city: loc.exact ? p.city : '', sessions: [] });
    groups.get(key).sessions.push(p);
  }
  mapState.places = [...groups.values()].sort((a, b) => b.sessions.length - a.sessions.length);
  mapState.unlocated = unlocated;
}

const mapEls = {};
function mapInit() {
  if (mapState.ready) return;
  mapState.ready = true;
  const svg = $('map-svg');
  mapEls.world = svgEl('g', { id: 'map-world' });
  mapEls.land = svgEl('g', { class: 'map-land' });
  mapEls.dots = svgEl('g', { class: 'map-dots' });
  mapEls.world.append(mapEls.land, mapEls.dots);
  svg.append(mapEls.world);
  for (const c of mapState.geo.countries) mapEls.land.append(svgEl('path', { d: c.d, class: 'map-country' }));
  svg.addEventListener('wheel', (e) => { e.preventDefault(); mapZoomAt(e.deltaY < 0 ? 1.4 : 1 / 1.4, e.clientX, e.clientY); }, { passive: false });
  svg.addEventListener('pointerdown', (e) => {
    if (e.target.closest?.('.map-dot')) return;
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
  $('map-reset').addEventListener('click', () => { mapState.k = 1; mapState.tx = 0; mapState.ty = 0; mapApply(true); });
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

// Puts the place in the middle of the map at the given zoom.
function mapFocus(place, k) {
  const r = $('map-svg').getBoundingClientRect(), px = r.width / MAP_W;
  mapState.k = Math.min(80, Math.max(1, k));
  mapState.tx = MAP_W / 2 - place.x * mapState.k;
  mapState.ty = MAP_TOP + (r.height / px) / 2 - place.y * mapState.k;
  mapApply(true);
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
      place.sessions.forEach((s, i) => {
        const a = i * 2.39996, d = rad(11) * Math.sqrt(i + 1), x = place.x + Math.cos(a) * d, y = place.y + Math.sin(a) * d;
        const isHit = mapState.hits.has(s);
        g.append(mapDot(x, y, rad(6), '', place, s, isHit ? 'hit' : q ? 'dim' : ''));
        if (isHit || k >= 30) {
          const tx = svgEl('text', { x: x + rad(9), y: y + rad(4), class: 'map-label', 'font-size': rad(11) });
          tx.textContent = s.user || s.device || '';
          g.append(tx);
        }
      });
    } else {
      const r = rad(Math.min(9 + Math.sqrt(n) * 3.2, 26));
      g.append(mapDot(place.x, place.y, r, n > 1 ? String(n) : '', place, null, hit ? 'hit' : q ? 'dim' : '', place.exact ? '' : 'approx'));
    }
  }
}

function mapDot(x, y, r, label, place, session, state, extra) {
  const a = svgEl('g', { class: `map-dot ${state} ${extra ?? ''}`.trim(), tabindex: '0', role: 'button' });
  const who = session ? session.user || session.device || '' : `${place.sessions.length} ${t('mapSessions')}`;
  a.setAttribute('aria-label', `${place.city || place.country}: ${who}`);
  a.append(svgEl('circle', { cx: x, cy: y, r }));
  if (label) {
    const tx = svgEl('text', { x, y: y + r * 0.34, 'font-size': r * 0.95, 'text-anchor': 'middle' });
    tx.textContent = label;
    a.append(tx);
  }
  const tip = svgEl('title');
  tip.textContent = session ? `${session.user || ''} · ${session.device || ''} · ${place.city || place.country}`
    : `${place.city || place.country}: ${place.sessions.length}${place.exact ? '' : ` (${t('mapApprox')})`}`;
  a.append(tip);
  const open = () => mapShowPlace(place, session);
  a.addEventListener('click', open);
  a.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } });
  return a;
}

// The sessions of one place as a list below the map; every user can be opened from it.
function mapShowPlace(place, only) {
  const box = $('map-place'), list = only ? [only] : place.sessions;
  box.hidden = false;
  box.replaceChildren();
  box.append(el('h3', { text: `${place.city ? `${place.city}, ` : ''}${place.country} (${list.length})` }));
  if (!place.exact) box.append(el('p', { class: 'muted', text: t(place.city ? 'mapApproxCity' : 'mapApproxNone') }));
  const zoom = el('button', { type: 'button', text: t('mapZoomHere') });
  zoom.addEventListener('click', () => mapFocus(place, 16));
  box.append(zoom, mapTable(list));
}

function mapTable(list) {
  const tbl = el('table', { class: 'smalltable' }), head = el('tr');
  for (const k of ['mapUser', 'mapDevice', 'mapMachine', 'mapGroup', '']) head.append(el('th', { scope: 'col', text: k ? t(k) : '' }));
  tbl.append(el('thead', {}, head));
  const body = el('tbody');
  for (const s of list) {
    const tr = el('tr'), td = el('td');
    tr.append(el('td', { text: s.user || '–' }), el('td', { text: s.device || '' }), el('td', { text: s.machine ? s.machine.split('.')[0] : '' }), el('td', { text: s.group || '' }));
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

// Search by user, device, machine, group or city: places with a match are highlighted and listed; "Show" zooms to the first match.
function mapSearch(go) {
  const q = geoKey(mapState.query), res = $('map-results');
  mapState.hits = new Set();
  res.replaceChildren();
  if (q) {
    const all = [...mapState.places.flatMap((pl) => pl.sessions), ...mapState.unlocated];
    for (const s of all) if ([s.user, s.device, s.machine, s.group, s.city, s.country].some((v) => geoKey(v).includes(q))) mapState.hits.add(s);
    const found = [...mapState.hits];
    res.append(el('p', { class: 'muted', text: found.length ? t('mapFound', { n: found.length }) : t('mapNoneFound') }));
    const ul = el('ul', { class: 'mapres' });
    for (const s of found.slice(0, 30)) {
      const pl = mapState.places.find((x) => x.sessions.includes(s));
      const b = el('button', { type: 'button', text: `${s.user || s.device} · ${s.device || ''} · ${s.city || s.country || t('mapUnknown')}` });
      b.addEventListener('click', () => { if (pl) { mapFocus(pl, 18); mapShowPlace(pl, null); } else $('map-unlocated').scrollIntoView({ block: 'nearest' }); });
      ul.append(el('li', {}, b));
    }
    res.append(ul);
    const first = found.length ? mapState.places.find((x) => x.sessions.includes(found[0])) : null;
    if (go && first) { mapFocus(first, found.length === 1 ? 18 : 6); mapShowPlace(first, null); return; }
  }
  mapDraw();
}

function mapRender() {
  const d = mapState.data, un = $('map-unlocated');
  $('map-total').textContent = t('mapTotal', { n: d.points.length, places: mapState.places.length });
  $('map-demo').hidden = true;
  un.replaceChildren();
  un.hidden = mapState.unlocated.length === 0;
  if (mapState.unlocated.length) {
    un.append(el('details', {}, el('summary', { text: t('mapUnlocated', { n: mapState.unlocated.length }) }),
      el('p', { class: 'muted', text: t('mapUnlocatedHint') }), mapTable(mapState.unlocated.slice(0, 200))));
  }
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
