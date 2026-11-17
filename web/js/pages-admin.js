// xc-osu! web: moderation (staff only, with two-factor authentication on). xc softworks (kudasaixc)
import { L, state, t, esc, api, act, requireSignIn, dateTimeText, userLink } from './core.js';

const TABS = [['reports', t('Reports', 'Signalements')], ['scores', t('Flagged scores', 'Scores signalés')], ['user', t('Player', 'Joueur')], ['audit', t('Log', 'Journal')]];

export async function admin({ query, go }) {
  requireSignIn();
  if (!state.me?.is_admin) return { title: t('Not found', 'Introuvable'), html: `<section class="panel narrow"><h1>${t('Not found', 'Introuvable')}</h1></section>` };
  const tab = TABS.some(([k]) => k === query.get('tab')) ? query.get('tab') : 'reports';
  return {
    title: t('Moderation', 'Modération'),
    html: `<h1 class="page-title">${t('Moderation', 'Modération')}</h1>
      <nav class="tabs">${TABS.map(([k, label]) => `<a href="${L('/admin?tab=' + k)}"${k === tab ? ' aria-current="page"' : ''}>${esc(label)}</a>`).join('')}</nav>
      <section class="panel" id="admin-body"><span class="muted">${t('Loading…', 'Chargement…')}</span></section>`,
    async mount(view) {
      const body = view.querySelector('#admin-body');
      try { await ({ reports, scores, user, audit })[tab](body, query, go); }
      catch (e) { body.innerHTML = `<p class="bad">${esc(e.message)}</p>`; }
    },
  };
}

async function reports(body, query) {
  const resolved = query.get('status') === 'resolved';
  const list = await api('/api/xc/admin/reports' + (resolved ? '?status=resolved' : ''));
  body.innerHTML = `<p><a href="${L('/admin?tab=reports' + (resolved ? '' : '&status=resolved'))}">${resolved ? t('Open reports', 'Signalements ouverts') : t('Resolved reports', 'Signalements traités')}</a></p>
    <ul class="list">${list.map((r) => `<li class="score" data-id="${r.id}"><div class="score-main">
      <b>${esc(r.type)} #${esc(r.target_id)}</b> ${r.target_user_id ? `<a href="${L('/admin?tab=user&id=' + r.target_user_id)}">${t('player', 'joueur')} #${esc(r.target_user_id)}</a>` : ''}
      <span class="score-meta">${esc(dateTimeText(r.created_at))} · ${t('by', 'par')} ${esc(r.reporter || '#' + r.reporter_id)} · ${esc(r.reason || '')}</span>
      ${r.target ? `<blockquote>${esc(r.target)}</blockquote>` : ''}${r.comments ? `<p>${esc(r.comments)}</p>` : ''}
      ${r.resolved_at ? `<p class="muted">${t('Resolved', 'Traité')}: ${esc(r.resolution)}</p>` : `<form class="form form-grid" data-resolve><input name="resolution" maxlength="500" required placeholder="${t('What was done', 'Ce qui a été fait')}"><div><button class="btn ghost" type="submit">${t('Resolve', 'Clore')}</button> <span class="msg"></span></div></form>`}
    </div></li>`).join('') || `<li class="muted">${t('Nothing here.', 'Rien ici.')}</li>`}</ul>`;
  body.querySelectorAll('[data-resolve]').forEach((form) => form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const id = form.closest('li').dataset.id;
    if (await act(form.querySelector('button'), form.querySelector('.msg'), () => api(`/api/xc/admin/reports/${id}/resolve`, { method: 'POST', json: { resolution: form.resolution.value } }), t('Resolved.', 'Traité.'))) form.remove();
  }));
}

async function scores(body) {
  const list = await api('/api/xc/admin/scores/flagged');
  body.innerHTML = `<ul class="list">${list.map((s) => `<li class="score" data-id="${s.id}"><div class="score-main">
      <a href="${L('/scores/' + s.id)}"><b>${t('Score', 'Score')} #${s.id}</b></a> · <a href="${L('/admin?tab=user&id=' + s.user_id)}">${esc(s.username)}</a> · ${t('beatmap', 'beatmap')} #${s.beatmap_id}
      <span class="score-meta">${esc(String(s.total_score))} · ${(s.accuracy * 100).toFixed(2)} % · ${s.max_combo}x · ${s.ranked ? t('ranked', 'classé') : t('not ranked', 'non classé')}</span>
      <p>${esc(s.reason)}</p></div>
      ${s.reason.startsWith('approved:') || s.reason.startsWith('unranked by staff:') ? '' : `<button class="btn ghost" data-decision="approve">${t('Approve', 'Valider')}</button> <button class="btn ghost danger" data-decision="unrank">${t('Unrank', 'Déclasser')}</button>`}</li>`).join('') || `<li class="muted">${t('Nothing to review.', 'Rien à examiner.')}</li>`}</ul><span class="msg" id="score-msg"></span>`;
  body.addEventListener('click', async (e) => {
    const button = e.target.closest('[data-decision]');
    if (!button) return;
    const li = button.closest('li');
    if (await act(button, body.querySelector('#score-msg'), () => api(`/api/xc/admin/scores/${li.dataset.id}/review`, { method: 'POST', json: { decision: button.dataset.decision } }), t('Done.', 'Fait.')))
      li.querySelectorAll('[data-decision]').forEach((b) => b.remove());
  });
}

