// xc-osu! web — sign in, registration, account settings. xc softworks (kudasaixc)
import { L, state, t, esc, num, countryName, MODES, modeName, api, signIn, signOut, loadMe, signedIn, requireSignIn, renderUserText, act, userLink, ApiError } from './core.js';

/** Only follow "next" back to a page of this site. */
const nextPath = (query) => { const n = query.get('next') || ''; return n.startsWith('/') && !n.startsWith('//') && !n.startsWith('/login') ? n : '/'; };

export async function login({ query, go, refreshShell }) {
  if (signedIn() && state.me) { go(nextPath(query), { replace: true }); return null; }
  return {
    title: t('Sign in', 'Connexion'),
    html: `<section class="panel narrow"><h1>${t('Sign in', 'Connexion')}</h1>
      <p class="muted">${t('Use the same account as in the game.', 'Utilise le même compte que dans le jeu.')}</p>
      <form id="login" class="form">
        <label>${t('Username', 'Nom d’utilisateur')}<input name="username" autocomplete="username" required autofocus></label>
        <label>${t('Password', 'Mot de passe')}<input name="password" type="password" autocomplete="current-password" required></label>
        <button class="btn" type="submit">${t('Sign in', 'Se connecter')}</button> <span class="msg" role="alert"></span>
      </form>
      <p class="muted small"><a href="${L('/register')}${query.get('next') ? '?next=' + encodeURIComponent(query.get('next')) : ''}">${t('Create an account', 'Créer un compte')}</a> · <a href="${L('/home/password-reset')}">${t('Forgot your password?', 'Mot de passe oublié ?')}</a></p></section>`,
    mount(view) {
      const form = view.querySelector('#login');
      form.addEventListener('submit', async (e) => {
        e.preventDefault();
        if (await act(form.querySelector('button'), form.querySelector('.msg'), () => signIn(form.username.value.trim(), form.password.value))) { refreshShell(); go(nextPath(query), { replace: true }); }
      });
    },
  };
}

export async function register({ query, go, refreshShell }) {
  if (signedIn() && state.me) { go('/', { replace: true }); return null; }
  return {
    title: t('Create an account', 'Créer un compte'),
    html: `<section class="panel narrow"><h1>${t('Create an account', 'Créer un compte')}</h1>
      <p class="muted">${t('One account for the game and this site.', 'Un seul compte pour le jeu et ce site.')}</p>
      <form id="register" class="form">
        <label>${t('Username', 'Nom d’utilisateur')}<input name="username" autocomplete="username" required minlength="3" maxlength="15"><small>${t('3 to 15 characters: letters, digits, spaces, - _ [ ]', '3 à 15 caractères : lettres, chiffres, espaces, - _ [ ]')}</small></label>
        <label>E-mail<input name="email" type="email" autocomplete="email" required></label>
        <label>${t('Password', 'Mot de passe')}<input name="password" type="password" autocomplete="new-password" required minlength="8"><small>${t('At least 8 characters', 'Au moins 8 caractères')}</small></label>
        <button class="btn" type="submit">${t('Create my account', 'Créer mon compte')}</button> <span class="msg" role="alert"></span>
      </form>
      <p class="muted small"><a href="${L('/login')}">${t('I already have an account', 'J’ai déjà un compte')}</a></p></section>`,
    mount(view) {
      const form = view.querySelector('#register');
      form.addEventListener('submit', async (e) => {
        e.preventDefault();
        const ok = await act(form.querySelector('button'), form.querySelector('.msg'), async () => {
          const r = await fetch('/users', { method: 'POST', body: new URLSearchParams({ 'user[username]': form.username.value.trim(), 'user[user_email]': form.email.value.trim(), 'user[password]': form.password.value }) });
          const data = await r.json().catch(() => ({}));
          const errors = data?.form_error?.user;
          if (errors) throw new ApiError(422, Object.values(errors).flat().join(' '));
          if (!r.ok) throw new ApiError(r.status, data.error || data.message || t('Registration failed. Try again in a moment.', 'Inscription impossible. Réessaie dans un instant.'));
          await signIn(form.username.value.trim(), form.password.value);
        });
        if (ok) { refreshShell(); go(nextPath(query) === '/' ? '/home/account/edit' : nextPath(query), { replace: true }); }
      });
    },
  };
}

