// xc-osu! web — beatmap listing, beatmap page, comments. xc softworks (kudasaixc)
import { L, WEB, state, t, esc, num, clock, dateText, timeTag, MODES, modeName, v2, userLink, avatar, stars, statusChip, scoreRow, sanitize, empty, act, signedIn, NeedSignIn } from './core.js';

const cover = (set, kind = 'card') => esc(set?.covers?.[kind] || set?.covers?.cover || '');

export function setCard(set) {
  const diffs = (set.beatmaps || []).slice().sort((a, b) => a.difficulty_rating - b.difficulty_rating);
  const range = diffs.length ? (diffs.length > 1 ? `${num(diffs[0].difficulty_rating, 1)}–${num(diffs.at(-1).difficulty_rating, 1)}` : num(diffs[0].difficulty_rating, 1)) : '';
  return `<a class="set-card" href="${L('/beatmapsets/' + set.id)}">
    <img src="${cover(set)}" alt="" loading="lazy" referrerpolicy="no-referrer">
    <div class="set-card-body">
      <b>${esc(set.title)}</b>
      <span class="muted">${esc(set.artist)}</span>
      <span class="set-card-meta">${statusChip(set.status)}<span>${t('by', 'par')} ${esc(set.creator)}</span>${range ? `<span>★ ${range}</span>` : ''}<span>${num(diffs.length)} diff.</span></span>
    </div>
  </a>`;
}

// ---------------------------------------------------------------- listing
export async function listing({ query, go }) {
  const q = query.get('q') || '', m = query.get('m') ?? '', s = query.get('s') || '';
  const statuses = [['', t('Has leaderboard', 'Avec classement')], ['ranked', t('Ranked', 'Classées')], ['qualified', t('Qualified', 'Qualifiées')], ['loved', 'Loved'], ['pending', t('Pending', 'En attente')], ['graveyard', t('Graveyard', 'Cimetière')], ['any', t('Any', 'Toutes')]];
  return {
    title: 'Beatmaps',
    html: `<h1 class="page-title">Beatmaps</h1>
      <form class="filters" id="set-filters">
        <input type="search" name="q" value="${esc(q)}" placeholder="${t('Title, artist, mapper… (stars>5 ar>=9 also work)', 'Titre, artiste, mappeur… (stars>5 ar>=9 marchent aussi)')}" aria-label="${t('Search beatmaps', 'Chercher des beatmaps')}">
        <select name="m" aria-label="${t('Game mode', 'Mode de jeu')}"><option value="">${t('All modes', 'Tous les modes')}</option>${MODES.map((mode, i) => `<option value="${i}"${String(i) === m ? ' selected' : ''}>${modeName(mode)}</option>`).join('')}</select>
        <select name="s" aria-label="${t('Status', 'Statut')}">${statuses.map(([v, label]) => `<option value="${v}"${v === s ? ' selected' : ''}>${esc(label)}</option>`).join('')}</select>
        <button class="btn" type="submit">${t('Search', 'Chercher')}</button>
      </form>
      <p class="muted small" id="set-total"></p>
      <div class="set-grid" id="set-grid"></div>
      <div class="list-foot" id="set-foot"></div>`,
    mount(view) {
      view.querySelector('#set-filters').addEventListener('submit', (e) => {
        e.preventDefault();
        const next = new URLSearchParams([...new FormData(e.target)].filter(([, v]) => v !== ''));
        go('/beatmapsets' + (next.size ? '?' + next : ''));
      });
      const grid = view.querySelector('#set-grid'), foot = view.querySelector('#set-foot');
      let cursor = null, shown = 0;
      const load = async () => {
        foot.innerHTML = `<span class="muted">${t('Loading…', 'Chargement…')}</span>`;
        const params = new URLSearchParams();
        if (q) params.set('q', q);
        if (m !== '') params.set('m', m);
        if (s) params.set('s', s);
        if (cursor) for (const [k, v] of Object.entries(cursor)) params.set(`cursor[${k}]`, v);
        try {
          const page = await v2('/beatmapsets/search?' + params);
          grid.insertAdjacentHTML('beforeend', page.beatmapsets.map(setCard).join(''));
          shown += page.beatmapsets.length;
          cursor = page.cursor;
          view.querySelector('#set-total').textContent = shown ? t(`${num(shown)} shown of about ${num(page.total)}`, `${num(shown)} affichées sur environ ${num(page.total)}`) : '';
          foot.innerHTML = !shown ? empty(t('No beatmap matches this search. Try fewer words or another status.', 'Aucune beatmap ne correspond. Essaie moins de mots ou un autre statut.'))
            : cursor && page.beatmapsets.length ? `<button type="button" class="btn ghost">${t('Show more', 'Afficher plus')}</button>` : '';
        } catch (e) {
          foot.innerHTML = `<span class="bad">${esc(e.message)}</span> <button type="button" class="btn ghost">${t('Try again', 'Réessayer')}</button>`;
        }
        foot.querySelector('button')?.addEventListener('click', load);
      };
      load();
    },
  };
}

