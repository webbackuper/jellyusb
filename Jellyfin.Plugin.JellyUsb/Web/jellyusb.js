/* JellyUSB — added to jellyfin-web by the JellyUSB server plugin.
 * 1. When a playback is refused, jellyfin-web opens its generic "Playback error" dialog: the script
 *    writes the JellyUSB message ("Support absent" / "Fichier introuvable", read from the server's
 *    answer) into that dialog before it is drawn, so only one dialog shows, in Jellyfin's design. The
 *    same message also arrives through the websocket: that duplicate is closed unseen.
 * 2. On Dashboard > Scheduled Tasks, show the running tasks at the top, with what keeps each USB
 *    medium busy (so you know why a drive cannot be unmounted).
 * 3. In a movie library, add a "MediaID" view to the view menu (top left): one row per USB medium,
 *    each with its background; a click on a medium shows all its movies, with a way back.
 * 4. Add a "Media" button to the header, after the library buttons, that opens that view.
 */
(function () {
    'use strict';
    var SETTINGS = /*JELLYUSB_SETTINGS*/null || { headers: ['Support absent', 'Fichier introuvable'], closeGenericDialog: true, tasksPanel: true, mediaView: true, mediaViewName: 'MediaID', mediaButton: true, mediaButtonName: 'Media' };
    var OURS = SETTINGS.headers.filter(Boolean).map(function (h) { return h.trim(); });
    var WINDOW_MS = 6000;

    /* ---------- 1. dialogs ---------- */
    var oursShownAt = 0;
    var recentOthers = [];
    var REPLACE_MS = 8000;
    var pending = null;          // { header, text, at, replaced }: refusal read from the server's answer
    var hideBackdropUntil = 0;

    function readRefusal(header) {
        try {
            if (header) {
                var m = JSON.parse(decodeURIComponent(header));
                pending = { header: m.header, text: m.text, at: Date.now(), replaced: null };
            }
        } catch (e) { /* not ours */ }
    }

    // The refusal is read as soon as the answer arrives, before jellyfin-web handles it (its error
    // dialog comes after, in a later step), whatever it uses to ask: XMLHttpRequest or fetch.
    if (SETTINGS.closeGenericDialog) {
        // The playback request says the script is here: the server then does not send the same message
        // through the websocket (it would open a second dialog).
        var PLAYBACK_INFO = /\/Items\/[0-9a-fA-F-]{32,36}\/PlaybackInfo/;
        if (window.XMLHttpRequest) {
            var nativeOpen = XMLHttpRequest.prototype.open;
            var nativeSend = XMLHttpRequest.prototype.send;
            XMLHttpRequest.prototype.open = function (method, url) {
                this.__jellyusbUrl = String(url || '');
                return nativeOpen.apply(this, arguments);
            };
            XMLHttpRequest.prototype.send = function () {
                var xhr = this;
                if (PLAYBACK_INFO.test(xhr.__jellyusbUrl || '')) {
                    try { xhr.setRequestHeader('X-JellyUsb-Script', '1'); } catch (e) { /* ignore */ }
                }
                xhr.addEventListener('readystatechange', function () {
                    if (xhr.readyState === 4 && xhr.status === 404) {
                        try { readRefusal(xhr.getResponseHeader('X-JellyUsb-Message')); } catch (e) { /* ignore */ }
                    }
                });
                return nativeSend.apply(this, arguments);
            };
        }
        if (window.fetch) {
            var nativeFetch = window.fetch;
            window.fetch = function (input, init) {
                var url = typeof input === 'string' ? input : (input && input.url) || '';
                if (PLAYBACK_INFO.test(url) && typeof Headers !== 'undefined' && !(input instanceof Request)) {
                    init = Object.assign({}, init || {});
                    var headers = new Headers(init.headers || {});
                    headers.set('X-JellyUsb-Script', '1');
                    init.headers = headers;
                }
                var call = init === undefined ? nativeFetch.call(this, input) : nativeFetch.call(this, input, init);
                return call.then(function (r) {
                    if (r && r.status === 404 && r.headers) { readRefusal(r.headers.get('X-JellyUsb-Message')); }
                    return r;
                });
            };
        }
    }

    function rewrite(dlg, m) {
        var title = dlg.querySelector('.formDialogHeaderTitle');
        if (title) { title.textContent = m.header; }
        var text = dlg.querySelector('.dialogContentInner .text') || dlg.querySelector('.dialogContentInner');
        if (text) { text.textContent = m.text; text.style.whiteSpace = 'pre-line'; }
    }

    function hideNow(dlg) {
        var container = dlg.closest('.dialogContainer') || dlg;
        container.style.visibility = 'hidden';
        hideBackdropUntil = Date.now() + 1000;   // its backdrop is added right after it
    }

    function dialogTitle(dlg) {
        var el = dlg.querySelector('.formDialogHeaderTitle');
        return el ? (el.innerText || el.textContent || '').trim() : '';
    }

    function isSimpleAlert(dlg) {
        // jellyfin-web alert(): one button, no form fields
        return dlg.querySelectorAll('.formDialogFooter button').length === 1 && !dlg.querySelector('input, select, textarea');
    }

    function closeDialog(dlg) {
        var button = dlg.querySelector('.formDialogFooter button');
        if (button && document.body.contains(dlg)) {
            button.click();
        }
    }

    function onDialog(dlg) {
        if (dlg.__jellyusbSeen || dlg.__jellyusbRewritten) { return; }
        dlg.__jellyusbSeen = true;
        var now = Date.now();
        var title = dialogTitle(dlg);

        if (pending && now - pending.at < REPLACE_MS) {
            var isOurs = OURS.indexOf(title) >= 0;
            if (isOurs && pending.replaced && document.body.contains(pending.replaced)) {
                // the same message, through the websocket: already shown in jellyfin-web's dialog
                hideNow(dlg);
                setTimeout(function () { closeDialog(dlg); }, 0);
                return;
            }
            if (!isOurs && !pending.replaced && isSimpleAlert(dlg)) {
                rewrite(dlg, pending);
                pending.replaced = dlg;
                dlg.__jellyusbRewritten = true;
                oursShownAt = now;
                return;
            }
        }
        if (OURS.indexOf(title) >= 0) {
            oursShownAt = now;
            recentOthers.forEach(function (o) { if (now - o.at < WINDOW_MS) { closeDialog(o.dlg); } });
            recentOthers = [];
            return;
        }

        if (!isSimpleAlert(dlg)) { return; }
        if (now - oursShownAt < WINDOW_MS) {
            setTimeout(function () { closeDialog(dlg); }, 0);
        } else {
            recentOthers.push({ dlg: dlg, at: now });
            recentOthers = recentOthers.filter(function (o) { return now - o.at < WINDOW_MS; });
        }
    }

    function scan(node) {
        if (!(node instanceof HTMLElement)) { return; }
        if (node.classList.contains('dialogBackdrop') && Date.now() < hideBackdropUntil) {
            node.style.visibility = 'hidden';
            hideBackdropUntil = 0;
            return;
        }
        if (node.classList.contains('dialog')) { onDialog(node); }
        var inner = node.querySelectorAll ? node.querySelectorAll('.dialog') : [];
        for (var i = 0; i < inner.length; i++) { onDialog(inner[i]); }
    }

    if (SETTINGS.closeGenericDialog) {
        new MutationObserver(function (mutations) {
            mutations.forEach(function (m) { m.addedNodes.forEach(scan); });
            // the title is set right after the dialog is added
            setTimeout(function () {
                document.querySelectorAll('.dialog').forEach(function (d) {
                    if (!d.__jellyusbTitled && dialogTitle(d)) { d.__jellyusbTitled = true; d.__jellyusbSeen = false; onDialog(d); }
                });
            }, 50);
        }).observe(document.documentElement, { childList: true, subtree: true });
    }

    /* ---------- 2. scheduled tasks panel ---------- */
    var PANEL_ID = 'jellyusbActivityPanel';
    var timer = null;

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function duration(seconds) {
        if (seconds == null || seconds < 0) { return ''; }
        var s = Math.floor(seconds), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
        return h ? h + ' h ' + m + ' min' : m ? m + ' min ' + (s % 60) + ' s' : (s % 60) + ' s';
    }

    function fileName(path) {
        return path ? path.split('/').pop() : '';
    }

    function render(panel, data) {
        var html = '<h2 class="sectionTitle" style="margin-top:0">En cours d\'exécution</h2>';
        if (!data.Tasks.length) {
            html += '<p class="fieldDescription">Aucune tâche planifiée en cours.</p>';
        } else {
            html += '<div class="paperList">';
            data.Tasks.forEach(function (t) {
                var pct = t.Progress == null ? null : Math.round(t.Progress * 10) / 10;
                html += '<div class="listItem" style="display:block;padding:.6em 1em">'
                    + '<div><b>' + esc(t.Name) + '</b> <span class="secondary">— ' + esc(t.Category) + '</span>'
                    + (pct == null ? '' : ' <span style="float:right">' + pct + ' %</span>') + '</div>'
                    + (pct == null ? '' : '<div style="height:4px;background:rgba(128,128,128,.3);margin-top:.4em;border-radius:2px">'
                        + '<div style="height:4px;width:' + pct + '%;background:#00a4dc;border-radius:2px"></div></div>')
                    + '</div>';
            });
            html += '</div>';
        }

        html += '<h2 class="sectionTitle">Supports USB</h2>';
        if (!data.Media.length) {
            html += '<p class="fieldDescription">Aucun support sous le chemin de base.</p>';
        }
        data.Media.forEach(function (m) {
            var state = !m.Present ? 'absent' : (m.Users.length ? 'occupé : ne pas démonter' : 'libre : peut être démonté');
            var color = !m.Present ? 'gray' : (m.Users.length ? '#f5a623' : '#2ecc71');
            html += '<div class="paperList" style="margin-bottom:.6em"><div class="listItem" style="display:block;padding:.6em 1em">'
                + '<div><span style="display:inline-block;width:.7em;height:.7em;border-radius:50%;background:' + color + ';margin-right:.5em"></span>'
                + '<b>' + esc(m.MediaId) + '</b> <span class="secondary">— ' + state + '</span></div>';
            m.Users.forEach(function (u) {
                html += '<div style="margin:.35em 0 0 1.2em;font-size:90%">'
                    + '<b>' + esc(u.Kind) + (u.ProcessId ? ' (pid ' + u.ProcessId + ')' : '') + '</b> : ' + esc(u.Description)
                    + (u.RunningSeconds != null ? ' <span class="secondary">depuis ' + duration(u.RunningSeconds) + '</span>' : '')
                    + (u.File ? '<div class="secondary" style="word-break:break-all">' + esc(fileName(u.File)) + '</div>' : '')
                    + '</div>';
            });
            html += '</div></div>';
        });
        html += '<p class="fieldDescription">JellyUSB — mis à jour toutes les 2 s. Seuls les processus de Jellyfin sont visibles ; '
            + 'pour un autre programme : <code>sudo fuser -vm /mnt/usb/&lt;MediaID&gt;</code>.'
            + (data.Note ? ' ' + esc(data.Note) : '') + '</p>';
        panel.innerHTML = html;
    }

    function refresh(panel) {
        if (!window.ApiClient) { return; }
        window.ApiClient.getJSON(window.ApiClient.getUrl('JellyUsb/Activity')).then(function (data) {
            if (document.body.contains(panel)) { render(panel, data); }
        }, function () {
            panel.innerHTML = '<p class="fieldDescription">JellyUSB : activité indisponible.</p>';
        });
    }

    function ensurePanel() {
        var onTasksPage = /dashboard\/tasks\/?(\?|$)/.test(location.hash) || /dashboard\/tasks\/?$/.test(location.pathname);
        var page = document.getElementById('scheduledTasksPage');
        var existing = document.getElementById(PANEL_ID);
        if (!onTasksPage || !page) {
            if (existing) { existing.remove(); }
            if (timer) { clearInterval(timer); timer = null; }
            return;
        }

        if (!existing) {
            var host = page.querySelector('.content-primary') || page;
            existing = document.createElement('div');
            existing.id = PANEL_ID;
            existing.style.margin = '1em 0 2em';
            host.insertBefore(existing, host.firstChild);
            refresh(existing);
        }

        if (!timer) {
            timer = setInterval(function () {
                var p = document.getElementById(PANEL_ID);
                if (p) { refresh(p); }
            }, 2000);
        }
    }

    if (SETTINGS.tasksPanel) {
        setInterval(ensurePanel, 700);
        window.addEventListener('hashchange', ensurePanel);
    }

    /* ---------- 3. MediaID view of a movie library ---------- */
    var VIEW_ID = 'jellyusbMediaView';
    var ROW_ITEMS = 24;
    var PAGE_ITEMS = 100;
    var mediaCache = {};       // libraryId -> { at, list } (or a pending promise)
    var currentKey = null;     // what the view shows now

    function hashParams() {
        var h = location.hash || '';
        var q = h.indexOf('?');
        return { route: q < 0 ? h : h.slice(0, q), params: new URLSearchParams(q < 0 ? '' : h.slice(q + 1)) };
    }

    function libraryId() {
        var p = hashParams();
        return p.route === '#/movies' ? p.params.get('topParentId') : null;
    }

    function viewHash(mediaId) {
        var p = hashParams();
        p.params.set('jellyusb', 'media');
        if (mediaId) { p.params.set('mediaId', mediaId); } else { p.params.delete('mediaId'); }
        return p.route + '?' + p.params.toString();
    }

    function leaveHash() {
        var p = hashParams();
        p.params.delete('jellyusb');
        p.params.delete('mediaId');
        return p.route + '?' + p.params.toString();
    }

    function getMedia(lib, fresh) {
        var c = mediaCache[lib];
        if (c && c.list && !fresh && Date.now() - c.at < 60000) { return Promise.resolve(c.list); }
        if (c && c.pending) { return c.pending; }
        var pending = window.ApiClient.getJSON(window.ApiClient.getUrl('JellyUsb/Libraries/' + lib + '/Media')).then(function (list) {
            mediaCache[lib] = { at: Date.now(), list: list || [] };
            return mediaCache[lib].list;
        }, function () {
            mediaCache[lib] = { at: Date.now(), list: [] };
            return [];
        });
        mediaCache[lib] = { pending: pending, list: c && c.list, at: c ? c.at : 0 };
        return pending;
    }

    function getItems(lib, mediaId, start, limit, sortBy) {
        return window.ApiClient.getJSON(window.ApiClient.getUrl('JellyUsb/Libraries/' + lib + '/Media/' + encodeURIComponent(mediaId) + '/Items',
            { startIndex: start, limit: limit, sortBy: sortBy || 'SortName' }));
    }

    function backgroundUrl(mediaId) {
        // the version in the address: a new release redraws the backgrounds instead of the cached ones
        return window.ApiClient.getUrl('JellyUsb/Media/' + encodeURIComponent(mediaId) + '/Background', { v: SETTINGS.version || '1' });
    }

    function addStyle() {
        if (document.getElementById('jellyusbMediaStyle')) { return; }
        var st = document.createElement('style');
        st.id = 'jellyusbMediaStyle';
        st.textContent = ''
            + '#' + VIEW_ID + '{padding:1em 0 3em}'
            + '.jusb-row{margin-bottom:1.6em}'
            + '.jusb-rowhead{display:flex;align-items:baseline;gap:.6em;padding:0 3.3%;margin:0 0 .5em;color:inherit;text-decoration:none}'
            + '.jusb-rowhead h2{margin:0}.jusb-rowhead:hover h2{text-decoration:underline}'
            + '.jusb-meta{opacity:.7;font-size:.95em}'
            + '.jusb-dot{display:inline-block;width:.6em;height:.6em;border-radius:50%;margin-right:.35em}'
            + '.jusb-scroller{display:flex;gap:.9em;overflow-x:auto;padding:0 3.3% .6em;scroll-padding:0 3.3%;scroll-snap-type:x proximity}'
            + '.jusb-card{flex:0 0 auto;width:9.5em;color:inherit;text-decoration:none;scroll-snap-align:start}'
            + '.jusb-poster{width:100%;aspect-ratio:2/3;border-radius:.3em;background:#222 center/cover no-repeat;display:block;object-fit:cover}'
            + '.jusb-title{margin-top:.35em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;text-align:center}'
            + '.jusb-year{opacity:.6;font-size:.85em;text-align:center}'
            + '.jusb-fond{flex:0 0 auto;width:23.75em;aspect-ratio:16/9;border-radius:.4em;overflow:hidden;position:relative;display:block;scroll-snap-align:start}'
            + '.jusb-fond img{width:100%;height:100%;display:block}'
            + '.jusb-badge{position:absolute;top:.6em;right:.6em;background:rgba(0,0,0,.6);color:#fff;border-radius:1em;padding:.15em .7em;font-size:.85em}'
            + '.jusb-more{flex:0 0 auto;width:9.5em;aspect-ratio:2/3;border-radius:.3em;display:flex;align-items:center;justify-content:center;'
            + 'background:rgba(128,128,128,.18);color:inherit;text-decoration:none;text-align:center;padding:.5em;box-sizing:border-box}'
            + '.jusb-absent .jusb-poster{opacity:.55}'
            + '.jusb-head{padding:0 3.3%;margin-bottom:1.4em}'
            + '.jusb-back{display:inline-block;background:rgba(128,128,128,.22);color:inherit;border-radius:2em;padding:.4em 1.1em;'
            + 'margin-bottom:.9em;text-decoration:none}'
            + '.jusb-back:hover{background:rgba(128,128,128,.4)}'
            + '.jusb-headbody{display:flex;gap:1.4em;align-items:center;flex-wrap:wrap}'
            + '.jusb-headimg{height:10em;aspect-ratio:16/9;border-radius:.4em;display:block}'
            + '.jusb-headbody h1{margin:0 0 .2em}'
            + '.jusb-grid{display:flex;flex-wrap:wrap;gap:1.2em .9em;padding:0 3.3%}'
            + '.jusb-note{padding:0 3.3%;opacity:.7}'
            + '@media (max-width:600px){.jusb-card,.jusb-more{width:7.2em}.jusb-fond{width:17em}.jusb-headimg{height:7em}}';
        document.head.appendChild(st);
    }

    function stateText(m) {
        return m.Present
            ? '<span class="jusb-dot" style="background:#2ecc71"></span>branché'
            : '<span class="jusb-dot" style="background:gray"></span>absent';
    }

    function filmCount(n) {
        return n + (n > 1 ? ' films' : ' film');
    }

    function cardHtml(item) {
        var serverId = window.ApiClient.serverId();
        var img = item.ImageTags && item.ImageTags.Primary
            ? window.ApiClient.getUrl('Items/' + item.Id + '/Images/Primary', { maxHeight: 360, tag: item.ImageTags.Primary, quality: 90 })
            : '';
        return '<a class="jusb-card" href="#/details?id=' + encodeURIComponent(item.Id) + '&serverId=' + encodeURIComponent(serverId) + '" title="' + esc(item.Name) + '">'
            + (img ? '<img class="jusb-poster" loading="lazy" alt="" src="' + esc(img) + '">' : '<span class="jusb-poster"></span>')
            + '<div class="jusb-title">' + esc(item.Name) + '</div>'
            + '<div class="jusb-year">' + esc(item.ProductionYear || '') + '</div></a>';
    }

    function renderRows(view, lib, list) {
        if (!list.length) {
            view.innerHTML = '<p class="jusb-note">Aucun support USB dans cette bibliothèque.</p>';
            return;
        }
        view.innerHTML = list.map(function (m, i) {
            return '<div class="jusb-row' + (m.Present ? '' : ' jusb-absent') + '">'
                + '<a class="jusb-rowhead" href="' + esc(viewHash(m.MediaId)) + '"><h2 class="sectionTitle sectionTitle-cards">' + esc(m.MediaId) + ' ›</h2>'
                + '<span class="jusb-meta">' + filmCount(m.MovieCount) + ' · ' + stateText(m) + '</span></a>'
                + '<div class="jusb-scroller" data-row="' + i + '">'
                + '<a class="jusb-fond" href="' + esc(viewHash(m.MediaId)) + '" title="' + esc(m.MediaId) + '"><img alt="' + esc(m.MediaId) + '" src="' + esc(backgroundUrl(m.MediaId)) + '">'
                + '<span class="jusb-badge">' + (m.Present ? 'branché' : 'absent') + '</span></a>'
                + '</div></div>';
        }).join('');
        list.forEach(function (m, i) {
            getItems(lib, m.MediaId, 0, ROW_ITEMS, 'DateCreated').then(function (r) {
                var row = view.querySelector('.jusb-scroller[data-row="' + i + '"]');
                if (!row) { return; }
                var html = (r.Items || []).map(cardHtml).join('');
                if (r.TotalRecordCount > (r.Items || []).length) {
                    html += '<a class="jusb-more" href="' + esc(viewHash(m.MediaId)) + '">Voir les ' + filmCount(r.TotalRecordCount) + ' ›</a>';
                }
                row.insertAdjacentHTML('beforeend', html);
            });
        });
    }

    function renderMedium(view, lib, list, mediaId) {
        var m = list.filter(function (x) { return x.MediaId === mediaId; })[0];
        var back = '<a class="jusb-back" href="' + esc(viewHash(null)) + '">‹ ' + esc(SETTINGS.mediaViewName || 'MediaID') + '</a>';
        if (!m) {
            view.innerHTML = '<div class="jusb-head">' + back + '</div><p class="jusb-note">' + esc(mediaId) + ' n\'est pas dans cette bibliothèque.</p>';
            return;
        }
        view.innerHTML = '<div class="jusb-head">' + back + '<div class="jusb-headbody">'
            + '<img class="jusb-headimg" alt="' + esc(m.MediaId) + '" src="' + esc(backgroundUrl(m.MediaId)) + '">'
            + '<div><h1>' + esc(m.MediaId) + '</h1><div class="jusb-meta">' + filmCount(m.MovieCount) + ' · ' + stateText(m)
            + (m.Present ? '' : '<br>Branchez ce support pour lire ses films.') + '</div></div></div></div>'
            + '<div class="jusb-grid' + (m.Present ? '' : ' jusb-absent') + '"></div><div class="jusb-sentinel" style="height:1px"></div>';
        var grid = view.querySelector('.jusb-grid');
        var sentinel = view.querySelector('.jusb-sentinel');
        var next = 0, total = null, loading = false;
        function more() {
            if (loading || (total !== null && next >= total) || !document.body.contains(grid)) { return; }
            loading = true;
            getItems(lib, mediaId, next, PAGE_ITEMS, 'SortName').then(function (r) {
                total = r.TotalRecordCount;
                next += (r.Items || []).length;
                grid.insertAdjacentHTML('beforeend', (r.Items || []).map(cardHtml).join(''));
                loading = false;
                if (!(r.Items || []).length) { total = next; }
                if (sentinel.getBoundingClientRect().top < window.innerHeight + 600) { more(); }
            }, function () { loading = false; });
        }
        if ('IntersectionObserver' in window) {
            new IntersectionObserver(function (entries) {
                if (entries.some(function (e) { return e.isIntersecting; })) { more(); }
            }, { rootMargin: '600px' }).observe(sentinel);
        }
        more();
    }

    function visibleMoviesPage() {
        var pages = document.querySelectorAll('#moviesPage');
        for (var i = 0; i < pages.length; i++) {
            if (!pages[i].classList.contains('hide') && pages[i].offsetParent !== null) { return pages[i]; }
        }
        return pages.length ? pages[pages.length - 1] : null;
    }

    function titleSpan() {
        var b = document.querySelector('[aria-controls="library-view-menu"]');
        return b ? b.querySelector('.MuiTypography-root') || b.firstChild : null;
    }

    function setTitle(on) {
        var span = titleSpan();
        if (!span) { return; }
        if (on) {
            if (span.getAttribute('data-jusb-title') === null) { span.setAttribute('data-jusb-title', span.textContent); }
            if (span.textContent !== SETTINGS.mediaViewName) { span.textContent = SETTINGS.mediaViewName; }
        } else if (span.getAttribute('data-jusb-title') !== null) {
            // jellyfin-web may already have written the name of the view just chosen: keep it.
            if (span.textContent === SETTINGS.mediaViewName) { span.textContent = span.getAttribute('data-jusb-title'); }
            span.removeAttribute('data-jusb-title');
        }
    }

    function hide(el) {
        if (el && !el.hasAttribute('data-jusb-hidden')) { el.setAttribute('data-jusb-hidden', el.style.display || ''); el.style.display = 'none'; }
    }

    // The play, shuffle, filter and sort buttons of the header act on jellyfin-web's own view: hide them.
    function hideToolbarExtras() {
        var button = document.querySelector('[aria-controls="library-view-menu"]');
        var toolbar = button && button.closest('.MuiToolbar-root');
        for (var el = button; el && toolbar && el !== toolbar; el = el.parentElement) {
            Array.prototype.forEach.call(el.parentElement.children, function (sib) {
                if (sib !== el) { hide(sib); }
            });
        }
    }

    function exitView() {
        var view = document.getElementById(VIEW_ID);
        if (view) { view.remove(); }
        Array.prototype.forEach.call(document.querySelectorAll('[data-jusb-hidden]'), function (c) {
            c.style.display = c.getAttribute('data-jusb-hidden');
            c.removeAttribute('data-jusb-hidden');
        });
        setTitle(false);
        currentKey = null;
    }

    function ensureMediaView() {
        var lib = libraryId();
        var p = hashParams();
        if (!lib || p.params.get('jellyusb') !== 'media' || !window.ApiClient) { exitView(); return; }
        var page = visibleMoviesPage();
        if (!page) { return; }
        addStyle();
        var mediaId = p.params.get('mediaId');
        var key = lib + '|' + (mediaId || '');
        var view = document.getElementById(VIEW_ID);
        if (view && view.parentElement !== page) { exitView(); view = null; }
        Array.prototype.forEach.call(page.children, function (c) {
            if (c.id !== VIEW_ID) { hide(c); }
        });
        hideToolbarExtras();
        setTitle(true);
        if (view && currentKey === key) { return; }
        if (!view) {
            view = document.createElement('div');
            view.id = VIEW_ID;
            page.appendChild(view);
        }
        currentKey = key;
        view.innerHTML = '<p class="jusb-note">Chargement…</p>';
        window.scrollTo(0, 0);
        getMedia(lib, true).then(function (list) {
            if (currentKey !== key || !document.body.contains(view)) { return; }
            if (mediaId) { renderMedium(view, lib, list, mediaId); } else { renderRows(view, lib, list); }
        });
    }

    function closeMenu(menu) {
        var backdrop = menu.querySelector('.MuiBackdrop-root, .MuiModal-backdrop');
        if (backdrop) { backdrop.click(); return; }
        menu.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    }

    function ensureMenuItem() {
        var menu = document.getElementById('library-view-menu');
        var lib = libraryId();
        if (!menu || !lib) { return; }
        var ul = menu.querySelector('ul[role="menu"]');
        if (!ul || ul.querySelector('.jusb-menuitem')) { return; }
        var natives = ul.querySelectorAll('li[role="menuitem"]');
        if (!natives.length) { return; }
        var inView = hashParams().params.get('jellyusb') === 'media';

        // A native choice while our view is shown: let jellyfin-web switch, then drop our parameter.
        Array.prototype.forEach.call(natives, function (li) {
            if (inView) { li.classList.remove('Mui-selected'); }
            if (li.__jusbHooked) { return; }
            li.__jusbHooked = true;
            li.addEventListener('click', function () {
                if (hashParams().params.get('jellyusb') !== 'media') { return; }
                setTimeout(function () {
                    if (hashParams().params.get('jellyusb') === 'media') { location.replace(leaveHash()); }
                }, 0);
            }, true);
        });

        var cached = mediaCache[lib];
        var add = function (list) {
            if (!list.length || ul.querySelector('.jusb-menuitem') || !document.body.contains(ul)) { return; }
            var li = natives[natives.length - 1].cloneNode(false);
            li.classList.remove('Mui-selected');
            if (inView) { li.classList.add('Mui-selected'); }
            li.classList.add('jusb-menuitem');
            li.setAttribute('tabindex', '-1');
            li.textContent = SETTINGS.mediaViewName || 'MediaID';
            li.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                closeMenu(menu);
                if (!inView || hashParams().params.get('mediaId')) { location.hash = viewHash(null); }
            });
            ul.appendChild(li);
        };
        if (cached && cached.list) { add(cached.list); } else { getMedia(lib).then(add); }
    }

    if (SETTINGS.mediaView) {
        setInterval(ensureMediaView, 500);
        window.addEventListener('hashchange', function () { setTimeout(ensureMediaView, 0); });
        new MutationObserver(ensureMenuItem).observe(document.body, { childList: true, subtree: true });
    }

    /* ---------- 4. "Media" button in the header ---------- */
    var BUTTON_ID = 'jellyusbHeaderButton';
    // 2.5" disk: enclosure, platter (with spindle), activity LED and port; same 24x24 grid as the Material icons.
    var DISK_PATH = 'M7 2h10a3 3 0 0 1 3 3v14a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3V5a3 3 0 0 1 3-3z'
        + 'M12 4.5a5 5 0 1 0 0 10a5 5 0 1 0 0-10z'
        + 'M12 8.3a1.2 1.2 0 1 0 0 2.4a1.2 1.2 0 1 0 0-2.4z'
        + 'M7.2 17.4h3.6v1.6H7.2z'
        + 'M14.2 17.2h2.6v2h-2.6z';
    var headerLibrary = { userId: null, at: 0, id: null, pending: false };

    function findMediaLibrary() {
        var userId = window.ApiClient && window.ApiClient.getCurrentUserId && window.ApiClient.getCurrentUserId();
        if (!userId) { return; }
        if (headerLibrary.pending || (headerLibrary.userId === userId && Date.now() - headerLibrary.at < 300000)) { return; }
        headerLibrary.pending = true;
        window.ApiClient.getJSON(window.ApiClient.getUrl('UserViews', { userId: userId })).then(function (views) {
            var movies = ((views && views.Items) || []).filter(function (v) { return v.CollectionType === 'movies'; });
            // the first movie library that has USB media
            return movies.reduce(function (chain, lib) {
                return chain.then(function (found) {
                    return found || getMedia(lib.Id).then(function (list) { return list.length ? lib.Id : null; });
                });
            }, Promise.resolve(null));
        }).then(function (id) {
            headerLibrary = { userId: userId, at: Date.now(), id: id, pending: false };
        }, function () {
            headerLibrary = { userId: userId, at: Date.now(), id: null, pending: false };
        });
    }

    function navButtons(container) {
        return Array.prototype.filter.call(container.children, function (el) {
            return el.tagName === 'A' && el.id !== BUTTON_ID && /^#\/(home\?tab=1|movies|tv|music|list|livetv|collections|books|homevideos|musicvideos|photos)/.test(el.getAttribute('href') || '');
        });
    }

    function ensureHeaderButton() {
        // the header only: the side menu has a "Favorites" link too, with another layout
        var fav = document.querySelector('header a[href="#/home?tab=1"]');
        var existing = document.getElementById(BUTTON_ID);
        if (existing && !existing.closest('header')) { existing.remove(); existing = null; }
        if (!fav || !fav.parentElement) { if (existing) { existing.remove(); } return; }
        findMediaLibrary();
        var container = fav.parentElement;
        var buttons = navButtons(container);
        if (!headerLibrary.id) { if (existing) { existing.remove(); } return; }

        var button = existing;
        if (!button) {
            var template = buttons[buttons.length - 1] || fav;
            button = template.cloneNode(true);
            button.id = BUTTON_ID;
            button.removeAttribute('aria-current');
            var svg = button.querySelector('svg');
            if (svg) {
                svg.setAttribute('viewBox', '0 0 24 24');
                svg.setAttribute('data-testid', 'JellyUsbDiskIcon');
                svg.innerHTML = '<path fill-rule="evenodd" d="' + DISK_PATH + '"></path>';
            }
            // the label is the button's last text node
            Array.prototype.slice.call(button.childNodes).forEach(function (n) { if (n.nodeType === 3) { n.remove(); } });
            button.appendChild(document.createTextNode(SETTINGS.mediaButtonName || 'Media'));
        }
        var href = '#/movies?topParentId=' + encodeURIComponent(headerLibrary.id) + '&collectionType=movies&jellyusb=media';
        if (button.getAttribute('href') !== href) { button.setAttribute('href', href); }

        // right after the last library button (before the overflow "more" button, if any)
        var last = buttons[buttons.length - 1] || fav;
        if (button.previousElementSibling !== last || button.parentElement !== container) {
            last.insertAdjacentElement('afterend', button);
        }

        // active look: the one jellyfin-web gives the current library (blue); that library looks inactive meanwhile
        var inView = hashParams().params.get('jellyusb') === 'media';
        var inactive = buttons.filter(function (b) { return /MuiButton-colorInherit/.test(b.className); })[0];
        var active = buttons.filter(function (b) { return /MuiButton-colorPrimary/.test(b.className); })[0];
        var inactiveCls = inactive ? (inactive.getAttribute('data-jusb-cls') || inactive.className) : null;
        if (inView) {
            if (active && !active.hasAttribute('data-jusb-cls') && inactiveCls) {
                button.className = active.className;
                active.setAttribute('data-jusb-cls', active.className);
                active.className = inactiveCls;
            }
            button.setAttribute('aria-current', 'page');
        } else {
            Array.prototype.forEach.call(container.querySelectorAll('[data-jusb-cls]'), function (b) {
                b.className = b.getAttribute('data-jusb-cls');
                b.removeAttribute('data-jusb-cls');
            });
            if (inactiveCls && button.className !== inactiveCls) { button.className = inactiveCls; }
            button.removeAttribute('aria-current');
        }
    }

    if (SETTINGS.mediaView && SETTINGS.mediaButton) {
        setInterval(ensureHeaderButton, 500);
        window.addEventListener('hashchange', function () { setTimeout(ensureHeaderButton, 50); });
    }
})();

