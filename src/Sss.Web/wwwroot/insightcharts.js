'use strict';
// The charts of the Insights tab (Director-style): trend lines in the key figures, logon time bars per delivery group, health gauge, sessions by state, a week heat map of the
// logons, utilisation, application start times and costs. Every chart has an info box on hover (or tap / keyboard focus) and uses the colour variables of the page,
// so light and dark mode both work. The tables with the exact numbers stay below each chart.

// An info box next to a mark of an svg: show(build) fills it with the lines build() returns.
function chartHost(svg) {
  const tip = el('div', { class: 'daytip', role: 'status', hidden: '' }), wrap = el('div', { class: 'daywrap' }, svg, tip);
  const hide = () => { tip.hidden = true; };
  const bind = (node, build) => {
    const show = () => {
      tip.replaceChildren(...build());
      tip.hidden = false;
      const host = wrap.getBoundingClientRect(), r = node.getBoundingClientRect();
      tip.style.left = `${Math.min(host.width - tip.offsetWidth - 4, Math.max(4, r.left - host.left + r.width / 2 - tip.offsetWidth / 2))}px`;
      const above = r.top - host.top - tip.offsetHeight - 6;
      tip.style.top = `${above >= 0 ? above : r.bottom - host.top + 6}px`;
    };
    node.addEventListener('mouseenter', show); node.addEventListener('click', show); node.addEventListener('focus', show);
    node.addEventListener('mouseleave', hide); node.addEventListener('blur', hide);
    if (!node.hasAttribute('tabindex')) node.setAttribute('tabindex', '0');
  };
  return { wrap, bind };
}

const svgText = (parent, x, y, txt, attrs = {}) => { const e = svgEl('text', { x, y, ...attrs }); e.textContent = txt; parent.append(e); return e; };
const chartCard = (title, hint, ...kids) => el('section', { class: 'chcard' }, el('h3', { text: title }), hint ? el('p', { class: 'muted chhint', text: hint }) : '', ...kids);
const legendOf = (items) => el('div', { class: 'chlegend' }, ...items.map(([cls, txt]) => el('span', {}, el('span', { class: `swatch ${cls}` }), txt)));
const tline = (txt, cls = '') => el('div', { class: cls, text: txt });

// A small trend line with an area and a dot at the last value (null values leave a gap).
function sparkSvg(values, cls = 'cat0') {
  const w = 140, h = 30, v = values.filter((x) => x != null);
  const svg = svgEl('svg', { viewBox: `0 0 ${w} ${h}`, class: `tspark ${cls}`, 'aria-hidden': 'true', preserveAspectRatio: 'none' });
  if (v.length < 2) return svg;
  const mx = Math.max(...v), mn = Math.min(...v), n = values.length;
  const pts = values.map((x, i) => (x == null ? null : [(i / (n - 1)) * (w - 4) + 2, h - 3 - ((x - mn) / (mx - mn || 1)) * (h - 8)]));
  const segs = []; let cur = [];
  for (const p of pts) { if (p) cur.push(p); else if (cur.length) { segs.push(cur); cur = []; } }
  if (cur.length) segs.push(cur);
  for (const s of segs) if (s.length > 1) {
    svg.append(svgEl('path', { class: 'sparea', d: `M${s[0][0]},${h} L${s.map((p) => p.join(',')).join(' L')} L${s.at(-1)[0]},${h}Z` }), svgEl('polyline', { class: 'spline', points: s.map((p) => p.join(',')).join(' ') }));
  }
  const last = pts.filter(Boolean).at(-1);
  if (last) svg.append(svgEl('circle', { class: 'spdot', cx: last[0], cy: last[1], r: 2.6 }));
  return svg;
}

