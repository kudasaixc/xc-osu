// xc-osu! web: home, rankings, scores, teams, rooms, friends, search, news. xc softworks (kudasaixc)
import { L, state, t, esc, num, pct, rankText, dateText, dateTimeText, timeTag, countryChip, countryName, MODES, modeName, modeOfInt, v2, api, download,
  avatar, userLink, grade, modsHtml, stars, sanitize, empty, act, requireSignIn, ApiError } from './core.js';
import { setCard, commentThread } from './pages-beatmap.js';

const rowUser = (row) => row.user || row.User;

// ---------------------------------------------------------------- home
// The engineering story is told in English on purpose: it is the language the stack speaks.
const FEAT = `
      <section class="feat" lang="en" aria-labelledby="feat-title">
        <header class="feat-head">
          <p class="eyebrow"><span class="feat-dot" aria-hidden="true"></span>Under the hood</p>
          <h2 id="feat-title">This is not a web demo.<br><span class="feat-em">It is osu!lazer itself, compiled to WebAssembly.</span></h2>
          <p class="feat-lead">A desktop rhythm game expects a graphics card, several threads, a real database and a native audio library. A browser tab offers none of that. So the game's own code was kept, and the entire platform underneath it was rebuilt until lazer ran on the web: no install, no plugin, no emulator, no streaming video, no “lite” mode. The game is executing on your machine.</p>
        </header>
        <ul class="feat-grid">
          <li class="feat-card"><p class="feat-k">01 · The source</p><b>The real game, not a remake</b>
            <p>Every hit circle, slider and pp value comes from the actual osu!lazer source tree that desktop players already run. The browser is a new platform, not a new game.</p></li>
          <li class="feat-card"><p class="feat-k">02 · The runtime</p><b>C# ahead-of-time compiled to WebAssembly</b>
            <p>The .NET 10 runtime is compiled ahead of time into WebAssembly, so lazer's C# runs as machine code the browser executes directly: no interpreter crawling through the gameplay loop.</p></li>
          <li class="feat-card"><p class="feat-k">03 · The renderer</p><b>WebGL2, driven by the original renderer</b>
            <p>The framework's draw loop talks to a WebGL2 context through a hand-written platform layer. Even the shader compiler, a native library on desktop, was compiled to WebAssembly.</p></li>
          <li class="feat-card"><p class="feat-k">04 · The threads</p><b>Real threads, inside a browser</b>
            <p>Cross-origin isolation unlocks WebAssembly threads: drawing, game logic, audio and input each get a dedicated worker. The page's main thread stays idle while the game runs.</p></li>
          <li class="feat-card"><p class="feat-k">05 · The database</p><b>Realm's own engine, compiled to Wasm</b>
            <p>Lazer stores beatmaps and settings in Realm. Rather than a look-alike, Realm's C++ core was compiled to WebAssembly with a filesystem that gives it the locking and memory mapping it expects: the same database format as desktop.</p></li>
          <li class="feat-card"><p class="feat-k">06 · The audio</p><b>BASS, replaced by an engine of our own</b>
            <p>Desktop lazer plays sound through BASS, which does not exist on the web. xcbass keeps the same API, with its own decoders and its own tempo and pitch processing, feeding a WebAudio worklet.</p></li>
        </ul>
        <dl class="feat-nums">
          <div><dt>of WebAssembly engine, brotli-compressed and streamed from the edge</dt><dd>~230 MB</dd></div>
          <div><dt>dedicated threads: draw, update, audio and input</dt><dd>4</dd></div>
          <div><dt>from key press to hit sound, end to end</dt><dd>~63 ms</dd></div>
          <div><dt>installs, plugins, app stores</dt><dd>0</dd></div>
        </dl>
        <p class="feat-foot"><span>Behind the tab: a full lazer-compatible server (scores, pp, rankings, multiplayer and spectator) on ASP.NET Core and PostgreSQL.</span>
          <a href="${L('/news/2026-11-09-how-lazer-fits-in-a-tab')}">How a desktop rhythm game fits in a tab →</a></p>
      </section>`;