/** /b/123 and /beatmaps/123 only know the difficulty: find its set and continue there. */
export async function beatmapRedirect({ params: [id], go }) {
  const map = await v2('/beatmaps/' + id);
  go(`/beatmapsets/${map.beatmapset_id}#${map.mode || 'osu'}/${map.id}`, { replace: true });
  return null;
}

// ---------------------------------------------------------------- beatmap page
export async function beatmapset({ params: [id], hash }) {
  const set = await v2('/beatmapsets/' + id);
  const all = [...(set.beatmaps || []), ...(set.converts || [])].sort((a, b) => a.mode_int - b.mode_int || Number(a.convert) - Number(b.convert) || a.difficulty_rating - b.difficulty_rating);
  const native = all.filter((b) => !b.convert);
  const [hashMode, hashId] = hash.split('/');
  const picked = all.find((b) => String(b.id) === hashId && (!hashMode || b.mode === hashMode)) || native.find((b) => b.mode === hashMode) || native.at(-1) || all[0];

  return {
    title: `${set.artist} – ${set.title}`,
    html: `<article class="set">
      <header class="set-head">
        <img class="set-cover" src="${cover(set, 'cover')}" alt="" referrerpolicy="no-referrer">
        <div class="set-head-body">
          <h1>${esc(set.title)}</h1>
          <p class="set-artist">${esc(set.artist)}</p>
          <p class="set-sub">${statusChip(set.status)} ${t('mapped by', 'mappée par')} <b>${esc(set.creator)}</b> · ${set.ranked_date ? `${t('ranked', 'classée le')} ${dateText(set.ranked_date)}` : `${t('updated', 'mise à jour le')} ${dateText(set.last_updated)}`}</p>
          <div class="set-actions">
            <button type="button" class="btn" id="fav" aria-pressed="false"></button>
            <a class="btn ghost" href="/api/v2/beatmapsets/${set.id}/download" download="${set.id}.osz">${t('Download (.osz)', 'Télécharger (.osz)')}</a>
            ${set.preview_url ? `<audio controls preload="none" src="${esc(set.preview_url)}" aria-label="${t('Preview', 'Extrait')}"></audio>` : ''}
          </div>
          <p class="muted small">${t('In the game, find it from the beatmap listing with this ID:', 'Dans le jeu, retrouve-la depuis la liste des beatmaps avec cet ID :')} <code>${set.id}</code></p>
        </div>
      </header>

      <section class="panel">
        <div class="diffs" id="diffs" role="tablist" aria-label="${t('Difficulties', 'Difficultés')}">
          ${all.map((b) => `<button type="button" role="tab" class="diff" data-id="${b.id}" data-mode="${esc(b.mode)}" title="${esc(b.version)}${b.convert ? ' (convert)' : ''}"><span class="diff-mode">${esc(modeName(b.mode))}${b.convert ? '*' : ''}</span>${stars(b.difficulty_rating)}</button>`).join('') || empty(t('This set has no difficulty.', 'Ce set n’a aucune difficulté.'))}
        </div>
        <div id="diff-detail"></div>
      </section>

      <section class="panel"><h2>${t('Leaderboard', 'Classement')}</h2><div id="board"></div></section>

      <section class="panel"><h2>${t('Description', 'Description')}</h2>
        <div class="user-text">${sanitize(set.description?.description) || empty(t('No description.', 'Pas de description.'))}</div>
        <dl class="about">
          ${set.source ? `<div><dt>Source</dt><dd>${esc(set.source)}</dd></div>` : ''}
          ${set.genre?.name ? `<div><dt>Genre</dt><dd>${esc(set.genre.name)}</dd></div>` : ''}
          ${set.language?.name ? `<div><dt>${t('Language', 'Langue')}</dt><dd>${esc(set.language.name)}</dd></div>` : ''}
          ${set.tags ? `<div><dt>Tags</dt><dd class="tags">${set.tags.split(/\s+/).filter(Boolean).slice(0, 40).map((tag) => `<a href="${L('/beatmapsets?q=' + encodeURIComponent(tag))}">${esc(tag)}</a>`).join(' ')}</dd></div>` : ''}
        </dl>
      </section>

      <section class="panel"><h2>${t('Comments', 'Commentaires')}</h2><div id="comments"></div></section>
    </article>`,
    mount(view) {
      const detail = view.querySelector('#diff-detail'), board = view.querySelector('#board');
      let boardRequest = 0;
      const show = async (map, push) => {
        for (const b of view.querySelectorAll('.diff')) b.setAttribute('aria-selected', String(b.dataset.id === String(map.id) && b.dataset.mode === map.mode));
        if (push) history.replaceState(null, '', `${WEB}/beatmapsets/${set.id}#${map.mode}/${map.id}`);
        detail.innerHTML = `<h2 class="diff-name">${esc(map.version)} ${stars(map.difficulty_rating)}${map.convert ? ` <span class="muted small">${t('converted', 'convertie')}</span>` : ''}</h2>
          <dl class="stat-grid">
            <div><dt>${t('Length', 'Durée')}</dt><dd>${clock(map.total_length)}</dd></div>
            <div><dt>BPM</dt><dd>${num(map.bpm ?? set.bpm, 1)}</dd></div>
            <div><dt>${t('Max combo', 'Combo max')}</dt><dd>${map.max_combo ? num(map.max_combo) + 'x' : '—'}</dd></div>
            <div><dt>${t('Objects', 'Objets')}</dt><dd>${num((map.count_circles || 0) + (map.count_sliders || 0) + (map.count_spinners || 0))}</dd></div>
            <div><dt>CS</dt><dd>${num(map.cs, 1)}</dd></div><div><dt>AR</dt><dd>${num(map.ar, 1)}</dd></div>
            <div><dt>OD</dt><dd>${num(map.accuracy, 1)}</dd></div><div><dt>HP</dt><dd>${num(map.drain, 1)}</dd></div>
          </dl>`;
        const request = ++boardRequest;
        board.innerHTML = `<span class="muted">${t('Loading…', 'Chargement…')}</span>`;
        try {
          const data = await v2(`/beatmaps/${map.id}/scores?mode=${map.mode}`);
          if (request !== boardRequest) return;
          const mine = data.user_score?.score;
          board.innerHTML = (data.scores || []).length
            ? `<ol class="list">${data.scores.map((sc, i) => scoreRow(sc, { showUser: true, showMap: false, position: i + 1 })).join('')}</ol>`
              + (mine && !data.scores.some((sc) => sc.id === mine.id) ? `<p class="muted small">${t('Your best', 'Ton meilleur score')}</p><ul class="list">${scoreRow(mine, { showUser: true, showMap: false, position: data.user_score.position })}</ul>` : '')
            : empty(set.is_scoreable === false ? t('This beatmap has no leaderboard.', 'Cette beatmap n’a pas de classement.') : t('Nobody has set a score here yet. Be the first!', 'Personne n’a encore de score ici. Sois le premier !'));
        } catch (e) { if (request === boardRequest) board.innerHTML = `<span class="bad">${esc(e.message)}</span>`; }
      };
      for (const b of view.querySelectorAll('.diff'))
        b.addEventListener('click', () => show(all.find((x) => String(x.id) === b.dataset.id && x.mode === b.dataset.mode), true));
      if (picked) show(picked, false); else board.innerHTML = empty(t('No leaderboard.', 'Pas de classement.'));

      const fav = view.querySelector('#fav');
      let on = !!set.has_favourited, count = set.favourite_count || 0;
      const paint = () => { fav.textContent = `${on ? '♥' : '♡'} ${on ? t('Favourited', 'En favori') : t('Favourite', 'Favori')} · ${num(count)}`; fav.setAttribute('aria-pressed', String(on)); fav.classList.toggle('ghost', !on); };
      paint();
      fav.addEventListener('click', async () => {
        if (!signedIn()) throw new NeedSignIn();
        await act(fav, null, async () => {
          const r = await v2(`/beatmapsets/${set.id}/favourites`, { method: 'POST', form: { action: on ? 'unfavourite' : 'favourite' } });
          on = !on; count = r.favourite_count;
        });
        paint();
      });

      view.querySelector('#comments').append(commentThread({ type: 'beatmapset', id: set.id }));
    },
  };
}

