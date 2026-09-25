import { test } from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from '@playwright/test';
import { access, mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { captureLayout } from './safe-diagnostics.mjs';

test('failure evidence retains overflow geometry but excludes secret-bearing page content', async () => {
  // GIVEN an isolated synthetic page with secrets in every common browser surface.
  const root = await mkdtemp(join(tmpdir(), 'safe-layout-'));
  const browser = await chromium.launch();
  try {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
    await context.addCookies([{ name: 'session', value: 'SECRET_CANARY', domain: 'example.test', path: '/' }]);
    const page = await context.newPage();
    await page.setContent(`<style>button {position:absolute;left:380px;width:120px}</style>
      <button id="SECRET_CANARY" title="SECRET_CANARY">SECRET_CANARY</button>
      <input value="SECRET_CANARY"><a href="https://example.test/recover/SECRET_CANARY">SECRET_CANARY</a>
      <img alt="SECRET_CANARY"><script>localStorage</script>`);
    // WHEN collecting diagnostics for the intentionally overflowing layout.
    const result = await captureLayout(page, root);
    const json = await readFile(join(result, 'layout.json'), 'utf8');
    const layout = JSON.parse(json);
    // THEN the offending element can be identified and its overflow measured.
    const button = layout.elements.find(element => element.tag === 'button');
    assert.equal(button.selector, 'html:nth-child(1)>body:nth-child(2)>button:nth-child(1)');
    assert.ok(button.x + button.width > layout.viewport.width);
    assert.ok((await readFile(join(result, 'layout.png'))).length > 100);
    // AND the upload directory contains only reconstructed pixels and geometry, never raw browser data.
    assert.deepEqual((await readdir(result)).sort(), ['layout.json', 'layout.png']);
    assert.ok(!json.includes('SECRET_CANARY'));
    assert.ok(!json.includes('example.test'));
    assert.equal(layout.elements.some(element => element.tag === 'script'), false);
    assert.deepEqual(Object.keys(button).sort(), ['height', 'selector', 'tag', 'width', 'x', 'y']);
  } finally { await browser.close(); await rm(root, { recursive: true, force: true }); }
});

test('changing secrets cannot change retained pixels or geometry; capture budgets are bounded', async () => {
  // GIVEN identical layout with different secret text, attributes, pseudo-content and background URLs.
  const root = await mkdtemp(join(tmpdir(), 'safe-layout-'));
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
    for (const [index, secret] of ['CANARY_ONE', 'CANARY_TWO'].entries()) {
      await page.setContent(`<style>div {width:100px;height:50px;overflow:hidden;background:url('https://invalid.test/${secret}')} div::before {content:'${secret}'}</style><div data-secret="${secret}">${secret}</div>`);
      await captureLayout(page, root);
    }
    // THEN both pixel and JSON artifacts are byte-identical despite different secrets.
    for (const file of ['layout.json', 'layout.png']) {
      assert.deepEqual(await readFile(join(root, 'failure-0', file)), await readFile(join(root, 'failure-1', file)));
    }
    // WHEN many failures arrive, only ten bounded captures are retained.
    for (let index = 2; index < 11; index++) await captureLayout(page, root);
    assert.equal((await readdir(root)).length, 10);
    assert.equal(await captureLayout(page, root), undefined);
  } finally { await browser.close(); await rm(root, { recursive: true, force: true }); }
});

