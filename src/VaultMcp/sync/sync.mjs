#!/usr/bin/env node
// Obsidian Headless sync sidecar (ADR-0010 phase 2, step 2, Route A).
//
// Runs next to the vault MCP server in Azure Container Apps and keeps a pull-only copy of the allow-listed folders of the
// vault on the shared Azure Files share that the server reads. Every cycle: make sure `ob` is logged in and the vault is set
// up, derive the Sync exclusion list from adr-sidekick.md (the same note the server reads), apply it when it changed, run one
// `ob sync`, then delete local copies of folders that are now excluded. It fails closed: without the note and without a seed
// list it syncs nothing, because an empty exclusion list would pull the whole vault.
//
// Settings (environment; secrets arrive as Container Apps secret references, never as plain settings):
//   OB_VAULT               remote vault name or id (required)
//   OB_VAULT_PASSWORD      end-to-end encryption password of that vault (secret; omit for a standard-encryption vault)
//   OB_EMAIL, OB_PASSWORD  Obsidian account for a non-interactive login (secrets). Without them, log in once by hand:
//                          az containerapp exec ... --command "ob login"   (the login state persists under STATE_PATH)
//   SYNC_EXCLUDE_SEED      comma-separated top-level folders to exclude before adr-sidekick.md exists locally
//   SYNC_INTERVAL_SECONDS  seconds between cycles (default 60)
//   SYNC_FILE_TYPES        attachment types to sync (default empty: Markdown only)
//   SYNC_PRUNE             delete local copies of excluded folders after each cycle (default true)
//   OB_DEVICE_NAME         how this client shows in Sync's version history (default the container app name)
//   VAULT_PATH, STATE_PATH mount points (default /vault and /state); HOME must point at STATE_PATH so `ob` keeps its login there
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';
import { parseAllowList, computeExclusions, parseSeed, toSyncArgument, FILE_NAME } from './exclusions.mjs';

const env = process.env;
const vaultPath = env.VAULT_PATH || '/vault';
const statePath = env.STATE_PATH || '/state';
const interval = Math.max(15, Number(env.SYNC_INTERVAL_SECONDS || 60));
const prune = (env.SYNC_PRUNE || 'true').toLowerCase() !== 'false';
const checkOnly = process.argv.includes('--check');
const once = process.argv.includes('--once');
const appliedFile = path.join(statePath, 'excluded-folders.txt');

const log = (...a) => console.log(new Date().toISOString(), ...a);
const fail = (code, message) => { console.error(message); process.exit(code); };

/** Runs `ob` with the given arguments; secrets are passed as arguments but never logged. */
function ob(args, { secretArgs = [] } = {}) {
  const shown = args.map(a => (secretArgs.includes(a) ? '<secret>' : a)).join(' ');
  // input: '' closes stdin, so a prompt the --json flag did not suppress ends with an error instead of hanging the cycle.
  const result = spawnSync('ob', args, { encoding: 'utf8', input: '', env: { ...env, HOME: statePath }, timeout: 15 * 60 * 1000 });
  if (result.error) return { ok: false, out: '', err: `ob could not start: ${result.error.message}`, shown };
  return { ok: result.status === 0, out: result.stdout ?? '', err: result.stderr ?? '', shown };
}

function checkConfiguration() {
  const problems = [];
  if (!env.OB_VAULT) problems.push('OB_VAULT is not set (the remote vault name or id).');
  if (spawnSync('ob', ['sync-list-local', '--json'], { encoding: 'utf8', input: '', env: { ...env, HOME: statePath } }).error) problems.push('the ob command (obsidian-headless) is not installed.');
  if (!fs.existsSync(vaultPath)) problems.push(`VAULT_PATH ${vaultPath} does not exist (mount the vault share there).`);
  if (!fs.existsSync(statePath)) problems.push(`STATE_PATH ${statePath} does not exist (mount the state share there).`);
  if ((env.OB_EMAIL ? 1 : 0) + (env.OB_PASSWORD ? 1 : 0) === 1) problems.push('OB_EMAIL and OB_PASSWORD must be set together or not at all.');
  return problems;
}

function loggedIn() {
  // `ob login` with no options prints the account when a login is stored; in --json mode it never prompts.
  const r = ob(['login', '--json']);
  return r.ok;
}

function login() {
  if (loggedIn()) return true;
  if (env.OB_EMAIL && env.OB_PASSWORD) {
    const r = ob(['login', '--email', env.OB_EMAIL, '--password', env.OB_PASSWORD, '--json'], { secretArgs: [env.OB_EMAIL, env.OB_PASSWORD] });
    if (r.ok) { log('logged in to Obsidian Sync with the account from Key Vault'); return true; }
    log('login failed:', r.err.trim().split('\n')[0] || '(no message)', '- if the account uses two-factor authentication, log in once by hand with az containerapp exec');
    return false;
  }
  log('not logged in and no OB_EMAIL/OB_PASSWORD: waiting. Log in once with: az containerapp exec --name <sync app> --resource-group <rg> --command "ob login"');
  return false;
}