// Logon time per delivery group: bar = average, light bar = 90th percentile, dashed line = limit; the colour says whether the group is fine.
function renderGroupBars(groups) {
  const box = $('ins-groupbars');
  const list = groups.filter((g) => g.sessions > 0).slice(0, 12);
  if (!list.length) { box.replaceChildren(); return; }
  const lim = state.quality?.logonWarnSeconds ?? 45, W = 1000, L = 170, R = 250, rh = 44, top = 4;
  const max = Math.max(lim * 1.4, ...list.map((g) => g.p90Seconds)), sc = (W - L - R) / max;
  const svg = svgEl('svg', { viewBox: `0 0 ${W} ${list.length * rh + top + 22}`, class: 'chart2', role: 'img', 'aria-label': t('chGroupBarsTitle') });
  const { wrap, bind } = chartHost(svg);
  list.forEach((g, i) => {
    const y = top + i * rh, lvl = g.sessions < 5 ? 'few' : g.avgSeconds >= lim ? 'crit' : g.avgSeconds >= lim * 0.7 ? 'warn' : 'good';
    const row = svgEl('g', { class: 'hitrow' });
    row.append(svgEl('rect', { x: 0, y, width: W, height: rh - 4, fill: 'transparent' }), svgEl('rect', { class: 'trk', x: L, y: y + 8, width: max * sc, height: 22, rx: 6 }),
      svgEl('rect', { class: `p90 ${lvl}`, x: L, y: y + 8, width: Math.max(4, g.p90Seconds * sc), height: 22, rx: 6 }),
      svgEl('rect', { class: `avg ${lvl}`, x: L, y: y + 13, width: Math.max(3, g.avgSeconds * sc), height: 12, rx: 4 }));
    svgText(row, L - 8, y + 24, g.group.length > 22 ? `${g.group.slice(0, 21)}…` : g.group, { 'text-anchor': 'end' });
    svgText(row, L + max * sc + 12, y + 24, `Ø ${g.avgSeconds} s · P90 ${g.p90Seconds} s`, { class: 'v' });
    svg.append(row);
    bind(row, () => [el('strong', { text: g.group }), tline(`${g.sessions} ${t('colSessions')}`), tline(`${t('insAvg')}: ${g.avgSeconds} s`), tline(`${t('insMedian')}: ${g.medianSeconds} s`), tline(`${t('insP90')}: ${g.p90Seconds} s`),
      g.sessions < 5 ? tline(t('insFew'), 'muted') : '']);
  });
  svg.append(svgEl('line', { class: 'limit', x1: L + lim * sc, x2: L + lim * sc, y1: 0, y2: list.length * rh + top }));
  svgText(svg, L + lim * sc, list.length * rh + top + 16, t('chLimit', { s: lim }), { 'text-anchor': 'middle' });
  box.replaceChildren(chartCard(t('chGroupBarsTitle'), t('chGroupBarsHint'), wrap, legendOf([['cat0', t('chAvg')], ['lt', t('chP90')]])));
}

