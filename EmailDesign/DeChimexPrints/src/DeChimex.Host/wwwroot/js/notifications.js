/* ============================================================
   DE-CHIMEX PRINT HUB V2 - real-time notifications client
   Connects to /hubs/notifications (SignalR, cookie auth), keeps
   the sidebar unread badge current, and prepends live unread items
   to the /admin/notifications history when it is open. Server counts
   and persisted IsRead values remain authoritative.
   ============================================================ */
(function () {
  'use strict';

  var badge = document.getElementById('notifBadge');
  if (!badge || !window.signalR) return; // admin layout only; skip if the client failed to load

  var csrfMeta = document.querySelector('meta[name="csrf-token"]');
  var csrf = csrfMeta ? csrfMeta.getAttribute('content') : '';
  var knownIds = Object.create(null);
  var knownIdOrder = [];

  function rememberId(id) {
    if (!id) return true;
    var key = String(id);
    if (knownIds[key]) return false;
    knownIds[key] = true;
    knownIdOrder.push(key);
    if (knownIdOrder.length > 500) delete knownIds[knownIdOrder.shift()];
    return true;
  }

  /* ---------- badge ---------- */
  var unread = 0;
  var countVersion = 0;
  function renderBadge() {
    if (unread > 0) {
      badge.textContent = unread > 99 ? '99+' : String(unread);
      badge.hidden = false;
    } else {
      badge.hidden = true;
    }
  }
  function fetchUnreadCount() {
    var version = ++countVersion;
    return fetch('/admin/notifications/unread-count', { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (version === countVersion && data && typeof data.count === 'number') {
          unread = data.count;
          renderBadge();
        }
      })
      .catch(function () { /* keep last known count */ });
  }
  function postRead(id) {
    return fetch('/admin/notifications/' + encodeURIComponent(id) + '/read', {
      method: 'POST', credentials: 'same-origin',
      headers: { RequestVerificationToken: csrf, Accept: 'application/json' }
    }).then(function (r) {
      if (!r.ok) throw new Error('read-update-failed');
      return r.json().catch(function () { return { ok: true }; });
    });
  }
  function showReadError() {
    if (window.DeChimexToast)
      window.DeChimexToast.show('Could not update notification', 'Please try again. It is still unread.', null);
  }
  document.addEventListener('click', function (e) {
    var anchor = e.target && e.target.closest ? e.target.closest('a[data-notification-open-id]') : null;
    if (!anchor || anchor.getAttribute('data-notification-is-read') === 'true') return;
    e.preventDefault();
    if (anchor.getAttribute('aria-busy') === 'true') return;
    anchor.setAttribute('aria-busy', 'true');
    var notificationId = anchor.getAttribute('data-notification-open-id');
    var destination = anchor.href;
    postRead(notificationId).then(function () {
      anchor.setAttribute('data-notification-is-read', 'true');
      var row = anchor.closest('[data-notification-id]');
      if (row) row.setAttribute('data-is-read', 'true');
      unread = Math.max(0, unread - 1);
      renderBadge();
      fetchUnreadCount();
      window.location.assign(destination);
    }).catch(function () {
      anchor.removeAttribute('aria-busy');
      showReadError();
    });
  });

  /* ---------- live row rendering (mirrors Index.cshtml markup) ---------- */
  var listBody = null;
  var onNotificationsPage = false;
  function isNotificationsListPage() {
    return window.location.pathname.replace(/\/+$/, '') === '/admin/notifications';
  }
  function esc(s) {
    var d = document.createElement('div');
    d.textContent = s == null ? '' : String(s);
    return d.innerHTML;
  }
  function fmtDate(iso) {
    var dt = new Date(iso);
    if (isNaN(dt.getTime())) return '';
    var months = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    return dt.getDate() + ' ' + months[dt.getMonth()] + ' ' + dt.getFullYear() + ' ' + pad(dt.getHours()) + ':' + pad(dt.getMinutes());
  }
  function rowHtml(n) {
    var currentUrl = window.location.pathname + window.location.search;
    var actions = '';
    if (!n.isRead) {
      actions = '<span class="badge badge-primary">New</span>';
      if (n.link) {
        actions += '<a class="btn btn-outline btn-sm" style="margin-left:8px" href="' + esc(n.link) + '" ' +
          'data-notification-open-id="' + esc(n.id) + '" data-notification-is-read="false">Open</a>';
      }
      actions += '<form action="/admin/notifications/' + esc(n.id) + '/read" method="post" style="display:inline;margin-left:8px">' +
        '<input type="hidden" name="__RequestVerificationToken" value="' + esc(csrf) + '">' +
        '<input type="hidden" name="returnUrl" value="' + esc(currentUrl) + '">' +
        '<button type="submit" class="btn btn-ghost btn-sm">Mark read</button></form>';
    } else {
      actions = '<span class="badge badge-neutral">Read</span>';
      if (n.link) actions += '<a class="btn btn-outline btn-sm" style="margin-left:8px" href="' + esc(n.link) + '" ' +
        'data-notification-open-id="' + esc(n.id) + '" data-notification-is-read="true">Open</a>';
    }
    return '<div class="flex items-start gap-3" data-notification-id="' + esc(n.id) + '" data-is-read="' + (n.isRead ? 'true' : 'false') + '" style="padding:12px 0;border-bottom:1px solid var(--n-100)">' +
      '<div class="flex-1">' +
      '<div class="font-semibold" style="font-size:14px">' + esc(n.title) + '</div>' +
      '<div class="text-sm text-muted">' + esc(n.message) + '</div>' +
      '<div class="text-xs text-muted mt-1">' + fmtDate(n.createdUtc) + '</div>' +
      '</div><div class="flex items-center gap-2">' + actions + '</div></div>';
  }
  function prependLive(n) {
    if (!onNotificationsPage) return;
    if (!listBody) {
      listBody = document.querySelector('.card .card-body');
      if (!listBody) return;
      var empty = listBody.querySelector('.text-muted');
      if (empty && /^no notifications\.?$/i.test(empty.textContent.trim())) empty.remove();
    }
    var tpl = document.createElement('div');
    tpl.innerHTML = rowHtml(n).trim();
    var row = tpl.firstElementChild;
    if (row) listBody.insertBefore(row, listBody.firstChild);
  }

  /* A PWA notification click carries the row ID in its destination URL. Persist the
     read state at the destination, then remove the one-shot query parameter. */
  function consumePushReadIntent() {
    var target = new URL(window.location.href);
    var id = target.searchParams.get('dcxReadNotification');
    if (!id) return Promise.resolve();
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)) {
      target.searchParams.delete('dcxReadNotification');
      window.history.replaceState({}, '', target.pathname + target.search + target.hash);
      return Promise.resolve();
    }
    return postRead(id).then(function () {
      target.searchParams.delete('dcxReadNotification');
      window.history.replaceState({}, '', target.pathname + target.search + target.hash);
      fetchUnreadCount();
    }).catch(function () {
      target.searchParams.delete('dcxReadNotification');
      window.history.replaceState({}, '', target.pathname + target.search + target.hash);
      showReadError();
    });
  }

  /* ---------- toasts (skipped when the open page is the target) ---------- */
  function toast(n) {
    if (!window.DeChimexToast) return;
    // The workspace page surfaces its own richer toast for events on the open
    // order; a second generic toast here would be pure noise.
    if (n && n.link && window.location.pathname === n.link) return;
    var link = n.link ? {
      href: n.link,
      text: 'View',
      onClick: function (e) {
        if (!n.id || n.isRead) return;
        e.preventDefault();
        var anchor = e.currentTarget;
        if (anchor.getAttribute('aria-busy') === 'true') return;
        anchor.setAttribute('aria-busy', 'true');
        postRead(n.id).then(function () {
          n.isRead = true;
          unread = Math.max(0, unread - 1);
          renderBadge();
          window.location.assign(n.link);
        }).catch(function () {
          anchor.removeAttribute('aria-busy');
          showReadError();
        });
      }
    } : null;
    window.DeChimexToast.show(n.title || 'Notification', n.message || '', link);
  }

  /* ---------- SignalR connection ---------- */
  // Include IDs already present in the server-rendered list so a push racing with
  // the initial GET cannot count the same unread row twice in the local badge.
  document.querySelectorAll('[data-notification-id]').forEach(function (row) {
    rememberId(row.getAttribute('data-notification-id'));
  });
  onNotificationsPage = isNotificationsListPage();
  var readIntentReady = consumePushReadIntent();
  readIntentReady.then(fetchUnreadCount);
  var started = false;
  var connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/notifications', { withCredentials: true, transport: signalR.HttpTransportType.WebSockets })
    .withAutomaticReconnect([0, 2000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on('notification', function (n) {
    if (!n) return;
    if (!rememberId(n.id)) {
      fetchUnreadCount();
      return;
    }
    unread += 1;
    renderBadge();
    prependLive(n); // new live items are rendered unread; event delivery never marks them read
    toast(n);
    fetchUnreadCount(); // reconcile local increments with the persisted unread count
  });

  connection.onreconnected(function () {
    fetchUnreadCount(); // resync after the gap; missed pushes are recovered by the full list on next load
  });

  connection.start().then(function () {
    started = true;
    fetchUnreadCount();
  }).catch(function (err) {
    // Anonymous or auth failure: the badge stays hidden, the page still works without real time.
    console.warn('Notifications hub not connected:', err && err.message ? err.message : err);
  });

  window.addEventListener('beforeunload', function () {
    if (started) connection.stop().catch(function () {});
  });
})();
