/* Encore request page (design v03 10.1). One ES2020 script, no build step, no dependencies.
 * Everything that comes from the server, Deezer, Lidarr or Jellyfin is put on the page with textContent or an
 * attribute setter. No markup string is ever built or parsed here (tests/js/page.test.mjs checks the banned calls).
 * The CSP also forbids inline script and inline style attributes. */
'use strict';

(function (root) {
  // ---------------------------------------------------------------- pure helpers (tested under node)

  var ACTIVE_STATES = ['queued', 'searching', 'downloading', 'importing'];

  var STATE_LABELS = {
    queued: 'Queued',
    searching: 'Searching',
    downloading: 'Downloading',
    importing: 'Importing',
    ready: 'Ready',
    failed: 'Failed',
    cancelled: 'Cancelled',
  };

  var ERROR_TEXT = {
    invalid_token: 'Your session ended. Please sign in again.',
    not_allowed: 'You are not allowed to make requests.',
    admin_only: 'Only an administrator can do that.',
    not_found: 'That request no longer exists.',
    conflict: 'That request cannot be changed any more.',
    not_released: 'That album has not been released yet.',
    daily_limit: 'You have reached your daily request limit. Try again tomorrow.',
    login_limited: 'Too many sign-in attempts. Please wait and try again.',
    songs_unavailable: 'Song requests are switched off.',
    albums_unavailable: 'Album requests are not available right now.',
    jellyfin_unavailable: 'Jellyfin did not answer. Try again in a moment.',
    bad_request: 'That did not look right. Check what you typed.',
    internal_error: 'Something went wrong on the server.',
  };

  /** Is this request state one that is still being worked on? */
  function isActive(state) {
    return ACTIVE_STATES.indexOf(state) !== -1;
  }

  /** Plain label for a request state; unknown states are shown as they are, tidied. */
  function stateLabel(state) {
    if (Object.prototype.hasOwnProperty.call(STATE_LABELS, state)) {
      return STATE_LABELS[state];
    }
    return typeof state === 'string' && state ? state.charAt(0).toUpperCase() + state.slice(1) : 'Unknown';
  }

  /** CSS class suffix for a chip. */
  function chipClass(state) {
    return Object.prototype.hasOwnProperty.call(STATE_LABELS, state) ? state : 'unknown';
  }

  /** Strips control characters (a text node cannot run script, but a stray newline or bell has no place in a title). */
  function clean(value) {
    if (value === null || value === undefined) {
      return '';
    }
    return String(value).replace(/[\u0000-\u001f\u007f]/g, ' ').trim();
  }

  /** Only http and https URLs may become an href or an img src. Anything else gives null. */
  function safeHttpUrl(value) {
    if (typeof value !== 'string') {
      return null;
    }
    var text = value.trim();
    if (!/^https?:\/\//i.test(text) || /[\u0000-\u001f\u007f\s"'<>`]/.test(text)) {
      return null;
    }
    return text;
  }

  /** Only https images are allowed by the CSP (img-src 'self' https: data:). */
  function safeImageUrl(value) {
    var url = safeHttpUrl(value);
    return url && /^https:/i.test(url) ? url : null;
  }

  /** The path base the page was loaded under: everything before the trailing /request. "/encore/request" -> "/encore". */
  function basePathOf(pathname) {
    var text = typeof pathname === 'string' ? pathname : '/request';
    var match = /^(.*?)\/request(?:\/.*)?$/.exec(text);
    var base = match ? match[1] : '';
    return base.replace(/\/+$/, '');
  }

  /** An API URL under the path base. */
  function apiUrl(base, path) {
    return (base || '') + '/api/v1/' + String(path).replace(/^\/+/, '');
  }

  /** {origin}{base}/request?q=... (the link the injected script shows on an https page; the page reads ?q= back). */
  function requestPageUrl(origin, base, query) {
    var url = String(origin || '').replace(/\/+$/, '') + (base || '') + '/request';
    return query ? url + '?q=' + encodeURIComponent(query) : url;
  }

  /** "Open in Jellyfin": {jellyfinUrl}/web/#/details?id={itemId}&serverId={serverId}; serverId left out while unknown. */
  function detailsLink(jellyfinUrl, itemId, serverId) {
    var site = safeHttpUrl(jellyfinUrl);
    var id = clean(itemId);
    if (!site || !/^[0-9a-fA-F-]{32,36}$/.test(id)) {
      return null;
    }
    var link = site.replace(/\/+$/, '') + '/web/#/details?id=' + encodeURIComponent(id);
    var server = clean(serverId);
    if (server) {
      link += '&serverId=' + encodeURIComponent(server);
    }
    return link;
  }

  /** The q parameter of a location.search string, trimmed to 100 characters. */
  function queryFromSearch(search) {
    var text = typeof search === 'string' ? search : '';
    var match = /[?&]q=([^&#]*)/.exec(text);
    if (!match) {
      return '';
    }
    try {
      return clean(decodeURIComponent(match[1].replace(/\+/g, ' '))).slice(0, 100);
    } catch (e) {
      return '';
    }
  }

  /** How long to wait before asking again: 5 s while anything is active, else 60 s. */
  function pollDelay(requests) {
    var list = Array.isArray(requests) ? requests : [];
    for (var i = 0; i < list.length; i++) {
      if (list[i] && isActive(list[i].state)) {
        return 5000;
      }
    }
    return 60000;
  }

  /** May this person cancel this request? Owner while queued or searching; an admin while any active state. */
  function canCancel(request, me) {
    if (!request || !me) {
      return false;
    }
    if (me.isAdmin) {
      return isActive(request.state);
    }
    return request.state === 'queued' || request.state === 'searching';
  }

  /** Failed requests can be retried by their owner or an admin. */
  function canRetry(request) {
    return !!request && request.state === 'failed';
  }

  /** "3 min ago" style text. */
  function timeAgo(iso, nowMs) {
    var then = Date.parse(iso);
    if (isNaN(then)) {
      return '';
    }
    var seconds = Math.max(0, Math.round(((typeof nowMs === 'number' ? nowMs : Date.now()) - then) / 1000));
    if (seconds < 60) {
      return 'just now';
    }
    var minutes = Math.round(seconds / 60);
    if (minutes < 60) {
      return minutes + ' min ago';
    }
    var hours = Math.round(minutes / 60);
    if (hours < 48) {
      return hours + ' h ago';
    }
    return Math.round(hours / 24) + ' days ago';
  }

  /** "artist - title" with either part optional. */
  function headline(row) {
    var artist = clean(row && row.artist);
    var title = clean(row && row.title);
    return artist && title ? artist + ' - ' + title : artist || title || 'Unknown';
  }

  /** The message to show for a failed API call. Server text is shown as plain text, so it is safe either way. */
  function errorMessage(status, code, message, retryAfter) {
    if (code === 'login_limited' && retryAfter) {
      var minutes = Math.max(1, Math.ceil(Number(retryAfter) / 60));
      return 'Too many sign-in attempts. Try again in about ' + minutes + ' min.';
    }
    if (status === 401 && code !== 'invalid_token' && message) {
      return clean(message);
    }
    if (Object.prototype.hasOwnProperty.call(ERROR_TEXT, code)) {
      return ERROR_TEXT[code];
    }
    return clean(message) || 'Something went wrong (' + status + ').';
  }

  /** A random 32 hex digit device id. */
  function newDeviceId(cryptoObject) {
    var bytes = new Uint8Array(16);
    if (cryptoObject && cryptoObject.getRandomValues) {
      cryptoObject.getRandomValues(bytes);
    } else {
      for (var i = 0; i < bytes.length; i++) {
        bytes[i] = Math.floor(Math.random() * 256);
      }
    }
    var out = '';
    for (var j = 0; j < bytes.length; j++) {
      out += (bytes[j] < 16 ? '0' : '') + bytes[j].toString(16);
    }
    return out;
  }

  var helpers = {
    isActive: isActive,
    stateLabel: stateLabel,
    chipClass: chipClass,
    clean: clean,
    safeHttpUrl: safeHttpUrl,
    safeImageUrl: safeImageUrl,
    basePathOf: basePathOf,
    apiUrl: apiUrl,
    requestPageUrl: requestPageUrl,
    detailsLink: detailsLink,
    queryFromSearch: queryFromSearch,
    pollDelay: pollDelay,
    canCancel: canCancel,
    canRetry: canRetry,
    timeAgo: timeAgo,
    headline: headline,
    errorMessage: errorMessage,
    newDeviceId: newDeviceId,
  };

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = helpers;
  }
  if (typeof document === 'undefined') {
    return;
  }

  // ---------------------------------------------------------------- the page

  var TOKEN_KEY = 'encore.token';
  var DEVICE_KEY = 'encore.deviceId';
  var base = basePathOf(root.location.pathname);

  var S = {
    cfg: null,
    me: null,
    token: null,
    view: 'search',
    mine: [],
    all: [],
    results: null,
    query: '',
    searching: false,
    searchError: '',
    rowErrors: {},
    quick: null,
    pollTimer: 0,
    searchTimer: 0,
    searchAbort: null,
    notice: '',
  };
  var ui = {};

  function store(name, value) {
    try {
      if (value === undefined) {
        return root.localStorage.getItem(name);
      }
      if (value === null) {
        root.localStorage.removeItem(name);
      } else {
        root.localStorage.setItem(name, value);
      }
    } catch (e) {
      /* storage can be blocked: the page then asks for sign-in each visit */
    }
    return null;
  }

  function deviceId() {
    var id = store(DEVICE_KEY);
    if (!id || !/^[0-9a-f]{32}$/.test(id)) {
      id = newDeviceId(root.crypto);
      store(DEVICE_KEY, id);
    }
    return id;
  }

  // ---- DOM building (textContent and attribute setters only)

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) {
      node.className = className;
    }
    if (text !== undefined && text !== null) {
      node.textContent = text;
    }
    return node;
  }

  function add(parent) {
    for (var i = 1; i < arguments.length; i++) {
      if (arguments[i]) {
        parent.appendChild(arguments[i]);
      }
    }
    return parent;
  }

  function button(label, className, handler) {
    var node = el('button', className || 'btn', label);
    node.type = 'button';
    node.addEventListener('click', handler);
    return node;
  }

  function chip(state, text) {
    return el('span', 'chip chip-' + chipClass(state), text || stateLabel(state));
  }

  function linkTo(href, label) {
    var node = el('a', 'link', label);
    node.href = href;
    node.target = '_blank';
    node.rel = 'noopener noreferrer';
    return node;
  }

  function cover(url) {
    var safe = safeImageUrl(url);
    if (!safe) {
      return el('div', 'cover cover-empty');
    }
    var img = el('img', 'cover');
    img.alt = '';
    img.loading = 'lazy';
    img.referrerPolicy = 'no-referrer';
    img.src = safe;
    return img;
  }

  function openLink(itemId) {
    var href = detailsLink(S.cfg && S.cfg.jellyfinUrl, itemId, S.cfg && S.cfg.jellyfinServerId);
    return href ? linkTo(href, 'Open in Jellyfin') : null;
  }

  // ---- API

  function ApiError(status, code, message, retryAfter) {
    this.status = status;
    this.code = code;
    this.retryAfter = retryAfter;
    this.text = errorMessage(status, code, message, retryAfter);
  }

  function api(method, path, body, signal) {
    var headers = { Accept: 'application/json' };
    if (S.token) {
      headers.Authorization = 'Bearer ' + S.token;
    }
    var options = { method: method, headers: headers, signal: signal, cache: 'no-store', credentials: 'omit' };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(body);
    }
    return root.fetch(apiUrl(base, path), options).then(function (response) {
      if (response.status === 204) {
        return null;
      }
      return response.text().then(function (text) {
        var data = null;
        try {
          data = text ? JSON.parse(text) : null;
        } catch (e) {
          data = null;
        }
        if (!response.ok) {
          var code = data && typeof data.error === 'string' ? data.error : '';
          if (response.status === 401 && S.token && code === 'invalid_token') {
            endSession('Your session ended. Please sign in again.');
          }
          throw new ApiError(response.status, code, data && data.message, response.headers.get('Retry-After'));
        }
        return data;
      });
    }, function (error) {
      if (error && error.name === 'AbortError') {
        throw error;
      }
      throw new ApiError(0, '', 'Encore did not answer. Check your connection.');
    });
  }

  function failText(error) {
    return error instanceof ApiError ? error.text : 'Something went wrong.';
  }

  // ---- sign-in

  function endSession(message) {
    S.token = null;
    S.me = null;
    S.mine = [];
    S.all = [];
    S.results = null;
    S.notice = message || '';
    store(TOKEN_KEY, null);
    stopPolling();
    stopQuick();
    renderSignIn();
  }

  function startSession(token) {
    S.token = token;
    store(TOKEN_KEY, token);
    return api('GET', 'me').then(function (me) {
      S.me = me;
      S.notice = '';
      renderMain();
      if (S.query.length >= 2) {
        runSearch();
      }
      loadAndPoll();
    });
  }

  function renderSignIn() {
    var root_ = ui.root;
    root_.replaceChildren();
    var card = el('section', 'card signin');
    add(card, el('h1', 'title', 'Request music'));
    add(card, el('p', 'muted', 'Sign in with your Jellyfin account.'));
    if (S.notice) {
      add(card, el('p', 'notice', S.notice));
    }
    var form = el('form', 'form');
    form.setAttribute('autocomplete', 'on');
    var user = el('input', 'input');
    user.type = 'text';
    user.name = 'username';
    user.placeholder = 'Username';
    user.autocomplete = 'username';
    user.required = true;
    user.setAttribute('aria-label', 'Username');
    var pass = el('input', 'input');
    pass.type = 'password';
    pass.name = 'password';
    pass.placeholder = 'Password';
    pass.autocomplete = 'current-password';
    pass.setAttribute('aria-label', 'Password');
    var error = el('p', 'error');
    error.setAttribute('role', 'alert');
    var submit = el('button', 'btn btn-primary', 'Sign in');
    submit.type = 'submit';
    add(form, user, pass, submit, error);
    form.addEventListener('submit', function (event) {
      event.preventDefault();
      error.textContent = '';
      submit.disabled = true;
      var body = { username: user.value, password: pass.value, deviceId: deviceId() };
      pass.value = '';
      api('POST', 'auth/login', body).then(function (result) {
        return startSession(result.token);
      }).catch(function (e) {
        error.textContent = failText(e);
        submit.disabled = false;
      });
    });
    add(card, form);
    if (S.cfg && S.cfg.quickConnect) {
      var quick = el('div', 'quick');
      add(quick, el('p', 'muted', 'or'));
      ui.quickBox = quick;
      renderQuickIdle();
      add(card, quick);
    }
    add(root_, card);
  }

  function renderQuickIdle() {
    var box = ui.quickBox;
    if (!box) {
      return;
    }
    box.replaceChildren();
    add(box, el('p', 'muted', 'or'));
    add(box, button('Sign in with Quick Connect', 'btn', startQuick));
  }

  function stopQuick() {
    if (S.quick && S.quick.timer) {
      root.clearTimeout(S.quick.timer);
    }
    S.quick = null;
  }

  function startQuick() {
    stopQuick();
    var box = ui.quickBox;
    box.replaceChildren();
    add(box, el('p', 'muted', 'Asking Jellyfin for a code...'));
    api('POST', 'auth/quickconnect', { deviceId: deviceId() }).then(function (result) {
      var session = { secret: result.secret, until: Date.now() + 5 * 60 * 1000, timer: 0 };
      S.quick = session;
      box.replaceChildren();
      add(box, el('p', 'muted', 'Approve this code in Jellyfin: your profile, then Quick Connect.'));
      add(box, el('p', 'code', clean(result.code)));
      var status = el('p', 'muted', 'Waiting for approval...');
      add(box, status);
      add(box, button('Cancel', 'btn', function () {
        stopQuick();
        renderQuickIdle();
      }));
      var tick = function () {
        if (S.quick !== session) {
          return;
        }
        if (Date.now() > session.until) {
          stopQuick();
          renderQuickIdle();
          add(box, el('p', 'error', 'The code expired. Start again.'));
          return;
        }
        api('POST', 'auth/quickconnect/poll', { secret: session.secret, deviceId: deviceId() }).then(function (poll) {
          if (S.quick !== session) {
            return;
          }
          if (poll && poll.authenticated && poll.token) {
            stopQuick();
            return startSession(poll.token);
          }
          session.timer = root.setTimeout(tick, 3000);
        }).catch(function (e) {
          if (S.quick !== session) {
            return;
          }
          stopQuick();
          renderQuickIdle();
          add(box, el('p', 'error', failText(e)));
        });
      };
      session.timer = root.setTimeout(tick, 3000);
    }).catch(function (e) {
      renderQuickIdle();
      add(box, el('p', 'error', failText(e)));
    });
  }

  function signOut() {
    var token = S.token;
    if (token) {
      api('POST', 'auth/logout').catch(function () { /* the token is dropped here either way */ });
    }
    endSession('');
  }

  // ---- main screen

  function renderMain() {
    var r = ui.root;
    r.replaceChildren();
    var header = el('header', 'bar');
    add(header, el('h1', 'title', 'Request music'));
    var who = el('div', 'who');
    add(who, el('span', 'muted', clean(S.me.name)));
    add(who, button('Sign out', 'btn btn-small', signOut));
    add(header, who);
    add(r, header);

    var info = el('p', 'muted info');
    var parts = [];
    if (!S.me.canRequest) {
      parts.push('You can search, but you are not allowed to make requests.');
    } else {
      if (typeof S.me.songsLeftToday === 'number') {
        parts.push('Songs left today: ' + S.me.songsLeftToday);
      }
      if (typeof S.me.albumsLeftToday === 'number') {
        parts.push('Albums left today: ' + S.me.albumsLeftToday);
      }
    }
    info.textContent = parts.join('  ');
    ui.info = info;
    add(r, info);

    var tabs = el('nav', 'tabs');
    tabs.setAttribute('role', 'tablist');
    ui.tabs = {};
    var names = [['search', 'Search'], ['mine', 'My requests']];
    if (S.me.isAdmin) {
      names.push(['all', 'Everyone']);
    }
    names.forEach(function (pair) {
      var tab = button(pair[1], 'tab', function () {
        showView(pair[0]);
      });
      tab.setAttribute('role', 'tab');
      ui.tabs[pair[0]] = tab;
      add(tabs, tab);
    });
    add(r, tabs);

    var searchPanel = el('section', 'panel');
    var input = el('input', 'input');
    input.type = 'search';
    input.placeholder = 'Search for a song, artist or album';
    input.setAttribute('aria-label', 'Search');
    input.maxLength = 100;
    input.value = S.query;
    input.autocomplete = 'off';
    input.addEventListener('input', function () {
      S.query = clean(input.value).slice(0, 100);
      root.clearTimeout(S.searchTimer);
      S.searchTimer = root.setTimeout(runSearch, 400);
    });
    input.addEventListener('keydown', function (event) {
      if (event.key === 'Enter') {
        event.preventDefault();
        root.clearTimeout(S.searchTimer);
        runSearch();
      }
    });
    ui.input = input;
    ui.results = el('div', 'results');
    add(searchPanel, input, ui.results);
    ui.panels = { search: searchPanel, mine: el('section', 'panel'), all: el('section', 'panel') };
    add(r, searchPanel, ui.panels.mine, S.me.isAdmin ? ui.panels.all : null);
    showView(S.view === 'all' && !S.me.isAdmin ? 'search' : S.view);
    renderResults();
  }

  function showView(name) {
    S.view = name;
    Object.keys(ui.panels).forEach(function (key) {
      ui.panels[key].hidden = key !== name;
    });
    Object.keys(ui.tabs).forEach(function (key) {
      ui.tabs[key].setAttribute('aria-selected', key === name ? 'true' : 'false');
      ui.tabs[key].classList.toggle('tab-on', key === name);
    });
    if (name === 'mine' || name === 'all') {
      loadAndPoll();
    }
    if (name === 'search') {
      ui.input.focus();
    }
  }

  // ---- search

  function runSearch() {
    root.clearTimeout(S.searchTimer);
    if (S.searchAbort) {
      S.searchAbort.abort();
      S.searchAbort = null;
    }
    S.searchError = '';
    if (S.query.length < 2) {
      S.results = null;
      S.searching = false;
      renderResults();
      return;
    }
    var controller = new root.AbortController();
    S.searchAbort = controller;
    S.searching = true;
    renderResults();
    var kinds = [];
    if (!S.cfg || S.cfg.songRequests !== false) {
      kinds.push('song');
    }
    if (!S.cfg || S.cfg.albumRequests !== false) {
      kinds.push('album');
    }
    var path = 'search?q=' + encodeURIComponent(S.query) + '&kinds=' + kinds.join(',');
    api('GET', path, undefined, controller.signal).then(function (data) {
      if (S.searchAbort !== controller) {
        return;
      }
      S.searchAbort = null;
      S.searching = false;
      S.results = data || {};
      S.rowErrors = {};
      renderResults();
    }).catch(function (e) {
      if (e && e.name === 'AbortError') {
        return;
      }
      S.searchAbort = null;
      S.searching = false;
      S.searchError = failText(e);
      renderResults();
    });
  }

  function section(title) {
    var box = el('section', 'section');
    add(box, el('h2', 'heading', title));
    return box;
  }

  function renderResults() {
    var box = ui.results;
    if (!box) {
      return;
    }
    box.replaceChildren();
    if (S.searching) {
      add(box, el('p', 'muted', 'Searching...'));
      return;
    }
    if (S.searchError) {
      add(box, el('p', 'error', S.searchError));
      return;
    }
    var data = S.results;
    if (!data) {
      add(box, el('p', 'muted', S.query.length > 0 ? 'Type at least 2 characters.' : 'Search for something to request.'));
      return;
    }
    var lib = data.library || {};
    var libSongs = Array.isArray(lib.songs) ? lib.songs : [];
    var libAlbums = Array.isArray(lib.albums) ? lib.albums : [];
    var songs = Array.isArray(data.songs) ? data.songs : [];
    var albums = Array.isArray(data.albums) ? data.albums : [];
    var shown = 0;

    if (libSongs.length + libAlbums.length > 0) {
      var libBox = section('In your library');
      libAlbums.forEach(function (row) {
        add(libBox, libraryRow(row, 'Album'));
      });
      libSongs.forEach(function (row) {
        add(libBox, libraryRow(row, 'Song'));
      });
      add(box, libBox);
      shown++;
    }
    if (songs.length > 0) {
      var songBox = section('Songs');
      songs.forEach(function (row) {
        add(songBox, resultRow(row, 'song'));
      });
      add(box, songBox);
      shown++;
    }
    if (albums.length > 0) {
      var albumBox = section('Albums');
      albums.forEach(function (row) {
        add(albumBox, resultRow(row, 'album'));
      });
      add(box, albumBox);
      shown++;
    }
    if (shown === 0) {
      add(box, el('p', 'muted', 'Nothing found.'));
    }
    if (Array.isArray(data.warnings) && data.warnings.length > 0) {
      add(box, el('p', 'muted warn', 'Some sources did not answer: ' + data.warnings.map(clean).join(', ')));
    }
  }

  function libraryRow(row, kind) {
    var item = el('article', 'row');
    add(item, cover(row.coverUrl));
    var text = el('div', 'text');
    add(text, el('div', 'name', headline(row)));
    var sub = [kind];
    if (row.album && kind === 'Song') {
      sub.push(clean(row.album));
    }
    if (row.year) {
      sub.push(String(row.year));
    }
    add(text, el('div', 'sub', sub.join(' - ')));
    add(item, text);
    var side = el('div', 'side');
    add(side, openLink(row.itemId));
    add(item, side);
    return item;
  }

  function rowKeyOf(row, kind) {
    return kind + ':' + clean(kind === 'song' ? row.deezerTrackId : row.foreignAlbumId);
  }

  function resultRow(row, kind) {
    var key = rowKeyOf(row, kind);
    var item = el('article', 'row');
    add(item, cover(row.coverUrl));
    var text = el('div', 'text');
    add(text, el('div', 'name', headline(row)));
    var sub = [];
    if (row.album && kind === 'song') {
      sub.push(clean(row.album));
    }
    if (row.year) {
      sub.push(String(row.year));
    }
    if (kind === 'album' && row.trackCount) {
      sub.push(row.trackCount + ' tracks');
    }
    if (kind === 'album' && row.albumType) {
      sub.push(clean(row.albumType));
    }
    if (row.explicit) {
      sub.push('explicit');
    }
    if (sub.length > 0) {
      add(text, el('div', 'sub', sub.join(' - ')));
    }
    if (S.rowErrors[key]) {
      add(text, el('div', 'error', S.rowErrors[key]));
    }
    add(item, text);

    var side = el('div', 'side');
    var active = row.request && typeof row.request === 'object' ? row.request : null;
    if (row.itemId || row.inLibrary) {
      add(side, el('span', 'chip chip-ready', 'In your library'));
      add(side, openLink(row.itemId));
    } else if (active) {
      add(side, chip(active.state));
      if (active.state === 'ready') {
        add(side, openLink(active.itemId));
      }
    } else if (kind === 'album' && row.released === false) {
      add(side, el('span', 'chip chip-unknown', 'Not released yet'));
    } else if (S.me && S.me.canRequest) {
      var go = button('Request', 'btn btn-primary btn-small', function () {
        go.disabled = true;
        requestRow(row, kind, key);
      });
      add(side, go);
    }
    add(item, side);
    return item;
  }

  function requestRow(row, kind, key) {
    var body = kind === 'song' ? { kind: 'song', deezerTrackId: row.deezerTrackId } : { kind: 'album', foreignAlbumId: row.foreignAlbumId };
    delete S.rowErrors[key];
    api('POST', 'requests', body).then(function (created) {
      row.request = created;
      renderResults();
      loadAndPoll();
    }).catch(function (e) {
      S.rowErrors[key] = failText(e);
      renderResults();
    });
  }

  // ---- My requests and Everyone

  function stopPolling() {
    if (S.pollTimer) {
      root.clearTimeout(S.pollTimer);
      S.pollTimer = 0;
    }
  }

  function loadAndPoll() {
    stopPolling();
    if (!S.token || !S.me) {
      return;
    }
    var jobs = [api('GET', 'requests?scope=mine').then(function (list) {
      S.mine = Array.isArray(list) ? list : (list && list.requests) || [];
    })];
    if (S.me.isAdmin && S.view === 'all') {
      jobs.push(api('GET', 'requests?scope=all').then(function (list) {
        S.all = Array.isArray(list) ? list : (list && list.requests) || [];
      }));
    }
    Promise.all(jobs).then(function () {
      renderRequests();
      refreshSearchChips();
    }).catch(function () {
      /* a failed poll is retried at the next tick */
    }).then(function () {
      if (!S.token) {
        return;
      }
      var delay = pollDelay(S.view === 'all' ? S.all.concat(S.mine) : S.mine);
      S.pollTimer = root.setTimeout(function () {
        if (root.document.hidden) {
          S.pollTimer = root.setTimeout(loadAndPoll, 5000);
          return;
        }
        loadAndPoll();
      }, delay);
    });
  }

  function refreshSearchChips() {
    if (!S.results) {
      return;
    }
    var byId = {};
    S.mine.forEach(function (request) {
      byId[request.id] = request;
    });
    var changed = false;
    ['songs', 'albums'].forEach(function (name) {
      (S.results[name] || []).forEach(function (row) {
        if (row.request && byId[row.request.id] && byId[row.request.id].state !== row.request.state) {
          row.request = byId[row.request.id];
          changed = true;
        }
      });
    });
    if (changed && !S.searching && S.view === 'search') {
      renderResults();
    }
  }

  function renderRequests() {
    fillRequests(ui.panels.mine, S.mine, false);
    if (S.me.isAdmin) {
      fillRequests(ui.panels.all, S.all, true);
    }
  }

  function fillRequests(panel, list, everyone) {
    panel.replaceChildren();
    if (list.length === 0) {
      add(panel, el('p', 'muted', everyone ? 'No requests yet.' : 'You have not requested anything yet.'));
      return;
    }
    list.forEach(function (request) {
      add(panel, requestCard(request, everyone));
    });
  }

  function requestCard(request, everyone) {
    var item = el('article', 'row');
    add(item, cover(request.coverUrl));
    var text = el('div', 'text');
    add(text, el('div', 'name', headline(request)));
    var sub = [];
    sub.push(request.kind === 'album' ? 'Album' : 'Song');
    if (request.album && request.kind !== 'album') {
      sub.push(clean(request.album));
    }
    if (request.year) {
      sub.push(String(request.year));
    }
    if (everyone && request.userName) {
      sub.push('by ' + clean(request.userName));
    }
    var when = timeAgo(request.updatedUtc || request.createdUtc);
    if (when) {
      sub.push(when);
    }
    add(text, el('div', 'sub', sub.join(' - ')));
    var detail = clean(request.detail);
    if (request.state === 'failed' && request.failReason && !detail) {
      detail = clean(request.failReason);
    }
    if (request.progress && typeof request.progress.tracks === 'number' && request.progress.of) {
      var bar = el('progress', 'bar-progress');
      bar.max = request.progress.of;
      bar.value = request.progress.tracks;
      add(text, bar);
    }
    if (detail) {
      add(text, el('div', 'sub', detail));
    }
    if (S.rowErrors['r' + request.id]) {
      add(text, el('div', 'error', S.rowErrors['r' + request.id]));
    }
    add(item, text);

    var side = el('div', 'side');
    add(side, chip(request.state));
    if (request.state === 'ready') {
      add(side, openLink(request.itemId));
    }
    if (canCancel(request, S.me)) {
      add(side, button('Cancel', 'btn btn-small', function () {
        act('DELETE', 'requests/' + encodeURIComponent(request.id), request);
      }));
    }
    if (canRetry(request)) {
      add(side, button('Retry', 'btn btn-small', function () {
        act('POST', 'requests/' + encodeURIComponent(request.id) + '/retry', request);
      }));
    }
    add(item, side);
    return item;
  }

  function act(method, path, request) {
    delete S.rowErrors['r' + request.id];
    api(method, path).then(function () {
      loadAndPoll();
    }).catch(function (e) {
      S.rowErrors['r' + request.id] = failText(e);
      renderRequests();
    });
  }

  // ---- start

  function init() {
    ui.root = document.getElementById('app');
    if (!ui.root) {
      return;
    }
    S.query = queryFromSearch(root.location.search);
    S.token = store(TOKEN_KEY);
    ui.root.replaceChildren(el('p', 'muted pad', 'Loading...'));
    api('GET', 'config').then(function (cfg) {
      S.cfg = cfg || {};
    }, function () {
      S.cfg = {};
    }).then(function () {
      if (!S.token) {
        renderSignIn();
        return;
      }
      return startSession(S.token).catch(function (e) {
        // An invalid token was already dropped by api(); any other failure keeps it for the next visit.
        if (S.token) {
          S.token = null;
          S.notice = failText(e);
          renderSignIn();
        }
      });
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})(typeof window !== 'undefined' ? window : globalThis);
