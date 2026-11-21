/* JellyUSB — added to jellyfin-web by the JellyUSB server plugin.
 * 1. When the JellyUSB message ("Support absent" / "Fichier introuvable") opens, close the generic
 *    "Playback error" dialog that jellyfin-web always shows when a playback is refused.
 * 2. On Dashboard > Scheduled Tasks, show the running tasks at the top, with what keeps each USB
 *    medium busy (so you know why a drive cannot be unmounted).
 */
(function () {
    'use strict';
    var SETTINGS = /*JELLYUSB_SETTINGS*/null || { headers: ['Support absent', 'Fichier introuvable'], closeGenericDialog: true, tasksPanel: true };
    var OURS = SETTINGS.headers.filter(Boolean).map(function (h) { return h.trim(); });
    var WINDOW_MS = 6000;

    /* ---------- 1. dialogs ---------- */
    var oursShownAt = 0;
    var recentOthers = [];

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
        if (dlg.__jellyusbSeen) { return; }
        dlg.__jellyusbSeen = true;
        var now = Date.now();
        var title = dialogTitle(dlg);
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
})();
