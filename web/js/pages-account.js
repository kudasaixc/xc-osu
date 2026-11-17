// xc-osu! web: sign in, registration, account settings. xc softworks (kudasaixc)
import { L, state, t, esc, num, countryName, MODES, modeName, api, signIn, signOut, loadMe, signedIn, requireSignIn, renderUserText, act, userLink, avatarUrl, ApiError, download, verifyCode, dateTimeText } from './core.js';

/** Only follow "next" back to a page of this site. */
const nextPath = (query) => { const n = query.get('next') || ''; return n.startsWith('/') && !n.startsWith('//') && !n.startsWith('/login') ? n : '/'; };

export async function login({ query, go, refreshShell }) {
  if (signedIn() && state.me) { go(nextPath(query), { replace: true }); return null; }
  if (signedIn() && state.needsCode) return codeForm({ query, go, refreshShell });
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
        if (await act(form.querySelector('button'), form.querySelector('.msg'), () => signIn(form.username.value.trim(), form.password.value))) {
          refreshShell();
          if (state.needsCode) go('/login' + (query.get('next') ? '?next=' + encodeURIComponent(query.get('next')) : ''), { replace: true });
          else go(nextPath(query), { replace: true });
        }
      });
    },
  };
}

/** Second step of signing in to an account with two-factor authentication. */
function codeForm({ query, go, refreshShell }) {
  return {
    title: t('Two-factor authentication', 'Double authentification'),
    html: `<section class="panel narrow"><h1>${t('Enter your code', 'Entre ton code')}</h1>
      <p class="muted">${t('Open your authenticator app and enter the 6-digit code shown for xc-osu!.', 'Ouvre ton application d’authentification et entre le code à 6 chiffres affiché pour xc-osu!.')}</p>
      <form id="code" class="form">
        <label>${t('Code', 'Code')}<input name="code" inputmode="numeric" autocomplete="one-time-code" pattern="[0-9 ]{6,7}" maxlength="7" required autofocus></label>
        <button class="btn" type="submit">${t('Verify', 'Vérifier')}</button> <span class="msg" role="alert"></span>
      </form>
      <p class="muted small"><button type="button" class="link" id="cancel">${t('Use another account', 'Utiliser un autre compte')}</button></p></section>`,
    mount(view) {
      const form = view.querySelector('#code');
      form.addEventListener('submit', async (e) => {
        e.preventDefault();
        if (await act(form.querySelector('button'), form.querySelector('.msg'), () => verifyCode(form.code.value.replace(/\s/g, '')))) { refreshShell(); go(nextPath(query), { replace: true }); }
      });
      view.querySelector('#cancel').addEventListener('click', async () => { await signOut(); refreshShell(); go('/login', { replace: true }); });
    },
  };
}

/** The link of the confirmation email lands here (#token=…): the token is posted, never sent in an address. */
export async function verifyEmail({ hash }) {
  const token = new URLSearchParams((hash || '').replace(/^#/, '')).get('token');
  return {
    title: t('Confirm your email address', 'Confirme ton adresse e-mail'),
    html: `<section class="panel narrow"><h1>${t('Confirm your email address', 'Confirme ton adresse e-mail')}</h1>
      ${token ? `<p>${t('Press the button to confirm this address for your xc-osu! account.', 'Appuie sur le bouton pour confirmer cette adresse pour ton compte xc-osu!.')}</p>
      <div><button class="btn" id="confirm">${t('Confirm', 'Confirmer')}</button> <span class="msg" role="status"></span></div>`
      : `<p>${t('This link is incomplete. Request a new one from your account settings.', 'Ce lien est incomplet. Redemande-en un depuis tes réglages.')}</p>`}
      <p><a href="${L('/home/account/edit')}">${t('Account settings', 'Réglages du compte')}</a></p></section>`,
    mount(view) {
      if (token) history.replaceState(null, '', location.pathname + location.search);
      const button = view.querySelector('#confirm');
      button?.addEventListener('click', async () => {
        if (await act(button, button.nextElementSibling, () => api('/api/xc/account/email/verify', { method: 'POST', json: { token } }), t('Address confirmed. Thank you!', 'Adresse confirmée. Merci !'))) button.disabled = true;
      });
    },
  };
}

const birthYearField = () => `<label>${t('Year of birth', 'Année de naissance')}<input name="birth_year" type="number" inputmode="numeric" min="${new Date().getFullYear() - 120}" max="${new Date().getFullYear()}" required>
  <small>${t('Only the year is kept. xc-osu! is for players aged 13 or over; under 16, private messages are limited to friends.', 'Seule l’année est gardée. xc-osu! est réservé aux joueurs de 13 ans ou plus ; avant 16 ans, les messages privés sont limités aux amis.')}</small></label>`;

/** Loads Cloudflare Turnstile once and renders the anti-robot check in `el`; resolves to a function giving the current token. */
let turnstileScript = null;
function turnstile(el, sitekey) {
  turnstileScript ||= new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
    script.async = true;
    script.onload = () => resolve(window.turnstile);
    script.onerror = () => reject(new Error(t('The anti-robot check could not load. Disable your content blocker for this site and try again.', 'La vérification anti-robot n’a pas pu se charger. Désactive ton bloqueur pour ce site et réessaie.')));
    document.head.append(script);
  });
  let token = '';
  return turnstileScript.then((ts) => {
    const id = ts.render(el, { sitekey, theme: 'dark', language: state.lang, callback: (v) => { token = v; }, 'expired-callback': () => { token = ''; }, 'error-callback': () => { token = ''; } });
    return { token: () => token, reset: () => { token = ''; ts.reset(id); } };
  });
}

