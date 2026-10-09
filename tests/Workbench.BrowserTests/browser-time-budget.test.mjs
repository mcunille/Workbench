import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

test('runtime timeout extensions fail the gate even when their test bodies pass', { timeout: 30_000 }, async () => {
  // GIVEN real passing cases that extend their budget after discovery, and one ordinary passing case.
  const root = await mkdtemp(join(tmpdir(), 'browser-time-budget-'));
  const fixture = fileURLToPath(new URL('./diagnostic-fixture.ts', import.meta.url)).replaceAll('\\', '/');
  const reporter = fileURLToPath(new URL('./diagnostic-reporter.ts', import.meta.url)).replaceAll('\\', '/');
  const cli = fileURLToPath(new URL('./node_modules/@playwright/test/cli.js', import.meta.url));
  const output = join(root, 'results.json');
  try {
    await writeFile(join(root, 'package.json'), JSON.stringify({ type: 'module' }));
    await writeFile(join(root, 'budget.spec.ts'), `
      import { test } from ${JSON.stringify(fixture)};
      test('ordinary', async () => {});
      test('test setter', async () => { test.setTimeout(120_000); });
      test('info setter', async ({}, info) => { info.setTimeout(120_000); });
      test('slow annotation', async () => { test.slow(); });
    `);
    await writeFile(join(root, 'playwright.config.ts'), `export default {
      testDir: '.', testMatch: '*.spec.ts', timeout: 30_000, workers: 1,
      reporter: [[${JSON.stringify(reporter)}, {outputFile:${JSON.stringify(output)}}]],
      outputDir: ${JSON.stringify(join(root, 'raw'))}
    };`);
    // WHEN the actual runner and gate reporter observe the final runtime budgets.
    const result = spawnSync(process.execPath, [cli, 'test', '--config', join(root, 'playwright.config.ts')],
      { encoding: 'utf8', timeout: 20_000, maxBuffer: 1024 * 1024 });
    assert.ifError(result.error);
    const report = JSON.parse(await readFile(output, 'utf8'));
    // THEN all four bodies pass, but the three budget extensions make the run fail.
    assert.equal(result.status, 1);
    assert.equal(report.status, 'failed');
    assert.equal(report.tests.length, 4);
    assert.ok(report.tests.every(entry => entry.status === 'passed'));
    assert.equal(report.tests.filter(entry => entry.timeout > 30_000).length, 3);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
