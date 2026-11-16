// Node tests for contrib/jellyfin-request-search.js (v03 §10.2). Run: node --test tests/js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const scriptPath = path.join(root, 'contrib', 'jellyfin-request-search.js');
const importPath = path.join(root, 'contrib', 'jellyfin-request-search.import.json');
const require = createRequire(import.meta.url);
const h = require(scriptPath);

test('import JSON Script is identical to the .js file', () => {
  const js = readFileSync(scriptPath, 'utf8');
  const json = JSON.parse(readFileSync(importPath, 'utf8'));
  assert.equal(json.CustomJavaScripts.length, 1);
  assert.equal(json.CustomJavaScripts[0].Script, js);
});

test('import entry flags: enabled, requires authentication, not a module, no Src', () => {
  const e = JSON.parse(readFileSync(importPath, 'utf8')).CustomJavaScripts[0];
  assert.equal(e.Name, 'Encore request search');
  assert.equal(e.Enabled, true);
  assert.equal(e.RequiresAuthentication, true);
  assert.equal(e.Module, false);
  assert.equal(e.Src, '');
});

test('script has no top-level import or export (the Injector wraps it in an IIFE)', () => {
  const js = readFileSync(scriptPath, 'utf8');
  assert.doesNotMatch(js, /^\s*(import|export)\s/m);
});

test('loading in node exports helpers without touching the browser', () => {
  for (const name of ['chooseMode', 'normalizeQuery', 'queryOk', 'searchUrl', 'requestsUrl', 'activeRequestsUrl',
    'fallbackUrl', 'tokenAllowed', 'stateLabel', 'isActive', 'buildCards', 'cardLabel']) {
    assert.equal(typeof h[name], 'function', name);
  }
  assert.equal(globalThis.__encoreRequestSearch, undefined);
});

test('chooseMode picks the URL by page protocol', () => {
  assert.deepEqual(h.chooseMode('http:', 'http://e:5095/', ''), { mode: 'api', base: 'http://e:5095' });
  assert.deepEqual(h.chooseMode('http:', 'http://e:5095', 'https://enc.example'), { mode: 'api', base: 'http://e:5095' });
  assert.deepEqual(h.chooseMode('https:', 'http://e:5095', 'https://enc.example/'), { mode: 'api', base: 'https://enc.example' });
  assert.deepEqual(h.chooseMode('http:', '', 'https://enc.example'), { mode: 'none', base: '' });
  assert.deepEqual(h.chooseMode('https:', '', ''), { mode: 'none', base: '' });
});

test('https page without an https URL gets only the fallback link', () => {
  const c = h.chooseMode('https:', 'http://e:5095', '');
  assert.deepEqual(c, { mode: 'link', base: 'http://e:5095' });
  assert.equal(h.fallbackUrl(c.base, 'AC/DC & co'), 'http://e:5095/request?q=AC%2FDC%20%26%20co');
});

test('query normalisation and limits', () => {
  assert.equal(h.normalizeQuery('  a   b\tc  '), 'a b c');
  assert.equal(h.normalizeQuery(null), '');
  assert.equal(h.queryOk('a'), false);
  assert.equal(h.queryOk('ab'), true);
  assert.equal(h.queryOk('x'.repeat(100)), true);
  assert.equal(h.queryOk('x'.repeat(101)), false);
});

test('API URLs are built from the base with encoding', () => {
  assert.equal(h.searchUrl('http://e:5095/', 'a b&c'), 'http://e:5095/api/v1/search?q=a%20b%26c&kinds=song,album');
  assert.equal(h.requestsUrl('http://e:5095/x/'), 'http://e:5095/x/api/v1/requests');
  assert.equal(h.activeRequestsUrl('http://e:5095'), 'http://e:5095/api/v1/requests?scope=mine&active=true');
});

test('token only for a same-origin Jellyfin', () => {
  assert.equal(h.tokenAllowed('https://jf.example/', 'https://jf.example'), true);
  assert.equal(h.tokenAllowed('https://jf.example/jellyfin', 'https://jf.example'), true);
  assert.equal(h.tokenAllowed('http://jf.example', 'https://jf.example'), false);
  assert.equal(h.tokenAllowed('https://other.example', 'https://jf.example'), false);
  assert.equal(h.tokenAllowed('not a url', 'https://jf.example'), false);
  assert.equal(h.tokenAllowed(undefined, 'https://jf.example'), false);
});

test('state labels and active states', () => {
  assert.equal(h.stateLabel('downloading'), 'Downloading');
  assert.equal(h.stateLabel('ready'), 'Ready');
  assert.equal(h.stateLabel('weird'), '');
  assert.equal(h.stateLabel('upcoming'), 'Upcoming');
  assert.equal(h.isActive('queued'), true);
  assert.equal(h.isActive('upcoming'), true);
  assert.equal(h.isActive('importing'), true);
  for (const s of ['ready', 'failed', 'cancelled', '', null]) assert.equal(h.isActive(s), false, String(s));
});