export async function register({ query, go, refreshShell }) {
  if (signedIn() && state.me) { go('/', { replace: true }); return null; }
  const config = await api('/api/xc/registration').catch(() => ({}));
  return {
    title: t('Create an account', 'Créer un compte'),
    html: `<section class="panel narrow"><h1>${t('Create an account', 'Créer un compte')}</h1>
      <p class="muted">${t('One account for the game and this site.', 'Un seul compte pour le jeu et ce site.')}</p>
      <form id="register" class="form">
        <label>${t('Username', 'Nom d’utilisateur')}<input name="username" autocomplete="username" required minlength="3" maxlength="15"><small>${t('3 to 15 characters: letters, digits, spaces, - _ [ ]', '3 à 15 caractères : lettres, chiffres, espaces, - _ [ ]')}</small></label>
        <label>E-mail<input name="email" type="email" autocomplete="email" required></label>
        <label>${t('Password', 'Mot de passe')}<input name="password" type="password" autocomplete="new-password" required minlength="8" maxlength="256"><small>${t('8 to 256 characters. Passwords known from data breaches are refused.', '8 à 256 caractères. Les mots de passe connus de fuites de données sont refusés.')}</small></label>
        ${birthYearField()}
        ${config.turnstile_site_key ? '<div id="turnstile" class="turnstile"></div>' : ''}
        <p class="muted small">${t('By creating an account you accept the', 'En créant un compte, tu acceptes les')} <a href="${L('/wiki/Legal')}">${t('terms', 'conditions')}</a> ${t('and the', 'et la')} <a href="${L('/wiki/Privacy')}">${t('privacy policy', 'politique de confidentialité')}</a>.</p>
        <button class="btn" type="submit">${t('Create my account', 'Créer mon compte')}</button> <span class="msg" role="alert"></span>
      </form>
      <p class="muted small"><a href="${L('/login')}">${t('I already have an account', 'J’ai déjà un compte')}</a></p></section>`,
    mount(view) {
      const form = view.querySelector('#register');
      const check = config.turnstile_site_key ? turnstile(view.querySelector('#turnstile'), config.turnstile_site_key) : null;
      check?.catch((e) => { const msg = form.querySelector('.msg'); msg.textContent = e.message; msg.className = 'msg bad'; });
      form.addEventListener('submit', async (e) => {
        e.preventDefault();
        const year = Number(form.birth_year.value);
        if (new Date().getFullYear() - year < 13) { const msg = form.querySelector('.msg'); msg.textContent = t('xc-osu! accounts are for players aged 13 or over.', 'Les comptes xc-osu! sont réservés aux joueurs de 13 ans ou plus.'); msg.className = 'msg bad'; return; }
        let pending = false;
        const widget = check ? await check.catch(() => null) : null;
        const ok = await act(form.querySelector('button'), form.querySelector('.msg'), async () => {
          if (check && !widget?.token()) throw new ApiError(422, t('Please complete the anti-robot check.', 'Complète la vérification anti-robot.'));
          const fields = { 'user[username]': form.username.value.trim(), 'user[user_email]': form.email.value.trim(), 'user[password]': form.password.value, 'user[birth_year]': String(year) };
          if (widget) fields['cf-turnstile-response'] = widget.token();
          const r = await fetch('/users', { method: 'POST', body: new URLSearchParams(fields) });
          widget?.reset(); // a token is single-use
          const data = await r.json().catch(() => ({}));
          const errors = data?.form_error?.user;
          if (errors) throw new ApiError(422, Object.values(errors).flat().join(' '));
          if (!r.ok) throw new ApiError(r.status, data.error || data.message || t('Registration failed. Try again in a moment.', 'Inscription impossible. Réessaie dans un instant.'));
          // the answer is the same when the address already has an account (its owner gets an email instead), so signing in may fail here.
          try { await signIn(form.username.value.trim(), form.password.value); }
          catch { pending = true; }
        });
        if (ok && pending) {
          const msg = form.querySelector('.msg');
          msg.textContent = t('Check your inbox to continue.', 'Consulte ta boîte mail pour continuer.'); msg.className = 'msg good';
          return;
        }
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
      ${token ? `<label>${t('New password', 'Nouveau mot de passe')}<input name="password" type="password" autocomplete="new-password" minlength="8" maxlength="256" required></label>
      <label>${t('Confirm password', 'Confirme le mot de passe')}<input name="confirmation" type="password" autocomplete="new-password" minlength="8" maxlength="256" required></label>` : `<label>E-mail<input name="email" type="email" autocomplete="email" maxlength="254" required></label>`}
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


export async function settings({ go, refreshShell, hash }) {
  requireSignIn();
  const acct = await api('/api/xc/account');
  const pr = acct.profile || {};
  const field = (key, label, max = 100) => `<label>${esc(label)}<input name="${key}" value="${esc(pr[key] || '')}" maxlength="${max}"></label>`;
  const styles = [['mouse', t('Mouse', 'Souris')], ['keyboard', t('Keyboard', 'Clavier')], ['tablet', t('Tablet', 'Tablette')], ['touch', t('Touch screen', 'Écran tactile')]];

  return {
    title: t('Settings', 'Réglages'),
    html: `<h1 class="page-title">${t('Settings', 'Réglages')}</h1>
      <p class="muted">${t('Signed in as', 'Connecté en tant que')} <a href="${L('/users/' + acct.id)}"><b>${esc(acct.username)}</b></a> · ${esc(acct.email)}</p>

      ${acct.email_verified ? '' : `<section class="panel" id="email-confirm"><h2>${t('Confirm your email address', 'Confirme ton adresse e-mail')}</h2>
        <p class="muted">${acct.email_confirmation_required
          ? t('You can already play. Chat, comments, reports, your avatar, cover and me! page, teams and hosting multiplayer rooms unlock once your address is confirmed. Open the link we sent you; it is valid for 30 minutes.', 'Tu peux déjà jouer. Le chat, les commentaires, les signalements, ton avatar, ta bannière et ta page me!, les équipes et la création de salons multijoueur se débloquent une fois ton adresse confirmée. Ouvre le lien qu’on t’a envoyé ; il est valable 30 minutes.')
          : t('A confirmed address is what lets you recover your account if you lose your password.', 'Une adresse confirmée te permet de récupérer ton compte si tu perds ton mot de passe.')}</p>
        <div><button class="btn ghost" id="verify-email">${t('Send the confirmation email again', 'Renvoyer l’e-mail de confirmation')}</button> <span class="msg" id="verify-email-msg" role="alert"></span></div></section>`}
      ${acct.birth_year == null ? `<form class="panel form" id="birth"><h2>${t('Your age', 'Ton âge')}</h2>
        <p class="muted">${t('Chat, comments, teams and multiplayer rooms need your year of birth (asked once, it cannot be changed afterwards).', 'Le chat, les commentaires, les équipes et les salons multijoueur demandent ton année de naissance (demandée une seule fois, elle ne peut plus être changée ensuite).')}</p>
        ${birthYearField()}
        <div><button class="btn" type="submit">${t('Save', 'Enregistrer')}</button> <span class="msg" role="status"></span></div></form>` : ''}
      <section class="panel" id="images"><h2>${t('Avatar and cover', 'Avatar et bannière')}</h2>
        <div class="images">
          <div><img id="avatar-img" class="avatar" src="${esc(avatarUrl(acct))}" width="120" height="120" alt="${t('Current avatar', 'Avatar actuel')}">
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
          <label>${t('Country', 'Pays')}<input value="${esc(acct.country && acct.country !== 'XX' ? countryName(acct.country) : t('Unknown', 'Inconnu'))}" disabled title="${t('Set from your connection when the account was created.', 'Défini d’après ta connexion à la création du compte.')}"></label>
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
          <textarea name="page" rows="8" maxlength="60000" placeholder="${t('osu! BBCode: [b]bold[/b], [centre]…[/centre], [size=150]…[/size], [color=#ff66aa]…[/color], [url=https://…]link[/url], [img]https://…[/img], [box=Title]…[/box], [notice]…[/notice]', 'BBCode osu! : [b]gras[/b], [centre]…[/centre], [size=150]…[/size], [color=#ff66aa]…[/color], [url=https://…]lien[/url], [img]https://…[/img], [box=Titre]…[/box], [notice]…[/notice]')}">${esc(pr.page || '')}</textarea></label>
        <details class="preview"><summary>${t('Preview', 'Aperçu')}</summary><div class="user-text" id="page-preview"></div></details>
        <div><button class="btn" type="submit">${t('Save profile', 'Enregistrer le profil')}</button> <span class="msg" role="status"></span></div>
      </form>

      <section class="panel" id="team"><h2>${t('Team', 'Équipe')}</h2><div id="team-body"><span class="muted">${t('Loading…', 'Chargement…')}</span></div><span class="msg" id="team-msg" role="status"></span></section>

      <form class="panel form" id="password"><h2>${t('Password', 'Mot de passe')}</h2>
        <div class="form-grid">
          <label>${t('Current password', 'Mot de passe actuel')}<input name="current" type="password" autocomplete="current-password" required></label>
          <label>${t('New password', 'Nouveau mot de passe')}<input name="next" type="password" autocomplete="new-password" required minlength="8" maxlength="256"><small>${t('8 to 256 characters', '8 à 256 caractères')}</small></label>
        </div>
        <div><button class="btn" type="submit">${t('Change password', 'Changer le mot de passe')}</button> <span class="msg" role="status"></span></div>
      </form>

      <section class="panel" id="two-factor"><h2>${t('Two-factor authentication', 'Double authentification')}</h2>
        <p class="muted">${acct.two_factor
          ? t('On. Signing in (in the game or here) asks for the code of your authenticator app.', 'Activée. La connexion (dans le jeu ou ici) demande le code de ton application d’authentification.')
          : t('Protect your account with a code from an authenticator app (Aegis, Google Authenticator, 1Password…) in addition to your password.', 'Protège ton compte avec un code d’application d’authentification (Aegis, Google Authenticator, 1Password…) en plus du mot de passe.')}</p>
        <form class="form form-grid" id="tfa">
          <label>${t('Password', 'Mot de passe')}<input name="password" type="password" autocomplete="current-password" required></label>
          ${acct.two_factor ? `<label>${t('Current code', 'Code actuel')}<input name="code" inputmode="numeric" autocomplete="one-time-code" maxlength="7" required></label>` : ''}
          <div><button class="btn${acct.two_factor ? ' ghost danger' : ''}" type="submit">${acct.two_factor ? t('Turn off', 'Désactiver') : t('Set up', 'Configurer')}</button> <span class="msg" role="status"></span></div>
        </form>
        <div id="tfa-setup" hidden>
          <p>${t('Add this key to your authenticator app (or open the link on your phone), then enter the code it shows.', 'Ajoute cette clé dans ton application d’authentification (ou ouvre le lien sur ton téléphone), puis entre le code affiché.')}</p>
          <p><code id="tfa-secret"></code> · <a id="tfa-link" href="#">${t('open in the app', 'ouvrir dans l’application')}</a></p>
          <form class="form form-grid" id="tfa-enable"><label>${t('Code', 'Code')}<input name="code" inputmode="numeric" autocomplete="one-time-code" maxlength="7" required></label>
            <div><button class="btn" type="submit">${t('Turn on', 'Activer')}</button> <span class="msg" role="status"></span></div></form>
        </div>
      </section>

      <section class="panel" id="sessions"><h2>${t('Signed-in devices', 'Appareils connectés')}</h2>
        <p class="muted">${t('Each place where your account is signed in (the game, this site, another browser). End any you do not recognise, then change your password.', 'Chaque endroit où ton compte est connecté (le jeu, ce site, un autre navigateur). Ferme ceux que tu ne reconnais pas, puis change ton mot de passe.')}</p>
        <ul class="list" id="session-list"><li class="muted">${t('Loading…', 'Chargement…')}</li></ul>
        <div><button type="button" class="btn ghost" id="revoke-others">${t('Sign out everywhere else', 'Déconnecter partout ailleurs')}</button> <button type="button" class="btn ghost" id="logout">${t('Sign out of this site', 'Se déconnecter de ce site')}</button> <span class="msg" id="session-msg" role="status"></span></div></section>

      <section class="panel" id="telemetry"><h2>${t('Diagnostics', 'Diagnostics')}</h2>
        <p class="muted">${t('The game can send performance measurements (frame times, loading times, errors, graphics card model) to help fix stutters. They are not linked to your account and are deleted after 45 days. Same switch as in the launcher options.', 'Le jeu peut envoyer des mesures de performance (temps d’image, temps de chargement, erreurs, modèle de carte graphique) pour corriger les saccades. Elles ne sont pas liées à ton compte et sont supprimées après 45 jours. Même réglage que dans les options du launcher.')}</p>
        <label class="check"><input type="checkbox" id="telemetry-on"> ${t('Send performance reports from this browser', 'Envoyer les rapports de performance depuis ce navigateur')}</label></section>

      <section class="panel" id="data"><h2>${t('Your data', 'Tes données')}</h2>
        <p class="muted">${t('Download everything xc-osu! keeps about your account (profile, scores, comments, messages…) as a JSON file.', 'Télécharge tout ce que xc-osu! garde sur ton compte (profil, scores, commentaires, messages…) dans un fichier JSON.')} <a href="${L('/wiki/Privacy')}">${t('Privacy', 'Confidentialité')}</a></p>
        <div><button type="button" class="btn ghost" id="export">${t('Download my data', 'Télécharger mes données')}</button> <span class="msg" role="status"></span></div>
        <form class="form" id="delete-account" style="margin-top:18px">
          <h3>${t('Delete my account', 'Supprimer mon compte')}</h3>
          <p class="muted">${t('This erases your account, scores, replays, screenshots, friends, private messages and messages sent, right away and for good. Comments that received replies stay as “deleted”. It cannot be undone.', 'Ceci efface ton compte, tes scores, replays, captures, amis, messages privés et messages envoyés, tout de suite et pour de bon. Les commentaires qui ont reçu des réponses restent sous la forme « supprimé ». C’est irréversible.')}</p>
          <div class="form-grid">
            <label>${t('Password', 'Mot de passe')}<input name="password" type="password" autocomplete="current-password" required></label>
            <label>${t('Type your username to confirm', 'Tape ton pseudo pour confirmer')}<input name="confirm" autocomplete="off" required></label>
          </div>
          <div><button class="btn ghost danger" type="submit">${t('Delete my account for good', 'Supprimer mon compte définitivement')}</button> <span class="msg" role="status"></span></div>
        </form></section>`,

    mount(view) {
      // ---- year of birth
      const birth = view.querySelector('#birth');
      birth?.addEventListener('submit', async (e) => {
        e.preventDefault();
        if (await act(birth.querySelector('button'), birth.querySelector('.msg'), () => api('/api/xc/account', { method: 'PUT', json: { birth_year: Number(birth.birth_year.value) } }), t('Saved.', 'Enregistré.'))) birth.querySelector('button').disabled = true;
      });

      // ---- two-factor authentication
      const tfa = view.querySelector('#tfa'), setup = view.querySelector('#tfa-setup');
      tfa.addEventListener('submit', async (e) => {
        e.preventDefault();
        if (acct.two_factor) {
          if (await act(tfa.querySelector('button'), tfa.querySelector('.msg'), () => api('/api/xc/account/2fa/disable', { method: 'POST', json: { password: tfa.password.value, code: tfa.code.value } }), t('Two-factor authentication is off.', 'Double authentification désactivée.'))) tfa.querySelector('button').disabled = true;
          return;
        }
        let data;
        if (await act(tfa.querySelector('button'), tfa.querySelector('.msg'), async () => { data = await api('/api/xc/account/2fa/setup', { method: 'POST', json: { password: tfa.password.value } }); })) {
          view.querySelector('#tfa-secret').textContent = data.secret.replace(/(.{4})/g, '$1 ').trim();
          view.querySelector('#tfa-link').href = data.uri;
          setup.hidden = false; tfa.hidden = true;
        }
      });
      const enable = view.querySelector('#tfa-enable');
      enable.addEventListener('submit', async (e) => {
        e.preventDefault();
        if (await act(enable.querySelector('button'), enable.querySelector('.msg'), () => api('/api/xc/account/2fa/enable', { method: 'POST', json: { code: enable.code.value } }), t('Two-factor authentication is on. Your other devices were signed out.', 'Double authentification activée. Tes autres appareils ont été déconnectés.'))) enable.querySelector('button').disabled = true;
      });

      // ---- sessions
      const sessionList = view.querySelector('#session-list'), sessionMsg = view.querySelector('#session-msg');
      const loadSessions = async () => {
        const list = await api('/api/xc/account/sessions');
        sessionList.innerHTML = list.map((s) => `<li class="score" data-session="${esc(s.id)}"><div class="score-main"><b>${s.current ? t('This browser', 'Ce navigateur') : t('Signed-in device', 'Appareil connecté')}</b>
          <span class="score-meta">${s.created_at ? t('since', 'depuis le') + ' ' + esc(dateTimeText(s.created_at)) : ''}${s.verified ? '' : ' · ' + t('waiting for its code', 'en attente du code')}</span></div>
          ${s.current ? '' : `<button type="button" class="btn ghost" data-end>${t('Sign out', 'Déconnecter')}</button>`}</li>`).join('') || `<li class="muted">${t('No other session.', 'Aucune autre session.')}</li>`;
      };
      sessionList.addEventListener('click', async (e) => {
        const li = e.target.closest('[data-end]')?.closest('li');
        if (li && await act(e.target, sessionMsg, () => api('/api/xc/account/sessions/' + encodeURIComponent(li.dataset.session), { method: 'DELETE' }))) loadSessions();
      });
      const others = view.querySelector('#revoke-others');
      others.addEventListener('click', async () => { if (await act(others, sessionMsg, () => api('/api/xc/account/sessions/revoke-others', { method: 'POST' }), t('Your other devices were signed out.', 'Tes autres appareils ont été déconnectés.'))) loadSessions(); });
      loadSessions().catch((e) => { sessionList.innerHTML = `<li class="bad">${esc(e.message)}</li>`; });

      // ---- telemetry (read by the game page of this same site: js/xc-telemetry.js)
      // the same switch as the launcher's "diagnostics" option (stored by the launcher in xc.launcher.v1).
      const tm = view.querySelector('#telemetry-on');
      const launcher = () => { try { return JSON.parse(localStorage.getItem('xc.launcher.v1') || '{}') || {}; } catch { return {}; } };
      tm.checked = launcher().telemetry !== false;
      tm.addEventListener('change', () => { try { const o = launcher(); o.telemetry = tm.checked; localStorage.setItem('xc.launcher.v1', JSON.stringify(o)); } catch { } });

      // ---- images
      const imgMsg = view.querySelector('#img-msg');
      const bust = (url) => url.split('?')[0] + '?t=' + Date.now();
      const upload = (inputId, kind, imgId, okText) => view.querySelector(inputId).addEventListener('change', async (e) => {
        const file = e.target.files[0];
        if (!file) return;
        if (await act(null, imgMsg, () => api('/api/xc/account/' + kind, { method: 'POST', file }), okText)) {
          view.querySelector(imgId).src = bust(kind === 'avatar' ? avatarUrl(acct) : `/api/xc/cover/${acct.id}`);
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

      view.querySelector('#verify-email')?.addEventListener('click', async (e) => {
        await act(e.currentTarget, view.querySelector('#verify-email-msg'),
          () => api('/api/xc/account/email/request-verification', { method: 'POST' }),
          t('Confirmation email requested.', 'Email de confirmation demandé.'));
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
          playmode: form.playmode.value, pm_friends_only: form.pm_friends_only.checked,
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
        if (await act(pw.querySelector('button'), pw.querySelector('.msg'), () => api('/api/xc/account/password', { method: 'POST', json: { current_password: pw.current.value, new_password: pw.next.value } }), t('Password changed.', 'Mot de passe changé.'))) { await signOut(); location.assign(L("/login")); }
      });

      view.querySelector('#logout').addEventListener('click', async () => { await signOut(); refreshShell(); go('/'); });

      // ---- data export and account deletion
      const exp = view.querySelector('#export');
      exp.addEventListener('click', () => act(exp, exp.nextElementSibling, () => download('/api/xc/account/export', `xc-osu-account-${state.me.id}.json`)));
      const del = view.querySelector('#delete-account');
      del.addEventListener('submit', async (e) => {
        e.preventDefault();
        if (!confirm(t('Delete your account for good?', 'Supprimer ton compte définitivement ?'))) return;
        if (await act(del.querySelector('button'), del.querySelector('.msg'), () => api('/api/xc/account/delete', { method: 'POST', json: { password: del.password.value, confirm: del.confirm.value.trim() } }))) {
          await signOut(); refreshShell(); go('/');
        }
      });

      // ---- team
      const body = view.querySelector('#team-body'), teamMsg = view.querySelector('#team-msg');
      const loadTeam = async () => {
        const fresh = await api('/api/xc/account');
        const mine = fresh.team_id ? await api('/api/xc/teams/' + fresh.team_id) : null;
        if (!mine?.id) {
          body.innerHTML = `<p class="muted">${t('You are not in a team.', 'Tu n’es dans aucune équipe.')}</p>
            <form class="form form-grid" id="team-create">
              <label>${t('Team name', 'Nom de l’équipe')}<input name="name" required minlength="2" maxlength="30"></label>
              <label>${t('Tag (2–4 letters or digits)', 'Tag (2 à 4 lettres ou chiffres)')}<input name="short_name" required pattern="[A-Za-z0-9]{2,4}" maxlength="4"></label>
              <div><button class="btn" type="submit">${t('Create team', 'Créer l’équipe')}</button></div>
            </form>
            <div class="form" id="team-join">
              <label>${t('Or join an existing team', 'Ou rejoindre une équipe existante')}<input type="search" id="team-search" maxlength="30" autocomplete="off" placeholder="${t('Search by name or tag', 'Chercher par nom ou tag')}"></label>
              <ul class="list" id="team-results"></ul>
              <div><button class="btn ghost" type="button" id="team-more" hidden>${t('Show more', 'Afficher plus')}</button></div>
            </div>`;
          const create = body.querySelector('#team-create');
          create.addEventListener('submit', async (e) => { e.preventDefault(); if (await act(create.querySelector('button'), teamMsg, () => api('/api/xc/teams', { method: 'POST', json: { name: create.name.value, short_name: create.short_name.value } }), t('Team created.', 'Équipe créée.'))) loadTeam(); });
          // the list is paged server-side (50 per page): search narrows it, "show more" walks the rest.
          const results = body.querySelector('#team-results'), more = body.querySelector('#team-more'), search = body.querySelector('#team-search');
          let page = 1, run = 0;
          const fill = async (reset) => {
            const mineRun = ++run;
            if (reset) page = 1;
            const list = await api(`/api/xc/teams?page=${page}&q=${encodeURIComponent(search.value.trim())}`).catch(() => []);
            if (mineRun !== run) return;
            const rows = list.map((tm) => `<li class="score"><div class="score-main"><a class="score-map" href="${L('/teams/' + tm.id)}"><b>${esc(tm.name)}</b> <span class="team-tag">${esc(tm.short_name)}</span></a><span class="score-meta">${num(tm.members_count)} ${t('members', 'membres')}</span></div><button class="btn ghost" type="button" data-join="${tm.id}">${t('Join', 'Rejoindre')}</button></li>`).join('');
            if (reset) results.innerHTML = rows || `<li class="muted">${search.value.trim() ? t('No team matches.', 'Aucune équipe ne correspond.') : t('No team exists yet.', 'Aucune équipe pour l’instant.')}</li>`;
            else results.insertAdjacentHTML('beforeend', rows);
            more.hidden = list.length < 50;
          };
          let timer;
          search.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => fill(true), 250); });
          more.addEventListener('click', () => { page++; fill(false); });
          results.addEventListener('click', async (e) => {
            const btn = e.target.closest('[data-join]');
            if (btn && await act(btn, teamMsg, () => api(`/api/xc/teams/${btn.dataset.join}/join`, { method: 'POST' }), t('Team joined.', 'Équipe rejointe.'))) loadTeam();
          });
          await fill(true);
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
