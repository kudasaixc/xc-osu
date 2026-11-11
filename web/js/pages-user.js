// xc-osu! web — player profile. xc softworks (kudasaixc)
import { L, ASSETS, state, t, esc, num, pct, rankText, duration, dateText, timeTag, countryChip, countryName, MODES, modeName, v2, api,
  avatar, teamTag, grade, stars, scoreRow, pagedList, renderUserText, empty, act, signedIn, NeedSignIn } from './core.js';
import { setCard } from './pages-beatmap.js';

let medalsPromise = null;
const medals = () => (medalsPromise ||= fetch(ASSETS + 'data/medals.json').then((r) => r.json()).catch(() => { medalsPromise = null; return []; }));

function rankChart(history) {
  const points = (history?.data || []).map((rank, i) => ({ rank, i })).filter((p) => p.rank > 0);
  if (points.length < 5) return ''; // a couple of days is a dash in a corner, not a history
  const total = history.data.length, ranks = points.map((p) => p.rank);
  const best = Math.min(...ranks), worst = Math.max(...ranks), span = Math.max(1, worst - best);
  const W = 600, H = 110, pad = 8;
  const xy = (p) => `${(pad + (p.i / Math.max(1, total - 1)) * (W - 2 * pad)).toFixed(1)},${(pad + ((p.rank - best) / span) * (H - 2 * pad)).toFixed(1)}`;
  return `<figure class="rank-chart">
    <svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" role="img" aria-label="${esc(t(`Global rank over the last ${total} days: best #${best}, lowest #${worst}`, `Rang mondial sur les ${total} derniers jours : meilleur #${best}, plus bas #${worst}`))}">
      <polyline points="${points.map(xy).join(' ')}" fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round" stroke-linecap="round" vector-effect="non-scaling-stroke"/>
    </svg>
    <figcaption><span>${t(`Rank over ${total} days`, `Rang sur ${total} jours`)}</span><span>${t('best', 'meilleur')} ${rankText(best)} · ${t('lowest', 'plus bas')} ${rankText(worst)}</span></figcaption>
  </figure>`;
}

function activityLine(ev) {
  const map = ev.beatmap ? `<a href="${L(ev.beatmap.url || '/')}">${esc(ev.beatmap.title || 'beatmap')}</a>` : '';
  switch (ev.type) {
    case 'achievement': return t(`Unlocked the medal <b>${esc(ev.achievement?.name)}</b>`, `A débloqué la médaille <b>${esc(ev.achievement?.name)}</b>`);
    case 'rank': return `${grade(ev.scoreRank)} ${t(`Reached rank <b>#${num(ev.rank)}</b> on ${map}`, `A atteint le rang <b>#${num(ev.rank)}</b> sur ${map}`)} <span class="muted">(${esc(modeName(ev.mode))})</span>`;
    case 'rank_lost': case 'rankLost': return t(`Lost first place on ${map}`, `A perdu la première place sur ${map}`);
    default: return esc(ev.type);
  }
}

export async function profile({ params: [lookup, modeParam], go, refreshShell, setHue }) {
  const user = await v2(`/users/${encodeURIComponent(lookup)}${modeParam ? '/' + modeParam : ''}`);
  const mode = modeParam || user.playmode || 'osu';
  const s = user.statistics || {}, g = s.grade_counts || {};
  const mine = state.me?.id === user.id;
  const info = [
    [t('Location', 'Lieu'), user.location], [t('Interests', 'Centres d’intérêt'), user.interests], [t('Occupation', 'Occupation'), user.occupation],
    ['Discord', user.discord], ['Twitter', user.twitter],
    [t('Plays with', 'Joue avec'), (user.playstyle || []).map((p) => ({ mouse: t('mouse', 'souris'), keyboard: t('keyboard', 'clavier'), tablet: t('tablet', 'tablette'), touch: t('touch screen', 'écran tactile') }[p] || p)).join(', ')],
  ].filter(([, v]) => v);
  const site = (() => { try { const u = new URL(user.website); return /^https?:$/.test(u.protocol) ? u.href : null; } catch { return null; } })();
  const hue = Number.isFinite(user.profile_hue) ? user.profile_hue : null;
  const daily = user.daily_challenge_user_stats;

  const html = `
    <article class="profile">
      <header class="profile-head">
        <img class="profile-cover" src="${esc(user.cover?.url || user.cover_url)}" alt="">
        <div class="profile-id">
          ${avatar(user, 120)}
          <div class="profile-names">
            <h1>${teamTag(user.team)}${esc(user.username)}</h1>
            ${user.title ? `<p class="profile-title">${esc(user.title)}</p>` : ''}
            <p class="profile-sub">
              ${countryChip(user.country_code)} ${esc(countryName(user.country_code))}
              ${(user.groups || []).map((gr) => `<span class="group" style="--c:${esc(/^#[0-9a-f]{3,8}$/i.test(gr.colour || '') ? gr.colour : '#ff66aa')}" title="${esc(gr.name)}">${esc(gr.short_name)}</span>`).join('')}
              <span class="${user.is_online ? 'online' : 'muted'}">${user.is_online ? t('Online', 'En ligne') : `${t('Last seen', 'Vu')} ${timeTag(user.last_visit)}`}</span>
            </p>
            ${(user.previous_usernames || []).length ? `<p class="muted small">${t('Formerly', 'Anciennement')} ${user.previous_usernames.map(esc).join(', ')}</p>` : ''}
          </div>
          <div class="profile-actions">
            ${mine ? `<a class="btn" href="${L('/home/account/edit')}">${t('Edit my profile', 'Modifier mon profil')}</a>`
              : `<button type="button" class="btn" id="friend" aria-pressed="false"></button>`}
          </div>
        </div>
        <nav class="tabs" aria-label="${t('Game mode', 'Mode de jeu')}">
          ${MODES.map((m) => `<a href="${L(`/users/${user.id}/${m}`)}"${m === mode ? ' aria-current="page"' : ''}>${modeName(m)}</a>`).join('')}
        </nav>
      </header>

      <section class="panel stats">
        <div class="stat-hero">
          <div><dt>${t('Global rank', 'Rang mondial')}</dt><dd>${rankText(s.global_rank)}</dd></div>
          <div><dt>${t('Country rank', 'Rang national')}</dt><dd>${rankText(s.country_rank)}</dd></div>
          <div><dt>pp</dt><dd>${num(s.pp)}</dd></div>
          <div><dt>${t('Accuracy', 'Précision')}</dt><dd>${num(s.hit_accuracy, 2)} %</dd></div>
        </div>
        ${rankChart(user.rank_history) || `<p class="muted small">${s.is_ranked ? t('Rank history appears after a few days of play.', 'L’historique de rang apparaît après quelques jours de jeu.') : t('Not ranked in this mode yet.', 'Pas encore classé dans ce mode.')}</p>`}
        <dl class="stat-grid">
          <div><dt>${t('Play count', 'Parties jouées')}</dt><dd>${num(s.play_count)}</dd></div>
          <div><dt>${t('Play time', 'Temps de jeu')}</dt><dd>${duration(s.play_time)}</dd></div>
          <div><dt>${t('Ranked score', 'Score classé')}</dt><dd>${num(s.ranked_score)}</dd></div>
          <div><dt>${t('Total score', 'Score total')}</dt><dd>${num(s.total_score)}</dd></div>
          <div><dt>${t('Maximum combo', 'Combo maximum')}</dt><dd>${num(s.maximum_combo)}x</dd></div>
          <div><dt>${t('Total hits', 'Notes touchées')}</dt><dd>${num(s.total_hits)}</dd></div>
          <div><dt>${t('Level', 'Niveau')}</dt><dd>${num(s.level?.current)} <meter min="0" max="100" value="${Number(s.level?.progress) || 0}" title="${Number(s.level?.progress) || 0} %"></meter></dd></div>
          <div><dt>${t('Joined', 'Inscription')}</dt><dd>${dateText(user.join_date)}</dd></div>
        </dl>
        <div class="grades">${[['XH', g.ssh], ['X', g.ss], ['SH', g.sh], ['S', g.s], ['A', g.a]].map(([r, n]) => `<span>${grade(r)} ${num(n || 0)}</span>`).join('')}</div>
        ${daily?.playcount ? `<p class="muted small">${t('Daily challenge', 'Défi du jour')} : ${num(daily.playcount)} ${t('played', 'joués')} · ${t('current streak', 'série en cours')} ${num(daily.daily_streak_current)} · ${t('best', 'meilleure')} ${num(daily.daily_streak_best)}</p>` : ''}
      </section>

      ${(user.badges || []).length ? `<section class="panel"><h2>Badges</h2><div class="badges">${user.badges.map((b) => `<img src="${esc(b['image@2x_url'] || b.image_url)}" alt="${esc(b.description)}" title="${esc(b.description)}" height="40" loading="lazy">`).join('')}</div></section>` : ''}

      ${user.page?.raw || info.length || site ? `<section class="panel"><h2>${t('About', 'À propos')}</h2>
        ${info.length || site ? `<dl class="about">${info.map(([k, v]) => `<div><dt>${esc(k)}</dt><dd>${esc(v)}</dd></div>`).join('')}${site ? `<div><dt>${t('Website', 'Site')}</dt><dd><a href="${esc(site)}" target="_blank" rel="noopener nofollow">${esc(site.replace(/^https?:\/\//, '').replace(/\/$/, ''))}</a></dd></div>` : ''}</dl>` : ''}
        ${user.page?.raw ? `<div class="user-text">${renderUserText(user.page.raw)}</div>` : ''}</section>` : ''}

      <section class="panel" id="sec-pinned" hidden><h2>${t('Pinned scores', 'Scores épinglés')}</h2></section>
      <section class="panel"><h2>${t('Best performance', 'Meilleures performances')} <span class="count">${num(user.scores_best_count)}</span></h2><div id="sec-best"></div></section>
      <section class="panel"><h2>${t('First places', 'Premières places')} <span class="count">${num(user.scores_first_count)}</span></h2><div id="sec-firsts"></div></section>
      <section class="panel"><h2>${t('Last 24 hours', 'Dernières 24 heures')}</h2><div id="sec-recent"></div></section>
      <section class="panel"><h2>${t('Most played', 'Les plus jouées')} <span class="count">${num(user.beatmap_playcounts_count)}</span></h2><div id="sec-played"></div></section>
      <section class="panel"><h2>${t('Favourite beatmaps', 'Beatmaps favorites')} <span class="count">${num(user.favourite_beatmapset_count)}</span></h2><div id="sec-favs"></div></section>
      <section class="panel"><h2>${t('Medals', 'Médailles')} <span class="count">${num((user.user_achievements || []).length)}</span></h2><div id="sec-medals" class="medals"></div></section>
      <section class="panel"><h2>${t('Recent activity', 'Activité récente')}</h2><div id="sec-activity"></div></section>
    </article>`;

  return {
    title: user.username,
    html,
    mount(view) {
      if (hue != null) setHue(hue);
      const scores = (type, extra = '') => (offset, limit) => v2(`/users/${user.id}/scores/${type}?mode=${mode}&limit=${limit}&offset=${offset}${extra}`);
      view.querySelector('#sec-best').append(pagedList({ fetchPage: scores('best'), renderItem: (sc, i) => scoreRow(sc, { position: i + 1 }), emptyText: t('No ranked plays in this mode yet.', 'Aucune partie classée dans ce mode pour l’instant.') }));
      view.querySelector('#sec-firsts').append(pagedList({ fetchPage: scores('firsts'), renderItem: (sc) => scoreRow(sc), pageSize: 5, emptyText: t('No first places yet.', 'Aucune première place pour l’instant.') }));
      view.querySelector('#sec-recent').append(pagedList({ fetchPage: scores('recent', '&include_fails=1'), renderItem: (sc) => scoreRow(sc), pageSize: 5, emptyText: t('No plays in the last 24 hours.', 'Aucune partie ces dernières 24 heures.') }));
      view.querySelector('#sec-played').append(pagedList({
        fetchPage: (offset, limit) => v2(`/users/${user.id}/beatmapsets/most_played?limit=${limit}&offset=${offset}`), pageSize: 5,
        renderItem: (row) => `<li class="score"><div class="score-main"><a class="score-map" href="${L('/beatmapsets/' + row.beatmap?.beatmapset_id)}#${esc(row.beatmap?.mode || 'osu')}/${row.beatmap_id}"><b>${esc(row.beatmapset?.title)}</b> <span class="muted">${esc(row.beatmapset?.artist)}</span><span class="version">${esc(row.beatmap?.version)}</span></a></div>${stars(row.beatmap?.difficulty_rating)}<span class="score-pp">${num(row.count)}<small>×</small></span></li>`,
        emptyText: t('Nothing played yet.', 'Rien joué pour l’instant.'),
      }));
      view.querySelector('#sec-favs').append(pagedList({
        fetchPage: (offset, limit) => v2(`/users/${user.id}/beatmapsets/favourite?limit=${limit}&offset=${offset}`), pageSize: 6, tag: 'div', className: 'set-grid',
        renderItem: setCard, emptyText: t('No favourites yet.', 'Aucun favori pour l’instant.'),
      }));
      view.querySelector('#sec-activity').append(pagedList({
        fetchPage: (offset, limit) => v2(`/users/${user.id}/recent_activity?limit=${limit}&offset=${offset}`), pageSize: 8,
        renderItem: (ev) => `<li class="activity"><span>${activityLine(ev)}</span>${timeTag(ev.createdAt || ev.created_at)}</li>`,
        emptyText: t('No recent activity.', 'Aucune activité récente.'),
      }));

      // pinned scores only take room when there are some
      v2(`/users/${user.id}/scores/pinned?mode=${mode}&limit=20`).then((rows) => {
        if (!rows.length) return;
        const sec = view.querySelector('#sec-pinned');
        sec.hidden = false;
        sec.insertAdjacentHTML('beforeend', `<ul class="list">${rows.map((sc) => scoreRow(sc)).join('')}</ul>`);
      }).catch(() => { });

      medals().then((all) => {
        const host = view.querySelector('#sec-medals');
        const owned = new Map((user.user_achievements || []).map((a) => [a.achievement_id, a.achieved_at]));
        if (!owned.size) { host.innerHTML = empty(t('No medals yet.', 'Aucune médaille pour l’instant.')); return; }
        host.innerHTML = all.filter((m) => owned.has(m.id)).sort((a, b) => new Date(owned.get(b.id)) - new Date(owned.get(a.id)))
          .map((m) => `<figure class="medal" title="${esc(m.description)}"><img src="https://assets.ppy.sh/medals/web/${esc(m.slug)}.png" width="56" height="56" alt="" loading="lazy" referrerpolicy="no-referrer"><figcaption>${esc(m.name)}</figcaption></figure>`).join('');
      });

      const friend = view.querySelector('#friend');
      if (friend) {
        const paint = () => {
          const on = !!state.friends?.has(user.id);
          friend.textContent = on ? t('Remove friend', 'Retirer des amis') : t('Add friend', 'Ajouter en ami');
          friend.setAttribute('aria-pressed', String(on));
          friend.classList.toggle('ghost', on);
        };
        paint();
        friend.addEventListener('click', async () => {
          if (!signedIn()) throw new NeedSignIn();
          await act(friend, null, async () => {
            if (state.friends.has(user.id)) { await v2('/friends/' + user.id, { method: 'DELETE' }); state.friends.delete(user.id); }
            else { await v2('/friends', { method: 'POST', form: { target: user.id } }); state.friends.add(user.id); }
          });
          paint();
        });
      }
    },
  };
}
