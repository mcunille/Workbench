// Explicit live acceptance against two already-owned disposable development previews.
// Credentials stay in memory; only synthetic collection screenshots leave private state.
import { createRequire } from 'node:module';
import { readFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { execFile } from 'node:child_process';
const require = createRequire(new URL('../Workbench.BrowserTests/package.json', import.meta.url));
const { chromium, expect } = require('@playwright/test');
const run = promisify(execFile);
const [firstRoot, secondRoot, evidenceRoot] = process.argv.slice(2);
if (!firstRoot || !secondRoot || !evidenceRoot || firstRoot === secondRoot) throw new Error('Supply two distinct preview roots and an external evidence directory.');
async function command(root, name) {
  const { stdout } = await run('pwsh', ['-NoProfile', '-File', join(root, 'scripts', name), '-Json'], { timeout: 600_000, maxBuffer: 2_000_000 });
  return JSON.parse(stdout);
}
const roots = [firstRoot, secondRoot];
const previews = await Promise.all(roots.map(root => command(root, 'dev-status.ps1')));
if (previews.some(p => !p.Ready) || previews[0].Url === previews[1].Url || previews[0].EnvironmentId === previews[1].EnvironmentId) throw new Error('Two distinct ready previews are required.');
const browser = await chromium.launch({ headless: true });
let stage = 'sign-in';
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 980 } });
  const pages = [await context.newPage(), await context.newPage()];
  const saved = [];
  for (let index = 0; index < 2; index++) {
    const page = pages[index];
    const preview = previews[index];
    await page.goto(preview.Url);
    await page.getByLabel('Email', { exact: true }).fill(preview.AdminEmail);
    const password = (await readFile(join(roots[index], '.dev-environment/secrets/admin-password'), 'utf8')).trim();
    await page.getByLabel('Password', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Collection', exact: true })).toBeVisible();
    stage = `save item and photo ${index + 1}`;
    const name = `Worktree ${index + 1} sapphire ${Date.now()}`;
    await page.getByRole('link', { name: 'Add item', exact: true }).click();
    await page.getByLabel('Name', { exact: true }).fill(name);
    await page.getByLabel('Storage location (optional)', { exact: true }).fill(`Preview tray ${index + 1}`);
    await page.getByRole('button', { name: 'Save item', exact: true }).click();
    await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
    const photo = await page.evaluate(index => {
      const canvas = document.createElement('canvas'); canvas.width = 400; canvas.height = 300;
      const ctx = canvas.getContext('2d'); ctx.fillStyle = index ? '#59327c' : '#234b98'; ctx.fillRect(0, 0, 400, 300);
      ctx.fillStyle = '#dce6ff'; ctx.beginPath(); ctx.moveTo(200,40); ctx.lineTo(340,150); ctx.lineTo(200,260); ctx.lineTo(60,150); ctx.closePath(); ctx.fill();
      return canvas.toDataURL('image/png').split(',')[1];
    }, index);
    await page.getByLabel('Choose photograph', { exact: true }).setInputFiles({ name: 'synthetic.png', mimeType: 'image/png', buffer: Buffer.from(photo, 'base64') });
    await expect(page.getByAltText('Prepared photograph preview')).toBeVisible();
    await page.getByRole('button', { name: 'Upload photograph', exact: true }).click();
    await expect(page.getByText('Current saved photograph loaded.', { exact: true })).toBeVisible({ timeout: 30_000 });
    saved.push({ name, url: page.url() });
    await mkdir(evidenceRoot, { recursive: true });
    await page.screenshot({ path: join(evidenceRoot, `preview-${index + 1}.png`), fullPage: true });
    console.log(`Preview ${index + 1}: UI login, item save and photo upload passed.`);
  }
  stage = 'cross-preview isolation';
  for (let index = 0; index < 2; index++) {
    const page = pages[index];
    await page.reload();
    await expect(page.getByAltText(`Photograph of ${saved[index].name}`)).toBeVisible();
    const foreignId = saved[1 - index].url.split('/').at(-1);
    const denied = await page.request.get(`${previews[index].Url}/api/items/${foreignId}`);
    if (denied.status() !== 404) throw new Error('Foreign preview item was not isolated.');
  }
  const cookies = await context.cookies();
  for (const preview of previews) {
    if (!cookies.some(c => c.name === `.Workbench.Session.${preview.EnvironmentId}`)) throw new Error('Missing isolated session cookie.');
  }
  stage = 'stop first preview';
  await command(firstRoot, 'dev-down.ps1');
  stage = 'verify second preview while first is stopped';
  await pages[1].reload();
  await expect(pages[1].getByAltText(`Photograph of ${saved[1].name}`)).toBeVisible();
  stage = 'resume first preview';
  const restarted = await command(firstRoot, 'dev-up.ps1');
  stage = 'verify preferred port and retained session/photo';
  if (restarted.Url !== previews[0].Url) throw new Error('Uncontested preferred port changed after restart.');
  await pages[0].reload();
  await expect(pages[0].getByAltText(`Photograph of ${saved[0].name}`)).toBeVisible();
  console.log('Stop/resume preserved login, records and photos; other preview stayed available.');
  stage = 'logout isolation';
  await pages[0].getByRole('button', { name: 'User menu', exact: true }).click();
  await pages[0].getByRole('button', { name: 'Sign out', exact: true }).click();
  await expect(pages[0].getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
  await pages[1].reload();
  await expect(pages[1].getByAltText(`Photograph of ${saved[1].name}`)).toBeVisible();
  console.log('Two-preview session, database, photo and logout isolation passed.');
} catch {
  // Playwright traces/errors may echo filled inputs. Report only the failing phase.
  throw new Error(`Preview acceptance failed at: ${stage}. No credentials or browser traces were printed.`);
} finally { await browser.close(); }
