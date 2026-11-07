/* Page autonome « Demandes » — servie par le plugin Jellyfin C411 Torbox. */
(function () {
    'use strict';

    const CLIENT = 'C411 Torbox';
    const VERSION = '0.7.0';
    const OWN_AUTH_KEY = 'c411torbox_auth';
    const DEVICE_KEY = 'c411torbox_device';

    // Racine du serveur Jellyfin (gère une éventuelle « URL de base »).
    const path = window.location.pathname;
    const BASE = path.substring(0, path.toLowerCase().indexOf('/demandes/ui'));

    const STATUS_LABELS = {
        Pending: 'En attente de validation',
        Approved: 'Validée',
        AutoApproved: 'Auto-validée',
        Rejected: 'Refusée',
        SentToTorbox: 'Envoyée à Torbox',
        DownloadingOnTorbox: 'Téléchargement Torbox',
        CompletedOnTorbox: 'Terminé sur Torbox',
        Importing: 'Téléchargement vers le NAS',
        Imported: 'Rangé, arrive dans Jellyfin',
        ImportFailed: 'Échec du rangement',
        Available: 'Disponible',
        Failed: 'Échec',
        Cancelled: 'Annulée'
    };

    /** Statuts où le média peut encore arriver dans Jellyfin : l'admin peut le marquer disponible à la main. */
    const SENT_STATUSES = ['SentToTorbox', 'DownloadingOnTorbox', 'CompletedOnTorbox', 'Imported', 'ImportFailed'];

    const state = { auth: null, me: null, media: null, tab: 'search' };

    // ---------- utilitaires ----------

    const $ = (sel) => document.querySelector(sel);

    /** Crée un élément sans jamais interpréter de HTML (les titres de torrents ne sont pas fiables). */
    function el(tag, attrs, ...children) {
        const node = document.createElement(tag);
        for (const [k, v] of Object.entries(attrs || {})) {
            if (v === null || v === undefined || v === false) continue;
            if (k === 'class') node.className = v;
            else if (k.startsWith('on')) node.addEventListener(k.substring(2), v);
            else node.setAttribute(k, v === true ? '' : v);
        }
        for (const c of children.flat()) {
            if (c === null || c === undefined || c === false) continue;
            node.append(c instanceof Node ? c : document.createTextNode(String(c)));
        }
        return node;
    }

    function formatSize(bytes) {
        if (!bytes) return '—';
        const gb = bytes / 1024 / 1024 / 1024;
        return gb >= 1
            ? gb.toLocaleString('fr-FR', { maximumFractionDigits: 2 }) + ' Go'
            : (bytes / 1024 / 1024).toLocaleString('fr-FR', { maximumFractionDigits: 0 }) + ' Mo';
    }

    function formatDate(iso) {
        if (!iso) return '';
        return new Date(iso).toLocaleString('fr-FR', { dateStyle: 'short', timeStyle: 'short' });
    }

    function seedClass(n) {
        return n >= 20 ? 'seed-high' : n >= 5 ? 'seed-mid' : 'seed-low';
    }

    function safeImage(url) {
        return typeof url === 'string' && /^https:\/\//i.test(url) ? url : null;
    }

    function describe(r) {
        const pad = (n) => String(n).padStart(2, '0');
        if (r.scope === 'Movie' || r.scope === 'Book') return r.mediaTitle + (r.year ? ` (${r.year})` : '');
        if (r.scope === 'Episode') return `${r.mediaTitle} S${pad(r.season)}E${pad(r.episode)}`;
        return `${r.mediaTitle} – saison ${r.season}`;
    }

    /** L'utilisateur peut annuler sa demande tant qu'elle n'est pas partie sur Torbox. */
    function canUserCancel(r) {
        return r.status === 'Pending' || r.status === 'Failed';
    }

    /** L'admin peut annuler toute demande en cours, sauf pendant un envoi à Torbox ou un téléchargement vers le NAS. */
    function canAdminCancel(r) {
        return canUserCancel(r) || SENT_STATUSES.includes(r.status);
    }

    function canMarkAvailable(r) {
        return SENT_STATUSES.includes(r.status);
    }

    /** Libellé d'un épisode dans la liste de choix. */
    function episodeLabel(e) {
        const num = 'E' + String(e.episode).padStart(2, '0');
        return num + (e.title ? ' · ' + e.title : '') + (e.inLibrary ? ' (déjà dans Jellyfin)' : '');
    }

    /** Heure du prochain essai de rangement automatique, ou null. */
    function retryText(r, now) {
        if (r.status !== 'CompletedOnTorbox' || !r.retryImportAfter) return null;
        const at = new Date(r.retryImportAfter);
        if (at.getTime() <= (now || Date.now())) return 'Nouvel essai de rangement imminent';
        return 'Nouvel essai de rangement à ' + at.toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' });
    }

    /** Nombre de jours depuis la création, pour une demande signalée comme bloquée (sinon null). */
    function staleDays(r, now) {
        if (!r.staleNotifiedAt || !canMarkAvailable(r)) return null;
        return Math.floor(((now || Date.now()) - new Date(r.createdAt).getTime()) / 86400000);
    }

    // Tests (moteur JavaScript sans navigateur) : seules les fonctions pures sont exposées.
    if (typeof document === 'undefined') {
        globalThis.C411Ui = { STATUS_LABELS, formatSize, describe, seedClass, safeImage, canUserCancel, canAdminCancel, canMarkAvailable, episodeLabel, retryText, staleDays };
        return;
    }

    let toastTimer;
    function toast(message, isError) {
        const t = $('#toast');
        t.textContent = message;
        t.className = 'toast' + (isError ? ' error' : '');
        t.hidden = false;
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => { t.hidden = true; }, 5000);
    }

    function statusPill(r) {
        const pill = el('span', { class: 'pill st-' + r.status }, STATUS_LABELS[r.status] || r.status);
        const wrap = el('div', null, pill);
        const progress = r.status === 'DownloadingOnTorbox' ? r.torboxProgress
            : r.status === 'Importing' ? r.importProgress : null;
        if (typeof progress === 'number') {
            wrap.append(el('div', { class: 'progress', title: Math.round(progress * 100) + ' %' },
                el('div', { style: `width:${Math.round(progress * 100)}%` })));
        }
        if (r.status === 'Rejected' && r.rejectReason) wrap.append(el('div', { class: 'muted' }, 'Motif : ' + r.rejectReason));
        if (r.lastError && r.status !== 'Rejected') wrap.append(el('div', { class: 'muted' }, r.lastError));
        const retry = retryText(r);
        if (retry) wrap.append(el('div', { class: 'muted' }, retry));
        if (r.autoApprovalReason === 'Cached') wrap.append(el('div', { class: 'muted' }, 'auto : déjà en cache Torbox'));
        if (r.autoApprovalReason === 'Size') wrap.append(el('div', { class: 'muted' }, 'auto : sous le seuil'));
        return wrap;
    }

    // ---------- authentification ----------

    function deviceId() {
        let id = localStorage.getItem(DEVICE_KEY);
        if (!id) {
            id = (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random()).replace(/-/g, '');
            localStorage.setItem(DEVICE_KEY, id);
        }
        return id;
    }

    function authHeader(token) {
        let h = `MediaBrowser Client="${CLIENT}", Device="Navigateur", DeviceId="${deviceId()}", Version="${VERSION}"`;
        if (token) h += `, Token="${token}"`;
        return h;
    }

    /** Réutilise la session du client web Jellyfin (même origine) si elle existe. */
    function loadAuth() {
        try {
            const own = JSON.parse(localStorage.getItem(OWN_AUTH_KEY) || 'null');
            if (own && own.token) return own;
        } catch (e) { /* ignoré */ }
        try {
            const creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || 'null');
            const server = creds && Array.isArray(creds.Servers)
                ? creds.Servers.find((s) => s.AccessToken && s.UserId)
                : null;
            if (server) return { token: server.AccessToken, fromJellyfin: true };
        } catch (e) { /* ignoré */ }
        return null;
    }

    async function api(method, url, body) {
        const res = await fetch(BASE + '/' + url, {
            method,
            headers: Object.assign(
                { 'Authorization': authHeader(state.auth && state.auth.token), 'Accept': 'application/json' },
                body !== undefined ? { 'Content-Type': 'application/json' } : {}),
            body: body !== undefined ? JSON.stringify(body) : undefined
        });
        if (res.status === 401) {
            localStorage.removeItem(OWN_AUTH_KEY);
            state.auth = null;
            showLogin();
            throw new Error('Session expirée : reconnectez-vous.');
        }
        if (res.status === 204) return null;
        const text = await res.text();
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch (e) { /* corps non JSON */ }
        if (!res.ok) {
            throw new Error((data && (data.error || data.title)) || `Erreur HTTP ${res.status}`);
        }
        return data;
    }

    function showLogin() {
        $('#login').hidden = false;
        $('#tabs').hidden = true;
        document.querySelectorAll('.view').forEach((v) => { v.hidden = true; });
    }

    async function login(ev) {
        ev.preventDefault();
        $('#loginError').hidden = true;
        try {
            const res = await fetch(BASE + '/Users/AuthenticateByName', {
                method: 'POST',
                headers: { 'Authorization': authHeader(null), 'Content-Type': 'application/json' },
                body: JSON.stringify({ Username: $('#loginUser').value, Pw: $('#loginPw').value })
            });
            if (!res.ok) throw new Error('Identifiants incorrects.');
            const data = await res.json();
            state.auth = { token: data.AccessToken };
            localStorage.setItem(OWN_AUTH_KEY, JSON.stringify(state.auth));
            $('#loginPw').value = '';
            await start();
        } catch (e) {
            $('#loginError').textContent = e.message;
            $('#loginError').hidden = false;
        }
    }

    // ---------- navigation ----------

    function showTab(tab) {
        if (tab === 'admin' && !(state.me && state.me.isAdmin)) tab = 'search';
        state.tab = tab;
        document.querySelectorAll('.tab').forEach((b) => b.classList.toggle('active', b.dataset.tab === tab));
        document.querySelectorAll('.view').forEach((v) => { v.hidden = v.id !== 'view-' + tab; });
        if (window.location.hash !== '#' + tab) history.replaceState(null, '', '#' + tab);
        if (tab === 'mine') loadMine();
        if (tab === 'notifications') loadNotifications(true);
        if (tab === 'admin') loadAdmin();
    }

    // ---------- recherche de médias ----------

    async function searchMedia(ev) {
        ev.preventDefault();
        const query = $('#mediaQuery').value.trim();
        const kind = $('#mediaKind').value;
        const year = $('#mediaYear').value.trim();
        const box = $('#mediaResults');
        $('#mediaDetail').hidden = true;
        box.replaceChildren(el('div', { class: 'spinner' }, 'Recherche…'));
        try {
            const yearParam = year ? `&year=${encodeURIComponent(year)}` : '';
            const results = await api('GET', `C411Torbox/Media/Search?kind=${kind}&query=${encodeURIComponent(query)}${yearParam}`);
            box.replaceChildren();
            if (!results.length) {
                box.append(el('p', { class: 'muted' }, 'Aucun résultat.'));
                return;
            }
            for (const m of results) {
                const img = safeImage(m.imageUrl);
                box.append(el('button', { class: 'card', type: 'button', onclick: () => openMedia(m) },
                    m.inLibrary ? el('span', { class: 'tag' }, 'Dans la bibliothèque') : null,
                    el('div', { class: 'poster' }, img ? el('img', { src: img, alt: '', loading: 'lazy' })
                        : m.isFreeText ? 'Recherche libre' : (kind === 'Book' ? 'Pas de couverture' : 'Pas d\'affiche')),
                    el('div', { class: 'info' },
                        el('div', { class: 'title' }, m.isFreeText ? `« ${m.name} » tel quel` : m.name),
                        el('div', { class: 'muted' }, m.year || ''))));
            }
        } catch (e) {
            box.replaceChildren(el('p', { class: 'error' }, e.message));
        }
    }

    // ---------- choix de l'épisode ----------

    let episodeRequest = 0;

    /** Liste les épisodes de la saison (titres, déjà présents) ; à défaut, saisie libre du numéro. */
    async function loadEpisodes() {
        const m = state.media;
        const select = $('#episodeSelect');
        const input = $('#episodeInput');
        if (!m || m.kind !== 'Series' || !$('#episodeToggle').checked) return;
        const season = parseInt($('#seasonInput').value, 10);
        if (isNaN(season)) return;
        const ticket = ++episodeRequest;
        select.hidden = true;
        input.hidden = false;
        $('#episodeHint').textContent = 'Chargement des épisodes…';
        try {
            const list = await api('GET', `C411Torbox/Media/Episodes?mediaId=${encodeURIComponent(m.id)}&season=${season}`);
            if (ticket !== episodeRequest) return;
            select.replaceChildren();
            if (!list.length) {
                $('#episodeHint').textContent = 'Épisodes inconnus : saisissez le numéro.';
                return;
            }
            const first = list.find((e) => !e.inLibrary);
            for (const e of list) {
                select.append(el('option', { value: e.episode, disabled: e.inLibrary, selected: first === e }, episodeLabel(e)));
            }
            select.append(el('option', { value: 'other' }, 'Autre numéro…'));
            select.hidden = false;
            // Tous déjà présents : « Autre numéro… » est le seul choix, la saisie reste visible.
            input.hidden = !!first;
            $('#episodeHint').textContent = first ? '' : 'Tous les épisodes connus sont déjà dans Jellyfin.';
        } catch (e) {
            if (ticket === episodeRequest) $('#episodeHint').textContent = 'Liste des épisodes indisponible : saisissez le numéro.';
        }
    }

    /** Numéro d'épisode choisi (liste, ou saisie libre). */
    function chosenEpisode() {
        const select = $('#episodeSelect');
        if (!select.hidden && select.value !== 'other') return parseInt(select.value, 10);
        return parseInt($('#episodeInput').value, 10);
    }

    function openMedia(m) {
        state.media = m;
        const img = safeImage(m.imageUrl);
        $('#detailPoster').hidden = !img;
        if (img) $('#detailPoster').src = img;
        $('#detailTitle').textContent = m.name + (m.year ? ` (${m.year})` : '');
        $('#detailOverview').textContent = m.overview || '';
        $('#seriesFields').hidden = m.kind !== 'Series';
        $('#episodeSelect').replaceChildren();
        $('#episodeSelect').hidden = true;
        $('#episodeInput').hidden = false;
        $('#episodeHint').textContent = '';
        if (m.kind === 'Series') loadEpisodes();
        $('#torrentInfo').replaceChildren();
        $('#torrentTable').hidden = true;
        $('#mediaDetail').hidden = false;
        $('#mediaDetail').scrollIntoView({ behavior: 'smooth', block: 'start' });
        if (m.kind !== 'Series') searchTorrents();
    }

    async function searchTorrents(ev) {
        if (ev) ev.preventDefault();
        const m = state.media;
        if (!m) return;
        const body = { MediaId: m.id };
        if (m.kind === 'Series') {
            body.Season = parseInt($('#seasonInput').value, 10);
            if ($('#episodeToggle').checked) body.Episode = chosenEpisode();
        }
        const info = $('#torrentInfo');
        const tbody = $('#torrentTable tbody');
        $('#torrentTable').hidden = true;
        info.replaceChildren(el('div', { class: 'spinner' }, 'Recherche des torrents…'));
        try {
            const res = await api('POST', 'C411Torbox/Torrents/Search', body);
            info.replaceChildren();
            if (res.alreadyInLibrary) {
                info.append(el('div', { class: 'notice ok' }, 'Déjà disponible dans la bibliothèque Jellyfin : inutile de le demander.'));
                return;
            }
            if (res.scope === 'Season' && res.episodesInLibrary > 0) {
                info.append(el('div', { class: 'notice warn' }, `${res.episodesInLibrary} épisode(s) de cette saison sont déjà dans la bibliothèque.`));
            }
            if (res.warning) info.append(el('div', { class: 'notice warn' }, res.warning));
            if (!res.results.length) {
                info.append(el('p', { class: 'muted' }, 'Aucun torrent trouvé.'));
                return;
            }
            tbody.replaceChildren();
            for (const t of res.results) {
                const btn = el('button', { class: 'primary', type: 'button' }, 'Demander');
                btn.addEventListener('click', () => createRequest(t, btn));
                tbody.append(el('tr', { class: t.isCachedOnTorbox ? 'cached' : null },
                    el('td', { class: 'title' }, t.title),
                    el('td', { class: 'num' }, formatSize(t.sizeBytes)),
                    el('td', { class: 'num ' + seedClass(t.seeders) }, t.seeders),
                    el('td', { class: 'num' }, t.leechers),
                    el('td', null, t.isCachedOnTorbox
                        ? el('span', { class: 'pill flash', title: 'Déjà présent sur Torbox : disponible immédiatement' }, '⚡ Instantané')
                        : (t.infoHash ? '' : el('span', { class: 'muted', title: 'Hash inconnu' }, '?'))),
                    el('td', null, t.wouldAutoApprove
                        ? el('span', { class: 'pill auto' }, 'Automatique')
                        : el('span', { class: 'pill' }, 'Admin')),
                    el('td', null, btn)));
            }
            $('#torrentTable').hidden = false;
        } catch (e) {
            info.replaceChildren(el('p', { class: 'error' }, e.message));
        }
    }

    async function createRequest(t, btn) {
        btn.disabled = true;
        try {
            const r = await api('POST', 'C411Torbox/Requests', { ResultId: t.id });
            btn.textContent = 'Demandé';
            const msg = {
                Pending: 'Demande enregistrée : en attente de validation par un administrateur.',
                SentToTorbox: 'Demande validée automatiquement et envoyée à Torbox.',
                Failed: 'Demande validée, mais l\'envoi à Torbox a échoué : un administrateur va la relancer.'
            }[r.status] || 'Demande enregistrée (' + (STATUS_LABELS[r.status] || r.status) + ').';
            toast(msg, r.status === 'Failed');
        } catch (e) {
            btn.disabled = false;
            toast(e.message, true);
        }
    }

    // ---------- mes demandes ----------

    // refresh = true : le serveur relit d'abord Torbox et la bibliothèque (sans attendre la tâche planifiée).
    async function loadMine(refresh) {
        const tbody = $('#mineTable tbody');
        const btn = $('#mineRefresh');
        btn.disabled = true;
        tbody.replaceChildren(el('tr', null, el('td', { colspan: 6, class: 'spinner' }, refresh ? 'Actualisation…' : 'Chargement…')));
        try {
            const list = refresh
                ? await api('POST', 'C411Torbox/Requests/Mine/Refresh')
                : await api('GET', 'C411Torbox/Requests/Mine');
            tbody.replaceChildren();
            if (!list.length) tbody.append(el('tr', null, el('td', { colspan: 6, class: 'muted' }, 'Aucune demande pour l\'instant.')));
            for (const r of list) {
                tbody.append(el('tr', null,
                    el('td', null, describe(r)),
                    el('td', { class: 'title muted' }, r.torrentTitle),
                    el('td', { class: 'num' }, formatSize(r.sizeBytes)),
                    el('td', null, statusPill(r)),
                    el('td', null, formatDate(r.createdAt)),
                    el('td', null, canUserCancel(r)
                        ? el('button', { class: 'ghost', type: 'button', onclick: (e) => cancelMine(r, e.target) }, 'Annuler')
                        : null)));
            }
        } catch (e) {
            tbody.replaceChildren(el('tr', null, el('td', { colspan: 6, class: 'error' }, e.message)));
        } finally {
            btn.disabled = false;
        }
    }

    async function cancelMine(r, btn) {
        if (!window.confirm(`Annuler votre demande « ${describe(r)} » ?`)) return;
        btn.disabled = true;
        try {
            await api('POST', `C411Torbox/Requests/${r.id}/Cancel`);
            toast('Demande annulée.');
        } catch (e) {
            toast(e.message, true);
        }
        loadMine();
    }

    // ---------- notifications ----------

    async function loadNotifications(markRead) {
        try {
            const list = await api('GET', 'C411Torbox/Notifications');
            const unread = list.filter((n) => !n.readAt).length;
            $('#notifBadge').textContent = unread;
            $('#notifBadge').hidden = unread === 0;
            if (state.tab !== 'notifications') return;
            const ul = $('#notifList');
            ul.replaceChildren();
            if (!list.length) ul.append(el('li', { class: 'muted' }, 'Aucune notification.'));
            for (const n of list) {
                ul.append(el('li', { class: n.readAt ? null : 'unread' },
                    el('time', null, formatDate(n.createdAt) + (n.userId ? '' : ' · administrateurs')),
                    n.message));
            }
            if (markRead && unread > 0) {
                await api('POST', 'C411Torbox/Notifications/Read');
                $('#notifBadge').hidden = true;
            }
        } catch (e) {
            if (state.tab === 'notifications') toast(e.message, true);
        }
    }

    // ---------- administration ----------

    async function loadAdmin() {
        const tbody = $('#adminTable tbody');
        const filter = $('#adminFilter').value;
        tbody.replaceChildren(el('tr', null, el('td', { colspan: 9, class: 'spinner' }, 'Chargement…')));
        try {
            const query = filter === 'active' ? '?active=true' : filter ? '?status=' + filter : '';
            const list = await api('GET', 'C411Torbox/Admin/Requests' + query);
            tbody.replaceChildren();
            if (!list.length) tbody.append(el('tr', null, el('td', { colspan: 9, class: 'muted' }, 'Aucune demande.')));
            for (const r of list) {
                const actions = el('td', null);
                if (r.status === 'Pending' || r.status === 'Failed') {
                    actions.append(
                        el('button', { class: 'ok', type: 'button', onclick: (e) => adminAction(r, 'Approve', e.target) },
                            r.status === 'Failed' ? 'Renvoyer' : 'Valider'), ' ',
                        el('button', { class: 'bad', type: 'button', onclick: (e) => adminAction(r, 'Reject', e.target) }, 'Refuser'));
                } else if (r.status === 'ImportFailed') {
                    actions.append(
                        el('button', { class: 'ok', type: 'button', title: 'Retélécharger depuis Torbox et ranger', onclick: (e) => adminAction(r, 'RetryImport', e.target) }, 'Relancer'));
                }
                if (canMarkAvailable(r)) {
                    actions.append(' ', el('button', { class: 'ghost', type: 'button', title: 'Le média est dans Jellyfin mais n\'a pas été reconnu', onclick: (e) => adminAction(r, 'MarkAvailable', e.target) }, 'Disponible'));
                }
                if (canAdminCancel(r) && r.status !== 'Pending' && r.status !== 'Failed') {
                    actions.append(' ', el('button', { class: 'bad', type: 'button', onclick: (e) => adminAction(r, 'Cancel', e.target) }, 'Annuler'));
                }
                const days = staleDays(r);
                tbody.append(el('tr', { class: r.wasCachedOnTorbox ? 'cached' : null },
                    el('td', null, describe(r)),
                    el('td', null, r.userName),
                    el('td', { class: 'title muted' }, r.torrentTitle),
                    el('td', { class: 'num' }, formatSize(r.sizeBytes)),
                    el('td', { class: 'num ' + seedClass(r.seeders) }, r.seeders),
                    el('td', null, r.wasCachedOnTorbox ? el('span', { class: 'pill flash' }, '⚡') : ''),
                    el('td', null, statusPill(r), r.reviewedBy ? el('div', { class: 'muted' }, 'par ' + r.reviewedBy) : null,
                        days !== null ? el('div', { class: 'stale' }, `⚠ en cours depuis ${days} jours`) : null),
                    el('td', null, formatDate(r.createdAt)),
                    actions));
            }
            updatePendingBadge(filter === 'Pending' ? list.length : null);
        } catch (e) {
            tbody.replaceChildren(el('tr', null, el('td', { colspan: 9, class: 'error' }, e.message)));
        }
    }

    async function adminAction(r, action, btn) {
        let body;
        if (action === 'Reject') {
            const reason = window.prompt(`Refuser « ${describe(r)} » ?\nMotif (facultatif) :`, '');
            if (reason === null) return;
            body = { Reason: reason };
        } else if (action === 'Cancel') {
            if (!window.confirm(`Annuler la demande « ${describe(r)} » de ${r.userName} ?`)) return;
            body = { DeleteFromTorbox: !!r.torboxTorrentId && window.confirm('Supprimer aussi le torrent de Torbox ?\n(seulement si aucune autre demande ne l\'utilise)') };
        } else if (action === 'MarkAvailable') {
            if (!window.confirm(`Marquer « ${describe(r)} » comme disponible dans Jellyfin ?`)) return;
        }
        btn.disabled = true;
        try {
            const updated = await api('POST', `C411Torbox/Admin/Requests/${r.id}/${action}`, body);
            toast(action === 'Reject'
                ? 'Demande refusée.'
                : action === 'Cancel' ? 'Demande annulée.' + (updated.lastError ? ' ' + updated.lastError : '')
                : action === 'MarkAvailable' ? 'Demande marquée disponible.'
                : action === 'RetryImport' ? 'Rangement relancé : le téléchargement vers le NAS va reprendre.'
                : updated.status === 'Failed' ? 'Échec de l\'envoi à Torbox : ' + (updated.lastError || '') : 'Demande validée et envoyée à Torbox.',
                updated.status === 'Failed');
        } catch (e) {
            toast(e.message, true);
        }
        loadAdmin();
    }

    async function updatePendingBadge(count) {
        if (!(state.me && state.me.isAdmin)) return;
        if (count === null) {
            try { count = (await api('GET', 'C411Torbox/Admin/Requests?status=Pending')).length; } catch (e) { return; }
        }
        $('#pendingBadge').textContent = count;
        $('#pendingBadge').hidden = count === 0;
    }

    // ---------- démarrage ----------

    async function start() {
        state.auth = state.auth || loadAuth();
        if (!state.auth) {
            showLogin();
            return;
        }
        try {
            state.me = await api('GET', 'C411Torbox/Me');
        } catch (e) {
            if (state.auth) toast(e.message, true);
            return;
        }
        $('#login').hidden = true;
        $('#tabs').hidden = false;
        $('#userName').textContent = state.me.name;
        $('#adminTab').hidden = !state.me.isAdmin;
        showTab((window.location.hash || '#search').substring(1));
        loadNotifications(false);
        updatePendingBadge(null);
    }

    document.addEventListener('DOMContentLoaded', () => {
        $('#backLink').href = BASE + '/web/';
        $('#loginForm').addEventListener('submit', login);
        $('#mediaForm').addEventListener('submit', searchMedia);
        $('#torrentForm').addEventListener('submit', searchTorrents);
        $('#detailClose').addEventListener('click', () => { $('#mediaDetail').hidden = true; });
        $('#episodeToggle').addEventListener('change', (e) => {
            $('#episodeLabel').hidden = !e.target.checked;
            loadEpisodes();
        });
        $('#seasonInput').addEventListener('change', loadEpisodes);
        $('#episodeSelect').addEventListener('change', (e) => {
            $('#episodeInput').hidden = e.target.value !== 'other';
        });
        $('#mediaKind').addEventListener('change', (e) => {
            $('#mediaQuery').placeholder = e.target.value === 'Book'
                ? 'Titre du livre, de la BD ou du journal…'
                : 'Titre, lien TMDB / IMDb ou identifiant (tt0068646)…';
        });
        $('#adminFilter').addEventListener('change', loadAdmin);
        $('#adminRefresh').addEventListener('click', loadAdmin);
        $('#mineRefresh').addEventListener('click', () => loadMine(true));
        document.querySelectorAll('.tab').forEach((b) => b.addEventListener('click', () => showTab(b.dataset.tab)));
        start();
        setInterval(() => { if (state.me) { loadNotifications(false); updatePendingBadge(null); } }, 60000);
    });
})();
