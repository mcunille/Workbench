import { expect, test } from '@playwright/test';

const email = 'browser-admin@example.test';
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

async function signIn(page: import('@playwright/test').Page) {
  await page.goto('/');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(password);
  const loginResponse = page.waitForResponse((response) => response.url().endsWith('/api/auth/login'));
  await page.getByRole('button', { name: 'Sign in' }).click();
  expect((await loginResponse).status()).toBe(204);
  await expect(page.getByRole('heading', { name: 'Welcome to Browser Tenant' })).toBeVisible();
}

test('durable authentication survives navigation and supports revocation and sign-out', async ({ page }) => {
  await signIn(page);
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Welcome to Browser Tenant' })).toBeVisible();

  await page.getByRole('button', { name: 'Revoke', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();

  await signIn(page);
  const logoutResponse = page.waitForResponse((response) => response.url().endsWith('/api/auth/logout'));
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  expect((await logoutResponse).status()).toBe(204);
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});

test('recovery remains generic and tenant identifier substitution is not observable', async ({ page }) => {
  await page.goto('/recover');
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Recover account' }).click();
  await expect(page.getByRole('status')).toContainText('If the account is eligible');

  await signIn(page);
  const status = await page.evaluate(async () => {
    const tokenResponse = await fetch('/api/auth/antiforgery');
    const { requestToken } = (await tokenResponse.json()) as { requestToken: string };
    const response = await fetch('/api/tenant/users/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', {
      method: 'DELETE',
      headers: { 'X-CSRF-TOKEN': requestToken },
    });
    return response.status;
  });
  expect(status).toBe(404);
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