// The health score of the whole site as a gauge (red below 40, yellow below 70, green from 70) with a bar per delivery group, and the sessions by state as a ring.
function renderHealth(d) {
  const box = $('ins-health'), ms = (d.machines ?? []).filter((m) => m.score != null);
  const avg = (a) => (a.length ? Math.round(a.reduce((s, m) => s + m.score, 0) / a.length) : null);
  const site = avg(ms), byGroup = [...ms.reduce((mp, m) => mp.set(m.group || '?', [...(mp.get(m.group || '?') ?? []), m]), new Map())].map(([g, a]) => [g, avg(a), a.length]).sort((a, b) => a[1] - b[1]).slice(0, 8);
  const lvl = (v) => (v < 40 ? 'crit' : v < 70 ? 'warn' : 'good');

  const g = svgEl('svg', { viewBox: '0 0 560 190', class: 'chart2', role: 'img', 'aria-label': t('chHealthTitle') });
  const { wrap: gw, bind: gb } = chartHost(g), cx = 150, cy = 150, r = 105;
  const pt = (f) => [cx + r * Math.cos(Math.PI * (1 - f)), cy - r * Math.sin(Math.PI * (1 - f))];
  const arc = (f0, f1, cls, w) => { const [x0, y0] = pt(f0), [x1, y1] = pt(f1); g.append(svgEl('path', { class: `garc ${cls}`, 'stroke-width': w, d: `M${x0},${y0} A${r},${r} 0 0 1 ${x1},${y1}` })); };
  arc(0, 0.4, 'crit z', 22); arc(0.4, 0.7, 'warn z', 22); arc(0.7, 1, 'good z', 22);
  if (site != null) {
    arc(0.0001, Math.max(0.002, site / 100), lvl(site), 9);
    const [nx, ny] = pt(site / 100);
    g.append(svgEl('circle', { class: `gknob ${lvl(site)}`, cx: nx, cy: ny, r: 8 }));
  }
  svgText(g, cx, cy - 10, site == null ? '–' : String(site), { 'text-anchor': 'middle', class: 'gnum' });
  svgText(g, cx, cy + 14, t('chScoreSite'), { 'text-anchor': 'middle' });
  byGroup.forEach(([name, v, n], i) => {
    const y = 36 + i * 22, row = svgEl('g', { class: 'hitrow' });
    row.append(svgEl('rect', { x: 300, y: y - 12, width: 250, height: 22, fill: 'transparent' }));
    svgText(row, 300, y, name.length > 20 ? `${name.slice(0, 19)}…` : name);
    row.append(svgEl('rect', { class: 'trk', x: 300, y: y + 4, width: 200, height: 6, rx: 3 }), svgEl('rect', { class: `avg ${lvl(v)}`, x: 300, y: y + 4, width: Math.max(2, 2 * v), height: 6, rx: 3 }));
    svgText(row, 548, y, String(v), { 'text-anchor': 'end', class: 'v' });
    g.append(row);
    gb(row, () => [el('strong', { text: name }), tline(`${t('insScore')}: ${v}/100`), tline(`${n} ${t('chMachines')}`)]);
  });
  const left = chartCard(t('chHealthTitle'), t('chHealthHint'), site == null ? el('p', { class: 'muted', text: t('chNoScores') }) : gw);

  const act = d.activeSessions ?? 0, dis = d.disconnectedSessions ?? 0, tot = act + dis;
  let right;
  if (tot === 0) right = chartCard(t('chStatesTitle'), '', el('p', { class: 'muted', text: t('chNoSessions') }));
  else {
    const s = svgEl('svg', { viewBox: '0 0 420 200', class: 'chart2', role: 'img', 'aria-label': t('chStatesTitle') });
    const { wrap, bind } = chartHost(s), c2x = 100, c2y = 100, rr = 72, parts = [[t('stateActive'), act, 'cat1'], [t('stateDisconnected'), dis, 'cat3']].filter((p) => p[1] > 0);
    let a = -Math.PI / 2;
    for (const [name, v, cls] of parts) {
      const full = parts.length === 1, b = a + (v / tot) * Math.PI * 2, p = (x) => [c2x + rr * Math.cos(x), c2y + rr * Math.sin(x)];
      const [x0, y0] = p(a), [x1, y1] = p(full ? a + Math.PI * 2 - 0.001 : b - 0.02);
      const path = svgEl('path', { class: `ring ${cls}`, d: `M${x0},${y0} A${rr},${rr} 0 ${b - a > Math.PI ? 1 : 0} 1 ${x1},${y1}`, fill: 'none', 'stroke-width': 24 });
      s.append(path); bind(path, () => [el('strong', { text: name }), tline(`${v} ${t('chSessions')} · ${Math.round((v / tot) * 100)} %`)]); a = b;
    }
    svgText(s, c2x, c2y + 8, String(tot), { 'text-anchor': 'middle', class: 'gnum' }); svgText(s, c2x, c2y + 28, t('chSessions'), { 'text-anchor': 'middle' });
    parts.forEach(([name, v, cls], i) => {
      const y = 70 + i * 34;
      s.append(svgEl('rect', { class: `slice ${cls}`, x: 210, y: y - 11, width: 13, height: 13, rx: 3 }));
      svgText(s, 232, y, name); svgText(s, 350, y, String(v), { 'text-anchor': 'end', class: 'v' }); svgText(s, 400, y, `${Math.round((v / tot) * 100)} %`, { 'text-anchor': 'end' });
    });
    right = chartCard(t('chStatesTitle'), t('chStatesHint'), wrap);
  }
  box.replaceChildren(left, right);
}

// The logons of the last 7 days as a heat map: weekday against hour of the browser's own time (the current hour is outlined).
function renderHeat(more) {
  const box = $('ins-heat'), h = more.heat;
  if (!h?.logons?.length || !h.logons.some((n) => n > 0)) { box.replaceChildren(); return; }
  const start = new Date(h.start).getTime(), cell = Array.from({ length: 7 }, () => Array.from({ length: 24 }, () => ({ n: 0, sum: 0, w: 0, at: null })));
  h.logons.forEach((n, i) => {
    const d = new Date(start + i * 3600000), c = cell[(d.getDay() + 6) % 7][d.getHours()];
    c.n += n; c.at = c.at ?? d; if (h.avgSeconds[i] != null && n > 0) { c.sum += h.avgSeconds[i] * n; c.w += n; }
  });
  const max = Math.max(1, ...cell.flat().map((c) => c.n)), W = 1000, L = 52, cw = (W - L) / 24, ch = 24;
  const names = Array.from({ length: 7 }, (_, i) => new Intl.DateTimeFormat(state.lang, { weekday: 'short' }).format(new Date(2024, 0, 1 + i)));
  const svg = svgEl('svg', { viewBox: `0 0 ${W} ${7 * ch + 24}`, class: 'chart2', role: 'img', 'aria-label': t('chHeatTitle') });
  const { wrap, bind } = chartHost(svg), now = new Date(), nowCell = [(now.getDay() + 6) % 7, now.getHours()];
  const df = new Intl.DateTimeFormat(state.lang, { weekday: 'long', day: '2-digit', month: '2-digit' });
  cell.forEach((row, d) => {
    svgText(svg, L - 8, d * ch + 16, names[d], { 'text-anchor': 'end' });
    row.forEach((c, hr) => {
      const r = svgEl('rect', { class: `hcell${d === nowCell[0] && hr === nowCell[1] ? ' now' : ''}`, x: L + hr * cw + 1, y: d * ch + 1, width: cw - 2, height: ch - 2, rx: 4, 'fill-opacity': (0.06 + (c.n / max) * 0.94).toFixed(2) });
      svg.append(r);
      bind(r, () => [el('strong', { text: `${names[d]} ${String(hr).padStart(2, '0')}:00–${String((hr + 1) % 24).padStart(2, '0')}:00` }), tline(c.at ? df.format(c.at) : '', 'muted'), tline(`${c.n} ${t('chLogons')}`), c.w ? tline(`Ø ${Math.round(c.sum / c.w)} s`) : '']);
    });
  });
  for (let hr = 0; hr < 24; hr += 3) svgText(svg, L + hr * cw + cw / 2, 7 * ch + 17, `${String(hr).padStart(2, '0')}:00`, { 'text-anchor': 'middle' });
  box.replaceChildren(chartCard(t('chHeatTitle'), t('chHeatHint'), wrap));
}

