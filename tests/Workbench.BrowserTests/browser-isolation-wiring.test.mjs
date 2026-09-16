import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { mkdtemp, writeFile, readFile, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

test('intercepted project blocks undeclared API requests before reaching the server', { timeout: 60_000 }, async () => {
  // GIVEN the real diagnostic fixture against a local server that counts API requests.
  let backendRequests = 0;
  const server = createServer((request, response) => {
    if (request.url.startsWith('/api/')) backendRequests++;
    response.setHeader('Content-Type', 'text/html');
    response.end('<!doctype html><html><body>Guard fixture</body></html>');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const root = await mkdtemp(join(tmpdir(), 'browser-isolation-wiring-'));
  const baseURL = `http://127.0.0.1:${server.address().port}`;
  const fixture = fileURLToPath(new URL('./diagnostic-fixture.ts', import.meta.url)).replaceAll('\\', '/');
  const report = join(root, 'results.json');
  try {
    await writeFile(join(root, 'package.json'), JSON.stringify({ type: 'module' }));
    await writeFile(join(root, 'guard.spec.ts'), `
      import { test, expect } from ${JSON.stringify(fixture)};
      test('undeclared API', async ({ page }) => {
        await page.goto('/');
        await page.evaluate(() => fetch('/api/undeclared').catch(() => undefined));
      });
      test('declared API', async ({ page }) => {
        await page.route('**/api/declared', route => route.fulfill({json: {ok:true}}));
        await page.goto('/');
        expect(await page.evaluate(() => fetch('/api/declared').then(r => r.json()))).toEqual({ok:true});
      });
    `);
    await writeFile(join(root, 'playwright.config.ts'), `export default {
      testDir: '.', testMatch: '*.spec.ts', workers: 1,
      reporter: [['json', {outputFile:${JSON.stringify(report)}}]],
      outputDir: ${JSON.stringify(join(root, 'raw'))},
      metadata: {diagnosticsRoot:${JSON.stringify(join(root, 'safe'))}},
      projects: [{name:'intercepted'}], use: {baseURL:${JSON.stringify(baseURL)}, trace:'off'}
    };`);
    // WHEN executing one undeclared request and one explicit interception in the real fixture.
    const cli = fileURLToPath(new URL('./node_modules/@playwright/test/cli.js', import.meta.url));
    const result = await new Promise((resolve, reject) => {
      const child = spawn(process.execPath, [cli, 'test', '--config', join(root, 'playwright.config.ts')], { stdio: 'ignore' });
      const timer = setTimeout(() => { child.kill(); reject(new Error('Guard verification exceeded deadline.')); }, 45_000);
      child.once('error', error => { clearTimeout(timer); reject(error); });
      child.once('exit', code => { clearTimeout(timer); resolve(code); });
    });
    const results = JSON.parse(await readFile(report, 'utf8'));
    // THEN only the undeclared scenario fails, and neither request reached the live backend.
    assert.equal(result, 1);
    assert.equal(results.stats.unexpected, 1);
    assert.equal(results.stats.expected, 1);
    assert.equal(backendRequests, 0);
    // AND a teardown guard failure still runs automatic, bounded safe diagnostics.
    const captures = await readdir(join(root, 'safe'));
    assert.equal(captures.length, 1);
    assert.match(captures[0], /^failure-[0-9]$/);
    assert.deepEqual((await readdir(join(root, 'safe', captures[0]))).sort(), ['layout.json', 'layout.png']);
  } finally {
    await new Promise(resolve => server.close(resolve));
    await rm(root, { recursive: true, force: true });
  }
});