// ---------------------------------------------------------------- comments
/** Comment thread for one thing (`type` + `id`), or a read-only list for another query (`query`). */
export function commentThread({ type, id, query }) {
  const wrap = document.createElement('div');
  wrap.className = 'comments';
  const canPost = type != null;
  const load = async () => {
    wrap.innerHTML = `<span class="muted">${t('Loading…', 'Chargement…')}</span>`;
    let data;
    try { data = await v2('/comments?' + (query || `commentable_type=${type}&commentable_id=${id}`)); }
    catch (e) { wrap.innerHTML = `<span class="bad">${esc(e.message)}</span>`; return; }
    const users = new Map((data.users || []).map((u) => [u.id, u])), voted = new Set(data.user_votes || []);
    const replies = new Map();
    for (const c of data.included_comments || []) (replies.get(c.parent_id) || replies.set(c.parent_id, []).get(c.parent_id)).push(c);
    const one = (c, nested) => {
      const deleted = !!c.deleted_at, mine = state.me && (state.me.id === c.user_id || state.me.is_admin);
      return `<li class="comment" data-id="${c.id}">
        ${avatar(users.get(c.user_id), 36)}
        <div class="comment-body">
          <div class="comment-head">${userLink(users.get(c.user_id), { withAvatar: false })} ${timeTag(c.created_at)}${c.pinned ? ` <span class="status s-loved">${t('pinned', 'épinglé')}</span>` : ''}${c.edited_at ? ` <span class="muted small">${t('edited', 'modifié')}</span>` : ''}</div>
          <p class="comment-text${deleted ? ' muted' : ''}">${deleted ? t('This comment was deleted.', 'Ce commentaire a été supprimé.') : esc(c.message).replace(/\n/g, '<br>')}</p>
          ${deleted ? '' : `<div class="comment-actions">
            ${state.me && state.me.id === c.user_id ? `<span class="muted" title="${t('You can’t vote on your own comment', 'Tu ne peux pas voter pour ton propre commentaire')}">▲ ${num(c.votes_count)}</span>` : `<button type="button" class="link" data-vote aria-pressed="${voted.has(c.id)}">▲ ${num(c.votes_count)}</button>`}
            ${canPost && !nested ? `<button type="button" class="link" data-reply>${t('Reply', 'Répondre')}</button>` : ''}
            ${mine ? `<button type="button" class="link danger" data-delete>${t('Delete', 'Supprimer')}</button>` : ''}
            ${!canPost ? `<a class="link" href="${L(c.commentable_type === 'beatmapset' ? '/beatmapsets/' + c.commentable_id : '/')}">${t('See in context', 'Voir le contexte')}</a>` : ''}
          </div>`}
          ${!nested && replies.has(c.id) ? `<ul class="comment-replies">${replies.get(c.id).map((r) => one(r, true)).join('')}</ul>` : ''}
        </div>
      </li>`;
    };
    const form = (parent) => `<form class="comment-form"${parent ? ` data-parent="${parent}"` : ''}>
      <textarea name="message" rows="${parent ? 2 : 3}" maxlength="2000" required placeholder="${parent ? t('Write a reply', 'Écrire une réponse') : t('Write a comment', 'Écrire un commentaire')}" aria-label="${t('Comment', 'Commentaire')}"></textarea>
      <div><button class="btn" type="submit">${t('Post', 'Publier')}</button> <span class="msg"></span></div></form>`;
    wrap.innerHTML = (canPost ? (signedIn() ? form(null) : `<p class="muted"><a href="${L('/login?next=' + encodeURIComponent(location.pathname.slice(WEB.length) + location.hash))}">${t('Sign in', 'Connecte-toi')}</a> ${t('to comment.', 'pour commenter.')}</p>`) : '')
      + ((data.comments || []).length ? `<ul class="comment-list">${data.comments.map((c) => one(c, false)).join('')}</ul>` : empty(t('No comments yet.', 'Aucun commentaire pour l’instant.')));

    const submit = async (e) => {
      e.preventDefault();
      const f = e.target, fields = { 'comment[message]': f.message.value.trim(), 'comment[commentable_type]': type, 'comment[commentable_id]': id };
      if (f.dataset.parent) fields['comment[parent_id]'] = f.dataset.parent;
      if (await act(f.querySelector('button'), f.querySelector('.msg'), () => v2('/comments', { method: 'POST', form: fields }))) load();
    };
    for (const f of wrap.querySelectorAll('form')) f.addEventListener('submit', submit);
    wrap.querySelector('.comment-list')?.addEventListener('click', async (e) => {
      const btn = e.target.closest('button'), cid = e.target.closest('.comment')?.dataset.id;
      if (!btn || !cid) return;
      if (btn.hasAttribute('data-vote')) {
        if (!signedIn()) throw new NeedSignIn();
        const on = btn.getAttribute('aria-pressed') === 'true';
        await act(btn, null, async () => {
          const r = await v2(`/comments/${cid}/vote`, { method: on ? 'DELETE' : 'POST' });
          const fresh = r?.comments?.find?.((c) => String(c.id) === cid) || r?.comment;
          btn.setAttribute('aria-pressed', String(!on));
          btn.textContent = `▲ ${num(fresh?.votes_count ?? Math.max(0, parseInt(btn.textContent.replace(/\D/g, ''), 10) + (on ? -1 : 1)))}`;
        });
      } else if (btn.hasAttribute('data-reply')) {
        const body = btn.closest('.comment-body');
        if (!signedIn()) throw new NeedSignIn();
        if (!body.querySelector(':scope > form')) { body.insertAdjacentHTML('beforeend', form(cid)); const f = body.querySelector(':scope > form'); f.addEventListener('submit', submit); f.message.focus(); }
      } else if (btn.hasAttribute('data-delete')) {
        if (!confirm(t('Delete this comment? This cannot be undone.', 'Supprimer ce commentaire ? C’est définitif.'))) return;
        if (await act(btn, null, () => v2('/comments/' + cid, { method: 'DELETE' }))) load();
      }
    });
  };
  load();
  return wrap;
}
