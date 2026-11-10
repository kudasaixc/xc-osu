import { localSessions, clearLocalSessions } from './xc-telemetry.js';

// Read only: opening this view never starts collection or contacts the telemetry server.
export function showLocalTelemetry(t) {
  const dialog = document.createElement('dialog');
  dialog.id = 'xc-local-telemetry';
  dialog.style.cssText = 'color:#eee;background:#201b2c;border:1px solid #80708e;border-radius:14px;padding:24px;width:min(920px,92vw);max-height:85vh;overflow:auto';
  const el = (tag, text, parent = dialog) => { const n = document.createElement(tag); if (text != null) n.textContent = text; parent.append(n); return n; };
  el('h2', t('tm.local'));
  el('p', t('tm.help'));
  const actions = el('div'); actions.style.cssText = 'display:flex;gap:12px;flex-wrap:wrap';
  const button = (label, fn) => { const b = el('button', label, actions); b.type = 'button'; b.className = 'btn'; b.onclick = fn; return b; };
  const sessions = localSessions();
  button(t('tm.close'), () => dialog.close());
  const exportButton = button(t('tm.export'), () => {
    const url = URL.createObjectURL(new Blob([JSON.stringify({version:1, sessions}, null, 2)], {type:'application/json'}));
    const a = el('a'); a.href = url; a.download = 'xc-osu-telemetry-local.json'; a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  exportButton.disabled = !sessions.length;
  const clearButton = button(t('tm.clear'), () => {
    if (confirm(t('tm.confirm'))) { clearLocalSessions(); dialog.close(); showLocalTelemetry(t); }
  });
  clearButton.disabled = !sessions.length;
  if (!sessions.length) el('p', t('tm.empty'));
  for (const session of sessions) {
    const section = el('section');
    el('h3', new Date(session.started).toLocaleString(), section);
    el('p', `${session.meta?.build || '—'} · ${session.meta?.hw?.gpu || '—'} · ${session.items.length} ${t('tm.records')}`, section);
    if (session.dropped) el('p', `${t('tm.trimmed')} ${session.dropped}`, section);
    const flights = new Map();
    for (const item of session.items) {
      if (item.n !== 'flight' && item.n !== 'flight-context') continue;
      if (!flights.has(item.at)) flights.set(item.at, { parts: [], context: [] });
      flights.get(item.at)[item.n === 'flight' ? 'parts' : 'context'].push(item);
    }
    if (!flights.size) el('p', t('tm.noFlights'), section);
    for (const [at, flight] of flights) {
      const parts = flight.parts.sort((a,b) => a.part - b.part);
      const frames = parts.flatMap(p => p.frames || []).sort((a,b) => a[0] - b[0]);
      const complete = parts.length > 0 && new Set(parts.map(p=>p.part)).size === parts[0].parts && !parts.some(p=>p.dropped);
      const detail = el('details', null, section);
      el('summary', `${new Date(at).toLocaleTimeString()} · ${parts[0]?.scr || '—'} · ${complete ? t('tm.complete') : t('tm.partial')} · ${Math.max(0, ...frames.map(f => Number(f[1]) || 0)).toFixed(1)} ms`, detail);
      if (frames.length) {
        const canvas = el('canvas', null, detail); canvas.width = 820; canvas.height = 220;
        canvas.style.cssText = 'width:100%;height:auto;background:#15111e;margin-top:12px';
        canvas.setAttribute('role','img'); canvas.setAttribute('aria-label', t('tm.chart'));
        const ctx = canvas.getContext('2d'); const ymax = Math.max(34, ...frames.map(f => Number(f[1]) || 0));
        const x = v => 45 + (v + 2000) / 4000 * 750, y = v => 180 - v / ymax * 155;
        ctx.strokeStyle = '#72677c'; ctx.beginPath(); ctx.moveTo(x(0),20); ctx.lineTo(x(0),180); ctx.stroke();
        ctx.fillStyle = '#ddd'; ctx.font = '13px sans-serif';
        ctx.fillText(`${ymax.toFixed(1)} ms`, 3, 15); ctx.fillText('-2 s',45,205); ctx.fillText('0',x(0),205); ctx.fillText('+2 s',755,205);
        ctx.strokeStyle = '#ff85bc'; ctx.beginPath();
        frames.forEach((f,i) => { if (i) ctx.lineTo(x(f[0]),y(f[1])); else ctx.moveTo(x(f[0]),y(f[1])); }); ctx.stroke();
        el('p', t('tm.chart'), detail);
      }
      const events = flight.context.flatMap(p => p.events || []).sort((a,b)=>a.at-b.at);
      el('h4', t('tm.events'), detail);
      if (!events.length) el('p', t('tm.noEvents'), detail);
      const list = el('ul', null, detail);
      for (const event of events) {
        const { at: time, type, ...data } = event;
        el('li', `${time-at >= 0 ? '+' : ''}${Math.round(time-at)} ms · ${type} · ${JSON.stringify(data)}`, list);
      }
      const raw = el('details', null, detail); el('summary', t('tm.frames'), raw);
      const pre = el('pre', JSON.stringify({cols:parts[0]?.cols, frames},null,2), raw);
      pre.style.cssText = 'max-height:280px;overflow:auto;font-size:12px';
    }
    const raw = el('details', null, section); el('summary', t('tm.raw'), raw);
    const pre = el('pre', null, raw); pre.style.cssText = 'max-height:300px;overflow:auto;font-size:12px';
    raw.addEventListener('toggle', () => { if (raw.open && !pre.textContent) pre.textContent = JSON.stringify(session,null,2); });
  }
  dialog.addEventListener('close', () => dialog.remove());
  document.body.append(dialog); dialog.showModal();
}
