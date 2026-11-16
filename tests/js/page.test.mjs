import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const file = fileURLToPath(new URL('../../src/Encore/Requests/Page/app.js', import.meta.url));
const h = require(file);

test('the script is plain ES2020 with no module syntax and no markup writes', () => {
  const src = readFileSync(file, 'utf8');
  assert.doesNotMatch(src, /^\s*(import|export)\s/m);
  for (const banned of ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'eval(', 'new Function']) {
    assert.ok(!src.includes(banned), banned);
  }
  assert.doesNotMatch(src, /\?\?=|\|\|=|&&=|\.replaceAll\(|\.at\(/);
});

test('state labels and activity', () => {
  assert.equal(h.stateLabel('downloading'), 'Downloading');
  assert.equal(h.stateLabel('ready'), 'Ready');
  assert.equal(h.stateLabel('weird'), 'Weird');
  assert.equal(h.stateLabel(undefined), 'Unknown');
  assert.equal(h.chipClass('failed'), 'failed');
  assert.equal(h.chipClass('x y'), 'unknown');
  assert.equal(h.chipClass('constructor'), 'unknown');
  for (const s of ['queued', 'searching', 'downloading', 'importing']) assert.ok(h.isActive(s));
  for (const s of ['ready', 'failed', 'cancelled', 'x']) assert.ok(!h.isActive(s));
});

test('text and url cleaning (escaping is textContent; these guard what becomes an attribute)', () => {
  assert.equal(h.clean('a\u0000b\nc  '), 'a b c');
  assert.equal(h.clean(null), '');
  assert.equal(h.headline({ artist: '<b>A</b>', title: 'T' }), '<b>A</b> - T');
  assert.equal(h.headline({ title: 'T' }), 'T');
  assert.equal(h.headline({}), 'Unknown');
  assert.equal(h.safeHttpUrl('https://x.test/a.jpg'), 'https://x.test/a.jpg');
  assert.equal(h.safeHttpUrl('javascript:alert(1)'), null);
  assert.equal(h.safeHttpUrl('data:text/html,hi'), null);
  assert.equal(h.safeHttpUrl('https://x.test/a b'), null);
  assert.equal(h.safeHttpUrl('https://x.test/"onload='), null);
  assert.equal(h.safeImageUrl('http://x.test/a.jpg'), null);
  assert.equal(h.safeImageUrl('https://x.test/a.jpg'), 'https://x.test/a.jpg');
});

test('path base and urls', () => {
  assert.equal(h.basePathOf('/request'), '');
  assert.equal(h.basePathOf('/request/'), '');
  assert.equal(h.basePathOf('/encore/request'), '/encore');
  assert.equal(h.basePathOf('/a/b/request'), '/a/b');
  assert.equal(h.basePathOf('/other'), '');
  assert.equal(h.apiUrl('', 'config'), '/api/v1/config');
  assert.equal(h.apiUrl('/encore', '/me'), '/encore/api/v1/me');
  assert.equal(h.requestPageUrl('http://192.168.86.50:5095/', '', 'a b&c'), 'http://192.168.86.50:5095/request?q=a%20b%26c');
  assert.equal(h.requestPageUrl('https://x.test', '/encore', ''), 'https://x.test/encore/request');
});

test('the Open in Jellyfin details link, with and without serverId', () => {
  const id = '0123456789abcdef0123456789abcdef';
  assert.equal(
    h.detailsLink('http://jf.test:8096/', id, 'srv1'),
    'http://jf.test:8096/web/#/details?id=' + id + '&serverId=srv1');
  assert.equal(h.detailsLink('http://jf.test:8096', id, null), 'http://jf.test:8096/web/#/details?id=' + id);
  assert.equal(h.detailsLink('http://jf.test:8096', id, ''), 'http://jf.test:8096/web/#/details?id=' + id);
  assert.equal(h.detailsLink('http://jf.test', id, 'a&b'), 'http://jf.test/web/#/details?id=' + id + '&serverId=a%26b');
  assert.equal(h.detailsLink(null, id, 'srv1'), null);
  assert.equal(h.detailsLink('javascript:x', id, 'srv1'), null);
  assert.equal(h.detailsLink('http://jf.test', 'not an id', 'srv1'), null);
  assert.equal(h.detailsLink('http://jf.test', null, 'srv1'), null);
});

test('?q= prefill', () => {
  assert.equal(h.queryFromSearch('?q=radiohead'), 'radiohead');
  assert.equal(h.queryFromSearch('?x=1&q=a%20b+c'), 'a b c');
  assert.equal(h.queryFromSearch(''), '');
  assert.equal(h.queryFromSearch('?q=%E0%A4%A'), '');
  assert.equal(h.queryFromSearch('?q=' + 'x'.repeat(300)).length, 100);
});

test('polling delay and who may cancel or retry', () => {
  assert.equal(h.pollDelay([]), 60000);
  assert.equal(h.pollDelay([{ state: 'ready' }, { state: 'failed' }]), 60000);
  assert.equal(h.pollDelay([{ state: 'ready' }, { state: 'importing' }]), 5000);
  assert.equal(h.pollDelay(null), 60000);
  const user = { isAdmin: false };
  const admin = { isAdmin: true };
  assert.ok(h.canCancel({ state: 'queued' }, user));
  assert.ok(h.canCancel({ state: 'searching' }, user));
  assert.ok(!h.canCancel({ state: 'downloading' }, user));
  assert.ok(h.canCancel({ state: 'downloading' }, admin));
  assert.ok(h.canCancel({ state: 'importing' }, admin));
  assert.ok(!h.canCancel({ state: 'ready' }, admin));
  assert.ok(h.canRetry({ state: 'failed' }));
  assert.ok(!h.canRetry({ state: 'queued' }));
});

test('error messages and time text', () => {
  assert.match(h.errorMessage(429, 'daily_limit', 'x'), /daily request limit/);
  assert.match(h.errorMessage(429, 'login_limited', 'x', '900'), /15 min/);
  assert.equal(h.errorMessage(401, '', 'Wrong username or password.'), 'Wrong username or password.');
  assert.equal(h.errorMessage(500, 'odd', ''), 'Something went wrong (500).');
  assert.equal(h.errorMessage(409, 'odd', 'Plain server words'), 'Plain server words');
  const now = Date.parse('2026-11-15T12:00:00Z');
  assert.equal(h.timeAgo('2026-11-15T11:59:40Z', now), 'just now');
  assert.equal(h.timeAgo('2026-11-15T11:50:00Z', now), '10 min ago');
  assert.equal(h.timeAgo('2026-11-15T09:00:00Z', now), '3 h ago');
  assert.equal(h.timeAgo('2026-11-10T12:00:00Z', now), '5 days ago');
  assert.equal(h.timeAgo('nope', now), '');
});

test('device ids are 32 hex digits', () => {
  const id = h.newDeviceId({ getRandomValues: (b) => { b.fill(7); return b; } });
  assert.equal(id, '07'.repeat(16));
  assert.match(h.newDeviceId(null), /^[0-9a-f]{32}$/);
});

test('rowAction reads the real /search song rows and request objects', () => {
  const me = { canRequest: true };
  const song = (extra) => Object.assign({
    deezerTrackId: 1, title: 'One', artist: 'A', album: 'X', durationS: 200, coverUrl: null, explicit: false,
    owned: false, itemId: null, requestable: true, request: null,
  }, extra);
  assert.equal(h.rowAction(song(), 'song', me), 'request');
  assert.equal(h.rowAction(song({ owned: true, itemId: 'abc', requestable: false }), 'song', me), 'library');
  assert.equal(h.rowAction(song({ requestable: false }), 'song', me), 'none');
  assert.equal(h.rowAction(song({ request: { id: 'r1', state: 'searching', detail: 'Searching Soulseek', mine: false } }), 'song', me), 'status');
  assert.equal(h.rowAction(song({ request: { id: 'r1', kind: 'song', state: 'ready', itemId: 'abc' } }), 'song', me), 'status');
  assert.equal(h.rowAction(song(), 'song', { canRequest: false }), 'none');
  assert.equal(h.rowAction(song(), 'song', null), 'none');
  assert.equal(h.rowAction({ foreignAlbumId: 'mb', released: false }, 'album', me), 'unreleased');
  assert.equal(h.rowAction({ foreignAlbumId: 'mb', inLibrary: true }, 'album', me), 'library');
  assert.equal(h.rowAction(null, 'song', me), 'none');
});
