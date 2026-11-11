// xc-osu! web — shared plumbing: language, session, API calls, formatting, safe HTML. xc softworks (kudasaixc)

export const WEB = '/web';
export const L = (path = '/') => WEB + (path.startsWith('/') ? path : '/' + path);
export const ASSETS = new URL('..', import.meta.url).href; // the stamped asset folder this module was served from

// ---------------------------------------------------------------- language
const store = {
  get(key) { try { return JSON.parse(localStorage.getItem(key) || 'null'); } catch { return null; } },
  set(key, value) { try { value == null ? localStorage.removeItem(key) : localStorage.setItem(key, JSON.stringify(value)); } catch { } },
};
function initialLang() {
  const own = store.get('xc.web.lang');
  if (own === 'fr' || own === 'en') return own;
  const launcher = store.get('xc.launcher.v1')?.lang; // the game launcher on the same origin
  if (launcher === 'fr' || launcher === 'en') return launcher;
  return /^fr\b/i.test(navigator.language || '') ? 'fr' : 'en';
}
export const state = { lang: initialLang(), me: null, friends: null };
export const setLang = (lang) => { state.lang = lang; store.set('xc.web.lang', lang); document.documentElement.lang = lang; };
/** Pick the string for the current language: t('Sign in', 'Connexion'). */
export const t = (en, fr) => (state.lang === 'fr' ? fr : en);
const locale = () => (state.lang === 'fr' ? 'fr-FR' : 'en-US');

// ---------------------------------------------------------------- escaping / formatting
export const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
export const num = (n, digits = 0) => (n == null || !Number.isFinite(Number(n)) ? '—' : Number(n).toLocaleString(locale(), { maximumFractionDigits: digits, minimumFractionDigits: digits }));
export const pct = (fraction, digits = 2) => (fraction == null ? '—' : num(fraction * 100, digits) + ' %');
export const rankText = (n) => (n ? '#' + num(n) : '—');
export function duration(seconds) {
  if (seconds == null) return '—';
  const s = Math.round(seconds), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
  return h ? `${num(h)} h ${m} min` : `${m} min ${s % 60} s`;
}
export const clock = (seconds) => { const s = Math.round(seconds || 0); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };
export const dateText = (d) => (d ? new Date(d).toLocaleDateString(locale(), { year: 'numeric', month: 'long', day: 'numeric' }) : '—');
export const dateTimeText = (d) => (d ? new Date(d).toLocaleString(locale(), { dateStyle: 'medium', timeStyle: 'short' }) : '—');
export function ago(d) {
  if (!d) return '—';
  const diff = (new Date(d).getTime() - Date.now()) / 1000, rtf = new Intl.RelativeTimeFormat(locale(), { numeric: 'auto' });
  for (const [unit, size] of [['year', 31536000], ['month', 2592000], ['day', 86400], ['hour', 3600], ['minute', 60]])
    if (Math.abs(diff) >= size) return rtf.format(Math.round(diff / size), unit);
  return t('just now', 'à l’instant');
}
export const timeTag = (d) => (d ? `<time datetime="${esc(d)}" title="${esc(dateTimeText(d))}">${esc(ago(d))}</time>` : '—');
export function countryName(code) {
  if (!code || code === 'XX') return t('Unknown', 'Inconnu');
  try { return new Intl.DisplayNames([locale()], { type: 'region' }).of(code) || code; } catch { return code; }
}
export const countryChip = (code) => (code && code !== 'XX' ? `<span class="cc" title="${esc(countryName(code))}">${esc(code)}</span>` : '');

export const MODES = ['osu', 'taiko', 'fruits', 'mania'];
export const modeName = (m) => ({ osu: 'Standard', taiko: 'Taiko', fruits: 'Catch', mania: 'Mania' }[m] || m);
export const modeOfInt = (i) => MODES[i] || 'osu';