function vaultConfigured() {
  return ob(['sync-status', '--path', vaultPath, '--json']).ok;
}

function setupVault() {
  const args = ['sync-setup', '--vault', env.OB_VAULT, '--path', vaultPath, '--device-name', env.OB_DEVICE_NAME || env.CONTAINER_APP_NAME || 'azure-vault-sync', '--json'];
  const secretArgs = [];
  if (env.OB_VAULT_PASSWORD) { args.push('--password', env.OB_VAULT_PASSWORD); secretArgs.push(env.OB_VAULT_PASSWORD); }
  const r = ob(args, { secretArgs });
  if (!r.ok) { log('sync-setup failed:', r.err.trim().split('\n')[0] || '(no message)'); return false; }
  // Pull only, Markdown only, no Obsidian configuration or plugins: the share holds notes and nothing that runs.
  const c = ob(['sync-config', '--path', vaultPath, '--mode', 'pull-only', '--file-types', env.SYNC_FILE_TYPES || '', '--configs', '', '--json']);
  if (!c.ok) { log('sync-config failed:', c.err.trim().split('\n')[0] || '(no message)'); return false; }
  log('vault set up for pull-only sync into', vaultPath);
  return true;
}

function listFolders(relative) {
  try {
    return fs.readdirSync(path.join(vaultPath, relative), { withFileTypes: true }).filter(d => d.isDirectory() && !d.isSymbolicLink()).map(d => d.name);
  } catch { return []; }
}

/** The exclusion list for this cycle, or null when nothing allows a safe sync yet. */
function desiredExclusions() {
  const note = path.join(vaultPath, FILE_NAME);
  if (fs.existsSync(note)) {
    const allow = parseAllowList(fs.readFileSync(note, 'utf8'));
    return { source: FILE_NAME, allow, excluded: computeExclusions(allow, listFolders) };
  }
  const seed = parseSeed(env.SYNC_EXCLUDE_SEED);
  if (seed.length > 0) return { source: 'SYNC_EXCLUDE_SEED', allow: [], excluded: seed };
  return null;
}

function readApplied() { try { return fs.readFileSync(appliedFile, 'utf8').trim(); } catch { return null; } }

function applyExclusions(excluded) {
  const argument = toSyncArgument(excluded);
  if (readApplied() === argument) return true;
  const r = ob(['sync-config', '--path', vaultPath, '--excluded-folders', argument, '--json']);
  if (!r.ok) { log('sync-config --excluded-folders failed:', r.err.trim().split('\n')[0] || '(no message)'); return false; }
  fs.writeFileSync(appliedFile, argument + '\n');
  log(`exclusions applied (${excluded.length}):`, excluded.join(', ') || '(none)');
  return true;
}

function pruneExcluded(excluded) {
  const root = fs.realpathSync(vaultPath);
  for (const folder of excluded) {
    const target = path.join(root, folder);
    let real;
    try { real = fs.realpathSync(target); } catch { continue; }
    if (!real.startsWith(root + path.sep) || path.basename(real).startsWith('.')) continue;
    if (fs.lstatSync(target).isSymbolicLink()) continue;
    fs.rmSync(target, { recursive: true, force: true });
    log('removed local copy of excluded folder', folder);
  }
}

async function cycle() {
  if (!login()) return false;
  if (!vaultConfigured() && !setupVault()) return false;
  const desired = desiredExclusions();
  if (desired === null) {
    log(`refusing to sync: ${FILE_NAME} is not in the share yet and SYNC_EXCLUDE_SEED is empty; an empty exclusion list would pull the whole vault. Set the seed to the top-level folders that must stay private.`);
    return false;
  }
  if (!applyExclusions(desired.excluded)) return false;
  const s = ob(['sync', '--path', vaultPath]);
  if (!s.ok) { log('sync failed:', (s.err || s.out).trim().split('\n').slice(-1)[0] || '(no message)'); return false; }
  log(`synced; exclusions from ${desired.source}; allowed folders: ${desired.allow.join(', ') || '(none yet)'}`);
  if (prune) pruneExcluded(desired.excluded);
  return true;
}

const problems = checkConfiguration();
if (checkOnly) {
  if (problems.length > 0) fail(2, problems.join('\n'));
  console.log('configuration ok');
  process.exit(0);
}
if (problems.length > 0) fail(2, problems.join('\n'));

log(`vault sync sidecar starting: vault=${vaultPath} state=${statePath} interval=${interval}s prune=${prune}`);
for (;;) {
  let ok = false;
  try { ok = await cycle(); } catch (e) { log('cycle failed:', e.message); }
  if (once) process.exit(ok ? 0 : 1);
  await sleep((ok ? interval : Math.min(interval * 5, 600)) * 1000);
}
