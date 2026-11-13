// xc-osu! web: page shell and router. xc softworks (kudasaixc)
import { L, WEB, state, t, setLang, esc, avatar, loadMe, signOut, ApiError, NeedSignIn } from './core.js';
import * as users from './pages-user.js';
import * as maps from './pages-beatmap.js';
import * as misc from './pages-misc.js';
import * as account from './pages-account.js';
import * as content from './pages-content.js';

// Paths mirror the ones the game opens (…/users/5, …/beatmapsets/12#osu/34, …/home/account/edit) so its links land here.
const ROUTES = [
  [/^\/$/, misc.home],
  [/^\/(?:users|u)\/([^/]+)(?:\/(osu|taiko|fruits|mania))?$/, users.profile],
  [/^\/users\/([^/]+)\/posts$/, misc.unavailable],
  [/^\/beatmapsets$/, maps.listing],
  [/^\/(?:beatmapsets|s)\/(\d+)(?:\/discussion.*)?$/, maps.beatmapset],
  [/^\/(?:beatmaps|b)\/(\d+)$/, maps.beatmapRedirect],
  [/^\/scores\/(?:(?:osu|taiko|fruits|mania)\/)?(\d+)$/, misc.score],
  [/^\/rankings(?:\/(osu|taiko|fruits|mania))?(?:\/(performance|score|country))?$/, misc.rankings],
  [/^\/teams$/, misc.teams],
  [/^\/teams\/(\d+)$/, misc.team],
  [/^\/multiplayer\/rooms\/(\d+)$/, misc.room],
  [/^\/home\/friends$/, misc.friends],
  [/^\/home\/search$/, misc.search],
  [/^\/(?:home\/)?news$/, content.news],
  [/^\/(?:home\/)?news\/([^/]+)$/, content.newsPost],
  [/^\/(?:help\/)?wiki(?:\/(.*))?$/, content.wiki],
  [/^\/(?:home\/)?changelog(?:\/([^/]+)(?:\/([^/]+))?)?$/, content.changelog],
  [/^\/community\/forums\/topics\/create$/, content.reportIssue],
  [/^\/comments$/, misc.comments],
  [/^\/home\/account\/edit$/, account.settings],
  [/^\/home\/password-reset$/, account.passwordReset],
  [/^\/login$/, account.login],
  [/^\/register$/, account.register],
  [/^\/home$/, misc.home],
  [/^\/(?:community|store|help|legal|home\/support|p)(?:\/.*)?$/, misc.unavailable],
];

const app = document.getElementById('app');
let navigation = 0;

function shell() {
  const kept = app.querySelector('#view'); // re-drawing the header must not wipe the page under it
  const me = state.me;
  app.innerHTML = `
    <a class="skip" href="#view">${t('Skip to content', 'Aller au contenu')}</a>
    <header class="top">
      <div class="top-in">
        <a class="brand" href="${L('/')}"><span class="brand-mark" aria-hidden="true">xc!</span><span class="brand-name">xc-osu!</span></a>
        <nav class="nav" aria-label="${t('Main', 'Principal')}">
          <a href="${L('/rankings')}">${t('Rankings', 'Classements')}</a>
          <a href="${L('/beatmapsets')}">Beatmaps</a>
          <a href="${L('/teams')}">${t('Teams', 'Équipes')}</a>
          <a href="${L('/news')}">${t('News', 'Actus')}</a>
          <a href="${L('/wiki')}">Wiki</a>
        </nav>
        <form class="top-search" role="search" id="top-search">
          <input type="search" name="query" placeholder="${t('Find a player or a beatmap', 'Chercher un joueur ou une beatmap')}" aria-label="${t('Search', 'Recherche')}">
        </form>
        <a class="btn play" href="/">${t('Play', 'Jouer')}</a>
        ${me ? `
          <details class="me-menu">
            <summary>${avatar(me, 32)}<span>${esc(me.username)}</span></summary>
            <div class="menu">
              <a href="${L('/users/' + me.id)}">${t('My profile', 'Mon profil')}</a>
              <a href="${L('/home/friends')}">${t('Friends', 'Amis')}</a>
              <a href="${L('/home/account/edit')}">${t('Settings', 'Réglages')}</a>
              <button type="button" id="sign-out">${t('Sign out', 'Déconnexion')}</button>
            </div>
          </details>` : `<a class="btn ghost" href="${L('/login')}" id="sign-in-link">${t('Sign in', 'Connexion')}</a>`}
      </div>
    </header>
    <main id="view" tabindex="-1"></main>
    <footer class="foot">
      <span>xc-osu!: xc softworks</span>
      <span class="foot-links">
        <a href="${L('/home/changelog')}">${t('Changelog', 'Journal des modifications')}</a>
        <a href="/">${t('Open the game', 'Ouvrir le jeu')}</a>
        <button type="button" class="link" id="lang">${state.lang === 'fr' ? 'English' : 'Français'}</button>
      </span>
    </footer>`;
  if (kept) app.querySelector('#view').replaceWith(kept);
  app.querySelector('#top-search').addEventListener('submit', (e) => {
    e.preventDefault();
    const query = new FormData(e.target).get('query').trim();
    if (query) go('/home/search?query=' + encodeURIComponent(query));
  });
  app.querySelector('#sign-out')?.addEventListener('click', async () => { await signOut(); shell(); render(); });
  app.querySelector('#lang').addEventListener('click', () => { setLang(state.lang === 'fr' ? 'en' : 'fr'); shell(); render(); });
  // keep "where was I" when sending someone to sign in
  app.querySelector('#sign-in-link')?.addEventListener('click', (e) => { e.preventDefault(); go('/login?next=' + encodeURIComponent(current())); });
}