// ---------------------------------------------------------------- session + API
export class ApiError extends Error {
  constructor(status, message, data) { super(message); this.status = status; this.data = data; }
}
/** Thrown when a page needs a signed-in player; the router sends them to the sign-in page and back. */
export class NeedSignIn extends Error { }

const AUTH_KEY = 'xc.web.auth';
let auth = store.get(AUTH_KEY);
export const signedIn = () => !!auth?.access;
function setAuth(next) { auth = next; store.set(AUTH_KEY, next); }

async function tokenRequest(fields) {
  const r = await fetch('/oauth/token', { method: 'POST', body: new URLSearchParams({ client_id: '5', client_secret: 'xc', scope: '*', ...fields }) });
  const data = await r.json().catch(() => ({}));
  if (!r.ok || !data.access_token) throw new ApiError(r.status, data.message || data.error_description || t('Incorrect sign in', 'Identifiants incorrects'), data);
  setAuth({ access: data.access_token, refresh: data.refresh_token });
}
let refreshing = null;
function refresh() {
  if (!auth?.refresh) return Promise.reject(new NeedSignIn());
  refreshing ||= tokenRequest({ grant_type: 'refresh_token', refresh_token: auth.refresh })
    .catch(() => { setAuth(null); state.me = null; state.friends = null; throw new NeedSignIn(); })
    .finally(() => { refreshing = null; });
  return refreshing;
}

/**
 * Call the server. `json` sends a JSON body, `form` a urlencoded one, `file` a multipart upload (field "file").
 * A 401 is retried once with a refreshed token; if that fails the player is signed out.
 */
export async function api(path, { method = 'GET', json, form, file, retry = true } = {}) {
  const init = { method, headers: {} };
  if (auth?.access) init.headers.Authorization = 'Bearer ' + auth.access;
  if (json !== undefined) { init.body = JSON.stringify(json); init.headers['Content-Type'] = 'application/json'; }
  else if (form !== undefined) init.body = new URLSearchParams(form);
  else if (file !== undefined) { init.body = new FormData(); init.body.append('file', file); }
  let r;
  try { r = await fetch(path, init); }
  catch { throw new ApiError(0, t('The server cannot be reached. Check your connection and try again.', 'Serveur injoignable. Vérifie ta connexion et réessaie.')); }
  if (r.status === 401 && auth?.access && retry) { await refresh(); return api(path, { method, json, form, file, retry: false }); }
  const text = await r.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { }
  if (r.status === 401) throw new NeedSignIn();
  if (!r.ok) throw new ApiError(r.status, data?.error || data?.message || `${t('Error', 'Erreur')} ${r.status}`, data);
  return lowerKeys(data);
}

// The server writes a few fields the way the game's own classes name them ("Title", "Artist", "User"); give them their usual names too.
const ALIASES = { Title: 'title', Artist: 'artist', Source: 'source', User: 'user', Author: 'author' };
function lowerKeys(value) {
  if (Array.isArray(value)) value.forEach(lowerKeys);
  else if (value && typeof value === 'object') {
    for (const key in value) {
      lowerKeys(value[key]);
      if (ALIASES[key] && value[ALIASES[key]] === undefined) value[ALIASES[key]] = value[key];
    }
  }
  return value;
}
export const v2 = (path, options) => api('/api/v2' + path, options);
export const requireSignIn = () => { if (!signedIn()) throw new NeedSignIn(); };

