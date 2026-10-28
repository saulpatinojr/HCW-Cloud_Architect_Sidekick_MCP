import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseAllowList, computeExclusions, parseSeed, toSyncArgument, normalizeFolder } from './exclusions.mjs';

const tree = {
  '': ['Work', 'Projects', 'Personal', 'Journal', '.obsidian', '.trash'],
  'Work': ['Clients', '.trash'],
  'Projects': ['Cloud', 'Home', 'Archive'],
  'Projects/Cloud': ['Azure'],
  'Personal': ['Health'],
};
const list = p => tree[p] ?? [];

test('a block allow-list gives the shallowest exclusions and nothing inside an allowed folder', () => {
  const allow = parseAllowList('---\nallow:\n  - Work\n  - Projects/Cloud\ntag: adr-context\n---\nbody');
  assert.deepEqual(allow, ['Work', 'Projects/Cloud']);
  assert.deepEqual(computeExclusions(allow, list), ['Journal', 'Personal', 'Projects/Archive', 'Projects/Home']);
});

test('an inline allow-list and quotes parse the same way', () => {
  assert.deepEqual(parseAllowList('---\nallow: ["Work", \'Projects/Cloud\']\n---'), ['Work', 'Projects/Cloud']);
});

test('no note, no front matter or an empty list excludes every top-level folder (nothing is readable, nothing is synced)', () => {
  assert.deepEqual(parseAllowList('no front matter'), []);
  assert.deepEqual(parseAllowList('---\ntag: x\n---'), []);
  assert.deepEqual(computeExclusions([], list), ['Journal', 'Personal', 'Projects', 'Work']);
});

test('climbing, absolute, hidden and empty entries are dropped, as the server drops them', () => {
  assert.deepEqual(parseAllowList('---\nallow:\n  - ../Secrets\n  - .hidden\n  - Work/.trash\n  - ./Work/\n  - \n  - Work\\Clients\n---'), ['Work', 'Work/Clients']);
  assert.equal(normalizeFolder('/abs'), null);
  assert.equal(normalizeFolder('a/../b'), null);
});

test('hidden folders are never excluded or descended (Sync handles .obsidian through --configs)', () => {
  assert.deepEqual(computeExclusions(['Work'], list).filter(f => f.startsWith('.')), []);
});

test('the seed is a top-level list and the Sync argument is comma-joined', () => {
  assert.deepEqual(parseSeed(' Personal, Journal ,,Journal,.obsidian'), ['Personal', 'Journal']);
  assert.equal(toSyncArgument(['Journal', 'Projects/Home']), 'Journal,Projects/Home');
  assert.throws(() => toSyncArgument(['a,b']), /comma/);
});