const current = () => (location.pathname.slice(WEB.length) || '/') + location.search + location.hash;

/** Navigate inside the site. `path` is site-relative ("/users/5"). */
export function go(path, { replace = false } = {}) {
  history[replace ? 'replaceState' : 'pushState'](null, '', WEB + path);
  return render();
}
export const refreshShell = () => shell();

function markActive(path) {
  for (const a of app.querySelectorAll('.nav a')) {
    const target = new URL(a.href).pathname.slice(WEB.length);
    a.toggleAttribute('aria-current', path === target || path.startsWith(target + '/'));
  }
  app.querySelector('.me-menu')?.removeAttribute('open');
}

// Each part of the site has its own hue; every colour of the page derives from it (see web.css). A profile then applies the player's own.
const HUES = [[/^\/(rankings|multiplayer)/, 45], [/^\/(beatmapsets|beatmaps|b|s|scores)(\/|$)/, 200], [/^\/teams/, 165], [/^\/(help\/)?wiki/, 100], [/^\/(home\/)?(news|changelog)/, 15]];
export const setHue = (hue) => document.documentElement.style.setProperty('--h', String(hue));

async function render() {
  const id = ++navigation;
  const view = app.querySelector('#view');
  let path = location.pathname.slice(WEB.length).replace(/\/+$/, '') || '/';
  try { path = decodeURI(path); } catch { }
  markActive(path);
  setHue((HUES.find(([re]) => re.test(path)) || [0, 333])[1]);
  view.innerHTML = `<div class="loading" role="status">${t('Loading…', 'Chargement…')}</div>`;
  const ctx = { path, query: new URLSearchParams(location.search), hash: location.hash.slice(1), go, refreshShell, setHue, params: [] };
  let page;
  try {
    const route = ROUTES.find(([re]) => re.test(path));
    if (!route) page = misc.notFound(ctx);
    else { ctx.params = route[0].exec(path).slice(1); page = await route[1](ctx); }
  } catch (e) {
    if (id !== navigation) return;
    if (e instanceof NeedSignIn) { shell(); return go('/login?next=' + encodeURIComponent(current()), { replace: true }); }
    page = e instanceof ApiError && e.status === 404 ? misc.notFound(ctx) : misc.failed(e, render);
  }
  if (id !== navigation || !page) return; // a newer navigation (or a redirect inside the page) took over
  document.title = page.title ? `${page.title} · xc-osu!` : 'xc-osu!';
  view.innerHTML = page.html;
  try { await page.mount?.(view, ctx); }
  catch (e) { if (e instanceof NeedSignIn) return go('/login?next=' + encodeURIComponent(current()), { replace: true }); console.error(e); }
  if (!location.hash) window.scrollTo(0, 0);
}

// in-site links navigate without reloading; everything else (the game, replays, external sites) behaves normally
document.addEventListener('click', (e) => {
  if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
  const a = e.target.closest?.('a[href]');
  if (!a || a.target || a.hasAttribute('download')) return;
  const url = new URL(a.href);
  if (url.origin !== location.origin || !(url.pathname === WEB || url.pathname.startsWith(WEB + '/'))) return;
  if (url.pathname === location.pathname && url.search === location.search && url.hash !== location.hash) return; // in-page anchor
  e.preventDefault();
  go(url.pathname.slice(WEB.length) + url.search + url.hash);
});
// A click outside the account menu closes it.
document.addEventListener('click', (e) => { const menu = app.querySelector('.me-menu[open]'); if (menu && !menu.contains(e.target)) menu.removeAttribute('open'); });
window.addEventListener('popstate', render);
// An action that discovers the session is gone (expired token) ends up here rather than as a silent failure.
window.addEventListener('unhandledrejection', (e) => {
  if (!(e.reason instanceof NeedSignIn)) return;
  e.preventDefault(); shell(); go('/login?next=' + encodeURIComponent(current()), { replace: true });
});

setLang(state.lang);
shell();
loadMe().catch(() => null).then(() => { shell(); render(); });
