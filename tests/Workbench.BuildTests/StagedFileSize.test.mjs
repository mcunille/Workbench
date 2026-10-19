import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync, readFileSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const checker = fileURLToPath(new URL('../../scripts/check-staged-file-size.mjs', import.meta.url));
const installer = fileURLToPath(new URL('../../scripts/install-git-hooks.mjs', import.meta.url));

function repository(t) {
  const root = mkdtempSync(join(tmpdir(), 'workbench-size-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const env = { ...process.env };
  for (const key of Object.keys(env)) {
    if (key.startsWith('GIT_')) delete env[key];
  }
  const git = (...args) => {
    const result = spawnSync('git', args, { cwd: root, env, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    return result.stdout;
  };
  git('init', '--quiet');
  git('config', 'core.autocrlf', 'false');
  git('config', 'user.name', 'Size test');
  git('config', 'user.email', 'size@example.invalid');
  git('-c', 'core.hooksPath=', 'commit', '--quiet', '--allow-empty', '-m', 'Baseline');
  const write = (path, content, stage = true) => {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), content);
    if (stage) git('add', '--', path);
  };
  return { root, env, git, write, check: () => spawnSync(process.execPath, [checker], { cwd: root, env, encoding: 'utf8' }) };
}

function installation(t, repo) {
  const external = mkdtempSync(join(tmpdir(), "workbench trusted hooks '-"));
  t.after(() => rmSync(external, { recursive: true, force: true }));
  return { external, install: destination => spawnSync(process.execPath, [installer, destination], {
    cwd: repo.root, env: repo.env, encoding: 'utf8',
  }) };
}

test('rejects every oversized staged file with actionable splitting instructions', t => {
  const repo = repository(t);
  // GIVEN authored staged files exceed the limit, including multibyte text and a spaced name.
  repo.write('large file.ts', 'x'.repeat(20_001));
  repo.write('unicode.md', 'é'.repeat(10_001));
  // WHEN checking the index.
  const result = repo.check();
  // THEN all violations show their byte/token counts and concrete remediation steps.
  assert.equal(result.status, 1, result.stderr);
  for (const text of ['large file.ts', 'unicode.md', '20001', '20002', '5000.25', '5000.5']) {
    assert.ok(result.stderr.includes(text), result.stderr);
  }
  assert.match(result.stderr, /split.*responsibilit/i);
  assert.match(result.stderr, /helpers.*components/i);
  assert.match(result.stderr, /imports/i);
  assert.match(result.stderr, /tests/i);
  assert.match(result.stderr, /git add/i);
});

test('allows the exact byte boundary and ignores unstaged and untracked oversized files', t => {
  const repo = repository(t);
  // GIVEN one staged file at the boundary and large files outside the index.
  repo.write('boundary.ts', 'x'.repeat(20_000));
  repo.write('unstaged.ts', 'small');
  repo.git('-c', 'core.hooksPath=', 'commit', '--quiet', '-m', 'Small files');
  repo.write('unstaged.ts', 'x'.repeat(30_000), false);
  repo.write('untracked.ts', 'x'.repeat(30_000), false);
  repo.write('boundary.ts', 'y'.repeat(20_000));
  // WHEN checking the index, THEN exactly 5,000 tokens passes.
  const result = repo.check();
  assert.equal(result.status, 0, result.stderr);
});

test('uses the staged blob when unstaged edits shrink or enlarge the same file', t => {
  const repo = repository(t);
  // GIVEN a large staged blob hidden by a small working copy.
  repo.write('partial.ts', 'x'.repeat(20_001));
  repo.write('partial.ts', 'small', false);
  // WHEN checking, THEN the staged oversized content is rejected.
  assert.equal(repo.check().status, 1);
  // GIVEN a small staged blob hidden by a large working copy.
  repo.git('add', '--', 'partial.ts');
  repo.write('partial.ts', 'x'.repeat(30_000), false);
  // WHEN checking, THEN the staged small content passes.
  const result = repo.check();
  assert.equal(result.status, 0, result.stderr);
});

test('exempts known generated artifacts and binary assets but checks authored lookalikes', t => {
  const repo = repository(t);
  // GIVEN staged dependency locks, API output, EF generated metadata, and a binary asset.
  for (const path of ['package-lock.json', 'nested/packages.lock.json',
    'src/Workbench.Client/src/api/generated.ts', 'src/Workbench.Client/openapi/Workbench.Server_beta.json',
    'src/Workbench.Server/Persistence/Migrations/Initial.Designer.cs',
    'src/Workbench.Server/Persistence/Migrations/WorkbenchDbContextModelSnapshot.cs']) {
    repo.write(path, 'x'.repeat(20_001));
  }
  repo.write('asset.png', Buffer.alloc(30_000));
  // WHEN checking, THEN generated output and binary content pass.
  assert.equal(repo.check().status, 0);
  // GIVEN authored migration logic and an ordinary JSON document exceed the limit.
  repo.write('src/Workbench.Server/Persistence/Migrations/Initial.cs', 'x'.repeat(20_001));
  repo.write('data.json', 'x'.repeat(20_001));
  repo.write('generated.ts', 'x'.repeat(20_001));
  // WHEN checking, THEN generated-looking names outside approved paths are still checked.
  const result = repo.check();
  assert.equal(result.status, 1);
  for (const path of ['Initial.cs', 'data.json', 'generated.ts']) assert.ok(result.stderr.includes(path), result.stderr);
});

test('checks rename destinations and ignores deleted files', t => {
  const repo = repository(t);
  // GIVEN an existing oversized file that is deleted, followed by a rename of oversized content.
  repo.write('old.ts', 'x'.repeat(20_001));
  repo.git('-c', 'core.hooksPath=', 'commit', '--quiet', '-m', 'Old file');
  repo.git('rm', '--quiet', 'old.ts');
  // WHEN only the deletion is staged, THEN there is no file to reject.
  assert.equal(repo.check().status, 0);
  repo.write('renamed.ts', 'x'.repeat(20_001));
  // WHEN the same content is staged at a new path, THEN the destination is rejected.
  const result = repo.check();
  assert.equal(result.status, 1);
  assert.match(result.stderr, /renamed\.ts/);
});

test('fails closed outside a Git repository', t => {
  const root = mkdtempSync(join(tmpdir(), 'workbench-not-git-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  // GIVEN no index, WHEN checking, THEN a Git failure cannot authorize a commit.
  const result = spawnSync(process.execPath, [checker], { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /unable to check staged files/i);
});

test('installed hook blocks real commits using an external snapshot', t => {
  const repo = repository(t);
  const { external, install } = installation(t, repo);
  // GIVEN a reviewed hook installed outside the checkout, at a path with shell metacharacters.
  const result = install(external);
  assert.equal(result.status, 0, result.stderr);
  repo.write('large.ts', 'x'.repeat(20_001));
  // AND a replacement checker staged inside the checkout cannot disable the installed snapshot.
  repo.write('scripts/check-staged-file-size.mjs', 'process.exit(0);');
  // WHEN committing through Git, THEN the actual hook rejects the oversized staged file.
  const rejected = spawnSync('git', ['commit', '--quiet', '-m', 'Too large'], {
    cwd: repo.root, env: repo.env, encoding: 'utf8',
  });
  assert.equal(rejected.status, 1, rejected.stderr);
  assert.match(rejected.stderr, /large\.ts/);
  assert.match(rejected.stderr, /Split each file/);
  // WHEN the file is split below the limit and staged again, THEN committing succeeds.
  repo.write('large.ts', 'x'.repeat(10_000));
  repo.write('helper.ts', 'x'.repeat(10_001));
  repo.git('commit', '--quiet', '-m', 'Split by responsibility');
});

test('installation refuses checkout destinations and preserves existing hooks', t => {
  const repo = repository(t);
  const { external, install } = installation(t, repo);
  // GIVEN an internal destination, WHEN installing, THEN automatic execution of checkout code is refused.
  const internal = install(join(repo.root, 'hooks'));
  assert.equal(internal.status, 1);
  assert.match(internal.stderr, /outside.*checkout/i);
  // GIVEN an existing default hook, WHEN installing, THEN it is preserved.
  const oldHook = join(repo.root, '.git', 'hooks', 'pre-commit');
  writeFileSync(oldHook, '#!/bin/sh\nexit 0\n');
  const existing = install(external);
  assert.equal(existing.status, 1);
  assert.match(existing.stderr, /existing.*hook/i);
  assert.equal(readFileSync(oldHook, 'utf8'), '#!/bin/sh\nexit 0\n');
  // GIVEN an existing hooksPath, WHEN installing, THEN that configuration is preserved.
  repo.git('config', 'core.hooksPath', join(external, 'custom'));
  assert.equal(install(external).status, 1);
  assert.equal(repo.git('config', '--get', 'core.hooksPath').trim(), join(external, 'custom'));
});

test('installation rejects destinations through symlinks and inside another linked worktree', t => {
  const repo = repository(t);
  const { external, install } = installation(t, repo);
  // GIVEN an external directory junction or symlink leading back into the checkout.
  const alias = join(external, 'checkout-alias');
  symlinkSync(repo.root, alias, process.platform === 'win32' ? 'junction' : 'dir');
  // WHEN installing through that alias, THEN the resolved internal location is rejected.
  const linkedPath = install(join(alias, 'hooks'));
  assert.equal(linkedPath.status, 1, linkedPath.stderr);
  assert.match(linkedPath.stderr, /outside.*checkout/i);
  // GIVEN a second worktree of this repository.
  const other = join(external, 'other-checkout');
  repo.git('worktree', 'add', '--quiet', '--detach', other);
  // WHEN installing inside it from the first checkout, THEN shared hooks remain outside both.
  const otherPath = install(join(other, 'hooks'));
  assert.equal(otherPath.status, 1, otherPath.stderr);
  assert.match(otherPath.stderr, /outside.*checkout/i);
});

test('honors the active index on an unborn branch, including unusual filenames', t => {
  const repo = repository(t);
  // GIVEN an unborn branch with a large staged file and a separate empty alternate index.
  repo.git('checkout', '--quiet', '--orphan', 'new-history');
  const path = process.platform === 'win32' ? 'naïve file.ts' : 'naïve\nfile.ts';
  repo.write(path, 'x'.repeat(20_001));
  // WHEN checking the initial commit, THEN the large staged file is rejected.
  assert.equal(repo.check().status, 1);
  const env = { ...repo.env, GIT_INDEX_FILE: join(repo.root, 'alternate-index') };
  const empty = spawnSync('git', ['read-tree', '--empty'], { cwd: repo.root, env, encoding: 'utf8' });
  assert.equal(empty.status, 0, empty.stderr);
  // WHEN Git selects the empty alternate index, THEN unrelated staged content is ignored.
  const result = spawnSync(process.execPath, [checker], { cwd: repo.root, env, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
});

test('default installation creates a runnable snapshot under the user home outside the checkout', t => {
  const repo = repository(t);
  const { external } = installation(t, repo);
  // GIVEN an isolated home, WHEN installing without a directory override.
  const env = { ...repo.env, HOME: external, USERPROFILE: external };
  const result = spawnSync(process.execPath, [installer], { cwd: repo.root, env, encoding: 'utf8' });
  // THEN the documented default is an external reviewed snapshot and enforces real commits.
  assert.equal(result.status, 0, result.stderr);
  const configured = repo.git('config', '--local', '--get', 'core.hooksPath').trim();
  assert.ok(configured.startsWith(external.replaceAll('\\', '/') + '/.local/share/workbench/git-hooks/reviewed-'), configured);
  repo.write('large.ts', 'x'.repeat(20_001));
  const rejected = spawnSync('git', ['commit', '--quiet', '-m', 'Too large'], { cwd: repo.root, env, encoding: 'utf8' });
  assert.equal(rejected.status, 1, rejected.stderr);
  assert.match(rejected.stderr, /large\.ts/);
});

test('Windows short path aliases cannot install hook tooling inside the checkout', { skip: process.platform !== 'win32' }, t => {
  const repo = repository(t);
  const { install } = installation(t, repo);
  // GIVEN an 8.3 alias for the checkout, as used by Windows runner temp directories.
  const alias = spawnSync('cmd.exe', ['/d', '/c', `for %I in ("${repo.root}") do @echo %~sI`], {
    encoding: 'utf8', windowsVerbatimArguments: true,
  });
  assert.equal(alias.status, 0, alias.stderr);
  const short = alias.stdout.trim();
  if (!short.includes('~')) {
    t.skip('This volume does not provide 8.3 aliases; CI runner aliases remain covered where available.');
    return;
  }
  // WHEN installing through the alias, THEN it resolves to the protected checkout and is refused.
  const result = install(join(short, 'hooks'));
  assert.equal(result.status, 1, result.stdout + result.stderr);
  assert.match(result.stderr, /outside.*checkout/i);
});