test('buildCards keeps only what is not in the library', () => {
  const cards = h.buildCards({
    // Song rows exactly as GET /api/v1/search returns them (T85 SongRow).
    songs: [
      { deezerTrackId: 1, title: 'One', artist: 'A', album: 'X', durationS: 200, coverUrl: 'c1', explicit: false,
        owned: false, itemId: null, requestable: true, request: null },
      { deezerTrackId: 2, title: 'Owned', artist: 'A', album: 'X', durationS: 200, coverUrl: null, explicit: false,
        owned: true, itemId: 'abc', requestable: false, request: null },
      { deezerTrackId: 3, title: 'Not requestable', artist: 'A', album: 'X', durationS: 200, coverUrl: null, explicit: false,
        owned: false, itemId: null, requestable: false, request: null },
      { title: 'No id', artist: 'A', owned: false, requestable: true },
      { deezerTrackId: 4, title: 'Busy', artist: 'B', album: 'Y', durationS: 100, coverUrl: null, explicit: true,
        owned: false, itemId: null, requestable: true, request: { id: 'r9', state: 'downloading', detail: 'Downloading from a peer', mine: true } },
    ],
    // Album rows exactly as GET /api/v1/search returns them (T90 AlbumRow).
    albums: [
      { foreignAlbumId: 'mb-1', title: 'Album', artist: 'C', foreignArtistId: 'a', year: 2027, albumType: 'Album', secondaryTypes: [],
        trackCount: 9, coverUrl: null, released: false, releaseDate: '2027-03-01', owned: false, itemId: null, requestable: true, request: null },
      { foreignAlbumId: 'mb-2', title: 'Owned album', artist: 'C', foreignArtistId: 'a', year: 1997, albumType: 'Album', secondaryTypes: [],
        trackCount: 12, coverUrl: null, released: true, owned: true, itemId: null, requestable: false, request: null },
      { foreignAlbumId: 'mb-3', title: 'Wanted', artist: 'C', foreignArtistId: 'a', year: 2000, albumType: 'Album', secondaryTypes: ['Live'],
        trackCount: 10, coverUrl: 'c3', released: true, owned: false, itemId: null, requestable: true,
        request: { id: 'r3', state: 'searching', detail: 'Waiting for Soulseek', mine: false } },
      { foreignAlbumId: 'mb-4', title: 'Undated', artist: 'C', foreignArtistId: 'a', year: null, albumType: 'Album', secondaryTypes: [],
        trackCount: null, coverUrl: null, released: false, releaseDate: null, owned: false, itemId: null, requestable: true, request: null },
      { title: 'No mbid' },
    ],
  });
  assert.deepEqual(cards.map(c => c.key), ['song:1', 'song:4', 'album:mb-1', 'album:mb-3', 'album:mb-4']);
  assert.equal(cards[2].upcoming, 'Upcoming: 2027-03-01');
  assert.equal(cards[3].upcoming, '');
  assert.equal(cards[4].upcoming, 'Upcoming: date not known yet');
  assert.equal(cards[0].upcoming, undefined);
  assert.equal(cards[3].cover, 'c3');
  assert.equal(cards[3].released, true);
  assert.equal(cards[3].request.state, 'searching');
  assert.deepEqual(cards[0].body, { kind: 'song', deezerTrackId: 1 });
  assert.equal(cards[0].cover, 'c1');
  assert.deepEqual(cards[1].request, { id: 'r9', state: 'downloading', detail: 'Downloading from a peer', mine: true });
  assert.deepEqual(cards[2].body, { kind: 'album', foreignAlbumId: 'mb-1' });
  assert.equal(cards[2].released, false);
  assert.equal(h.cardLabel(cards[0]), 'A - One');
  assert.equal(h.cardLabel({ artist: '', title: 'T' }), 'T');
});

test('buildCards tolerates bad input', () => {
  assert.deepEqual(h.buildCards(null), []);
  assert.deepEqual(h.buildCards('x'), []);
  assert.deepEqual(h.buildCards({ songs: 'no', albums: null }), []);
});

test('upcoming label for albums not released yet', () => {
  assert.equal(h.upcomingLabel({ released: false, releaseDate: '2027-03-01' }), 'Upcoming: 2027-03-01');
  assert.equal(h.upcomingLabel({ released: false, releaseDate: null }), 'Upcoming: date not known yet');
  assert.equal(h.upcomingLabel({ released: false, releaseDate: 'soon' }), 'Upcoming: date not known yet');
  assert.equal(h.upcomingLabel({ released: true, releaseDate: '1997-05-21' }), '');
  assert.equal(h.upcomingLabel(null), '');
});

test('the card shows the upcoming badge next to a Request button, and 409 no longer means not released', () => {
  const js = readFileSync(scriptPath, 'utf8');
  assert.match(js, /encore-request-upcoming/);
  assert.doesNotMatch(js, /not_released|Not released yet/);
});

test('the retry time is shown in the browser time zone from nextTryUtc', () => {
  const next = '2026-11-20T19:00:00Z';
  const local = new Date(next).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  assert.equal(h.detailText({ detail: 'Not found yet, trying again later', nextTryUtc: next }), 'Not found yet, trying again at ' + local);
  assert.equal(h.detailText({ detail: 'Searching', nextTryUtc: next }), 'Searching');
  assert.equal(h.detailText({ detail: 'Not found yet, trying again later', nextTryUtc: null }), 'Not found yet, trying again later');
  assert.equal(h.detailText(null), '');
});

test('result tabs: counts of cards, default and remembered tab, label', () => {
  const cards = [{ kind: 'song' }, { kind: 'song' }, { kind: 'album' }];
  assert.deepEqual(h.tabCounts(cards), { songs: 2, albums: 1 });
  assert.deepEqual(h.tabCounts(null), { songs: 0, albums: 0 });
  assert.equal(h.chooseTab({ songs: 2, albums: 1 }, null), 'songs');
  assert.equal(h.chooseTab({ songs: 0, albums: 1 }, null), 'albums');
  assert.equal(h.chooseTab({ songs: 2, albums: 1 }, 'albums'), 'albums');
  assert.equal(h.chooseTab({ songs: 2, albums: 0 }, 'albums'), 'songs');
  assert.equal(h.tabLabel('albums', 1), 'Albums (1)');
});
