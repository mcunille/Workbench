import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdir, rm, writeFile } from 'node:fs/promises';
import { basename, dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';

const runFile = promisify(execFile);
const project = join(import.meta.dirname, 'FinancialEvidenceSeed.csproj');

test('synthetic evidence seeder rejects the wrong run token and database before connecting', async () => {
  // GIVEN a disposable browser run token and a credential-free decoy file.
  const run = `browser-${randomBytes(6).toString('hex')}`;
  const runDirectory = join(tmpdir(), run);
  const expectedFile = join(runDirectory, 'setup.connection');
  const wrongFile = join(tmpdir(), `browser-${randomBytes(6).toString('hex')}`, 'setup.connection');
  const ids = [randomUUID(), randomUUID(), randomUUID()];
  await mkdir(runDirectory);
  try {
    await writeFile(expectedFile, 'Server=127.0.0.1,1433;Database=workbench_not_this_run;Encrypt=False;');
    const invoke = async (file) => {
      try {
        await runFile('dotnet', ['run', '--no-build', '--configuration', 'Release', '--project', project, '--', 'add', file, ...ids],
          { env: { ...process.env, WORKBENCH_BROWSER_RUN: run }, timeout: 30_000 });
        assert.fail('Seeder unexpectedly accepted a decoy fixture.');
      } catch (error) {
        return error;
      }
    };
    // WHEN a caller supplies another run token's file path.
    const wrongPath = await invoke(wrongFile);
    // THEN the path guard rejects it before reading or opening a connection.
    assert.match(wrongPath.stderr, /Only this browser run's protected connection file is accepted/);
    // WHEN the exact path names a different database.
    const wrongDatabase = await invoke(expectedFile);
    // THEN the database guard rejects it before opening a connection or writing.
    assert.match(wrongDatabase.stderr, /Fixture connection must target this loopback browser database/);
  } finally {
    if (basename(runDirectory) !== run || dirname(resolve(runDirectory)) !== resolve(tmpdir()))
      throw new Error('Refusing to remove an unexpected guard-test directory.');
    await rm(runDirectory, { recursive: true, force: true });
  }
});