export async function home() {
  const [ranking, news] = await Promise.all([
    v2('/rankings/osu/performance').catch(() => ({ ranking: [] })),
    v2('/news').catch(() => ({ news_posts: [] })),
  ]);
  const me = state.me;
  return {
    title: '',
    html: `<section class="hero"><div>
        <p class="eyebrow">osu!lazer · compiled to WebAssembly</p>
        <h1>${me ? t(`Welcome back, ${esc(me.username)}`, `Content de te revoir, ${esc(me.username)}`) : t('A full osu!lazer client, in a browser tab.', 'Un client osu!lazer complet, dans un onglet.')}</h1>
        <p>${t('The real lazer source, compiled to WebAssembly and streamed to your browser. No installer, no plugin, no emulator: one click and the game boots in your tab.', 'Le vrai code de lazer, compilé en WebAssembly et envoyé à ton navigateur. Aucun installateur, aucun plugin, aucun émulateur: un clic et le jeu démarre dans ton onglet.')}</p>
        <p class="hero-actions"><a class="btn play" href="/">${t('Play now', 'Jouer maintenant')}</a>
          ${me ? `<a class="btn ghost" href="${L('/users/' + me.id)}">${t('My profile', 'Mon profil')}</a>` : `<a class="btn ghost" href="${L('/register')}">${t('Create an account', 'Créer un compte')}</a>`}</p></div>
        <svg class="hero-art" viewBox="0 0 340 200" aria-hidden="true">
          <path d="M62 138 C 120 40, 190 40, 236 96" fill="none" stroke="#fff" stroke-width="58" stroke-linecap="round"/>
          <path d="M62 138 C 120 40, 190 40, 236 96" fill="none" stroke="hsl(333 60% 22%)" stroke-width="50" stroke-linecap="round"/>
          <circle cx="236" cy="96" r="25" fill="hsl(333 60% 30%)" stroke="#fff" stroke-width="4"/>
          <circle cx="62" cy="138" r="25" fill="hsl(333 100% 65%)" stroke="#fff" stroke-width="4"/>
          <text x="62" y="147" text-anchor="middle" font-size="26" font-weight="800" fill="#fff">1</text>
          <circle cx="262" cy="150" r="3" fill="#fff" opacity=".5"/><circle cx="276" cy="158" r="3" fill="#fff" opacity=".35"/>
          <circle cx="298" cy="168" r="25" fill="hsl(200 90% 55%)" stroke="#fff" stroke-width="4"/>
          <text x="298" y="177" text-anchor="middle" font-size="26" font-weight="800" fill="#fff">2</text>
          <circle class="approach" cx="298" cy="168" r="27" fill="none" stroke="hsl(200 90% 70%)" stroke-width="3"/>
        </svg>
      </section>
      ${FEAT}
      <div class="cols">
        <section class="panel"><h2>${t('Top players', 'Meilleurs joueurs')}</h2>
          ${ranking.ranking.length ? `<ol class="list">${ranking.ranking.slice(0, 8).map((row, i) => `<li class="score"><span class="pos">#${i + 1}</span><div class="score-main">${userLink(rowUser(row))}</div><span class="score-acc">${num(row.hit_accuracy, 2)} %</span><span class="score-pp">${num(row.pp)}<small>pp</small></span></li>`).join('')}</ol>
          <p><a href="${L('/rankings')}">${t('Full rankings', 'Classement complet')}</a></p>` : empty(t('No ranked player yet.', 'Aucun joueur classé pour l’instant.'))}
        </section>
        <section class="panel"><h2>${t('News', 'Actus')}</h2>
          ${news.news_posts.length ? news.news_posts.slice(0, 4).map((p) => `<a class="news-item" href="${L('/news/' + encodeURIComponent(p.slug))}"><b>${esc(p.title)}</b><span class="muted small">${dateText(p.published_at)}</span><span>${esc(p.preview)}</span></a>`).join('') : empty(t('No news yet.', 'Pas encore d’actus.'))}
        </section>
      </div>`,
  };
}

