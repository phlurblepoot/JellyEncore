/* Encore: "Not in library? Request" on the Jellyfin web search page (QBStream, Jellyfin 12.4).
   Paste into the JavaScript Injector plugin (or import jellyfin-request-search.import.json, then press Save).
   Fill in the two settings below. The https URL stays empty until Encore is published over https; until then an
   https Jellyfin page only gets a link to the Encore request page. Safe to run twice. */
(function () {
  'use strict';

  // ---- Settings -------------------------------------------------------------------------------------------
  var ENCORE_URL_HTTP = 'http://192.168.86.50:5095';
  var ENCORE_URL_HTTPS = '';
  // ---------------------------------------------------------------------------------------------------------

  var DEBOUNCE_MS = 600;
  var REFRESH_MS = 10000;
  var MIN_QUERY = 2;
  var MAX_QUERY = 100;
  var TERMINAL = { ready: 1, failed: 1, cancelled: 1 };

  // ---- Pure helpers (also exported for the node tests) ----------------------------------------------------
  function trimBase(u) {
    return String(u || '').replace(/\/+$/, '');
  }

  // 'api' = call Encore; 'link' = only the fallback link; 'none' = nothing configured.
  function chooseMode(protocol, httpUrl, httpsUrl) {
    var h = trimBase(httpUrl), s = trimBase(httpsUrl);
    if (protocol === 'https:') {
      if (s) return { mode: 'api', base: s };
      return h ? { mode: 'link', base: h } : { mode: 'none', base: '' };
    }
    if (h) return { mode: 'api', base: h };
    return { mode: 'none', base: '' };
  }

  function normalizeQuery(q) {
    return String(q == null ? '' : q).replace(/\s+/g, ' ').trim();
  }

  function queryOk(q) {
    return q.length >= MIN_QUERY && q.length <= MAX_QUERY;
  }

  function searchUrl(base, q) {
    return trimBase(base) + '/api/v1/search?q=' + encodeURIComponent(q) + '&kinds=song,album';
  }

  function requestsUrl(base) {
    return trimBase(base) + '/api/v1/requests';
  }

  function activeRequestsUrl(base) {
    return trimBase(base) + '/api/v1/requests?scope=mine&active=true';
  }

  function fallbackUrl(base, q) {
    return trimBase(base) + '/request?q=' + encodeURIComponent(q);
  }

  // The token goes only to a same-origin Jellyfin: serverAddress() must have the page's origin.
  function tokenAllowed(serverAddress, pageOrigin) {
    try {
      return new URL(String(serverAddress)).origin === pageOrigin;
    } catch (e) {
      return false;
    }
  }

  function stateLabel(state) {
    switch (state) {
      case 'queued': return 'Queued';
      case 'upcoming': return 'Upcoming';
      case 'searching': return 'Searching';
      case 'downloading': return 'Downloading';
      case 'importing': return 'Importing';
      case 'ready': return 'Ready';
      case 'failed': return 'Failed';
      case 'cancelled': return 'Cancelled';
      default: return '';
    }
  }

  function isActive(state) {
    return !!state && !TERMINAL[state];
  }

  // Search rows -> cards the section shows: only what is not already in the library.
  // Song rows (GET /api/v1/search): { deezerTrackId, title, artist, album, durationS, coverUrl, explicit, owned, itemId,
  // requestable, request: { id, state, detail, mine } | null }. Owned or not requestable rows are left out.
  // Album rows (Lidarr catalog): { foreignAlbumId, title, artist, foreignArtistId, year, albumType, secondaryTypes,
  // trackCount, coverUrl, released, releaseDate, owned, itemId, requestable, request }. Owned rows are left out; albums not
  // released yet are requestable and carry an "Upcoming" badge (with the date when Lidarr knows it).
  // "Upcoming: YYYY-MM-DD" (or "Upcoming: date not known yet") for an album not released yet; '' otherwise.
  function upcomingLabel(r) {
    if (!r || r.released !== false) return '';
    var d = typeof r.releaseDate === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(r.releaseDate) ? r.releaseDate : '';
    return d ? 'Upcoming: ' + d : 'Upcoming: date not known yet';
  }

  function buildCards(data) {
    var cards = [];
    if (!data || typeof data !== 'object') return cards;
    var songs = Array.isArray(data.songs) ? data.songs : [];
    var albums = Array.isArray(data.albums) ? data.albums : [];
    songs.forEach(function (r) {
      if (!r || r.owned || r.itemId || r.requestable === false) return;
      var id = r.deezerTrackId;
      if (id == null) return;
      cards.push({ kind: 'song', key: 'song:' + id, body: { kind: 'song', deezerTrackId: id },
        artist: r.artist || '', title: r.title || '', cover: r.coverUrl || '', request: r.request || null, released: true });
    });
    albums.forEach(function (r) {
      if (!r || r.owned || r.itemId) return;
      if (!r.foreignAlbumId) return;
      if (r.requestable === false) return;
      cards.push({ kind: 'album', key: 'album:' + r.foreignAlbumId, body: { kind: 'album', foreignAlbumId: r.foreignAlbumId },
        artist: r.artist || '', title: r.title || '', cover: r.coverUrl || '', request: r.request || null,
        released: r.released !== false, upcoming: upcomingLabel(r) });
    });
    return cards;
  }

  // The request detail with a retry time in the viewer's own time zone (the server sends nextTryUtc, a UTC timestamp).
  function detailText(req) {
    var detail = req && req.detail != null ? String(req.detail) : '';
    var next = req && req.nextTryUtc ? new Date(req.nextTryUtc) : null;
    if (next && !isNaN(next.getTime()) && /^Not found yet/.test(detail)) {
      return 'Not found yet, trying again at ' + next.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }
    return detail;
  }

  function cardLabel(c) {
    return (c.artist ? c.artist + ' - ' : '') + c.title;
  }

  var helpers = {
    chooseMode: chooseMode, normalizeQuery: normalizeQuery, queryOk: queryOk, searchUrl: searchUrl,
    requestsUrl: requestsUrl, activeRequestsUrl: activeRequestsUrl, fallbackUrl: fallbackUrl,
    tokenAllowed: tokenAllowed, stateLabel: stateLabel, isActive: isActive, buildCards: buildCards, cardLabel: cardLabel,
    upcomingLabel: upcomingLabel, detailText: detailText
  };
  if (typeof module !== 'undefined' && module.exports) { module.exports = helpers; }
  if (typeof window === 'undefined' || typeof document === 'undefined') return;

  // ---- Browser part ---------------------------------------------------------------------------------------
  if (window.__encoreRequestSearch) return;
  window.__encoreRequestSearch = true;

  var choice = chooseMode(location.protocol, ENCORE_URL_HTTP, ENCORE_URL_HTTPS);
  if (choice.mode === 'none') return;

  var section = null;          // the one .encore-request-section element
  var state = { query: '', cards: [], me: null, down: false, busy: {}, flash: {} };
  var searchCtl = null;        // AbortController of the running search
  var debounceTimer = null;
  var refreshTimer = null;
  var pageObserver = null;
  var observedPage = null;
  var attachedInput = null;
  var meLoaded = false;

  function debug(msg) {
    try { console.debug('[Encore request search] ' + msg); } catch (e) { /* ignore */ }
  }

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = text;
    return n;
  }

  // Read the token at call time, never cache it; send it only to a same-origin Jellyfin.
  function authHeaders(json) {
    var h = {};
    try {
      var c = window.ApiClient;
      if (c && tokenAllowed(c.serverAddress(), location.origin)) {
        var t = c.accessToken();
        if (t) h['Authorization'] = 'Bearer ' + t;
      }
    } catch (e) { /* no token */ }
    if (json) h['Content-Type'] = 'application/json';
    return h;
  }

  function api(method, url, body, signal) {
    var init = { method: method, headers: authHeaders(!!body), signal: signal };
    if (body) init.body = JSON.stringify(body);
    return fetch(url, init).then(function (res) {
      return res.text().then(function (txt) {
        var data = null;
        try { data = txt ? JSON.parse(txt) : null; } catch (e) { data = null; }
        if (!res.ok) {
          var err = new Error('HTTP ' + res.status);
          err.status = res.status;
          err.data = data;
          throw err;
        }
        return data;
      });
    });
  }

  function searchPage() {
    return document.querySelector('#searchPage');
  }

  function pageVisible() {
    var p = searchPage();
    return !!p && !p.classList.contains('hide') && p.offsetParent !== null;
  }

  function currentInput() {
    return document.querySelector('#searchPage #searchTextInput');
  }

  // ---- Rendering ------------------------------------------------------------------------------------------
  function ensureSection() {
    var page = searchPage();
    if (!page) return null;
    if (!section) {
      section = el('div', 'verticalSection encore-request-section');
      section.style.display = 'none';
    }
    if (section.parentNode !== page || page.lastElementChild !== section) {
      try { page.appendChild(section); } catch (e) { return null; }
    }
    return section;
  }

  function clear(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function card(c) {
    var box = el('div', 'encore-request-card');
    box.style.cssText = 'display:flex;flex-direction:column;width:9.5em;flex:0 0 auto;margin-right:1em;';
    var cover = el('div', 'encore-request-cover');
    cover.style.cssText = 'width:9.5em;height:9.5em;background:rgba(128,128,128,.25);border-radius:.3em;overflow:hidden;';
    if (c.cover && /^https?:\/\//i.test(c.cover)) {
      var img = el('img');
      img.setAttribute('src', c.cover);
      img.setAttribute('alt', '');
      img.setAttribute('loading', 'lazy');
      img.setAttribute('referrerpolicy', 'no-referrer');
      img.style.cssText = 'width:100%;height:100%;object-fit:cover;';
      cover.appendChild(img);
    }
    box.appendChild(cover);
    var name = el('div', 'encore-request-name', cardLabel(c));
    name.style.cssText = 'margin:.4em 0;font-size:.9em;overflow:hidden;text-overflow:ellipsis;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;';
    name.setAttribute('title', cardLabel(c));
    box.appendChild(name);
    var kind = el('div', 'encore-request-kind', c.kind === 'album' ? 'Album' : 'Song');
    kind.style.cssText = 'font-size:.75em;opacity:.7;margin-bottom:.4em;';
    box.appendChild(kind);

    var req = c.request;
    var flash = state.flash[c.key];
    if (req && req.state && (isActive(req.state) || req.state === 'ready')) {
      var chip = el('div', 'encore-request-chip', stateLabel(req.state));
      chip.style.cssText = 'font-size:.85em;padding:.3em .6em;border-radius:1em;background:rgba(128,128,128,.3);align-self:flex-start;';
      if (detailText(req)) chip.setAttribute('title', detailText(req));
      box.appendChild(chip);
    } else {
      if (c.upcoming) {
        var badge = el('div', 'encore-request-upcoming', c.upcoming);
        badge.style.cssText = 'font-size:.75em;padding:.2em .5em;border-radius:1em;background:rgba(128,128,128,.3);align-self:flex-start;margin-bottom:.3em;';
        box.appendChild(badge);
      }
      if (state.me && state.me.canRequest === false) {
        // no button: the section shows the "not enabled" notice once
      } else {
        var label = state.busy[c.key] ? 'Requesting...' : (req && req.state === 'failed' ? 'Request again' : 'Request');
        var btn = el('button', 'emby-button raised encore-request-button', label);
        btn.setAttribute('type', 'button');
        if (state.busy[c.key]) btn.disabled = true;
        btn.addEventListener('click', function () { createRequest(c); });
        box.appendChild(btn);
      }
    }
    if (flash) {
      var f = el('div', 'encore-request-flash', flash);
      f.style.cssText = 'font-size:.8em;margin-top:.3em;opacity:.85;';
      box.appendChild(f);
    }
    return box;
  }

  function render() {
    var sec = ensureSection();
    if (!sec) return;
    var q = state.query;
    clear(sec);
    if (!queryOk(q)) { sec.style.display = 'none'; return; }

    if (choice.mode === 'link') {
      sec.style.display = '';
      var linkTitle = el('h2', 'sectionTitle sectionTitle-cards padded-left');
      var a = el('a', 'encore-request-link', 'Not in library? Search requests on Encore');
      a.setAttribute('href', fallbackUrl(choice.base, q));
      a.setAttribute('target', '_blank');
      a.setAttribute('rel', 'noopener noreferrer');
      linkTitle.appendChild(a);
      sec.appendChild(linkTitle);
      return;
    }
    if (state.down) {
      sec.style.display = '';
      sec.appendChild(el('div', 'encore-request-unavailable padded-left', 'Requests unavailable'));
      return;
    }
    if (!state.cards.length) { sec.style.display = 'none'; return; }

    sec.style.display = '';
    sec.appendChild(el('h2', 'sectionTitle sectionTitle-cards padded-left', 'Not in library? Request'));
    if (state.me && state.me.canRequest === false) {
      sec.appendChild(el('div', 'encore-request-notice padded-left', 'Requests are not enabled for your account'));
    }
    var row = el('div', 'encore-request-row padded-left');
    row.style.cssText = 'display:flex;overflow-x:auto;padding-bottom:1em;';
    state.cards.forEach(function (c) { row.appendChild(card(c)); });
    sec.appendChild(row);
  }

  // ---- Data -----------------------------------------------------------------------------------------------
  function loadMe() {
    if (meLoaded || choice.mode !== 'api') return;
    meLoaded = true;
    api('GET', trimBase(choice.base) + '/api/v1/me', null, null).then(function (me) {
      state.me = me;
      render();
    }).catch(function () {
      meLoaded = false;
    });
  }

  function runSearch(q) {
    if (searchCtl) { searchCtl.abort(); searchCtl = null; }
    state.query = q;
    if (!queryOk(q)) { state.cards = []; state.down = false; render(); stopRefresh(); return; }
    if (choice.mode === 'link') { render(); return; }
    var ctl = new AbortController();
    searchCtl = ctl;
    loadMe();
    api('GET', searchUrl(choice.base, q), null, ctl.signal).then(function (data) {
      if (searchCtl !== ctl) return;
      state.down = false;
      state.cards = buildCards(data);
      state.flash = {};
      render();
      startRefresh();
    }).catch(function (e) {
      if (e && e.name === 'AbortError') return;
      if (searchCtl !== ctl) return;
      debug('search failed: ' + (e && e.message));
      state.cards = [];
      state.down = true;
      render();
      stopRefresh();
    });
  }

  function createRequest(c) {
    if (state.busy[c.key]) return;
    state.busy[c.key] = true;
    delete state.flash[c.key];
    render();
    api('POST', requestsUrl(choice.base), c.body, null).then(function (req) {
      c.request = req;
    }).catch(function (e) {
      var msg = 'Could not request this';
      if (e && e.status === 429) msg = 'Daily request limit reached';
      else if (e && e.status === 403) { msg = 'Requests are not enabled for your account'; if (state.me) state.me.canRequest = false; }
      else if (e && e.status === 401) msg = 'Sign in to Jellyfin again';
      else if (e && e.status === 409) msg = 'Already requested';
      else if (e && e.status === 503) msg = 'Requests unavailable right now';
      state.flash[c.key] = msg;
    }).then(function () {
      delete state.busy[c.key];
      render();
      startRefresh();
    });
  }

  // Status refresh: while the section is visible and something is active, re-read my active requests every 10 s.
  function anyActive() {
    return state.cards.some(function (c) { return c.request && isActive(c.request.state); });
  }

  function sectionShown() {
    return !!section && section.style.display !== 'none' && !!section.parentNode;
  }

  function refreshNow() {
    if (choice.mode !== 'api' || !pageVisible() || !sectionShown() || !anyActive()) return;
    api('GET', activeRequestsUrl(choice.base), null, null).then(function (list) {
      var rows = Array.isArray(list) ? list : (list && Array.isArray(list.requests) ? list.requests : []);
      var byId = {};
      rows.forEach(function (r) { if (r && r.id != null) byId[r.id] = r; });
      var changed = false;
      state.cards.forEach(function (c) {
        if (!c.request || c.request.id == null || !isActive(c.request.state)) return;
        var fresh = byId[c.request.id];
        if (fresh) {
          if (fresh.state !== c.request.state || fresh.detail !== c.request.detail || fresh.nextTryUtc !== c.request.nextTryUtc) changed = true;
          c.request = fresh;
        } else if (c.request.mine !== false) {
          // no longer active: read the single request for its final state (only my own requests can be read)
          api('GET', requestsUrl(choice.base) + '/' + encodeURIComponent(c.request.id), null, null).then(function (one) {
            if (one) { c.request = one; render(); }
          }).catch(function () { /* quiet */ });
        }
      });
      if (changed) render();
    }).catch(function (e) { debug('refresh failed: ' + (e && e.message)); });
  }

  function startRefresh() {
    if (refreshTimer || choice.mode !== 'api') return;
    refreshTimer = setInterval(function () {
      if (!pageVisible()) { stopRefresh(); return; }
      refreshNow();
    }, REFRESH_MS);
  }

  function stopRefresh() {
    if (refreshTimer) { clearInterval(refreshTimer); refreshTimer = null; }
  }

  // ---- Hooks ----------------------------------------------------------------------------------------------
  function onInput() {
    if (debounceTimer) clearTimeout(debounceTimer);
    var input = attachedInput;
    debounceTimer = setTimeout(function () {
      debounceTimer = null;
      if (input) runSearch(normalizeQuery(input.value));
    }, DEBOUNCE_MS);
  }

  // React replaces its own nodes under the page: keep the section the last child of #searchPage.
  function watchPage() {
    var page = searchPage();
    if (page === observedPage) return;
    if (pageObserver) { pageObserver.disconnect(); pageObserver = null; }
    observedPage = page;
    if (!page || typeof MutationObserver === 'undefined') return;
    pageObserver = new MutationObserver(function () {
      if (section && section.parentNode === page && page.lastElementChild === section) return;
      if (section) ensureSection();
    });
    pageObserver.observe(page, { childList: true });
  }

  function attach() {
    var input = currentInput();
    watchPage();
    if (!input) {
      attachedInput = null;
      if (!pageVisible()) stopRefresh();
      return;
    }
    if (input === attachedInput) return;
    if (attachedInput) attachedInput.removeEventListener('input', onInput);
    attachedInput = input;
    input.addEventListener('input', onInput);
    var q = normalizeQuery(input.value);
    if (!q) {
      try { q = normalizeQuery(new URLSearchParams(location.hash.split('?')[1] || '').get('query') || ''); } catch (e) { q = ''; }
    }
    if (q) runSearch(q); else { state.query = ''; render(); }
  }

  var attachTimer = null;
  function scheduleAttach() {
    if (attachTimer) return;
    attachTimer = setTimeout(function () { attachTimer = null; attach(); }, 100);
  }

  function hookNavigation() {
    ['pushState', 'replaceState'].forEach(function (name) {
      var orig = history[name];
      if (typeof orig !== 'function') return;
      history[name] = function () {
        var r = orig.apply(this, arguments);
        scheduleAttach();
        return r;
      };
    });
    window.addEventListener('popstate', scheduleAttach);
    window.addEventListener('hashchange', scheduleAttach);
    new MutationObserver(scheduleAttach).observe(document.body, { childList: true, subtree: true });
  }

  try {
    hookNavigation();
    attach();
  } catch (e) {
    debug('start failed: ' + (e && e.message));
  }
})();
