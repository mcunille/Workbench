import { randomUUID } from 'node:crypto';
import { expect, test } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';
import { adminApi, createThroughUi, enterAdminCredentials, library, publishThroughUi, saveApi, saveThroughUi, signInAdmin, syntheticContent } from './service-admin-fixture';

test('recovers revoked-session browsing while retaining a draft selection and search', async ({ page }) => {
  // GIVEN a fresh isolated session with a selected saved draft and an active catalog search.
  await signInAdmin(page, 1, true);
  const name = `Browser browse recovery ${randomUUID().slice(0, 8)}`;
  const draft = await saveApi(page.request, syntheticContent(name));
  await page.getByRole('button', { name: 'Drafts', exact: true }).click();
  await page.getByRole('checkbox', { name: `Select ${name}`, exact: true }).check();
  await page.getByRole('button', { name: 'Published catalog', exact: true }).click();
  await page.getByLabel('Search shared gems').fill(name);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(page.getByText('No shared entries match this search.')).toBeVisible();
  // WHEN the server session is revoked and a read discovers the real authorization failure.
  const token = (await (await page.request.get('/api/beta/service-admin/auth/antiforgery')).json()).requestToken;
  expect((await page.request.post('/api/beta/service-admin/auth/logout', { headers: { 'X-CSRF-TOKEN': token } })).status()).toBe(204);
  await page.getByRole('button', { name: 'Drafts', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Service-admin session recovery' })).toBeVisible();
  await expect(page.getByText('1 of 50 drafts selected')).toBeVisible();
  // WHEN the original account authenticates through the browser protocol THEN the draft list refreshes in place.
  const login = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/api/beta/service-admin/auth/login'));
  await enterAdminCredentials(page);
  expect((await login).status()).toBe(204);
  await expect(page.getByRole('region', { name: 'Service-admin session recovery' })).toHaveCount(0);
  await expect(page.getByRole('checkbox', { name: `Select ${name}`, exact: true })).toBeChecked();
  await page.getByRole('button', { name: 'Published catalog', exact: true }).click();
  await expect(page.getByLabel('Search shared gems')).toHaveValue(name);
  await page.getByRole('button', { name: 'Drafts', exact: true }).click();
  // AND review still submits the exact saved draft version retained before revocation.
  const review = page.waitForRequest(request => request.method() === 'POST' && request.url().endsWith(`${adminApi}/review`));
  await page.getByRole('link', { name: 'Review 1 draft', exact: true }).click();
  expect((await review).postDataJSON()).toEqual({ drafts: [{ draftId: draft.id, expectedDraftRowVersion: draft.rowVersion }] });
  await expect(page.getByRole('region', { name: 'Draft 1 changes', exact: true })).toBeVisible();
});

test('reauthenticates an ended session without reloading or losing editor changes', async ({ page, browser }) => {
  // GIVEN a successful UI save has cached an authenticated antiforgery token.
  await signInAdmin(page, 1, true);
  const draft = await createThroughUi(page, `Browser reauth ${randomUUID().slice(0, 8)}`);
  await page.getByLabel('Common name', { exact: true }).fill('Retained reauthentication edit');
  const otherContext = await browser.newContext();
  try {
    const other = await otherContext.newPage();
    await signInAdmin(other, 2);
    const latest = await saveApi(other.request, { ...draft.content, commonName: 'Other admin version' }, draft);
    // AND the current server session is revoked through its real logout endpoint without unmounting the editor.
    const token = (await (await page.request.get('/api/beta/service-admin/auth/antiforgery')).json()).requestToken;
    expect((await page.request.post('/api/beta/service-admin/auth/logout', { headers: { 'X-CSRF-TOKEN': token } })).status()).toBe(204);
    await page.getByRole('button', { name: 'Save draft', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Service-admin session recovery' })).toBeVisible();
    // WHEN signing in through the retained form THEN the real login protocol accepts the fresh anonymous token.
    const login = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/api/beta/service-admin/auth/login'));
    await enterAdminCredentials(page);
    expect((await login).status()).toBe(204);
    await expect(page.getByRole('region', { name: 'Service-admin session recovery' })).toHaveCount(0);
    // AND current concurrency versions are reconciled explicitly while local content survives.
    await expect(page.getByRole('heading', { name: 'Resolve changed versions' })).toBeVisible();
    await expect(page.getByLabel('Common name', { exact: true })).toHaveValue('Retained reauthentication edit');
    await expect(page.getByText('Other admin version', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Keep my edits with current versions' }).click();
    const saved = await saveThroughUi(page);
    expect(saved.rowVersion).not.toBe(latest.rowVersion);
    expect(saved.content.commonName).toBe('Retained reauthentication edit');
  } finally { await otherContext.close(); }
});

test('admin saves, reloads, edits and atomically publishes two entries, then retires a stable detail', async ({ page }) => {
  // GIVEN an independently signed-in service admin and small synthetic sourced claims.
  await signInAdmin(page);
  const suffix = randomUUID().slice(0, 8);
  const first = `Browser first ${suffix}`, second = `Browser second ${suffix}`;
  const firstDraft = await createThroughUi(page, first);
  // WHEN reloading a saved draft THEN its claims and citations remain persisted.
  await page.reload();
  await expect(page.getByLabel('Common name', { exact: true })).toHaveValue(first);
  await expect(page.getByRole('textbox', { name: 'Source 1 publication citation', exact: true })).toHaveValue('Synthetic acceptance claim; no mineralogical authority asserted.');
  await publishThroughUi(page, [first]);
  const secondDraft = await createThroughUi(page, second);
  await page.goto(`${library}/entries/${firstDraft.entryId}`);
  await page.getByRole('link', { name: 'Edit entry', exact: true }).click();
  const renamed = `${first} revised`;
  await page.getByLabel('Common name', { exact: true }).fill(renamed);
  await saveThroughUi(page);
  // WHEN reviewing both persisted changes THEN publication returns fresh details for both entries.
  await publishThroughUi(page, [renamed, second]);
  for (const [id, name] of [[firstDraft.entryId, renamed], [secondDraft.entryId, second]]) {
    await page.goto(`${library}/entries/${id}`);
    await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Synthetic browser citation' })).toHaveCount(2);
  }
  // WHEN a sourced entry is retired with an explanation THEN its stable URL still resolves.
  await page.getByRole('link', { name: 'Edit entry', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Retire entry', exact: true }).check();
  await page.getByLabel('Retirement explanation', { exact: true }).fill('Synthetic acceptance retirement.');
  await saveThroughUi(page);
  await publishThroughUi(page, [second]);
  await page.goto(`${library}/entries/${secondDraft.entryId}`);
  await expect(page.getByRole('region', { name: 'Retirement', exact: true })).toContainText('Synthetic acceptance retirement.');
  await expect(page.getByText('Retired', { exact: true })).toBeVisible();
});

test('two admins reconcile stale saved drafts and a changed published base without losing local edits', async ({ page, browser }) => {
  // GIVEN two independent admin sessions editing the same synthetic shared draft.
  await signInAdmin(page);
  const otherContext = await browser.newContext();
  try {
    const other = await otherContext.newPage();
    await signInAdmin(other, 2);
    const draft = await saveApi(page.request, syntheticContent(`Browser conflict ${randomUUID().slice(0, 8)}`));
    await page.goto(`${library}/drafts/${draft.id}`);
    await page.getByLabel('Common name', { exact: true }).fill('My retained local name');
    await saveApi(other.request, { ...draft.content, commonName: 'Other saved name' }, draft);
    // WHEN saving an outdated draft THEN current content is compared beside retained local edits.
    await page.getByRole('button', { name: 'Save draft', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Resolve changed versions' })).toBeVisible();
    await expect(page.getByLabel('Common name', { exact: true })).toHaveValue('My retained local name');
    await expect(page.getByText('Other saved name', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Save draft', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Keep my edits with current versions', exact: true }).click();
    const reconciled = await saveThroughUi(page);
    await publishThroughUi(page, [reconciled.content.commonName]);
    // GIVEN a separate draft based on that publication and another admin publishing a newer version.
    const detail = await (await page.request.get(`${adminApi}/${draft.entryId}`)).json();
    const stale = await saveApi(page.request, { ...draft.content, commonName: 'My pending base change' }, undefined, detail.rowVersion);
    await page.goto(`${library}/drafts/${stale.id}`);
    await page.getByLabel('Common name', { exact: true }).fill('My retained rebase');
    const newer = await saveApi(other.request, { ...draft.content, commonName: 'New published name' }, undefined, detail.rowVersion);
    await other.goto(library);
    await publishThroughUi(other, [newer.content.commonName]);
    // WHEN saving the stale base THEN an explicit rebase keeps local content and permits saving.
    await page.getByRole('button', { name: 'Save draft', exact: true }).click();
    await expect(page.getByText('New published name', { exact: true })).toBeVisible();
    await expect(page.getByLabel('Common name', { exact: true })).toHaveValue('My retained rebase');
    await expect(page.getByRole('button', { name: 'Save draft', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Keep my edits with current versions', exact: true }).click();
    const rebased = await saveThroughUi(page);
    expect(rebased.expectedPublishedRowVersion).not.toBe(detail.rowVersion);
    await page.reload();
    await expect(page.getByLabel('Common name', { exact: true })).toHaveValue('My retained rebase');
  } finally { await otherContext.close(); }
});

test('tenant and service-admin browser sessions cannot substitute for each other on direct APIs', async ({ page, browser }) => {
  // GIVEN a tenant browser session WHEN directly calling admin reads and mutations THEN authority is denied.
  await useAuthenticatedSession(page);
  expect((await page.request.get(adminApi)).status()).toBe(401);
  const tenantToken = (await (await page.request.get('/api/beta/auth/antiforgery')).json()).requestToken;
  expect((await page.request.post(`${adminApi}/review`, { headers: { 'X-CSRF-TOKEN': tenantToken }, data: { drafts: [] } })).status()).toBe(401);
  await page.goto(library);
  await expect(page.getByRole('heading', { name: 'Service-admin sign in' })).toBeVisible();
  // GIVEN a distinct service-admin-only session WHEN calling tenant routes THEN tenant authority is denied.
  const context = await browser.newContext();
  try {
    const admin = await context.newPage();
    await signInAdmin(admin, 2);
    expect((await admin.request.get('/api/beta/items')).status()).toBe(401);
    const token = (await (await admin.request.get('/api/beta/service-admin/auth/antiforgery')).json()).requestToken;
    expect((await admin.request.post('/api/beta/items', { headers: { 'X-CSRF-TOKEN': token }, data: {} })).status()).toBe(401);
  } finally { await context.close(); }
});

test('concurrent detail and draft reads remain available in one admin session', async ({ page }) => {
  // GIVEN one authenticated admin and a saved synthetic draft, as loaded together by detail/recovery views.
  await signInAdmin(page);
  const draft = await saveApi(page.request, syntheticContent(`Browser concurrent ${randomUUID().slice(0, 8)}`));
  await page.goto(library);
  await publishThroughUi(page, [draft.content.commonName]);
  // WHEN the browser sends overlapping shared-entry and saved-draft reads THEN every read succeeds.
  const responses = await Promise.all(Array.from({ length: 16 }, () => [
    page.request.get(`${adminApi}/${draft.entryId}`), page.request.get(`${adminApi}/drafts`),
  ]).flat());
  expect(responses.map(response => response.status())).toEqual(Array(32).fill(200));
});