async function user(body, query, go) {
  const id = Number(query.get('id'));
  body.innerHTML = `<form class="form form-grid" id="find"><label>${t('Player id', 'Id du joueur')}<input name="id" type="number" min="1" value="${id || ''}" required></label><div><button class="btn" type="submit">${t('Open', 'Ouvrir')}</button></div></form><div id="user-body"></div>`;
  body.querySelector('#find').addEventListener('submit', (e) => { e.preventDefault(); go('/admin?tab=user&id=' + Number(e.target.id.value)); });
  if (!id) return;
  const u = await api('/api/xc/admin/users/' + id);
  const box = body.querySelector('#user-body');
  box.innerHTML = `<h2>${userLink(u, { withAvatar: false })}</h2>
    <p class="muted">${esc(u.email)} · ${esc(u.country)} · ${t('joined', 'inscrit le')} ${esc(dateTimeText(u.join_date))} · ${u.reports_against} ${t('reports', 'signalements')}
      ${u.restricted ? ' · <b class="bad">' + t('restricted', 'restreint') + '</b>' : ''}${u.silenced_until && new Date(u.silenced_until) > new Date() ? ' · ' + t('silenced until', 'silencieux jusqu’au') + ' ' + esc(dateTimeText(u.silenced_until)) : ''}</p>
    <form class="form form-grid" id="silence"><label>${t('Silence (minutes, 0 lifts it)', 'Silence (minutes, 0 pour lever)')}<input name="minutes" type="number" min="0" max="525600" required></label>
      <label>${t('Reason', 'Raison')}<input name="reason" maxlength="300"></label><div><button class="btn ghost" type="submit">${t('Apply', 'Appliquer')}</button> <span class="msg"></span></div></form>
    <form class="form form-grid" id="restrict"><label>${t('Reason', 'Raison')}<input name="reason" maxlength="300" required></label>
      <div><button class="btn ghost danger" type="submit">${u.restricted ? t('Lift the restriction', 'Lever la restriction') : t('Restrict the account', 'Restreindre le compte')}</button> <span class="msg"></span></div></form>
    <h3>${t('Recent messages', 'Messages récents')}</h3>
    <ul class="list">${(u.recent_messages || []).map((m) => `<li class="score" data-message="${m.id}"><div class="score-main"><span class="score-meta">${esc(dateTimeText(m.timestamp))} · ${t('channel', 'canal')} ${m.channel_id}</span>${esc(m.content)}</div><button class="btn ghost" data-remove>${t('Remove', 'Retirer')}</button></li>`).join('') || `<li class="muted">-</li>`}</ul>
    <h3>${t('History', 'Historique')}</h3>
    <ul class="list">${(u.history || []).map((h) => `<li>${esc(dateTimeText(new Date(h.at * 1000)))} · ${esc(h.action)} · #${esc(h.by)} ${h.details ? '· ' + esc(h.details) : ''}</li>`).join('') || `<li class="muted">-</li>`}</ul>`;
  const silence = box.querySelector('#silence'), restrict = box.querySelector('#restrict');
  silence.addEventListener('submit', async (e) => { e.preventDefault(); await act(silence.querySelector('button'), silence.querySelector('.msg'), () => api(`/api/xc/admin/users/${id}/silence`, { method: 'POST', json: { minutes: Number(silence.minutes.value), reason: silence.reason.value } }), t('Done.', 'Fait.')); });
  restrict.addEventListener('submit', async (e) => {
    e.preventDefault();
    if (confirm(t('Are you sure?', 'Confirmer ?'))) await act(restrict.querySelector('button'), restrict.querySelector('.msg'), () => api(`/api/xc/admin/users/${id}/restrict`, { method: 'POST', json: { restricted: !u.restricted, reason: restrict.reason.value } }), t('Done.', 'Fait.'));
  });
  box.addEventListener('click', async (e) => {
    const li = e.target.closest('[data-remove]')?.closest('li');
    if (li && confirm(t('Remove this message?', 'Retirer ce message ?')) && await act(e.target, null, () => api('/api/xc/admin/chat/messages/' + li.dataset.message, { method: 'DELETE' }))) li.remove();
  });
}

async function audit(body) {
  const list = await api('/api/xc/admin/audit');
  body.innerHTML = `<div class="table-wrap"><table class="table"><thead><tr><th>${t('When', 'Quand')}</th><th>${t('Action', 'Action')}</th><th>${t('By', 'Par')}</th><th>${t('Target', 'Cible')}</th><th>${t('Details', 'Détails')}</th></tr></thead><tbody>
    ${list.map((a) => `<tr><td>${esc(dateTimeText(a.at))}</td><td>${esc(a.action)}</td><td>${a.actor_id ?? '-'}</td><td>${esc(a.target_type || '')} ${a.target_id ?? ''}</td><td>${esc(a.details ? JSON.stringify(a.details) : '')}</td></tr>`).join('')}
    </tbody></table></div>`;
}