// Utilisation: bar = average, tick = peak (red from 90 %), CPU and memory per machine.
function renderUsageChart(usage) {
  const box = $('ins-usage-chart'), list = (usage ?? []).slice(0, 12);
  if (!list.length) { box.replaceChildren(); return; }
  const W = 1000, L = 150, R = 190, rh = 44, sc = (W - L - R) / 100;
  const svg = svgEl('svg', { viewBox: `0 0 ${W} ${list.length * rh + 4}`, class: 'chart2', role: 'img', 'aria-label': t('chUsageTitle') });
  const { wrap, bind } = chartHost(svg);
  list.forEach((u, i) => {
    const y = i * rh + 2, row = svgEl('g', { class: 'hitrow' });
    row.append(svgEl('rect', { x: 0, y, width: W, height: rh - 2, fill: 'transparent' }));
    svgText(row, L - 8, y + 20, u.machine.split('.')[0], { 'text-anchor': 'end' });
    [[u.cpu, u.peakCpu, 'cat0', 0], [u.memory, u.peakMemory, 'cat2', 1]].forEach(([v, p, cls, j]) => {
      const yy = y + 6 + j * 17;
      row.append(svgEl('rect', { class: 'trk', x: L, y: yy, width: 100 * sc, height: 10, rx: 5 }), svgEl('rect', { class: `slice ${cls}`, x: L, y: yy, width: Math.max(2, v * sc), height: 10, rx: 5 }),
        svgEl('line', { class: `ptick${p >= 90 ? ' hot' : ''}`, x1: L + p * sc, x2: L + p * sc, y1: yy - 3, y2: yy + 13 }));
      svgText(row, L + 100 * sc + 12, yy + 10, `${j ? t('chMem') : t('chCpu')} ${Math.round(v)} % · ${t('chPeak')} ${Math.round(p)} %`, { class: p >= 90 ? 'v hot' : '' });
    });
    svg.append(row);
    bind(row, () => [el('strong', { text: u.machine }), tline(u.group, 'muted'), tline(`${t('chCpu')}: ${u.cpu} % (${t('chPeak')} ${u.peakCpu} %)`), tline(`${t('chMem')}: ${u.memory} % (${t('chPeak')} ${u.peakMemory} %)`)]);
  });
  box.replaceChildren(chartCard(t('chUsageTitle'), t('chUsageHint'), wrap, legendOf([['cat0', t('chCpu')], ['cat2', t('chMem')]])));
}