// ---------------------------------------------------------------- rankings
export async function rankings({ params: [modeParam, typeParam], query }) {
  const mode = modeParam || 'osu', type = typeParam || 'performance', page = Math.max(1, parseInt(query.get('page'), 10) || 1), country = (query.get('country') || '').toUpperCase();
  const data = await v2(`/rankings/${mode}/${type}?page=${page}${country ? '&country=' + encodeURIComponent(country) : ''}`);
  const rows = data.ranking || [], perPage = 50, pages = Math.max(1, Math.ceil((data.total || rows.length) / perPage));
  const link = (m, ty, p = 1) => L(`/rankings/${m}/${ty}`) + ((p > 1 || country) ? '?' + new URLSearchParams({ ...(p > 1 ? { page: p } : {}), ...(country && ty !== 'country' ? { country } : {}) }) : '');
  const types = [['performance', t('Performance', 'Performance')], ['score', t('Score', 'Score')], ['country', t('Countries', 'Pays')]];
  const table = type === 'country'
    ? `<table class="table"><thead><tr><th>#</th><th>${t('Country', 'Pays')}</th><th class="n">${t('Active players', 'Joueurs actifs')}</th><th class="n">${t('Play count', 'Parties')}</th><th class="n">${t('Ranked score', 'Score classé')}</th><th class="n">pp</th></tr></thead><tbody>
        ${rows.map((r, i) => `<tr><td>${(page - 1) * perPage + i + 1}</td><td>${countryChip(r.code)} <a href="${L(`/rankings/${mode}/performance`)}?country=${esc(r.code)}">${esc(countryName(r.code))}</a></td><td class="n">${num(r.active_users)}</td><td class="n">${num(r.play_count)}</td><td class="n">${num(r.ranked_score)}</td><td class="n"><b>${num(r.performance)}</b></td></tr>`).join('')}</tbody></table>`
    : `<table class="table"><thead><tr><th>#</th><th>${t('Player', 'Joueur')}</th><th class="n">${t('Accuracy', 'Précision')}</th><th class="n">${t('Play count', 'Parties')}</th><th class="n">${type === 'score' ? t('Total score', 'Score total') : t('Ranked score', 'Score classé')}</th><th class="n">${type === 'score' ? t('Ranked score', 'Score classé') : 'pp'}</th></tr></thead><tbody>
        ${rows.map((r, i) => `<tr${state.me?.id === rowUser(r)?.id ? ' class="me"' : ''}><td>${(page - 1) * perPage + i + 1}</td><td>${userLink(rowUser(r))}</td><td class="n">${num(r.hit_accuracy, 2)} %</td><td class="n">${num(r.play_count)}</td><td class="n">${num(type === 'score' ? r.total_score : r.ranked_score)}</td><td class="n"><b>${type === 'score' ? num(r.ranked_score) : num(r.pp)}</b></td></tr>`).join('')}</tbody></table>`;
  return {
    title: t('Rankings', 'Classements'),
    html: `<h1 class="page-title">${t('Rankings', 'Classements')}${country ? ` · ${esc(countryName(country))} <a class="small" href="${link(mode, type).split('?')[0]}">${t('(all countries)', '(tous les pays)')}</a>` : ''}</h1>
      <nav class="tabs">${MODES.map((m) => `<a href="${link(m, type)}"${m === mode ? ' aria-current="page"' : ''}>${modeName(m)}</a>`).join('')}</nav>
      <nav class="tabs sub">${types.map(([ty, label]) => `<a href="${link(mode, ty)}"${ty === type ? ' aria-current="page"' : ''}>${esc(label)}</a>`).join('')}</nav>
      <section class="panel table-wrap">${rows.length ? table : empty(t('Nobody is ranked here yet. Play a ranked beatmap to appear.', 'Personne n’est classé ici pour l’instant. Joue une beatmap classée pour apparaître.'))}</section>
      ${pages > 1 ? `<nav class="pager">${page > 1 ? `<a class="btn ghost" href="${link(mode, type, page - 1)}">← ${t('Previous', 'Précédent')}</a>` : ''}<span>${t('Page', 'Page')} ${page} / ${pages}</span>${page < pages ? `<a class="btn ghost" href="${link(mode, type, page + 1)}">${t('Next', 'Suivant')} →</a>` : ''}</nav>` : ''}`,
  };
}

