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
  assert.equal(h.isActive('queued'), true);
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
    albums: [
      { foreignAlbumId: 'mb-1', artist: 'C', title: 'Album', released: false },
      { foreignAlbumId: 'mb-2', title: 'Owned album', owned: true },
      { title: 'No mbid' },
    ],
  });
  assert.deepEqual(cards.map(c => c.key), ['song:1', 'song:4', 'album:mb-1']);
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