// Application start times: average and 90th percentile on one scale; the line between them is the spread.
function renderStartsChart(starts) {
  const box = $('ins-starts-chart'), list = (starts ?? []).slice(0, 10);
  if (!list.length) { box.replaceChildren(); return; }
  const W = 1000, L = 170, R = 230, rh = 38, mx = niceStep(Math.max(...list.map((a) => a.p90Ms)) / 1000), sc = (W - L - R) / mx;
  const svg = svgEl('svg', { viewBox: `0 0 ${W} ${list.length * rh + 28}`, class: 'chart2', role: 'img', 'aria-label': t('chStartsTitle') });
  const { wrap, bind } = chartHost(svg);
  for (let i = 0; i <= 4; i++) { const v = (mx / 4) * i; svg.append(svgEl('line', { class: 'gridl', x1: L + v * sc, x2: L + v * sc, y1: 0, y2: list.length * rh })); svgText(svg, L + v * sc, list.length * rh + 18, `${+v.toFixed(1)} s`, { 'text-anchor': 'middle' }); }
  list.forEach((a, i) => {
    const y = i * rh + 19, av = a.avgMs / 1000, p9 = a.p90Ms / 1000, row = svgEl('g', { class: 'hitrow' });
    row.append(svgEl('rect', { x: 0, y: y - 18, width: W, height: rh - 2, fill: 'transparent' }), svgEl('line', { class: 'spread', x1: L + av * sc, x2: L + p9 * sc, y1: y, y2: y }),
      svgEl('circle', { class: 'slice cat0', cx: L + av * sc, cy: y, r: 7 }), svgEl('circle', { class: 'slice cat3', cx: L + p9 * sc, cy: y, r: 5 }));
    svgText(row, L - 8, y + 4, a.name.length > 24 ? `${a.name.slice(0, 23)}…` : a.name, { 'text-anchor': 'end' });
    svgText(row, L + p9 * sc + 14, y + 4, `Ø ${av.toFixed(1)} s · P90 ${p9.toFixed(1)} s`, { class: 'v' });
    svg.append(row);
    bind(row, () => [el('strong', { text: a.name }), tline(`${a.launches} ${t('insStarts')}`), tline(`${t('insAvgStart')}: ${av.toFixed(1)} s`), tline(`${t('insP90Start')}: ${p9.toFixed(1)} s`),
      a.slowMachine ? tline(`${t('insSlowOn')}: ${a.slowMachine} (${(a.slowMachineMs / 1000).toFixed(1)} s)`) : '']);
  });
  box.replaceChildren(chartCard(t('chStartsTitle'), t('chStartsHint'), wrap, legendOf([['cat0', t('chAvg')], ['cat3', t('chP90')]])));
}

// Costs of the last 30 days per delivery group: compute and storage stacked, the saving written below (one chart per currency).
function renderCostsChart(costs) {
  const box = $('ins-costs-chart'), cur = [...new Set((costs ?? []).map((c) => c.currency))];
  if (!cur.length) { box.replaceChildren(); return; }
  const nf = new Intl.NumberFormat(state.lang, { maximumFractionDigits: 0 });
  box.replaceChildren(...cur.map((code) => {
    const list = costs.filter((c) => c.currency === code).slice(0, 10), W = 1000, L = 170, R = 210, rh = 50, mx = Math.max(1, ...list.map((c) => c.compute + c.storage)), sc = (W - L - R) / mx;
    const svg = svgEl('svg', { viewBox: `0 0 ${W} ${list.length * rh + 4}`, class: 'chart2', role: 'img', 'aria-label': t('chCostsTitle') });
    const { wrap, bind } = chartHost(svg);
    list.forEach((c, i) => {
      const y = i * rh + 4, row = svgEl('g', { class: 'hitrow' }), total = c.compute + c.storage;
      row.append(svgEl('rect', { x: 0, y, width: W, height: rh - 2, fill: 'transparent' }), svgEl('rect', { class: 'slice cat0', x: L, y: y + 6, width: Math.max(2, c.compute * sc), height: 22, rx: 5 }),
        svgEl('rect', { class: 'slice cat5', x: L + c.compute * sc, y: y + 6, width: Math.max(0, c.storage * sc), height: 22, rx: 5 }));
      svgText(row, L - 8, y + 22, c.group.length > 22 ? `${c.group.slice(0, 21)}…` : c.group, { 'text-anchor': 'end' });
      svgText(row, L + total * sc + 12, y + 22, `${nf.format(total)} ${code}`, { class: 'v' });
      if (c.savings > 0) svgText(row, L + total * sc + 12, y + 40, `▼ ${t('chSaved')} ${nf.format(c.savings)} ${code}`, { class: 'saved' });
      svg.append(row);
      bind(row, () => [el('strong', { text: c.group }), tline(`${t('insCompute')}: ${nf.format(c.compute)} ${code}`), tline(`${t('insStorage')}: ${nf.format(c.storage)} ${code}`), c.savings ? tline(`${t('insSavings')}: ${nf.format(c.savings)} ${code}`) : '']);
    });
    return chartCard(`${t('chCostsTitle')}${cur.length > 1 ? ` (${code})` : ''}`, t('chCostsHint'), wrap, legendOf([['cat0', t('insCompute')], ['cat5', t('insStorage')]]));
  }));
}