// ---------------------------------------------------------------- score
const HIT_LABELS = { perfect: 'Perfect', great: 'Great', good: 'Good', ok: 'Ok', meh: 'Meh', miss: 'Miss', large_tick_hit: 'Slider ticks', slider_tail_hit: 'Slider ends', small_tick_hit: 'Small ticks', large_bonus: 'Spinner bonus', small_bonus: 'Spinner spins', ignore_hit: null, ignore_miss: null, large_tick_miss: 'Tick misses', small_tick_miss: 'Small tick misses', combo_break: 'Combo breaks', legacy_combo_increase: null };
export async function score({ params: [id] }) {
  const s = await v2('/scores/' + id);
  const map = s.beatmap, set = map?.beatmapset, mode = modeOfInt(s.ruleset_id);
  const hits = Object.entries(s.statistics || {}).filter(([k]) => HIT_LABELS[k] !== null).map(([k, v]) => `<div><dt>${esc(HIT_LABELS[k] || k)}</dt><dd>${num(v)}${s.maximum_statistics?.[k] != null ? `<small> / ${num(s.maximum_statistics[k])}</small>` : ''}</dd></div>`).join('');
  return {
    title: `${s.user?.username || ''} – ${set?.title || t('Score', 'Score')}`,
    html: `<article class="set">
      <header class="set-head">
        ${set?.covers?.cover ? `<img class="set-cover" src="${esc(set.covers.cover)}" alt="" referrerpolicy="no-referrer">` : ''}
        <div class="set-head-body">
          <h1>${map ? `<a href="${L('/beatmapsets/' + map.beatmapset_id)}#${mode}/${map.id}">${esc(set?.title || 'Beatmap')}</a>` : t('Score', 'Score')}</h1>
          <p class="set-artist">${esc(set?.artist || '')}${map ? ` · ${esc(map.version)} ${stars(map.difficulty_rating)}` : ''}</p>
          <p class="set-sub">${t('Played by', 'Joué par')} ${userLink(s.user)} · ${dateTimeText(s.ended_at)} · ${esc(modeName(mode))}</p>
        </div>
      </header>
      <section class="panel">
        <div class="stat-hero">
          <div><dt>${t('Grade', 'Note')}</dt><dd>${grade(s.passed === false ? 'F' : s.rank)}</dd></div>
          <div><dt>${t('Score', 'Score')}</dt><dd>${num(s.total_score)}</dd></div>
          <div><dt>${t('Accuracy', 'Précision')}</dt><dd>${pct(s.accuracy)}</dd></div>
          <div><dt>pp</dt><dd>${s.pp != null ? num(s.pp, 1) : '-'}</dd></div>
        </div>
        <dl class="stat-grid">
          <div><dt>${t('Max combo', 'Combo max')}</dt><dd>${num(s.max_combo)}x${map?.max_combo ? `<small> / ${num(map.max_combo)}x</small>` : ''}</dd></div>
          <div><dt>Mods</dt><dd>${modsHtml(s.mods) || t('None', 'Aucun')}</dd></div>
          ${hits}
        </dl>
        <p class="set-actions">
          ${s.has_replay ? `<button type="button" class="btn" id="replay">${t('Download replay (.osr)', 'Télécharger le replay (.osr)')}</button>` : `<span class="muted">${t('No replay was saved for this score.', 'Aucun replay enregistré pour ce score.')}</span>`}
          <span class="msg" id="replay-msg"></span>
        </p>
      </section>
    </article>`,
    mount(view) {
      const btn = view.querySelector('#replay');
      btn?.addEventListener('click', () => act(btn, view.querySelector('#replay-msg'), () => download(`/api/v2/scores/${s.id}/download`, `xc-osu-replay-${s.id}.osr`)));
    },
  };
}