export async function passwordReset({ hash }) {
  const token = new URLSearchParams((hash || '').replace(/^#/, '')).get('token');
  const status = token ? { available: true } : await api('/api/xc/password-reset');
  return {
    title: t('Forgotten password', 'Mot de passe oublié'),
    html: `<section class="panel narrow"><h1>${token ? t('Choose a new password', 'Choisis un nouveau mot de passe') : t('Forgotten password', 'Mot de passe oublié')}</h1>
      ${!status.available ? `<p role="status">${t('Email recovery is not available yet. Please contact xc softworks support.', 'La récupération par e-mail n’est pas encore disponible. Contacte le support xc softworks.')}</p>` : `
      <p>${token ? t('This link expires after 25 minutes and can be used once.', 'Ce lien expire après 25 minutes et ne peut servir qu’une seule fois.') : t('Enter your account email to receive a password reset link.', 'Entre l’adresse e-mail de ton compte pour recevoir un lien de réinitialisation.')}</p>
      <form id="password-recovery" class="form">
      ${token ? `<label>${t('New password', 'Nouveau mot de passe')}<input name="password" type="password" autocomplete="new-password" minlength="8" maxlength="1024" required></label>
      <label>${t('Confirm password', 'Confirme le mot de passe')}<input name="confirmation" type="password" autocomplete="new-password" minlength="8" maxlength="1024" required></label>` : `<label>E-mail<input name="email" type="email" autocomplete="email" maxlength="254" required></label>`}
      <button class="btn" type="submit">${token ? t('Save password', 'Enregistrer le mot de passe') : t('Send reset link', 'Envoyer le lien')}</button><span class="msg" role="status"></span></form>`}
      <p><a href="${L('/login')}">${t('Back to sign in', 'Retour à la connexion')}</a></p></section>`,
    mount(view) {
      // Remove the token from the address bar; keep it only in this page's closure.
      if (token) history.replaceState(null, '', location.pathname + location.search);
      const form = view.querySelector('#password-recovery');
      if (!form) return;
      form.addEventListener('submit', async e => {
        e.preventDefault();
        const msg = form.querySelector('.msg');
        if (token && form.elements.password.value !== form.elements.confirmation.value) {
          msg.textContent = t('Passwords do not match.', 'Les mots de passe ne correspondent pas.'); return;
        }
        const ok = await act(form.querySelector('button'), msg, () => api('/api/xc/password-reset/' + (token ? 'complete' : 'request'), {
          method: 'POST', json: token ? { token, password: form.elements.password.value } : { email: form.elements.email.value.trim() },
        }), token ? t('Password updated. You can sign in again.', 'Mot de passe modifié. Tu peux te reconnecter.') : t('If this address belongs to an account, a link will be sent. Check your spam folder too.', 'Si cette adresse correspond à un compte, un lien sera envoyé. Vérifie aussi les indésirables.'));
        if (ok) { form.reset(); if (token) { signOut(); form.querySelector('button').disabled = true; } }
      });
    },
  };
}

const COUNTRIES = 'AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW'.split(' ');

export async function settings({ go, refreshShell, hash }) {
  requireSignIn();
  const acct = await api('/api/xc/account');
  const pr = acct.profile || {};
  const field = (key, label, max = 100) => `<label>${esc(label)}<input name="${key}" value="${esc(pr[key] || '')}" maxlength="${max}"></label>`;
  const countries = COUNTRIES.map((c) => [c, countryName(c)]).sort((a, b) => a[1].localeCompare(b[1], state.lang));
  const styles = [['mouse', t('Mouse', 'Souris')], ['keyboard', t('Keyboard', 'Clavier')], ['tablet', t('Tablet', 'Tablette')], ['touch', t('Touch screen', 'Écran tactile')]];

  return {
    title: t('Settings', 'Réglages'),
    html: `<h1 class="page-title">${t('Settings', 'Réglages')}</h1>
      <p class="muted">${t('Signed in as', 'Connecté en tant que')} <a href="${L('/users/' + acct.id)}"><b>${esc(acct.username)}</b></a> · ${esc(acct.email)}</p>

      <section class="panel" id="images"><h2>${t('Avatar and cover', 'Avatar et bannière')}</h2>
        <div class="images">
          <div><img id="avatar-img" class="avatar" src="${esc(acct.avatar_url)}" width="120" height="120" alt="${t('Current avatar', 'Avatar actuel')}">
            <label class="btn ghost file">${t('Change avatar', 'Changer l’avatar')}<input type="file" id="avatar-file" accept="image/png,image/jpeg,image/gif,image/webp"></label></div>
          <div class="grow"><img id="cover-img" class="cover-preview" src="${esc(acct.cover_url)}" alt="${t('Current cover', 'Bannière actuelle')}">
            <label class="btn ghost file">${t('Change cover', 'Changer la bannière')}<input type="file" id="cover-file" accept="image/png,image/jpeg,image/gif,image/webp"></label>
            <button type="button" class="btn ghost" id="cover-reset">${t('Use the default cover', 'Remettre la bannière par défaut')}</button></div>
        </div>
        <p class="muted small">${t('PNG, JPEG, GIF or WebP. The game shows the new image after its next restart.', 'PNG, JPEG, GIF ou WebP. Le jeu affiche la nouvelle image après son prochain redémarrage.')}</p>
        <span class="msg" id="img-msg" role="status"></span>
      </section>

      <form class="panel form" id="profile"><h2>${t('Profile', 'Profil')}</h2>
        <div class="form-grid">
          <label>${t('Country', 'Pays')}<select name="country"><option value="XX">${t('Not shown', 'Non affiché')}</option>${countries.map(([c, name]) => `<option value="${c}"${c === acct.country ? ' selected' : ''}>${esc(name)}</option>`).join('')}</select></label>
          <label>${t('Default game mode', 'Mode de jeu par défaut')}<select name="playmode">${MODES.map((m) => `<option value="${m}"${m === acct.playmode ? ' selected' : ''}>${modeName(m)}</option>`).join('')}</select></label>
          ${field('location', t('Location', 'Lieu'))}${field('interests', t('Interests', 'Centres d’intérêt'))}${field('occupation', t('Occupation', 'Occupation'))}
          ${field('website', t('Website', 'Site web'))}${field('discord', 'Discord')}${field('twitter', 'Twitter')}
          <label>${t('Profile colour', 'Couleur du profil')}
            <span class="hue"><input type="range" name="hue" min="0" max="360" value="${pr.profile_hue ?? 330}"><span class="hue-swatch" id="hue-swatch"></span>
            <label class="check"><input type="checkbox" name="hue_default"${pr.profile_hue == null ? ' checked' : ''}> ${t('default', 'par défaut')}</label></span></label>
        </div>
        <fieldset><legend>${t('I play with', 'Je joue avec')}</legend>${styles.map(([v, label]) => `<label class="check"><input type="checkbox" name="playstyle" value="${v}"${(pr.playstyle || []).includes(v) ? ' checked' : ''}> ${esc(label)}</label>`).join('')}</fieldset>
        <label class="check"><input type="checkbox" name="pm_friends_only"${pr.pm_friends_only ? ' checked' : ''}> ${t('Only my friends can send me private messages', 'Seuls mes amis peuvent m’envoyer des messages privés')}</label>
        <label>${t('About me (shown on my profile)', 'À propos de moi (affiché sur mon profil)')}
          <textarea name="page" rows="8" maxlength="60000" placeholder="${t('# Title, **bold**, *italic*, > quote, - list, links and [img]https://…[/img]', '# Titre, **gras**, *italique*, > citation, - liste, liens et [img]https://…[/img]')}">${esc(pr.page || '')}</textarea></label>
        <details class="preview"><summary>${t('Preview', 'Aperçu')}</summary><div class="user-text" id="page-preview"></div></details>
        <div><button class="btn" type="submit">${t('Save profile', 'Enregistrer le profil')}</button> <span class="msg" role="status"></span></div>
      </form>

      <section class="panel" id="team"><h2>${t('Team', 'Équipe')}</h2><div id="team-body"><span class="muted">${t('Loading…', 'Chargement…')}</span></div><span class="msg" id="team-msg" role="status"></span></section>

      <form class="panel form" id="password"><h2>${t('Password', 'Mot de passe')}</h2>
        <div class="form-grid">
          <label>${t('Current password', 'Mot de passe actuel')}<input name="current" type="password" autocomplete="current-password" required></label>
          <label>${t('New password', 'Nouveau mot de passe')}<input name="next" type="password" autocomplete="new-password" required minlength="8"><small>${t('At least 8 characters', 'Au moins 8 caractères')}</small></label>
        </div>
        <div><button class="btn" type="submit">${t('Change password', 'Changer le mot de passe')}</button> <span class="msg" role="status"></span></div>
      </form>

      <section class="panel"><h2>${t('Session', 'Session')}</h2>
        <p class="muted">${t('Signing out here does not sign you out of the game.', 'Se déconnecter ici ne te déconnecte pas du jeu.')}</p>
        <button type="button" class="btn ghost" id="logout">${t('Sign out of this site', 'Se déconnecter de ce site')}</button></section>`,

    mount(view) {
      // ---- images
      const imgMsg = view.querySelector('#img-msg');
      const bust = (url) => url.split('?')[0] + '?t=' + Date.now();
      const upload = (inputId, kind, imgId, okText) => view.querySelector(inputId).addEventListener('change', async (e) => {
        const file = e.target.files[0];
        if (!file) return;
        if (await act(null, imgMsg, () => api('/api/xc/account/' + kind, { method: 'POST', file }), okText)) {
          view.querySelector(imgId).src = bust(kind === 'avatar' ? acct.avatar_url : `/api/xc/cover/${acct.id}`);
          await loadMe(); refreshShell();
        }
        e.target.value = '';
      });
      upload('#avatar-file', 'avatar', '#avatar-img', t('Avatar updated.', 'Avatar mis à jour.'));
      upload('#cover-file', 'cover', '#cover-img', t('Cover updated.', 'Bannière mise à jour.'));
      const reset = view.querySelector('#cover-reset');
      reset.addEventListener('click', async () => {
        if (await act(reset, imgMsg, () => api('/api/xc/account/cover', { method: 'DELETE' }), t('Default cover restored.', 'Bannière par défaut rétablie.'))) view.querySelector('#cover-img').src = bust('/api/xc/cover/default');
      });

      // ---- profile
      const form = view.querySelector('#profile');
      const swatch = () => { view.querySelector('#hue-swatch').style.background = form.hue_default.checked ? 'var(--pink)' : `hsl(${form.hue.value} 90% 68%)`; form.hue.disabled = form.hue_default.checked; };
      form.hue.addEventListener('input', swatch); form.hue_default.addEventListener('change', swatch); swatch();
      const preview = () => { view.querySelector('#page-preview').innerHTML = renderUserText(form.page.value) || `<p class="muted">${t('Nothing to preview.', 'Rien à afficher.')}</p>`; };
      form.page.addEventListener('input', preview); preview();
      form.addEventListener('submit', async (e) => {
        e.preventDefault();
        const body = {
          country: form.country.value, playmode: form.playmode.value, pm_friends_only: form.pm_friends_only.checked,
          profile_hue: form.hue_default.checked ? null : Number(form.hue.value),
          playstyle: [...form.querySelectorAll('[name=playstyle]:checked')].map((c) => c.value), page: form.page.value,
        };
        for (const k of ['location', 'interests', 'occupation', 'website', 'discord', 'twitter']) body[k] = form[k].value;
        if (await act(form.querySelector('[type=submit]'), form.querySelector(':scope > div .msg'), () => api('/api/xc/account', { method: 'PUT', json: body }), t('Profile saved.', 'Profil enregistré.'))) { await loadMe(); refreshShell(); }
      });

      // ---- password
      const pw = view.querySelector('#password');
      pw.addEventListener('submit', async (e) => {
        e.preventDefault();
        if (await act(pw.querySelector('button'), pw.querySelector('.msg'), () => api('/api/xc/account/password', { method: 'POST', json: { current_password: pw.current.value, new_password: pw.next.value } }), t('Password changed.', 'Mot de passe changé.'))) pw.reset();
      });

      view.querySelector('#logout').addEventListener('click', async () => { await signOut(); refreshShell(); go('/'); });

      // ---- team
      const body = view.querySelector('#team-body'), teamMsg = view.querySelector('#team-msg');
      const loadTeam = async () => {
        const fresh = await api('/api/xc/account');
        const mine = fresh.team_id ? await api('/api/xc/teams/' + fresh.team_id) : null;
        if (!mine?.id) {
          const all = await api('/api/xc/teams');
          body.innerHTML = `<p class="muted">${t('You are not in a team.', 'Tu n’es dans aucune équipe.')}</p>
            <form class="form form-grid" id="team-create">
              <label>${t('Team name', 'Nom de l’équipe')}<input name="name" required minlength="2" maxlength="30"></label>
              <label>${t('Tag (2–4 letters or digits)', 'Tag (2 à 4 lettres ou chiffres)')}<input name="short_name" required pattern="[A-Za-z0-9]{2,4}" maxlength="4"></label>
              <div><button class="btn" type="submit">${t('Create team', 'Créer l’équipe')}</button></div>
            </form>
            ${all.length ? `<form class="form form-grid" id="team-join"><label>${t('Or join an existing team', 'Ou rejoindre une équipe existante')}<select name="id">${all.map((tm) => `<option value="${tm.id}">[${esc(tm.short_name)}] ${esc(tm.name)} (${num(tm.members_count)})</option>`).join('')}</select></label><div><button class="btn ghost" type="submit">${t('Join', 'Rejoindre')}</button></div></form>` : ''}`;
          const create = body.querySelector('#team-create');
          create.addEventListener('submit', async (e) => { e.preventDefault(); if (await act(create.querySelector('button'), teamMsg, () => api('/api/xc/teams', { method: 'POST', json: { name: create.name.value, short_name: create.short_name.value } }), t('Team created.', 'Équipe créée.'))) loadTeam(); });
          const join = body.querySelector('#team-join');
          join?.addEventListener('submit', async (e) => { e.preventDefault(); if (await act(join.querySelector('button'), teamMsg, () => api(`/api/xc/teams/${join.id.value}/join`, { method: 'POST' }), t('Team joined.', 'Équipe rejointe.'))) loadTeam(); });
          return;
        }
        const leader = mine.leader_id === fresh.id;
        body.innerHTML = `<p><a href="${L('/teams/' + mine.id)}"><b>${esc(mine.name)}</b></a> <span class="team-tag">${esc(mine.short_name)}</span>${leader ? ` <span class="muted">· ${t('you lead this team', 'tu diriges cette équipe')}</span>` : ''}</p>
          ${leader ? `<form class="form form-grid" id="team-edit">
              <label>${t('Team name', 'Nom de l’équipe')}<input name="name" required minlength="2" maxlength="30" value="${esc(mine.name)}"></label>
              <label>${t('Tag (2–4 letters or digits)', 'Tag (2 à 4 lettres ou chiffres)')}<input name="short_name" required pattern="[A-Za-z0-9]{2,4}" maxlength="4" value="${esc(mine.short_name)}"></label>
              <div><button class="btn" type="submit">${t('Save team', 'Enregistrer l’équipe')}</button> <label class="btn ghost file">${t('Change flag (max 2 MB)', 'Changer le drapeau (2 Mo max)')}<input type="file" id="team-flag" accept="image/png,image/jpeg,image/gif,image/webp"></label></div>
            </form>` : ''}
          <ul class="list">${(mine.members || []).map((m) => `<li class="score" data-id="${m.id}"><div class="score-main">${userLink(m)}</div>${m.id === mine.leader_id ? `<span class="status s-loved">${t('leader', 'chef')}</span>` : leader ? `<button type="button" class="btn ghost" data-kick>${t('Remove', 'Retirer')}</button>` : ''}</li>`).join('')}</ul>
          <button type="button" class="btn ghost danger" id="team-leave">${leader && (mine.members || []).length > 1 ? t('Leave (leadership passes to another member)', 'Quitter (un autre membre devient chef)') : leader ? t('Leave and delete the team', 'Quitter et supprimer l’équipe') : t('Leave team', 'Quitter l’équipe')}</button>`;
        const edit = body.querySelector('#team-edit');
        edit?.addEventListener('submit', async (e) => { e.preventDefault(); if (await act(edit.querySelector('button'), teamMsg, () => api('/api/xc/teams/' + mine.id, { method: 'PUT', json: { name: edit.name.value, short_name: edit.short_name.value } }), t('Team saved.', 'Équipe enregistrée.'))) loadTeam(); });
        body.querySelector('#team-flag')?.addEventListener('change', async (e) => { const file = e.target.files[0]; if (file) await act(null, teamMsg, () => api(`/api/xc/teams/${mine.id}/flag`, { method: 'POST', file }), t('Flag updated.', 'Drapeau mis à jour.')); e.target.value = ''; });
        body.querySelectorAll('[data-kick]').forEach((btn) => btn.addEventListener('click', async () => {
          const li = btn.closest('li');
          if (confirm(t('Remove this member from the team?', 'Retirer ce membre de l’équipe ?')) && await act(btn, teamMsg, () => api(`/api/xc/teams/${mine.id}/members/${li.dataset.id}`, { method: 'DELETE' }))) loadTeam();
        }));
        const leave = body.querySelector('#team-leave');
        leave.addEventListener('click', async () => {
          if (confirm(t('Leave this team?', 'Quitter cette équipe ?')) && await act(leave, teamMsg, () => api('/api/xc/teams/leave', { method: 'POST' }), t('You left the team.', 'Tu as quitté l’équipe.'))) { await loadTeam(); await loadMe(); refreshShell(); }
        });
      };
      loadTeam().catch((e) => { body.innerHTML = `<span class="bad">${esc(e.message)}</span>`; });
      if (hash) view.querySelector('#' + CSS.escape(hash))?.scrollIntoView();
    },
  };
}
