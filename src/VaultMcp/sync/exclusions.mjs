// Turns the allow-list in adr-sidekick.md (the same note the vault server reads, ADR-0010) into the exclusion list that
// Obsidian Sync understands. Sync only has "excluded folders", so every folder whose subtree holds no allow-listed folder
// is excluded at the shallowest level: for `allow: [Work, Projects/Cloud]` that is every top-level folder except Work and
// Projects, plus every sibling of Cloud inside Projects. Pure functions, no I/O: the caller passes a directory lister.

const FILE_NAME = 'adr-sidekick.md';

/** Reads the `allow:` list from the note's YAML front matter (block list or inline `[a, b]`), the subset VaultConfig.cs reads. */
export function parseAllowList(markdown) {
  const text = markdown.replace(/^﻿/, '').replace(/\r\n/g, '\n');
  if (!text.startsWith('---\n')) return [];
  const end = text.indexOf('\n---', 4);
  if (end < 0) return [];
  const lines = text.slice(4, end).split('\n');
  const raw = [];
  for (let i = 0; i < lines.length; i++) {
    const match = /^allow\s*:\s*(.*)$/.exec(lines[i]);
    if (!match) continue;
    const inline = match[1].trim();
    if (inline.startsWith('[')) {
      inline.replace(/^\[|\]$/g, '').split(',').forEach(v => raw.push(v));
    } else if (inline !== '') {
      raw.push(inline);
    } else {
      for (let j = i + 1; j < lines.length; j++) {
        const item = /^\s*-\s+(.*)$/.exec(lines[j]);
        if (!item) {
          if (lines[j].trim() === '' || /^\s/.test(lines[j])) continue;
          break;
        }
        raw.push(item[1]);
      }
    }
    break;
  }
  return [...new Set(raw.map(normalizeFolder).filter(f => f !== null))];
}

/** A folder entry as the server sees it: no quotes, forward slashes, no leading ./, no trailing /, never hidden, never climbing. */
export function normalizeFolder(value) {
  let v = String(value).trim().replace(/^["']|["']$/g, '').replace(/\\/g, '/').replace(/^\.\//, '').replace(/\/+$/, '');
  if (v === '' || v === '.') return null;
  const parts = v.split('/');
  if (parts.some(p => p === '' || p === '.' || p === '..' || p.startsWith('.'))) return null;
  return parts.join('/');
}

/**
 * The folders to pass to `ob sync-config --excluded-folders`, given the allow-list and a lister that returns the visible
 * sub-folder names of a relative folder ('' for the vault root). Hidden folders (leading dot) are never listed or excluded.
 */
export function computeExclusions(allow, listFolders) {
  const allowed = new Set(allow);
  const excluded = [];
  const walk = prefix => {
    for (const name of listFolders(prefix).filter(n => !n.startsWith('.')).sort()) {
      const path = prefix === '' ? name : `${prefix}/${name}`;
      if (allowed.has(path)) continue;                                   // allowed as a whole: keep, do not descend
      if ([...allowed].some(a => a.startsWith(path + '/'))) walk(path);  // an allowed folder lives below: descend
      else excluded.push(path);                                          // nothing allowed below: exclude here, the shallowest level
    }
  };
  walk('');
  return excluded;
}

/** The exclusion list a seed setting gives before the note has been synced: top-level names only, normalized, de-duplicated. */
export function parseSeed(value) {
  return [...new Set(String(value ?? '').split(',').map(normalizeFolder).filter(f => f !== null))];
}

/** Obsidian Sync takes a comma-separated list; a folder name holding a comma cannot be expressed, so it is refused. */
export function toSyncArgument(folders) {
  for (const f of folders) if (f.includes(',')) throw new Error(`folder name holds a comma and cannot be excluded: ${f}`);
  return folders.join(',');
}

export { FILE_NAME };
