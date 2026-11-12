// osu! BBCode, as osu-web renders it on a "me!" page (BBCodeFromDB): notice, box/spoilerbox, centre, size, color,
// url, img, imagemap, quote, code, list, spoiler, heading, youtube, audio, profile, email.
// Everything is escaped; only http(s) / site-relative URLs and checked colours/sizes reach the output.

const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

// tags whose content is taken literally up to their closing tag
const RAW = new Set(['img', 'code', 'c', 'youtube', 'audio', 'imagemap', 'email', 'profile']);
// tags that render as blocks: a newline right after the opening tag and around the closing tag is swallowed, like osu-web
const BLOCK = new Set(['notice', 'box', 'spoilerbox', 'centre', 'center', 'quote', 'list', 'heading', 'code', 'imagemap', 'youtube', 'audio']);
const KNOWN = new Set(['b', 'i', 'u', 's', 'strike', 'size', 'color', 'colour', 'url', 'spoiler', '*', ...RAW, ...BLOCK]);

const SIZES = [50, 85, 100, 150];

export function safeUrl(raw) {
  const u = String(raw || '').trim().replace(/^"(.*)"$/, '$1').replace(/ /g, '%20');
  if (/^https?:\/\/[^\s"'<>`]+$/i.test(u)) return u;
  if (/^\/(?!\/)[^\s"'<>`]*$/.test(u)) return u;
  if (/^www\.[^\s"'<>`]+$/i.test(u)) return 'https://' + u;
  return null;
}
const safeColor = (c) => { const v = String(c || '').trim(); return /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.test(v) || /^[a-z]{3,20}$/i.test(v) ? v : null; };
const link = (href, inner) => `<a href="${esc(href)}"${/^https?:/i.test(href) ? ' target="_blank" rel="noopener nofollow"' : ''}>${inner}</a>`;
const youtubeId = (s) => (/^(?:https?:\/\/)?(?:www\.|m\.)?(?:youtube\.com\/(?:watch\?(?:.*&)?v=|embed\/|shorts\/)|youtu\.be\/)?([\w-]{11})(?:[?&#].*)?$/.exec(s.trim()) || [])[1];

/** Parse into a tree: { tag, arg, children } nodes and plain strings. */
function parse(src) {
  const root = { tag: '', children: [] }, stack = [root];
  const re = /\[(\/?)([a-z]+|\*)(?:=((?:"[^"\]]*")|[^\]]*))?\]/gi;
  let pos = 0, m;
  const text = (s) => { if (s) stack[stack.length - 1].children.push(s); };
  while ((m = re.exec(src))) {
    const [whole, closing, name0, arg] = m, name = name0.toLowerCase();
    if (!KNOWN.has(name)) continue;
    text(src.slice(pos, m.index)); pos = re.lastIndex;
    if (closing) {
      const at = stack.findLastIndex((n) => n.tag === name || (name === 'center' && n.tag === 'centre'));
      if (at <= 0) { text(whole); continue; }
      stack.length = at; // closes the tag and anything left open inside it
      continue;
    }
    if (name === '*') { // a list item ends at the next item or at the end of its list
      const list = stack.findLastIndex((n) => n.tag === 'list');
      if (list <= 0) { text(whole); continue; }
      stack.length = list + 1;
      const item = { tag: '*', children: [] }; stack[list].children.push(item); stack.push(item);
      continue;
    }
    const node = { tag: name === 'center' ? 'centre' : name === 'colour' ? 'color' : name === 'strike' ? 's' : name, arg: arg?.replace(/^"(.*)"$/, '$1'), children: [] };
    stack[stack.length - 1].children.push(node);
    if (RAW.has(name)) {
      const end = src.toLowerCase().indexOf(`[/${name}]`, pos);
      node.raw = src.slice(pos, end < 0 ? src.length : end);
      pos = re.lastIndex = end < 0 ? src.length : end + name.length + 3;
    } else stack.push(node);
  }
  text(src.slice(pos));
  return root;
}

function renderText(s, { autolink = true } = {}) {
  let html = esc(s);
  if (autolink) html = html.replace(/(^|[\s(])(https?:\/\/[^\s<]+[^\s<.,;:!?)])/g, (_, pre, url) => pre + link(url.replace(/&amp;/g, '&'), url));
  return html.replace(/\n/g, '<br>');
}

function render(node, ctx = { inLink: false }) {
  const kids = (n, c = ctx) => {
    const out = [];
    n.children.forEach((child, i) => {
      if (typeof child === 'string') {
        let s = child;
        if (BLOCK.has(n.tag) && i === 0) s = s.replace(/^[ \t]*\n/, '');
        if (BLOCK.has(n.tag) && i === n.children.length - 1) s = s.replace(/\n[ \t]*$/, '');
        const prev = n.children[i - 1];
        if (prev && typeof prev !== 'string' && BLOCK.has(prev.tag)) s = s.replace(/^[ \t]*\n/, '');
        const next = n.children[i + 1];
        if (next && typeof next !== 'string' && BLOCK.has(next.tag)) s = s.replace(/\n[ \t]*$/, '');
        if (n.tag === 'list') s = s.trim() ? s : '';
        out.push(renderText(s, { autolink: !c.inLink }));
      } else out.push(render(child, c));
    });
    return out.join('');
  };
  const raw = (node.raw || '').trim();
  switch (node.tag) {
    case '': return kids(node);
    case 'b': return `<strong>${kids(node)}</strong>`;
    case 'i': return `<em>${kids(node)}</em>`;
    case 'u': return `<u>${kids(node)}</u>`;
    case 's': return `<del>${kids(node)}</del>`;
    case 'centre': return `<div class="bb-centre">${kids(node)}</div>`;
    case 'notice': return `<div class="bb-notice">${kids(node)}</div>`;
    case 'heading': return `<h2 class="bb-heading">${kids(node)}</h2>`;
    case 'spoiler': return `<span class="bb-spoiler" tabindex="0">${kids(node)}</span>`;
    case 'size': {
      const want = parseInt(node.arg, 10);
      if (!want) return kids(node);
      const size = SIZES.reduce((a, b) => (Math.abs(b - want) < Math.abs(a - want) ? b : a));
      return `<span style="font-size:${size}%">${kids(node)}</span>`;
    }
    case 'color': { const c = safeColor(node.arg); return c ? `<span style="color:${c}">${kids(node)}</span>` : kids(node); }
    case 'url': {
      const plain = node.children.every((c) => typeof c === 'string') ? node.children.join('') : '';
      const href = safeUrl(node.arg ?? plain);
      const inner = kids(node, { ...ctx, inLink: true });
      return href && !ctx.inLink ? link(href, inner || esc(href)) : inner;
    }
    case 'box': case 'spoilerbox':
      return `<details class="bb-box"><summary>${esc(node.tag === 'box' && node.arg ? node.arg : 'SPOILER')}</summary><div class="bb-box-body">${kids(node)}</div></details>`;
    case 'quote':
      return `<blockquote class="bb-quote">${node.arg ? `<h4>${esc(node.arg)} wrote:</h4>` : ''}${kids(node)}</blockquote>`;
    case 'list': {
      const tag = node.arg ? 'ol' : 'ul';
      return `<${tag} class="bb-list">${kids(node)}</${tag}>`;
    }
    case '*': return `<li>${kids(node).replace(/^(<br>)+|(<br>)+$/g, '')}</li>`;
    case 'img': { const src = safeUrl(raw); return src ? `<img class="bb-img" src="${esc(src)}" loading="lazy" referrerpolicy="no-referrer" alt="">` : esc(`[img]${node.raw}[/img]`); }
    case 'code': return `<pre class="bb-code">${esc(node.raw.replace(/^\n|\n$/g, ''))}</pre>`;
    case 'c': return `<code>${esc(node.raw)}</code>`;
    case 'email': { const addr = (node.arg || raw).trim(); return /^[^\s@<>"]+@[^\s@<>"]+$/.test(addr) ? `<a href="mailto:${esc(addr)}">${esc(raw || addr)}</a>` : esc(raw); }
    case 'profile': { const who = (node.arg || raw).trim(); return who ? `<a href="/web/users/${encodeURIComponent(who)}">${esc(raw || who)}</a>` : ''; }
    case 'youtube': { const id = youtubeId(raw); return id ? `<div class="bb-video"><iframe src="https://www.youtube-nocookie.com/embed/${id}" title="YouTube" allowfullscreen loading="lazy"></iframe></div>` : esc(raw); }
    case 'audio': { const src = safeUrl(raw); return src ? `<audio class="bb-audio" controls preload="none" src="${esc(src)}"></audio>` : esc(raw); }
    case 'imagemap': return imagemap(node.raw);
    default: return kids(node);
  }
}

// [imagemap]
// https://image
// x y width height url-or-# title      (percentages of the image)
// [/imagemap]
function imagemap(src) {
  const lines = src.trim().split('\n').map((l) => l.trim()).filter(Boolean);
  const img = safeUrl(lines.shift());
  if (!img) return '';
  const areas = lines.map((l) => /^([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+(\S+)(?:\s+(.*))?$/.exec(l)).filter(Boolean).map(([, x, y, w, h, url, title]) => {
    const style = `left:${+x}%;top:${+y}%;width:${+w}%;height:${+h}%`, href = url === '#' ? null : safeUrl(url);
    return href ? `<a class="bb-area" style="${style}" href="${esc(href)}" title="${esc(title || '')}" target="_blank" rel="noopener nofollow"></a>` : `<span class="bb-area" style="${style}" title="${esc(title || '')}"></span>`;
  });
  return `<div class="bb-imagemap"><img src="${esc(img)}" loading="lazy" referrerpolicy="no-referrer" alt="">${areas.join('')}</div>`;
}

/** Does this text use osu! BBCode (and should be rendered as such, not as the older markdown-ish format)? */
export const looksLikeBBCode = (s) => /\[(b|i|u|s|strike|size=|colou?r=|url|img|centre|center|notice|box|spoilerbox|spoiler|quote|code|list|heading|youtube|audio|imagemap|profile|email)\b[^\]]*\]/i.test(s);

export function renderBBCode(src) {
  return render(parse(String(src || '').replace(/\r\n?/g, '\n')));
}
