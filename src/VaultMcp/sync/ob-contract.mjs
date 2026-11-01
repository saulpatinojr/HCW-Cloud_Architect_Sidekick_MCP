// Every `ob` (obsidian-headless) command the sidecar runs, with every option it passes. Two checks hold the sidecar to it:
// sidecar.test.mjs proves sync.mjs uses nothing outside this list, and check-cli.mjs, run while the image is built, proves the
// installed client accepts every option in it. The client rejects an unknown option and exits non-zero, so an option it does
// not have would otherwise fail every cycle at run time (as `ob login --json` did on the first real deployment).
export const OB_CONTRACT = {
  'login': ['--email', '--password'],
  'sync-list-local': ['--json'],
  'sync-status': ['--path', '--json'],
  'sync-setup': ['--vault', '--path', '--device-name', '--password', '--json'],
  'sync-config': ['--path', '--mode', '--file-types', '--configs', '--excluded-folders', '--json'],
  'sync': ['--path'],
};
