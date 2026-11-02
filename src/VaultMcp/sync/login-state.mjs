// Whether the headless Obsidian client has a login stored. obsidian-headless 0.0.14 reads its token from the OBSIDIAN_AUTH_TOKEN
// environment variable, else from <config home>/obsidian-headless/auth_token (on Linux the config home is $XDG_CONFIG_HOME,
// default ~/.config). Checked by reading the file, not by running `ob login`: that command has no non-interactive status mode
// (`ob login --json` is rejected as an unknown option, which kept the first real deployment waiting after a successful login),
// and without arguments it prompts when the stored token is no longer valid. An expired token is caught by the next `ob` call,
// which then fails with a message the sidecar logs.
import fs from 'node:fs';
import path from 'node:path';

export function tokenPath(configHome) {
  return path.join(configHome, 'obsidian-headless', 'auth_token');
}

export function readToken(configHome) {
  try { return fs.readFileSync(tokenPath(configHome), 'utf8').trim() || null; } catch { return null; }
}

export function hasStoredLogin(env, configHome) {
  return Boolean(env.OBSIDIAN_AUTH_TOKEN || readToken(configHome));
}