test('an intentional Playwright failure retains safe files and still fails; passing tests retain nothing', async () => {
  const { spawnSync } = await import('node:child_process');
  const { writeFile } = await import('node:fs/promises');
  const { fileURLToPath } = await import('node:url');
  const root = await mkdtemp(join(tmpdir(), 'diagnostic-contract-'));
  const evidence = join(root, 'evidence');
  await writeFile(join(root, 'package.json'), JSON.stringify({ type: 'module' }));
  try {
    // GIVEN the production automatic fixture on a synthetic overflowing page, without a server or session.
    const fixture = fileURLToPath(new URL('./diagnostic-fixture.ts', import.meta.url)).replaceAll('\\', '/');
    await writeFile(join(root, 'contract.spec.ts'), `import { test, expect } from ${JSON.stringify(fixture)};
      import { mkdir, writeFile } from 'node:fs/promises';
      import { tmpdir } from 'node:os';
      import { join } from 'node:path';
      test('intentional layout failure', async ({ page }) => {
        const sessionRoot = join(tmpdir(), process.env.WORKBENCH_BROWSER_RUN);
        await mkdir(sessionRoot, {recursive:true});
        await writeFile(join(sessionRoot, 'primary-cookies.json'), 'SYNTHETIC_SESSION_CANARY');
        await writeFile(${JSON.stringify(join(root, 'session-path.txt'))}, sessionRoot);
        await page.setViewportSize({width:390,height:844});
        await page.setContent('<button style="position:absolute;left:380px;width:120px">Synthetic action</button>');
        const bounds = await page.locator('button').boundingBox();
        console.log('SECRET_CANARY');
        expect(bounds.x + bounds.width, 'SECRET_CANARY').toBeLessThanOrEqual(390);
      });
      test('passing layout', async ({ page }) => { await page.setContent('<button>OK</button>'); });`);
    await writeFile(join(root, 'playwright.config.ts'), `export default { testDir: '.', testMatch: '*.spec.ts', workers: 1, reporter: [[${JSON.stringify(fileURLToPath(new URL('./diagnostic-reporter.ts', import.meta.url))) }, {outputFile: ${JSON.stringify(join(root, 'results.json'))}}], ['line']], metadata: {diagnosticsRoot: ${JSON.stringify(evidence)}}, outputDir: ${JSON.stringify(join(root, 'raw'))}, use: {trace:'off'} };`);
    // WHEN Playwright runs the actual failing assertion and automatic teardown.
    const run = spawnSync('pwsh', ['-NoProfile', '-File', fileURLToPath(new URL('../../scripts/test-browser.ps1', import.meta.url)), '--config', join(root, 'playwright.config.ts')], { encoding: 'utf8', timeout: 60000 });
    // THEN its original failure remains nonzero and the logged artifact path resolves to retained files.
    assert.equal(run.status, 1, run.stdout + run.stderr);
    // AND retained JSON preserves outcomes and timings without error, console or attachment data.
    const summaryText = await readFile(join(root, 'results.json'), 'utf8');
    const summary = JSON.parse(summaryText);
    assert.equal(summary.status, 'failed');
    assert.equal(summary.tests.length, 2);
    assert.deepEqual(summary.tests.map(test => test.status), ['failed', 'passed']);
    assert.ok(summary.tests.every(test => test.duration >= 0 && test.file === 'contract.spec.ts'));
    assert.ok(!summaryText.includes('SECRET_CANARY'));
    assert.deepEqual(Object.keys(summary.tests[0]).sort(), ['column', 'duration', 'expectedStatus', 'file', 'id', 'line', 'retry', 'status']);
    assert.ok(run.stdout.includes('Safe browser layout evidence:'), run.stdout + run.stderr);
    const directories = await readdir(evidence);
    assert.equal(directories.length, 1);
    assert.ok(run.stdout.includes(join(evidence, directories[0])));
    assert.deepEqual((await readdir(join(evidence, directories[0]))).sort(), ['layout.json', 'layout.png']);
    assert.ok(!run.stdout.includes('error-context.md'), 'CI must not advertise discarded raw error contexts');
    assert.ok(run.stdout.includes('1 passed'));
    assert.ok(run.stdout.includes('Browser test SQL container and temporary files removed.'));
    const sessionRoot = await readFile(join(root, 'session-path.txt'), 'utf8');
    await assert.rejects(access(sessionRoot), { code: 'ENOENT' });
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('CI uploads only bounded safe diagnostics and normal specs use the automatic fixture', async () => {
  // GIVEN the checked-in workflow and normal browser suite.
  const workflow = await readFile(new URL('../../.github/workflows/ci.yml', import.meta.url), 'utf8');
  const config = await readFile(new URL('./playwright.config.ts', import.meta.url), 'utf8');
  // WHEN inspecting the upload boundary, raw Playwright output must never be included.
  const upload = workflow.split('      - name: Retain safe browser failure diagnostics')[1].split('      - name: Link browser diagnostics')[0];
  assert.ok(upload.includes('failure()'));
  assert.ok(upload.includes('retention-days: 7'));
  assert.deepEqual(upload.split('          path: |')[1].trim().split(/\r?\n/).map(line => line.trim()), [
    'artifacts/browser-diagnostics/failure-*/layout.json',
    'artifacts/browser-diagnostics/failure-*/layout.png',
  ]);
  assert.ok(config.includes("trace: 'off'"));
  assert.ok(config.includes("globalSetup: './diagnostic-setup.ts'"));
  // THEN all normal specs get teardown capture, including newly added specs.
  for (const file of await readdir(new URL('.', import.meta.url))) {
    if (!file.endsWith('.spec.ts')) continue;
    const source = await readFile(new URL(file, import.meta.url), 'utf8');
    assert.ok(!source.includes("from '@playwright/test'"), `${file} bypasses diagnostics`);
    assert.ok(source.includes("from './diagnostic-fixture'"), `${file} missing diagnostics`);
  }
});