// ---------------------------------------------------------------- teams
const teamFlag = (team, size = 40) => `<img class="team-flag" src="${esc(team.flag_url || `/api/xc/team-flag/${team.id}`)}" height="${size}" alt="" loading="lazy">`;
export async function teams({ query, go }) {
  const page = Math.max(1, Number.parseInt(query.get('page') || '1', 10) || 1);
  const q = (query.get('q') || '').trim().slice(0, 30);
  const qs = q ? '&q=' + encodeURIComponent(q) : '';
  const list = await api('/api/xc/teams?page=' + page + qs);
  return {
    title: t('Teams', 'Équipes'),
    html: `<h1 class="page-title">${t('Teams', 'Équipes')}</h1>
      <p class="muted">${t('A team puts a tag next to your name everywhere in the game.', 'Une équipe affiche un tag à côté de ton nom partout dans le jeu.')} <a href="${L('/home/account/edit')}#team">${t('Create or manage yours', 'Créer ou gérer la tienne')}</a></p>
      <form class="form form-grid" id="team-filter" role="search"><label>${t('Find a team', 'Trouver une équipe')}<input type="search" name="q" value="${esc(q)}" maxlength="30" placeholder="${t('Name or tag', 'Nom ou tag')}"></label><div><button class="btn ghost" type="submit">${t('Search', 'Rechercher')}</button></div></form>
      <section class="panel">${list.length ? `<ul class="list">${list.map((team) => `<li class="score">${teamFlag(team, 28)}<div class="score-main"><a class="score-map" href="${L('/teams/' + team.id)}"><b>${esc(team.name)}</b> <span class="team-tag">${esc(team.short_name)}</span></a><span class="score-meta">${t('created', 'créée')} ${timeTag(team.created_at)}</span></div><span class="score-pp">${num(team.members_count)}<small> ${t('members', 'membres')}</small></span></li>`).join('')}</ul>`
        : empty(q ? t('No team matches this search.', 'Aucune équipe ne correspond à cette recherche.') : t('No team exists yet. Be the first to create one from your settings.', 'Aucune équipe pour l’instant. Crée la première depuis tes réglages.'))}</section><nav>${page > 1 ? `<a class="btn ghost" href="${L('/teams')}?page=${page - 1}${qs}">←</a>` : ''} ${list.length === 50 ? `<a class="btn ghost" href="${L('/teams')}?page=${page + 1}${qs}">→</a>` : ''}</nav>`,
    mount(view) {
      const filter = view.querySelector('#team-filter');
      filter.addEventListener('submit', (e) => { e.preventDefault(); const value = filter.q.value.trim(); go('/teams' + (value ? '?q=' + encodeURIComponent(value) : '')); });
    },
  };
}
export async function team({ params: [id], go }) {
  const tm = await api('/api/xc/teams/' + id);
  if (!tm?.id) throw new ApiError(404, 'Team not found'); // the server answers {} for an unknown team
  const members = tm.members || [], isMember = !!state.me && members.some((m) => m.id === state.me.id), leads = state.me?.id === tm.leader_id;
  return {
    title: tm.name,
    html: `<article>
      <header class="team-head">${teamFlag(tm, 64)}<div><h1 class="page-title">${esc(tm.name)} <span class="team-tag">${esc(tm.short_name)}</span></h1>
        <p class="muted">${num(tm.members_count)} ${t('members', 'membres')} · ${t('created', 'créée le')} ${dateText(tm.created_at)}</p></div>
        <div class="profile-actions">${leads ? `<a class="btn" href="${L('/home/account/edit')}#team">${t('Manage', 'Gérer')}</a>` : isMember ? `<button type="button" class="btn ghost" id="leave">${t('Leave team', 'Quitter l’équipe')}</button>` : `<button type="button" class="btn" id="join">${t('Join team', 'Rejoindre l’équipe')}</button>`} <span class="msg" id="team-msg"></span></div>
      </header>
      <section class="panel"><h2>${t('Members', 'Membres')}</h2><ul class="list">${members.map((m) => `<li class="score"><div class="score-main">${userLink(m)}</div>${m.id === tm.leader_id ? `<span class="status s-loved">${t('leader', 'chef')}</span>` : ''}</li>`).join('')}</ul></section>
    </article>`,
    mount(view) {
      const msg = view.querySelector('#team-msg');
      const run = (btn, path) => btn?.addEventListener('click', async () => { requireSignIn(); if (await act(btn, msg, () => api(path, { method: 'POST' }))) go('/teams/' + id, { replace: true }); });
      run(view.querySelector('#join'), `/api/xc/teams/${id}/join`);
      run(view.querySelector('#leave'), '/api/xc/teams/leave');
    },
  };
}

