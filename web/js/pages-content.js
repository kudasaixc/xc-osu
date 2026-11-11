// xc-osu! web — written content: news, wiki, changelog. xc softworks (kudasaixc)
import { L, WEB, state, t, esc, dateText, v2, sanitize, ApiError, empty } from './core.js';

const locale = () => 'locale=' + state.lang;

/**
 * HTML written by xc-osu! itself (news, wiki): cleaned like any other HTML, then links to this site's own pages
 * ("/wiki/Sound", "https://<this site>/web/...") are made to open in place instead of in a new tab.
 */
function ownHtml(html) {
  const box = document.createElement('div');
  box.innerHTML = sanitize(html);
  for (const a of box.querySelectorAll('a[href]')) {
    let url;
    try { url = new URL(a.getAttribute('href'), location.origin); } catch { continue; }
    if (url.origin !== location.origin) continue;
    const path = url.pathname.startsWith(WEB + '/') || url.pathname === WEB ? url.pathname : WEB + url.pathname;
    a.setAttribute('href', path + url.search + url.hash);
    a.removeAttribute('target');
    a.removeAttribute('rel');
  }
  for (const table of box.querySelectorAll('table')) {
    table.classList.add('table');
    const wrap = document.createElement('div');
    wrap.className = 'table-wrap';
    table.replaceWith(wrap);
    wrap.append(table);
  }
  return box.innerHTML;
}

// ---------------------------------------------------------------- news
export async function news() {
  const data = await v2('/news?' + locale());
  const posts = data.news_sidebar?.news_posts || data.news_posts || [];
  return {
    title: t('News', 'Actualités'),
    html: `<h1 class="page-title">${t('News', 'Actualités')}</h1>
      ${posts.length ? `<div class="post-list">${posts.map((p) => `<a class="post-card" href="${L('/news/' + encodeURIComponent(p.slug))}">
        <span class="post-date">${dateText(p.published_at)}</span><b class="post-title">${esc(p.title)}</b><span class="post-preview">${esc(p.preview)}</span></a>`).join('')}</div>` : empty(t('No news yet.', 'Aucune actualité pour l’instant.'))}
      <p class="muted small">${t('Looking for what changed in each version?', 'Tu cherches ce qui a changé dans chaque version ?')} <a href="${L('/home/changelog')}">${t('Changelog', 'Journal des modifications')}</a></p>`,
  };
}

export async function newsPost({ params: [slug] }) {
  const p = await v2('/news/' + encodeURIComponent(slug) + '?' + locale());
  const near = (post, label) => post ? `<a class="post-near" href="${L('/news/' + encodeURIComponent(post.slug))}"><span class="muted small">${label}</span><b>${esc(post.title)}</b></a>` : '<span></span>';
  return {
    title: p.title,
    html: `<article class="article"><p class="crumbs"><a href="${L('/news')}">${t('News', 'Actualités')}</a></p>
      <h1>${esc(p.title)}</h1><p class="article-meta">${dateText(p.published_at)} · ${esc(p.author)}</p>
      <div class="prose">${ownHtml(p.content)}</div>
      <nav class="post-nav" aria-label="${t('Other posts', 'Autres articles')}">${near(p.navigation?.older, t('← Older', '← Plus ancien'))}${near(p.navigation?.newer, t('Newer →', 'Plus récent →'))}</nav></article>`,
  };
}

// ---------------------------------------------------------------- wiki
// reading order of the index; pages not listed come after, in alphabetical order
const ORDER = ['Getting_started', 'Performance', 'Sound', 'Controls', 'Beatmaps', 'Online_play', 'Account', 'Troubleshooting', 'Reporting_a_problem', 'Privacy'];
const rank = (path) => { const i = ORDER.indexOf(path); return i < 0 ? ORDER.length : i; };

