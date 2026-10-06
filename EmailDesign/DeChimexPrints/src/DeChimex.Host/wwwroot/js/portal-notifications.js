/* ============================================================
   DE-CHIMEX PRINT HUB V2 - portal notifications client
   Bell badge + read-only dropdown + explicit read actions for customers.
   Connects to /hubs/notifications (SignalR, cookie auth); the hub auto-joins
   the caller's user:{id} group, so customers only ever receive their own
   direct notifications. The server remains the source of truth for counts.
   ============================================================ */
(function () {
  'use strict';

  var bell = document.getElementById('portalNotifBell');
  var badge = document.getElementById('portalNotifBadge');
  var drop = document.getElementById('portalNotifDrop');
  if (!bell || !badge || !drop || !window.signalR) return;

  var csrfMeta = document.querySelector('meta[name="csrf-token"]');
  var csrf = csrfMeta ? csrfMeta.getAttribute('content') : '';
  var listEl = drop.querySelector('.portal-notif-list');
  var markAllButton = document.getElementById('portalNotifMarkAll');
  var loaded = false;
  var markAllPending = false;
  var unread = 0;
  var countVersion = 0;
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
  function renderBadge() {
    if (unread > 0) {
      badge.textContent = unread > 99 ? '99+' : String(unread);
      badge.hidden = false;
    } else {
      badge.hidden = true;
    }
    if (markAllButton) markAllButton.disabled = unread <= 0 || markAllPending;
    updateOsBadge();
  }
  // Badging API: app-icon unread count on installed PWAs. Progressive enhancement only;
  // the in-app badge above stays the authoritative indicator.
  function updateOsBadge() {
    if (!navigator.setAppBadge) return;
    try {
      if (unread > 0) navigator.setAppBadge(unread > 99 ? 99 : unread);
      else navigator.clearAppBadge();
    } catch (e) { /* unsupported here */ }
  }
  function fetchUnreadCount() {
    var version = ++countVersion;
    return fetch('/portal/notifications/unread-count', { credentials: 'same-origin', headers: { Accept: 'application/json' } })
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
    return fetch('/portal/notifications/' + encodeURIComponent(id) + '/read', {
      method: 'POST',
      credentials: 'same-origin',
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
  function setRowRead(row, notification) {
    notification.isRead = true;
    row.classList.remove('is-unread');
    row.classList.add('is-read');
    row.setAttribute('aria-label', (notification.title || 'Notification') + ' - read');
    var dot = row.querySelector('.portal-notif-dot');
    if (dot) dot.remove();
  }

  /* ---------- dropdown ---------- */
  function fmtDate(iso) {
    var dt = new Date(iso);
    if (isNaN(dt.getTime())) return '';
    var months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    return dt.getDate() + ' ' + months[dt.getMonth()] + ' ' + pad(dt.getHours()) + ':' + pad(dt.getMinutes());
  }
  function rowNode(n) {
    rememberId(n.id);
    var a = document.createElement('a');
    a.className = 'portal-notif-row ' + (n.isRead ? 'is-read' : 'is-unread');
    a.href = n.link || '#';
    a.setAttribute('data-notif-id', n.id || '');
    a.setAttribute('aria-label', (n.title || 'Notification') + (n.isRead ? ' - read' : ' - unread'));
    var title = document.createElement('div');
    title.className = 'portal-notif-title portal-notif-row-title';
    if (!n.isRead) {
      var dot = document.createElement('span');
      dot.className = 'portal-notif-dot';
      dot.setAttribute('aria-hidden', 'true');
      title.appendChild(dot);
    }
    var titleText = document.createElement('span');
    titleText.textContent = n.title || 'Notification';
    title.appendChild(titleText);
    var msg = document.createElement('div');
    msg.className = 'portal-notif-msg';
    msg.textContent = n.message || '';
    var when = document.createElement('div');
    when.className = 'portal-notif-when';
    // Recent-list DTOs carry createdDateUtc; live hub pushes carry createdUtc.
    when.textContent = fmtDate(n.createdDateUtc || n.createdUtc);
    a.appendChild(title);
    a.appendChild(msg);
    a.appendChild(when);
    a.addEventListener('click', function (e) {
      if (!n.link) { e.preventDefault(); return; }
      if (n.isRead || !n.id) return;
      e.preventDefault();
      if (a.getAttribute('aria-busy') === 'true') return;
      a.setAttribute('aria-busy', 'true');
      postRead(n.id).then(function () {
        setRowRead(a, n);
        unread = Math.max(0, unread - 1);
        renderBadge();
        // Persist succeeded; only now follow the notification's existing deep link.
        window.location.assign(n.link);
      }).catch(function () {
        a.removeAttribute('aria-busy');
        showReadError();
      });
    });
    return a;
  }
  function renderList(items) {
    if (!listEl) return;
    listEl.innerHTML = '';
    if (!items || items.length === 0) {
      var empty = document.createElement('div');
      empty.className = 'portal-notif-empty';
      empty.textContent = 'No notifications yet.';
      listEl.appendChild(empty);
      return;
    }
    items.slice(0, 10).forEach(function (n) { listEl.appendChild(rowNode(n)); });
  }
  function loadRecent() {
    return fetch('/portal/notifications/recent', { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(function (r) {
        if (!r.ok) throw new Error('recent-load-failed');
        return r.json();
      })
      .then(function (data) {
        loaded = true;
        renderList(data);
      })
      .catch(function () { loaded = false; /* retry when the dropdown is opened again */ });
  }
  function updateVisibleRowsAsRead() {
    if (!listEl) return;
    listEl.querySelectorAll('.portal-notif-row.is-unread').forEach(function (row) {
      row.classList.remove('is-unread');
      row.classList.add('is-read');
      row.setAttribute('aria-label', row.getAttribute('aria-label').replace(/ - unread$/, ' - read'));
      var dot = row.querySelector('.portal-notif-dot');
      if (dot) dot.remove();
    });
  }
  function markAllRead() {
    if (!markAllButton || markAllPending || unread <= 0) return;
    markAllPending = true;
    renderBadge();
    fetch('/portal/notifications/read-all', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { RequestVerificationToken: csrf, Accept: 'application/json' }
    }).then(function (r) {
      if (!r.ok) throw new Error('mark-all-failed');
      return r.json();
    }).then(function (data) {
      if (!data || data.ok !== true) throw new Error('mark-all-failed');
      updateVisibleRowsAsRead();
      markAllPending = false;
      unread = 0;
      renderBadge();
      fetchUnreadCount();
    }).catch(function () {
      markAllPending = false;
      renderBadge();
      showReadError();
    });
  }
  function toggleDrop(force) {
    var open = typeof force === 'boolean' ? force : drop.hidden;
    drop.hidden = !open;
    bell.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (open && !loaded) loadRecent();
    if (open) ensurePushRow();
  }
  if (markAllButton) markAllButton.addEventListener('click', markAllRead);
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
      window.location.assign(destination);
    }).catch(function () {
      anchor.removeAttribute('aria-busy');
      showReadError();
    });
  });

  /* A PWA notification click carries an ID in the target URL. Persist the read state
     on the destination page, then remove the one-shot query parameter from the address. */
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

  /* ---------- PWA push notifications (opt-in, never automatic) ---------- */
  var pushRowBuilt = false;
  function pushSupported() {
    return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
  }
  function ensurePushRow() {
    if (pushRowBuilt || !pushSupported()) return;
    var row = document.getElementById('portalNotifPush');
    if (!row) return;
    pushRowBuilt = true;
    renderPushRow();
  }
  function renderPushRow() {
    var row = document.getElementById('portalNotifPush');
    if (!row || !pushSupported()) return;
    row.textContent = '';
    var link = document.createElement('a');
    link.href = '#';
    link.style.cssText = 'display:flex;gap:8px;align-items:center;padding:10px 14px;border-top:1px solid var(--border);font-size:12.5px;font-weight:600;color:var(--n-700,#334155)';
    if (Notification.permission === 'denied') {
      var span = document.createElement('span');
      span.textContent = 'Notifications are turned off in your browser settings.';
      span.style.cssText = 'color:var(--n-400,#94a3b8);font-weight:500';
      link.appendChild(span);
      link.addEventListener('click', function (e) { e.preventDefault(); });
    } else {
      link.addEventListener('click', function (e) { e.preventDefault(); onPushToggle(); });
      // Label reflects the actual subscription state (fetched async).
      link.textContent = 'Checking notifications\u2026';
      navigator.serviceWorker.ready.then(function (reg) {
        return reg.pushManager.getSubscription();
      }).then(function (sub) {
        link.textContent = sub ? 'Turn off push notifications' : 'Turn on push notifications';
      }).catch(function () { link.textContent = 'Turn on push notifications'; });
    }
    row.appendChild(link);
  }
  function base64ToUint8Array(base64) {
    var padding = '='.repeat((4 - base64.length % 4) % 4);
    var b64 = (base64 + padding).replace(/-/g, '+').replace(/_/g, '/');
    var raw = window.atob(b64);
    var out = new Uint8Array(raw.length);
    for (var i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
    return out;
  }
  function onPushToggle() {
    navigator.serviceWorker.ready.then(function (reg) {
      return reg.pushManager.getSubscription().then(function (sub) {
        if (sub) {
          // Disable: tell the server first (it must stop sending), then drop locally.
          return postJson('/portal/notifications/push/unsubscribe', { endpoint: sub.endpoint })
            .then(function () { return sub.unsubscribe(); })
            .then(function () { window.DeChimexToast && window.DeChimexToast.show('Notifications', 'Push notifications turned off for this device.', null); renderPushRow(); });
        }
        // Enable: explicit user tap -> browser permission prompt -> subscription.
        return Notification.requestPermission().then(function (permission) {
          if (permission !== 'granted') { renderPushRow(); return; }
          return fetch('/portal/notifications/push/vapid-public-key', { credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (data) {
              if (!data || !data.key) throw new Error('unavailable');
              return reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: base64ToUint8Array(data.key) });
            })
            .then(function (sub2) {
              var keys = sub2.toJSON().keys || {};
              return postJson('/portal/notifications/push/subscribe', {
                endpoint: sub2.endpoint, keysP256dh: keys.p256dh, keysAuth: keys.auth,
                userAgent: navigator.userAgent
              });
            })
            .then(function () {
              window.DeChimexToast && window.DeChimexToast.show('Notifications', "You'll get updates about your orders on this device.", null);
              renderPushRow();
            });
        });
      });
    }).catch(function () {
      window.DeChimexToast && window.DeChimexToast.show('Notifications', 'Push notifications are not available right now.', null);
    });
  }
  function postJson(url, body) {
    return fetch(url, {
      method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', RequestVerificationToken: csrf },
      body: JSON.stringify(body)
    }).then(function (r) { if (!r.ok) throw new Error('push-post'); return r; });
  }
  bell.addEventListener('click', function (e) {
    e.stopPropagation();
    toggleDrop();
  });
  document.addEventListener('click', function (e) {
    if (!drop.hidden && !drop.contains(e.target)) toggleDrop(false);
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && !drop.hidden) toggleDrop(false);
  });

  /* ---------- SignalR ---------- */
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
    loaded = false; // refetch from the server; receiving the event never changes IsRead
    if (listEl && !drop.hidden) loadRecent();
    // No toast when the open page is the notification's target: the page's own
    // live sync already surfaced the event (workspace shows its own toast).
    var onTarget = n.link && window.location.pathname === n.link;
    if (!onTarget && window.DeChimexToast) {
      var toastLink = n.link ? {
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
      window.DeChimexToast.show(n.title || 'Notification', n.message || '', toastLink);
    }
    fetchUnreadCount(); // reconcile local increments with the persisted unread count
  });

  connection.onreconnected(function () {
    fetchUnreadCount();
    loaded = false;
  });

  connection.start().then(function () {
    started = true;
    fetchUnreadCount();
  }).catch(function (err) {
    console.warn('Portal notifications hub not connected:', err && err.message ? err.message : err);
  });

  window.addEventListener('beforeunload', function () {
    if (started) connection.stop().catch(function () { });
  });
})();
