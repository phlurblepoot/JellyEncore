// Entry for `node --test tests/js`: Node 22+ treats a directory argument as a module path (this index.js),
// not as a folder to search, so this file loads every *.test.mjs beside it.
// `node --test "tests/js/*.test.mjs"` runs the same tests directly.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

for (const f of fs.readdirSync(__dirname).filter((n) => n.endsWith('.test.mjs')).sort()) {
  import(pathToFileURL(path.join(__dirname, f)).href);
}