// ---------------------------------------------------------------- multiplayer room
export async function room({ params: [id] }) {
  const r = await v2('/rooms/' + id);
  const board = await v2(`/rooms/${id}/leaderboard`).catch(() => null);
  const playlist = r.playlist || [];
  return {
    title: r.name || t('Room', 'Salon'),
    html: `<h1 class="page-title">${esc(r.name || t('Room', 'Salon'))}</h1>
      <p class="muted">${r.host ? `${t('Hosted by', 'Créé par')} ${userLink(r.host)} · ` : ''}${num(r.participant_count ?? (r.recent_participants || []).length)} ${t('players', 'joueurs')}${r.ends_at ? ` · ${new Date(r.ends_at) < new Date() ? t('ended', 'terminé') : t('ends', 'se termine')} ${timeTag(r.ends_at)}` : ''}</p>
      <section class="panel"><h2>${t('Leaderboard', 'Classement')}</h2>
        ${(board?.leaderboard || []).length ? `<ol class="list">${board.leaderboard.map((row, i) => `<li class="score"><span class="pos">#${i + 1}</span><div class="score-main">${userLink(row.user)}<span class="score-meta">${num(row.attempts)} ${t('attempts', 'essais')} · ${num(row.completed)} ${t('completed', 'terminées')}</span></div><span class="score-acc">${pct(row.accuracy)}</span><span class="score-pp">${num(row.total_score)}</span></li>`).join('')}</ol>` : empty(t('No scores in this room yet.', 'Aucun score dans ce salon pour l’instant.'))}
      </section>
      <section class="panel"><h2>Playlist <span class="count">${num(playlist.length)}</span></h2>
        ${playlist.length ? `<ul class="list">${playlist.map((item) => { const map = item.beatmap, set = map?.beatmapset; return `<li class="score"><div class="score-main"><a class="score-map" href="${L('/beatmapsets/' + (map?.beatmapset_id ?? set?.id))}#${modeOfInt(item.ruleset_id)}/${item.beatmap_id ?? map?.id}"><b>${esc(set?.title || 'Beatmap')}</b> <span class="muted">${esc(set?.artist || '')}</span><span class="version">${esc(map?.version || '')}</span></a></div>${modsHtml(item.required_mods)}${map ? stars(map.difficulty_rating) : ''}</li>`; }).join('')}</ul>` : empty(t('Empty playlist.', 'Playlist vide.'))}
      </section>`,
  };
}

// ---------------------------------------------------------------- friends
export async function friends({ go }) {
  requireSignIn();
  const list = await v2('/friends');
  return {
    title: t('Friends', 'Amis'),
    html: `<h1 class="page-title">${t('Friends', 'Amis')}</h1>
      <section class="panel">${list.length ? `<ul class="list">${list.map((f) => `<li class="score" data-id="${f.target_id}"><div class="score-main">${userLink(f.target)}<span class="score-meta">${f.target.is_online ? `<span class="online">${t('Online', 'En ligne')}</span>` : `${t('Last seen', 'Vu')} ${timeTag(f.target.last_visit)}`}${f.mutual ? ` · ${t('mutual', 'mutuel')}` : ''}</span></div><span class="score-acc">${rankText(f.target.statistics?.global_rank)}</span><button type="button" class="btn ghost" data-remove>${t('Remove', 'Retirer')}</button></li>`).join('')}</ul>`
        : empty(t('No friends yet. Open a player’s profile and press “Add friend”.', 'Pas encore d’amis. Ouvre le profil d’un joueur et clique « Ajouter en ami ».'))}</section>`,
    mount(view) {
      view.addEventListener('click', async (e) => {
        const btn = e.target.closest('[data-remove]');
        if (!btn) return;
        const li = btn.closest('li');
        if (await act(btn, null, () => v2('/friends/' + li.dataset.id, { method: 'DELETE' }))) { state.friends?.delete(Number(li.dataset.id)); li.remove(); }
      });
    },
  };
}

// ---------------------------------------------------------------- search
export async function search({ query }) {
  const q = (query.get('query') || '').trim();
  if (!q) return { title: t('Search', 'Recherche'), html: `<h1 class="page-title">${t('Search', 'Recherche')}</h1>${empty(t('Type a player name or a beatmap title in the search box above.', 'Tape un nom de joueur ou un titre de beatmap dans la recherche en haut.'))}` };
  const [people, sets] = await Promise.all([
    v2('/search?mode=user&query=' + encodeURIComponent(q)).catch(() => null),
    v2('/beatmapsets/search?s=any&q=' + encodeURIComponent(q)).catch(() => null),
  ]);
  const players = people?.user?.data || [], maps = sets?.beatmapsets || [];
  return {
    title: q,
    html: `<h1 class="page-title">${t('Results for', 'Résultats pour')} “${esc(q)}”</h1>
      <section class="panel"><h2>${t('Players', 'Joueurs')} <span class="count">${num(people?.user?.total ?? players.length)}</span></h2>
        ${players.length ? `<ul class="list">${players.map((u) => `<li class="score"><div class="score-main">${userLink(u)}</div></li>`).join('')}</ul>` : empty(t('No player with that name.', 'Aucun joueur avec ce nom.'))}</section>
      <section class="panel"><h2>Beatmaps <span class="count">${num(sets?.total ?? maps.length)}</span></h2>
        ${maps.length ? `<div class="set-grid">${maps.slice(0, 12).map(setCard).join('')}</div><p><a href="${L('/beatmapsets?s=any&q=' + encodeURIComponent(q))}">${t('All beatmap results', 'Tous les résultats beatmaps')}</a></p>` : empty(t('No beatmap matches.', 'Aucune beatmap ne correspond.'))}</section>`,
  };
}

