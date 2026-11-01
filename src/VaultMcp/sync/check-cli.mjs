// Build-time check (Dockerfile): the installed `ob` accepts every command and option in ob-contract.mjs. Exits 1 and names
// what is missing, so an image that would fail at run time is never built.
import { spawnSync } from 'node:child_process';
import { OB_CONTRACT } from './ob-contract.mjs';

const missing = [];
for (const [command, options] of Object.entries(OB_CONTRACT)) {
  const r = spawnSync('ob', [command, '--help'], { encoding: 'utf8', input: '' });
  if (r.error || r.status !== 0) { missing.push(`ob ${command}: ${r.error?.message || (r.stderr || r.stdout).trim().split('\n')[0]}`); continue; }
  const help = r.stdout;
  for (const option of options) if (!new RegExp(`(^|[\\s,])${option}(\\s|$|<|,)`, 'm').test(help)) missing.push(`ob ${command} ${option}`);
}
if (missing.length) { console.error('the installed obsidian-headless does not accept:\n  ' + missing.join('\n  ')); process.exit(1); }
console.log(`obsidian-headless accepts all ${Object.values(OB_CONTRACT).flat().length} options the sidecar uses`);