/** /wiki, /wiki/Sound, /wiki/fr/Sound, /help/wiki/… (the language in the address is ignored: the site's own language is used). */
export async function wiki({ params: [rest], go }) {
  const path = (rest || '').replace(/^(?:[a-z]{2}(?:-[a-z]{2})?)(?:\/|$)/i, '').replace(/\/+$/, '');
  const pages = (await v2('/wiki-index?' + locale())).sort((a, b) => rank(a.path) - rank(b.path));

  if (!path || /^main_page$/i.test(path)) {
    return {
      title: 'Wiki',
      html: `<h1 class="page-title">Wiki</h1>
        <p class="lead">${t('How to get the game running well in your browser, and how its online side works.', 'Comment faire tourner le jeu correctement dans ton navigateur, et comment fonctionne sa partie en ligne.')}</p>
        <ul class="wiki-index">${pages.map((p) => `<li><a href="${L('/wiki/' + p.path)}">${esc(p.title)}</a></li>`).join('')}</ul>`,
    };
  }

  let page;
  try { page = await v2(`/wiki/${state.lang}/${path.split('/').map(encodeURIComponent).join('/')}`); }
  catch (e) {
    if (!(e instanceof ApiError) || e.status !== 404) throw e;
    return {
      title: 'Wiki',
      html: `<section class="notice"><h1>${t('This wiki page does not exist', 'Cette page du wiki n’existe pas')}</h1>
        <p>${t('The xc-osu! wiki only covers xc-osu! itself. Here is what it has:', 'Le wiki de xc-osu! ne couvre que xc-osu!. Voici ce qu’il contient :')}</p>
        <ul class="wiki-index">${pages.map((p) => `<li><a href="${L('/wiki/' + p.path)}">${esc(p.title)}</a></li>`).join('')}</ul></section>`,
    };
  }
  return {
    title: page.title + ' · Wiki',
    html: `<div class="wiki"><nav class="wiki-side" aria-label="Wiki"><a href="${L('/wiki')}"><b>Wiki</b></a>
        ${pages.map((p) => `<a href="${L('/wiki/' + p.path)}"${p.path === page.path ? ' aria-current="page"' : ''}>${esc(p.title)}</a>`).join('')}</nav>
      <article class="article">${page.locale !== state.lang ? `<p class="muted small">${t('This page is not available in English yet.', 'Cette page n’existe pas encore en français ; voici la version anglaise.')}</p>` : ''}
        <div class="prose">${ownHtml(page.html)}</div></article></div>`,
  };
}

/** The game's "report an issue" button (a forum thread on the official site) lands on the page that explains how to report here. */
export function reportIssue({ go }) { go('/wiki/Reporting_a_problem', { replace: true }); return null; }

// ---------------------------------------------------------------- changelog
const TYPE = { add: ['+', () => t('Added', 'Ajout')], fix: ['✓', () => t('Fixed', 'Correction')], misc: ['·', () => t('Other', 'Divers')] };

export async function changelog({ params: [version] }) {
  const data = await v2('/changelog?' + locale());
  const builds = data.builds || [];
  const one = (b) => {
    const groups = new Map();
    for (const e of b.changelog_entries || []) (groups.get(e.category) || groups.set(e.category, []).get(e.category)).push(e);
    return `<section class="build" id="v${esc(b.version)}"><h2><a href="${L('/home/changelog/lazer/' + encodeURIComponent(b.version))}">${esc(b.display_version)}</a> <span class="build-date">${dateText(b.created_at)}</span></h2>
      ${[...groups].map(([category, entries]) => `<h3>${esc(category)}</h3><ul class="entries">${entries.map((e) => {
        const [mark, label] = TYPE[String(e.type).toLowerCase()] || TYPE.misc;
        return `<li class="entry${e.major ? ' major' : ''}"><span class="entry-type" title="${label()}">${mark}</span><span>${esc(e.title)}</span></li>`;
      }).join('')}</ul>`).join('')}</section>`;
  };
  const shown = version ? builds.filter((b) => b.version === version) : builds;
  if (version && !shown.length) throw new ApiError(404, 'Build not found');
  return {
    title: t('Changelog', 'Journal des modifications'),
    html: `<h1 class="page-title">${t('Changelog', 'Journal des modifications')}</h1>
      ${version ? `<p class="crumbs"><a href="${L('/home/changelog')}">${t('All versions', 'Toutes les versions')}</a></p>` : `<p class="lead">${t('What changed in each version of xc-osu!. The longer stories are in the', 'Ce qui a changé dans chaque version de xc-osu!. Les explications détaillées sont dans les')} <a href="${L('/news')}">${t('news', 'actualités')}</a>.</p>`}
      <div class="changelog">${shown.length ? shown.map(one).join('') : empty(t('Nothing yet.', 'Rien pour l’instant.'))}</div>`,
  };
}
