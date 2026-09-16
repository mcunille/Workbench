import { openUserMenu } from './user-menu-fixture';
import { expect, test } from './diagnostic-fixture';
import { signInThroughUi, useAuthenticatedSession } from './auth-fixture';
import { browserBaseUrl } from './browser-environment';
import { verifyOwnerDataIsolation } from './owner-isolation-fixture';
import type { Page } from './diagnostic-fixture';

const signIn = (page: Page) => signInThroughUi(page, true, 'auth');

// Dedicated auth sessions share the real login budget with other browser scenarios.
test.setTimeout(120_000);

const email = 'browser-auth@example.test';
const password = 'Browser Correct Horse 9!';

for (const path of ['/recover', '/invite']) {
  test(`capability URL is scrubbed and remains usable at ${path}`, async ({ page }) => {
    // GIVEN a query capability and intercepted account APIs
    const token = 'browser-sentinel';
    const requests: import('@playwright/test').Request[] = [];
    page.on('request', (request) => requests.push(request));
    await page.route('**/api/auth/antiforgery', (route) => route.fulfill({ json: { requestToken: 'csrf' } }));
    const endpoint = path === '/invite' ? '/api/auth/invitations/consume' : '/api/auth/recovery/consume';
    await page.route(`**${endpoint}`, async (route) => {
      // THEN the capability travels only in the consumption body
      expect(route.request().postDataJSON()).toEqual({ token, newPassword: password });
      expect(route.request().headers()['referer']).toBeUndefined();
      await route.fulfill({ status: 204 });
    });
    await page.goto('/recover');
    // WHEN the link opens and the form is submitted
    await page.goto(`${path}?token=${token}&token=discarded`);
    await expect(page).toHaveURL(new RegExp(`${path}$`));
    await page.getByLabel('New password').fill(password);
    await page.getByRole('button').click();
    await expect(page.getByRole('status')).toContainText('Your password has been set');
    // THEN resources and API requests suppress referrers and history contains the scrubbed entry
    for (const request of requests.filter((request) => !request.isNavigationRequest())) {
      expect(request.headers()['referer']).toBeUndefined();
    }
    await page.goto('/recover');
    await page.goBack();
    await expect(page).toHaveURL(new RegExp(`${path}$`));
    await page.reload();
    await expect(page).toHaveURL(new RegExp(`${path}$`));
    await expect(page.getByLabel('New password')).toHaveCount(0);
  });
}

test('durable authentication survives navigation and supports revocation and sign-out', async ({ page, browser }) => {
  // GIVEN an authenticated session alongside any sessions retained by earlier scenarios.
  await signIn(page);
  const liveContext = await browser.newContext({ baseURL: browserBaseUrl });
  try {
    const live = await liveContext.newPage();
    await useAuthenticatedSession(live);
    const assertLiveSessionSurvives = await verifyOwnerDataIsolation(live, page);
    await page.reload();
    await expect(page.getByRole('heading', { name: 'Collection', exact: true })).toBeVisible();

    await openUserMenu(page);
    await page.getByRole('link', { name: 'Account', exact: true }).click();
    // WHEN revoking this session, other browser scenarios may have retained independent sessions.
    await page.getByRole('listitem').filter({ hasText: 'This session' })
      .getByRole('button', { name: 'Revoke', exact: true }).click();
    // THEN revocation signs this browser out without depending on the total session count.
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
    await assertLiveSessionSurvives();

    await signIn(page);
    const logoutResponse = page.waitForResponse((response) => response.url().endsWith('/api/auth/logout'));
    await openUserMenu(page);
    await page.getByRole('button', { name: 'Sign out', exact: true }).click();
    expect((await logoutResponse).status()).toBe(204);
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  } finally { await liveContext.close(); }
});

test('recovery shows generic feedback for an eligible account', async ({ page }) => {
  await page.goto('/recover');
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Recover account' }).click();
  await expect(page.getByRole('status')).toContainText('If the account is eligible');

  // Cross-tenant identifier substitution is covered through HTTP by TenantUserAdministrationTests.
});

for (const flow of [
  { path: '/recover', title: 'Reset password', endpoint: '/api/auth/recovery/consume' },
  { path: '/invite', title: 'Accept invitation', endpoint: '/api/auth/invitations/consume' },
]) {
  test(`${flow.title} reads a fragment token without sending it in request URLs`, async ({ page }) => {
    // GIVEN a syntactically valid but unissued token in an email-style fragment link.
    const token = 'a'.repeat(43);
    const urls: string[] = [];
    page.on('request', (request) => urls.push(request.url()));
    await page.goto(`${flow.path}#token=${token}`);
    await expect(page).toHaveURL(new RegExp(`${flow.path}$`));
    await expect(page.getByRole('heading', { name: flow.title })).toBeVisible();
    // WHEN the user submits the password form to the real API.
    await page.getByLabel('New password').fill('Browser Replacement Horse 9!');
    const response = page.waitForResponse((item) => item.url().endsWith(flow.endpoint));
    await page.getByRole('button', { name: flow.title }).click();
    // THEN the API rejects the unissued token and the browser shows the failure.
    expect((await response).status()).toBe(400);
    await expect(page.getByRole('alert')).toContainText('invalid or expired');
    expect(urls.every((url) => !url.includes(token))).toBe(true);
  });
}
