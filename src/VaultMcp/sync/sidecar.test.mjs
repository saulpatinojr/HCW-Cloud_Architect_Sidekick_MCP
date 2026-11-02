import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { hasStoredLogin, tokenPath } from './login-state.mjs';
import { OB_CONTRACT } from './ob-contract.mjs';

const source = fs.readFileSync(new URL('./sync.mjs', import.meta.url), 'utf8');

test('every ob call in sync.mjs uses only commands and options from the contract', () => {
  const calls = [...source.matchAll(/\[\s*'(login|sync(?:-[a-z]+)?)'([^\]]*)\]/g)];
  assert.ok(calls.length >= 6, `expected the sidecar's ob calls, found ${calls.length}`);
  for (const [, command, rest] of calls) {
    assert.ok(command in OB_CONTRACT, `ob ${command} is not in ob-contract.mjs`);
    for (const [, option] of rest.matchAll(/'(--[a-z-]+)'/g)) assert.ok(OB_CONTRACT[command].includes(option), `ob ${command} ${option} is not in ob-contract.mjs`);
  }
  for (const [, pushed] of source.matchAll(/args\.push\(([^)]*)\)/g))
    for (const [, option] of pushed.matchAll(/'(--[a-z-]+)'/g)) assert.ok(OB_CONTRACT['sync-setup'].includes(option), `ob sync-setup ${option} is not in ob-contract.mjs`);
});

test('the login check never runs ob login (it has no status mode and can prompt)', () => {
  const body = source.slice(source.indexOf('function loggedIn('), source.indexOf('function login('));
  assert.doesNotMatch(body, /ob\(/);
  assert.match(body, /hasStoredLogin\(/);
});

test('a stored token file counts as logged in, an empty or missing one does not', () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'ob-login-'));
  assert.equal(hasStoredLogin({}, home), false);
  fs.mkdirSync(path.dirname(tokenPath(home)), { recursive: true });
  fs.writeFileSync(tokenPath(home), '\n');
  assert.equal(hasStoredLogin({}, home), false);
  fs.writeFileSync(tokenPath(home), 'token-value');
  assert.equal(hasStoredLogin({}, home), true);
  fs.rmSync(home, { recursive: true, force: true });
});

test('OBSIDIAN_AUTH_TOKEN counts as logged in without a file', () => {
  assert.equal(hasStoredLogin({ OBSIDIAN_AUTH_TOKEN: 'x' }, path.join(os.tmpdir(), 'no-such-dir')), true);
});

test('the cycle checks for pull-only mode before it can reach ob sync', () => {
  const body = source.slice(source.indexOf('async function cycle('));
  const guard = body.indexOf('ensurePullOnly()');
  assert.ok(guard > 0, 'cycle() must call ensurePullOnly()');
  assert.ok(guard < body.indexOf("['sync', '--path'"), 'ensurePullOnly() must run before ob sync');
  assert.match(source, /syncMode\(\) === 'pull-only'/);
});

test('only ob login uses the share as its config home; every other command keeps its state on local disk', () => {
  const body = source.slice(source.indexOf('function obEnv('), source.indexOf('/** Runs `ob`'));
  assert.match(body, /if \(forLogin\) return \{ \.\.\.env, HOME: statePath, XDG_CONFIG_HOME: loginHome \}/);
  assert.match(body, /XDG_CONFIG_HOME: workHome/);
  assert.match(source, /env: obEnv\(args\[0\] === 'login'\)/);
  assert.doesNotMatch(source, /XDG_CONFIG_HOME: (configHome|`\$\{statePath\})/);
});

test('exclusions are compared with what the client holds, never with a remembered file', () => {
  assert.doesNotMatch(source, /appliedFile|readApplied/);
  const body = source.slice(source.indexOf('function applyExclusions('), source.indexOf('function pruneExcluded('));
  assert.match(body, /heldExclusions\(\) === wanted/);
  assert.match(body, /heldExclusions\(\) !== wanted/);
});
