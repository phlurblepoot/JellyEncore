/* Encore: Jellyfin Web live-connection fix (QBStream, Jellyfin 12.4).
   Jellyfin 12.4 turns legacy auth off, but the bundled web client still opens
   /socket?api_key=..., which the server now rejects ("Token is required").
   Without that socket nothing can remote-control a web player (Encore radio,
   "play on" from other devices). This renames that one query parameter to
   ApiKey, which 12.4 accepts. Only /socket URLs are touched. Safe to run twice.
   Remove once Jellyfin Web sends ApiKey itself. */
(function () {
  'use strict';
  var Native = window.WebSocket;
  if (!Native || Native.__qbSocketFix) return;

  function fixUrl(url) {
    try {
      var s = String(url);
      if (/\/socket\?/i.test(s) && /[?&]api_key=/.test(s)) {
        return s.replace(/([?&])api_key=/, '$1ApiKey=');
      }
    } catch (e) { /* leave the URL as it is */ }
    return url;
  }

  function FixedWebSocket(url, protocols) {
    var u = fixUrl(url);
    return arguments.length > 1 ? new Native(u, protocols) : new Native(u);
  }
  FixedWebSocket.prototype = Native.prototype;
  ['CONNECTING', 'OPEN', 'CLOSING', 'CLOSED'].forEach(function (k) {
    Object.defineProperty(FixedWebSocket, k, { value: Native[k] });
  });
  FixedWebSocket.__qbSocketFix = true;
  window.WebSocket = FixedWebSocket;

  // If the client already tried (and failed) before this ran, let it reconnect now.
  try {
    var api = window.ApiClient;
    if (api && typeof api.ensureWebSocket === 'function') api.ensureWebSocket();
  } catch (e) { /* the client retries on its own later */ }
})();