export async function loadMe() {
  if (!signedIn()) { state.me = null; state.friends = null; return null; }
  try {
    state.me = await v2('/me');
    state.friends = new Set((await v2('/friends')).map((f) => f.target_id));
  } catch (e) { if (!(e instanceof NeedSignIn)) throw e; state.me = null; state.friends = null; }
  return state.me;
}
export async function signIn(username, password) {
  await tokenRequest({ grant_type: 'password', username, password });
  return loadMe();
}
export async function signOut() {
  try { if (auth?.access) await fetch('/api/v2/oauth/tokens/current', { method: 'DELETE', headers: { Authorization: 'Bearer ' + auth.access } }); } catch { }
  setAuth(null); state.me = null; state.friends = null;
}
/** Fetch a protected file (replay, beatmap archive) with the session token and hand it to the browser as a download. */
export async function download(path, filename) {
  const r = await fetch(path, { headers: auth?.access ? { Authorization: 'Bearer ' + auth.access } : {} });
  if (r.status === 401) throw new NeedSignIn();
  if (!r.ok) throw new ApiError(r.status, t('This file is not available.', 'Ce fichier n’est pas disponible.'));
  const named = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(r.headers.get('Content-Disposition') || '');
  const url = URL.createObjectURL(await r.blob());
  const a = Object.assign(document.createElement('a'), { href: url, download: named ? decodeURIComponent(named[1]) : filename });
  document.body.append(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
}

// ---------------------------------------------------------------- safe HTML for content we did not write
const ALLOWED = new Set(['A', 'B', 'BLOCKQUOTE', 'BR', 'CENTER', 'CODE', 'DEL', 'DIV', 'EM', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'HR', 'I', 'IMG', 'LI', 'OL', 'P', 'PRE', 'S', 'SPAN', 'STRONG', 'TABLE', 'TBODY', 'TD', 'TH', 'THEAD', 'TR', 'U', 'UL']);
const DROPPED = new Set(['SCRIPT', 'STYLE', 'IFRAME', 'OBJECT', 'EMBED', 'FORM', 'INPUT', 'BUTTON', 'TEXTAREA', 'SELECT', 'LINK', 'META', 'SVG', 'MATH', 'VIDEO', 'AUDIO', 'TEMPLATE']);
const safeUrl = (value) => { try { const u = new URL(value, location.origin); return u.protocol === 'https:' || u.protocol === 'http:' ? u.href : null; } catch { return null; } };
/** Keeps basic formatting from third-party HTML (beatmap descriptions come from a mirror) and drops everything else. */
export function sanitize(html) {
  const doc = new DOMParser().parseFromString(`<body>${html || ''}</body>`, 'text/html');
  const walk = (node) => {
    for (const child of [...node.childNodes]) {
      if (child.nodeType === Node.TEXT_NODE) continue;
      if (child.nodeType !== Node.ELEMENT_NODE || DROPPED.has(child.tagName)) { child.remove(); continue; }
      walk(child);
      if (!ALLOWED.has(child.tagName)) { child.replaceWith(...child.childNodes); continue; }
      const href = child.tagName === 'A' ? safeUrl(child.getAttribute('href')) : null;
      const src = child.tagName === 'IMG' ? safeUrl(child.getAttribute('src')) : null;
      for (const attr of [...child.attributes]) child.removeAttribute(attr.name);
      if (child.tagName === 'A') { if (href) { child.setAttribute('href', href); child.setAttribute('rel', 'noopener nofollow'); child.setAttribute('target', '_blank'); } }
      if (child.tagName === 'IMG') { if (src) { child.setAttribute('src', src); child.setAttribute('loading', 'lazy'); child.setAttribute('referrerpolicy', 'no-referrer'); } else child.remove(); }
    }
  };
  walk(doc.body);
  return doc.body.innerHTML;
}

/** Player-written text (me! page): escaped first, then a small set of Markdown and BBCode marks is turned into tags. */
export function renderUserText(raw) {
  const inline = (s) => esc(s)
    .replace(/\[img\](https?:\/\/[^\s\[\]"']+)\[\/img\]/gi, '<img src="$1" loading="lazy" referrerpolicy="no-referrer" alt="">')
    .replace(/!\[[^\]]*\]\((https?:\/\/[^\s)"']+)\)/g, '<img src="$1" loading="lazy" referrerpolicy="no-referrer" alt="">')
    .replace(/\[url=(https?:\/\/[^\s\[\]"']+)\](.+?)\[\/url\]/gi, '<a href="$1" target="_blank" rel="noopener nofollow">$2</a>')
    .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)"']+)\)/g, '<a href="$2" target="_blank" rel="noopener nofollow">$1</a>')
    .replace(/\[(b|i|u|s)\](.+?)\[\/\1\]/gi, '<$1>$2</$1>')
    .replace(/\*\*(.+?)\*\*/g, '<b>$1</b>').replace(/(^|[^*])\*([^*\s][^*]*)\*/g, '$1<i>$2</i>')
    .replace(/(^|[\s(])(https?:\/\/[^\s<]+[^\s<.,;:!?)])/g, '$1<a href="$2" target="_blank" rel="noopener nofollow">$2</a>');
  const out = [];
  let list = false, table = null;
  const cells = (line) => line.trim().replace(/^\||\|$/g, '').split('|').map((c) => c.trim());
  const closeTable = () => { if (table) { out.push(`<div class="table-wrap"><table class="table"><thead><tr>${table.head.map((c) => `<th>${inline(c)}</th>`).join('')}</tr></thead><tbody>${table.rows.map((r) => `<tr>${r.map((c) => `<td>${inline(c)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`); table = null; } };
  const lines = String(raw || '').replace(/\r\n/g, '\n').split('\n');
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (/^\s*\|.*\|\s*$/.test(line)) {
      if (table) { table.rows.push(cells(line)); continue; }
      if (/^\s*\|[\s:|-]+\|\s*$/.test(lines[i + 1] || '')) { if (list) { out.push('</ul>'); list = false; } table = { head: cells(line), rows: [] }; i++; continue; }
    }
    closeTable();
    const item = /^\s*[-*]\s+(.*)$/.exec(line);
    if (list && !item) { out.push('</ul>'); list = false; }
    const heading = /^(#{1,4})\s+(.*)$/.exec(line), quote = /^>\s?(.*)$/.exec(line);
    if (item) { if (!list) { out.push('<ul>'); list = true; } out.push(`<li>${inline(item[1])}</li>`); }
    else if (heading) out.push(`<h${heading[1].length + 1}>${inline(heading[2])}</h${heading[1].length + 1}>`);
    else if (quote) out.push(`<blockquote>${inline(quote[1])}</blockquote>`);
    else if (/^\s*(---+|\[hr\])\s*$/i.test(line)) out.push('<hr>');
    else if (line.trim() === '') out.push('<div class="gap"></div>');
    else out.push(`<p>${inline(line)}</p>`);
  }
  if (list) out.push('</ul>');
  closeTable();
  return out.join('');
}

// ---------------------------------------------------------------- shared fragments
export const avatar = (user, size = 40) => `<img class="avatar" src="${esc(user?.avatar_url || '/api/xc/avatar/default')}" width="${size}" height="${size}" alt="" loading="lazy">`;
export const teamTag = (team) => (team?.id ? `<a class="team-tag" href="${L('/teams/' + team.id)}" title="${esc(team.name)}">${esc(team.short_name)}</a>` : '');
export const userLink = (user, { withAvatar = true, size = 28 } = {}) => (user
  ? `<span class="user-link">${withAvatar ? avatar(user, size) : ''}${countryChip(user.country_code)}${teamTag(user.team)}<a href="${L('/users/' + user.id)}">${esc(user.username)}</a></span>`
  : `<span class="muted">${t('deleted player', 'joueur supprimé')}</span>`);

const GRADES = { XH: 'SS', X: 'SS', SH: 'S', S: 'S', A: 'A', B: 'B', C: 'C', D: 'D', F: 'F' };
export const grade = (rank) => `<span class="grade g-${esc(String(rank || 'F').toLowerCase())}" title="${esc(rank)}">${GRADES[rank] || esc(rank || '?')}</span>`;
export const modsHtml = (mods) => ((mods || []).length ? `<span class="mods">${mods.map((m) => `<span class="mod">${esc(m.acronym || m)}</span>`).join('')}</span>` : '');
export const stars = (rating) => { const v = Number(rating) || 0; return `<span class="stars" style="--hue:${Math.max(0, 200 - v * 28)}">★ ${num(v, 2)}</span>`; };
export const statusChip = (status) => `<span class="status s-${esc(status)}">${esc(({ ranked: t('Ranked', 'Classée'), approved: t('Approved', 'Approuvée'), qualified: t('Qualified', 'Qualifiée'), loved: t('Loved', 'Loved'), pending: t('Pending', 'En attente'), wip: 'WIP', graveyard: t('Graveyard', 'Cimetière') })[status] || status)}</span>`;
export const empty = (text) => `<p class="empty">${esc(text)}</p>`;

export function scoreRow(score, { showUser = false, showMap = true, position = null } = {}) {
  const map = score.beatmap, set = map?.beatmapset || score.beatmapset;
  const title = showMap && map
    ? `<a class="score-map" href="${L(`/beatmapsets/${map.beatmapset_id}`)}#${modeOfInt(score.ruleset_id)}/${map.id}"><b>${esc(set?.title || t('Unknown beatmap', 'Beatmap inconnue'))}</b> <span class="muted">${esc(set?.artist || '')}</span><span class="version">${esc(map.version || '')}</span></a>`
    : '';
  return `<li class="score">
    ${position != null ? `<span class="pos">#${position}</span>` : ''}
    ${grade(score.passed === false ? 'F' : score.rank)}
    <div class="score-main">${showUser ? userLink(score.user) : ''}${title}<span class="score-meta">${timeTag(score.ended_at)} · ${num(score.total_score)} · ${num(score.max_combo)}x</span></div>
    ${modsHtml(score.mods)}
    <span class="score-acc">${pct(score.accuracy)}</span>
    <a class="score-pp" href="${L('/scores/' + score.id)}" title="${t('Score details', 'Détails du score')}">${score.pp != null ? num(score.pp) + '<small>pp</small>' : '—'}</a>
  </li>`;
}

/**
 * A list that loads one page at a time. `fetchPage(offset, limit)` returns an array; a short page means the end.
 * Returns the element so pages can place it; failures are shown in place with a retry button.
 */
export function pagedList({ fetchPage, renderItem, pageSize = 10, emptyText, tag = 'ul', className = 'list' }) {
  const wrap = document.createElement('div');
  wrap.innerHTML = `<${tag} class="${className}"></${tag}><div class="list-foot"></div>`;
  const list = wrap.firstElementChild, foot = wrap.lastElementChild;
  let offset = 0;
  const load = async () => {
    foot.innerHTML = `<span class="muted">${t('Loading…', 'Chargement…')}</span>`;
    try {
      const rows = await fetchPage(offset, pageSize);
      list.insertAdjacentHTML('beforeend', rows.map((row, i) => renderItem(row, offset + i)).join(''));
      offset += rows.length;
      if (offset === 0) { foot.innerHTML = empty(emptyText || t('Nothing here yet.', 'Rien ici pour l’instant.')); return; }
      foot.innerHTML = rows.length < pageSize ? '' : `<button type="button" class="btn ghost">${t('Show more', 'Afficher plus')}</button>`;
    } catch (e) {
      foot.innerHTML = `<span class="bad">${esc(e.message)}</span> <button type="button" class="btn ghost">${t('Try again', 'Réessayer')}</button>`;
    }
    foot.querySelector('button')?.addEventListener('click', load);
  };
  load();
  return wrap;
}

/** Runs an action from a button: disables it meanwhile and reports the outcome in `messageEl` (if given). */
export async function act(button, messageEl, fn, okText) {
  if (button) button.disabled = true;
  if (messageEl) { messageEl.textContent = ''; messageEl.className = 'msg'; }
  try {
    await fn();
    if (messageEl && okText) { messageEl.textContent = okText; messageEl.className = 'msg good'; }
    return true;
  } catch (e) {
    if (e instanceof NeedSignIn) throw e;
    if (messageEl) { messageEl.textContent = e.message; messageEl.className = 'msg bad'; } else alert(e.message);
    return false;
  } finally { if (button) button.disabled = false; }
}
