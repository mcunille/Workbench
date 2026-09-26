import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, copyFileSync, writeFileSync, chmodSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const root = fileURLToPath(new URL('../../', import.meta.url));
const windows = process.platform === 'win32';
const shells = windows ? ['cmd', 'sh'] : ['sh'];

for (const shell of shells) {
  test(`${shell}: Live is rejected before engine dispatch; ordinary arguments survive`, () => {
    // GIVEN the real launcher and a harmless engine that records dispatch through stdout.
    const dir = mkdtempSync(join(tmpdir(), 'workbench-launcher-'));
    try {
      const launcher = join(dir, shell === 'cmd' ? 'impeccable.cmd' : 'impeccable');
      copyFileSync(join(root, '.agents/skills/impeccable/scripts', shell === 'cmd' ? 'impeccable.cmd' : 'impeccable'), launcher);
      const engine = join(dir, shell === 'cmd' ? 'engine.cmd' : 'engine');
      writeFileSync(engine, shell === 'cmd'
        ? '@echo off\r\necho ENGINE:%*\r\necho SELF:%IMPECCABLE_SELF%\r\nexit /b 37\r\n'
        : '#!/bin/sh\nprintf "ENGINE:"\nprintf "<%s>" "$@"\nprintf "\\n"\nexit 37\n');
      chmodSync(engine, 0o755);
      const run = (args, enginePath = engine) => {
        const env = { ...process.env, IMPECCABLE_BIN: enginePath };
        delete env.IMPECCABLE_SELF;
        delete env.IMPECCABLE_SKILL_DIR;
        return shell === 'cmd'
          ? spawnSync(process.env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', `""${launcher}" ${args.map(a => `"${a}"`).join(' ')}"`], { env, encoding: 'utf8', windowsVerbatimArguments: true })
          : spawnSync(windows ? resolve(process.env.ProgramFiles, 'Git/bin/bash.exe') : '/bin/sh', [launcher, ...args], { env, encoding: 'utf8' });
      };

      // WHEN ordinary commands include spaces and words containing, but not naming, Live.
      const control = run(['detect', '--target', 'a live page.html']);
      // THEN engine arguments and its exit status are preserved.
      assert.ifError(control.error);
      assert.equal(control.status, 37, control.stderr);
      assert.match(control.stdout, /ENGINE:.*detect.*--target.*a live page\.html/);
      // AND argument inspection preserves the launcher's own path for engine discovery.
      if (shell === 'cmd') assert.ok(control.stdout.includes(`SELF:${launcher}`), control.stdout);
      assert.equal(run([]).status, 37);
      assert.equal(run(['context', '--target', './live']).status, 37);

      for (const args of [ ['live'], ['live-server', '--background'], ['live-inject'],
        ['LiVe'], ['LIVE-POLL'], ['--target', 'app', 'live'], ['--', 'live-server'],
        ['', 'live-server'], ['live-future-command'] ]) {
        // WHEN Live is requested, including indirect ordering and future Live verbs.
        for (const enginePath of [engine, join(dir, 'missing-engine')]) {
          const result = run(args, enginePath);
          // THEN rejection occurs without an engine dispatch, lookup, or download.
          assert.ifError(result.error);
          assert.equal(result.status, 1, `${args}: ${result.stdout} ${result.stderr}`);
          assert.match(result.stderr, /Live integration is disabled/);
          assert.doesNotMatch(result.stdout + result.stderr, /ENGINE:|download|setup needs/);
        }
      }
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
}
