// Run manually only after reviewing this installer and its checker.
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, writeFileSync, chmodSync } from 'node:fs';
import { homedir } from 'node:os';
import { basename, dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { createHash } from 'node:crypto';

function git(args, allowMissing = false) {
  const result = spawnSync('git', ['-c', 'core.fsmonitor=false', ...args], { encoding: 'utf8' });
  if (allowMissing && result.status === 1) return '';
  if (result.error || result.status !== 0) throw new Error(result.error?.message || result.stderr.trim());
  return result.stdout.trim();
}

function canonical(path) {
  // Native resolution also expands Windows 8.3 aliases (for example RUNNER~1).
  if (existsSync(path)) return realpathSync.native(path);
  return join(canonical(dirname(path)), basename(path));
}

function within(path, root) {
  const rel = relative(root, path);
  return rel === '' || (!isAbsolute(rel) && rel !== '..' && !rel.startsWith('..\\') && !rel.startsWith('../'));
}

const quote = text => `'${text.replaceAll("'", "'\\''")}'`;

try {
  if (process.argv.length > 3) throw new Error('Usage: node scripts/install-git-hooks.mjs [external-hooks-directory]');
  git(['rev-parse', '--show-toplevel']);
  const roots = git(['worktree', 'list', '--porcelain', '-z']).split('\0')
    .filter(field => field.startsWith('worktree ')).map(field => canonical(field.slice(9)));
  const requested = process.argv[2] ? resolve(process.argv[2]) : join(homedir(), '.local', 'share', 'workbench', 'git-hooks');
  if (roots.some(root => within(canonical(requested), root))) {
    throw new Error('Hook tooling must be installed outside every repository checkout.');
  }
  if (git(['config', '--get', 'core.hooksPath'], true)) {
    throw new Error('Existing core.hooksPath is configured; review and preserve existing hooks before replacing it.');
  }
  const currentHooks = git(['rev-parse', '--git-path', 'hooks']);
  if (existsSync(currentHooks)) {
    const existing = readdirSync(currentHooks).find(name => !name.endsWith('.sample'));
    if (existing) throw new Error(`Existing Git hook directory contains ${existing}; installation stopped to preserve existing hooks.`);
  }
  const source = readFileSync(new URL('./check-staged-file-size.mjs', import.meta.url));
  mkdirSync(requested, { recursive: true });
  const destination = process.argv[2] ? realpathSync.native(requested) : mkdtempSync(join(requested, 'reviewed-'));
  const checker = join(destination, 'check-staged-file-size.mjs');
  const hook = join(destination, 'pre-commit');
  // Both files are fixed snapshots. The hook never imports or executes files from a checkout.
  writeFileSync(checker, source, { flag: 'wx' });
  const executable = process.execPath.replaceAll('\\', '/');
  const script = checker.replaceAll('\\', '/');
  writeFileSync(hook, `#!/bin/sh\nexec ${quote(executable)} ${quote(script)}\n`, { flag: 'wx' });
  chmodSync(hook, 0o755);
  git(['config', '--local', 'core.hooksPath', destination.replaceAll('\\', '/')]);
  console.log(`Installed reviewed pre-commit hook: ${destination}`);
  console.log(`Checker SHA-256: ${createHash('sha256').update(source).digest('hex')}`);
  console.log('This repository-local setting applies to its linked worktrees. Reinstall manually after reviewing updates.');
} catch (error) {
  console.error(`Unable to install Git hooks: ${error.message}`);
  process.exitCode = 1;
}
