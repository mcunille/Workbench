import { expect, type Page, type Cookie } from '@playwright/test';
import { readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// Login, logout and revocation scenarios use their own sessions. Ordinary scenarios
// reuse two distinct sessions without consuming the shared network login budget.
export async function signInThroughUi(page: Page, navigate = true) {
  if (navigate) await page.goto('/');
  await expect(async () => {
    await page.getByLabel('Email', { exact: true }).fill('browser-admin@example.test');
    await page.getByLabel('Password', { exact: true }).fill('Browser Correct Horse 9!');
    const response = page.waitForResponse(response => new URL(response.url()).pathname === '/api/auth/login');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    expect((await response).status()).toBe(204);
  }).toPass({ timeout: 70_000, intervals: [1_000, 5_000, 10_000] });
  await expect(page.getByRole('heading', { name: 'Collection', exact: true })).toBeVisible();
}

export async function useAuthenticatedSession(page: Page, session: 'primary' | 'secondary' = 'primary') {
  const run = process.env.WORKBENCH_BROWSER_RUN;
  if (!run || !/^browser-[a-f0-9]{12}$/.test(run)) {
    throw new Error('Run browser tests through npm test so the parent owns session cleanup.');
  }
  const path = join(tmpdir(), run, `${session}-cookies.json`);
  let cookies: Cookie[] | undefined;
  try {
    cookies = JSON.parse(await readFile(path, 'utf8')) as Cookie[];
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
  }
  if (cookies) {
    await page.context().addCookies(cookies);
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Collection', exact: true })).toBeVisible();
  } else {
    await signInThroughUi(page);
    // Cookies only: each context keeps its own appearance and other local storage.
    await writeFile(path, JSON.stringify(await page.context().cookies()), { mode: 0o600, flag: 'wx' });
  }
}
