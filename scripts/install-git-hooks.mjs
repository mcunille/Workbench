// Run manually only after reviewing this installer and its checker.
import { spawnSync } from 'node:child_process';
import { accessSync, constants, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, statSync, writeFileSync, chmodSync } from 'node:fs';
import { homedir } from 'node:os';
import { basename, delimiter, dirname, isAbsolute, join, resolve } from 'node:path';
import { createHash } from 'node:crypto';

let gitExecutable;
function git(args, allowMissing = false) {
  const result = spawnSync(gitExecutable, ['-c', 'core.fsmonitor=false', ...args], { encoding: 'utf8' });
  if (allowMissing && result.status === 1) return null;
  if (result.error || result.status !== 0) throw new Error(result.error?.message || result.stderr.trim());
  return result.stdout.trim();
}

function canonical(path) {
  // Native resolution also expands Windows 8.3 aliases (for example RUNNER~1).
  if (existsSync(path)) return realpathSync.native(path);
  return join(canonical(dirname(path)), basename(path));
}

function insideCheckout(path) {
  for (let dir = canonical(path); ; dir = dirname(dir)) {
    if (existsSync(join(dir, '.git'))) return true;
    if (dirname(dir) === dir) return false;
  }
}

function trustedGit() {
  // Enumerate absolute PATH entries ourselves: Windows executable lookup also searches cwd.
  for (const dir of (process.env.PATH || '').split(delimiter)) {
    if (!isAbsolute(dir)) continue;
    const candidate = join(dir, process.platform === 'win32' ? 'git.exe' : 'git');
    if (!existsSync(candidate) || !statSync(candidate).isFile() || insideCheckout(candidate)) continue;
    try { accessSync(candidate, constants.X_OK); } catch { continue; }
    return canonical(candidate);
  }
  throw new Error('No trusted Git executable found on an absolute PATH entry outside Git checkouts.');
}

const quote = text => `'${text.replaceAll("'", "'\\''")}'`;

try {
  if (process.argv.length > 3) throw new Error('Usage: node scripts/install-git-hooks.mjs [external-hooks-directory]');
  gitExecutable = trustedGit();
  if (insideCheckout(process.execPath)) throw new Error('Node must be installed outside Git checkouts.');
  git(['rev-parse', '--show-toplevel']);
  const roots = git(['worktree', 'list', '--porcelain', '-z']).split('\0')
    .filter(field => field.startsWith('worktree ')).map(field => canonical(field.slice(9)));
  const requested = process.argv[2] ? resolve(process.argv[2]) : join(homedir(), '.local', 'share', 'workbench', 'git-hooks');
  if (insideCheckout(requested)) {
    throw new Error('Hook tooling must be installed outside every repository checkout.');
  }
  for (const root of roots) {
    if (git(['-C', root, 'config', '--worktree', '--get', 'core.hooksPath'], true) !== null) {
      throw new Error(`Worktree ${JSON.stringify(root)} has core.hooksPath configured. Review its existing hooks and migrate the setting before installation; inspect with git -C <worktree> config --worktree --get core.hooksPath.`);
    }
  }
  if (git(['config', '--get', 'core.hooksPath'], true) !== null) {
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
  writeFileSync(hook, `#!/bin/sh\nexec ${quote(executable)} ${quote(script)} ${quote(gitExecutable.replaceAll('\\', '/'))}\n`, { flag: 'wx' });
  chmodSync(hook, 0o755);
  git(['config', '--local', 'core.hooksPath', destination.replaceAll('\\', '/')]);
  console.log(`Installed reviewed pre-commit hook: ${destination}`);
  console.log(`Checker SHA-256: ${createHash('sha256').update(source).digest('hex')}`);
  console.log(`Trusted Git executable: ${gitExecutable}`);
  console.log('This repository-local setting applies to its linked worktrees. Reinstall manually after reviewing updates.');
} catch (error) {
  console.error(`Unable to install Git hooks: ${error.message}`);
  process.exitCode = 1;
}