// ---------------------------------------------------------------- news
export async function news() {
  const data = await v2('/news');
  return {
    title: t('News', 'Actus'),
    html: `<h1 class="page-title">${t('News', 'Actus')}</h1><section class="panel">
      ${data.news_posts.length ? data.news_posts.map((p) => `<a class="news-item" href="${L('/news/' + encodeURIComponent(p.slug))}"><b>${esc(p.title)}</b><span class="muted small">${dateText(p.published_at)} · ${esc(p.author)}</span><span>${esc(p.preview)}</span></a>`).join('') : empty(t('No news yet.', 'Pas encore d’actus.'))}</section>`,
  };
}
export async function newsPost({ params: [slug] }) {
  const p = await v2('/news/' + encodeURIComponent(slug));
  return {
    title: p.title,
    html: `<article class="panel post"><p><a href="${L('/news')}">← ${t('All news', 'Toutes les actus')}</a></p><h1>${esc(p.title)}</h1><p class="muted small">${dateText(p.published_at)} · ${esc(p.author)}</p><div class="user-text">${sanitize(p.content)}</div></article>`,
  };
}

// ---------------------------------------------------------------- comments by a player (the game links to /comments?user_id=…)
export async function comments({ query }) {
  const userId = parseInt(query.get('user_id'), 10);
  const user = userId ? await v2('/users/' + userId).catch(() => null) : null;
  return {
    title: t('Comments', 'Commentaires'),
    html: `<h1 class="page-title">${t('Comments', 'Commentaires')}${user ? ` ${t('by', 'de')} <a href="${L('/users/' + user.id)}">${esc(user.username)}</a>` : ''}</h1><section class="panel" id="thread"></section>`,
    mount(view) {
      const thread = commentThread({ query: 'sort=new' });
      view.querySelector('#thread').append(thread);
      if (!userId) return;
      // the server lists every comment; keep this player's ones
      new MutationObserver(() => { for (const li of thread.querySelectorAll('.comment')) if (!li.querySelector(`a[href$="/users/${userId}"]`)) li.hidden = true; }).observe(thread, { childList: true, subtree: true });
    },
  };
}

// ---------------------------------------------------------------- simple pages
export function unavailable() {
  return {
    title: t('Not available', 'Indisponible'),
    html: `<section class="panel notice"><h1>${t('This part does not exist on xc-osu! yet', 'Cette partie n’existe pas encore sur xc-osu!')}</h1>
      <p>${t('Forums, tournaments, the store and supporter pages are not part of xc-osu!. Profiles, rankings, beatmaps, teams, news and the wiki are.', 'Les forums, les tournois, la boutique et les pages supporter ne font pas partie de xc-osu!. Les profils, les classements, les beatmaps, les équipes, les actualités et le wiki, si.')}</p>
      <p>${t('To report a problem or ask for help, see', 'Pour signaler un problème ou demander de l’aide, voir')} <a href="${L('/wiki/Reporting_a_problem')}">${t('Reporting a problem', 'Signaler un problème')}</a>.</p>
      <p><a class="btn" href="${L('/')}">${t('Back to home', 'Retour à l’accueil')}</a></p></section>`,
  };
}
export function notFound() {
  return {
    title: t('Not found', 'Introuvable'),
    html: `<section class="panel notice"><h1>${t('Nothing here', 'Rien ici')}</h1>
      <p>${t('This page, player or beatmap does not exist (or no longer does). Check the address or search for it above.', 'Cette page, ce joueur ou cette beatmap n’existe pas (ou plus). Vérifie l’adresse ou utilise la recherche en haut.')}</p>
      <p><a class="btn" href="${L('/')}">${t('Back to home', 'Retour à l’accueil')}</a></p></section>`,
  };
}
export function failed(error, retry) {
  return {
    title: t('Error', 'Erreur'),
    html: `<section class="panel notice"><h1>${t('This page could not be loaded', 'Cette page n’a pas pu être chargée')}</h1>
      <p>${esc(error?.message || String(error))}</p><p><button type="button" class="btn" id="retry">${t('Try again', 'Réessayer')}</button></p></section>`,
    mount(view) { view.querySelector('#retry').addEventListener('click', retry); },
  };
}
