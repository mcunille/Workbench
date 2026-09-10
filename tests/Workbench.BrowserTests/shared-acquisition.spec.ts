import { expect, test } from '@playwright/test';
import { useAuthenticatedSession } from './auth-fixture';
import { acquisitionPanel, createOrigin, createPiece } from './shared-acquisition-fixture';
import { lifecycle } from './restoration-fixture';
import { setAppearance } from './user-menu-fixture';

test.setTimeout(120_000);
test.use({ actionTimeout: 20_000 });

async function selectAcquisition(page: import('@playwright/test').Page, source: string) {
  await page.getByRole('searchbox', { name: 'Search acquisitions', exact: true }).fill(source);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await page.getByRole('button', { name: new RegExp(`^Select ${source}`) }).click();
}

test('H10 a saved origin opens a shared acquisition without losing collection search', async ({ page }) => {
  // GIVEN a saved piece with acquisition context and a narrowed collection.
  await useAuthenticatedSession(page);
  const name = `H10 stone ${crypto.randomUUID()}`;
  const piece = await createPiece(page, name);
  const origin = await createOrigin(page, piece.id, 'H10 fair');
  const sibling = await createPiece(page, `Archived sibling ${crypto.randomUUID()}`);
  const csrf = await (await page.request.get('/api/auth/antiforgery')).json();
  const link = await page.request.put(`/api/items/${sibling.id}/acquisition-link`, {
    headers: { 'X-CSRF-TOKEN': csrf.requestToken },
    data: { expectedItemVersion: sibling.version, expectedAcquisitionId: null, expectedAcquisitionVersion: null,
      targetAcquisitionId: origin.acquisition.id, targetAcquisitionVersion: origin.acquisition.version },
  });
  expect(link.status()).toBe(200);
  await lifecycle(page, sibling.id, 'archive', (await link.json()).itemVersion);
  await page.goto('/inventory');
  await page.getByRole('searchbox', { name: 'Search collection', exact: true }).fill(name);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await page.locator(`a[href="/inventory/${piece.id}"]`).click();
  // WHEN opening the acquisition THEN its shared context and originating piece are available.
  await acquisitionPanel(page).getByRole('link', { name: 'View acquisition', exact: true }).click();
  await expect(page.getByText('H10 fair', { exact: true })).toBeVisible();
  await expect(page.locator(`a[href="/inventory/${piece.id}"]`).filter({ hasText: name })).toBeVisible();
  // WHEN traversing an archived sibling THEN read-only context retains the original active collection destination.
  await page.getByLabel('Show archived pieces', { exact: true }).check();
  await page.getByRole('link', { name: sibling.name, exact: true }).click();
  await acquisitionPanel(page).getByRole('link', { name: 'View acquisition', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Back to collection', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Connect existing piece', exact: true })).toHaveCount(0);
  // WHEN returning through the archived piece THEN the original collection search is retained.
  await page.getByRole('link', { name: 'Back to piece', exact: true }).click();
  await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
  await expect(page.getByRole('searchbox', { name: 'Search collection', exact: true })).toHaveValue(name);
  await expect(page.locator(`a[href="/inventory/${piece.id}"]`)).toBeVisible();
});

test('H10 three stones share corrections, archive relationships and deliberate link removal', async ({ page }) => {
  // GIVEN three independently identified pieces and one saved acquisition.
  await useAuthenticatedSession(page);
  const token = crypto.randomUUID();
  const source = `Fair ${token}`;
  const stones = await Promise.all(['Blue', 'Green', 'Violet'].map(color => createPiece(page, `${color} ${token}`)));
  const origin = await createOrigin(page, stones[0].id, source);
  // WHEN each additional piece is connected THEN all use the original acquisition identity.
  for (const stone of stones.slice(1)) {
    await page.goto(`/inventory/${stone.id}`);
    await acquisitionPanel(page).getByRole('button', { name: 'Connect to an acquisition', exact: true }).click();
    await selectAcquisition(page, source);
    await page.getByRole('button', { name: 'Save connection', exact: true }).click();
    await expect(acquisitionPanel(page).getByRole('button', { name: 'Edit acquisition', exact: true })).toBeVisible();
    await expect(acquisitionPanel(page).getByText(source, { exact: true })).toBeVisible();
    const current = await (await page.request.get(`/api/items/${stone.id}/acquisition`)).json();
    expect(current.acquisition.id).toBe(origin.acquisition.id);
  }
  // WHEN correcting shared notes THEN each piece reads the correction, not a divergent copy.
  await page.getByRole('button', { name: 'Edit acquisition', exact: true }).click();
  await expect(page.getByText(/every associated piece, including archived pieces/i)).toBeVisible();
  await page.getByLabel('Provenance notes (optional)', { exact: true }).fill('Corrected fair recollection');
  await page.getByRole('button', { name: 'Save acquisition', exact: true }).click();
  await expect(acquisitionPanel(page).getByText('Corrected fair recollection', { exact: true })).toBeVisible();
  for (const stone of stones) {
    const current = await (await page.request.get(`/api/items/${stone.id}/acquisition`)).json();
    expect(current.acquisition.notes).toBe('Corrected fair recollection');
  }
  // GIVEN an archived associated stone WHEN browsing normally THEN it is hidden but the link survives.
  const current = await (await page.request.get(`/api/items/${stones[1].id}`)).json();
  const archived = await lifecycle(page, stones[1].id, 'archive', current.version);
  await acquisitionPanel(page).getByRole('link', { name: 'View acquisition', exact: true }).click();
  await expect(page.getByRole('link', { name: stones[0].name, exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: stones[1].name, exact: true })).toHaveCount(0);
  await page.getByLabel('Show archived pieces', { exact: true }).check();
  await page.getByRole('link', { name: stones[1].name, exact: true }).click();
  await expect(acquisitionPanel(page).getByText('Corrected fair recollection', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Edit acquisition', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Change acquisition', exact: true })).toHaveCount(0);
  await acquisitionPanel(page).getByRole('link', { name: 'View acquisition', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Connect existing piece', exact: true })).toHaveCount(0);
  await lifecycle(page, stones[1].id, 'restore', archived.version);
  // WHEN explicitly removing one mistaken connection THEN both permanent identities remain.
  await page.goto(`/inventory/${stones[2].id}`);
  await acquisitionPanel(page).getByRole('button', { name: 'Remove connection', exact: true }).click();
  await page.getByRole('button', { name: 'Remove connection', exact: true }).click();
  await expect(acquisitionPanel(page).getByText('No acquisition recorded.', { exact: true })).toBeVisible();
  expect((await page.request.get(`/api/items/${stones[2].id}`)).status()).toBe(200);
  expect((await page.request.get(`/api/acquisitions/${origin.acquisition.id}`)).status()).toBe(200);
});

test('H10 a lost connection response retains original tokens and requires review on retry', async ({ page }) => {
  // GIVEN a saved acquisition and a new item whose connection response will be lost.
  await useAuthenticatedSession(page);
  const source = `Retry fair ${crypto.randomUUID()}`;
  const first = await createPiece(page, `Origin ${crypto.randomUUID()}`);
  const second = await createPiece(page, `Second ${crypto.randomUUID()}`);
  await createOrigin(page, first.id, source);
  await page.goto(`/inventory/${second.id}`);
  await acquisitionPanel(page).getByRole('button', { name: 'Connect to an acquisition', exact: true }).click();
  await selectAcquisition(page, source);
  const submissions: unknown[] = [];
  await page.route(`**/api/items/${second.id}/acquisition-link`, async route => {
    submissions.push(route.request().postDataJSON());
    if (submissions.length > 1) return route.continue();
    expect((await route.fetch()).status()).toBe(200);
    await route.abort('failed');
  });
  // WHEN transport loses a committed save THEN the exact request remains retryable.
  await page.getByRole('button', { name: 'Save connection', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText(/confirm/i);
  await page.getByRole('button', { name: 'Retry connection save', exact: true }).click();
  expect(submissions).toHaveLength(2);
  expect(submissions[1]).toEqual(submissions[0]);
  // THEN conflict review is readable at 320px and preserves its state across appearances.
  await expect(page.getByRole('heading', { name: 'Review current connection', exact: true })).toBeVisible();
  await page.setViewportSize({ width: 320, height: 900 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' }, { name: 'prefers-reduced-transparency', value: 'reduce' }] });
  for (const dark of [false, true]) {
    await setAppearance(page, dark);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expect(page.getByRole('heading', { name: 'Review current connection', exact: true })).toBeVisible();
  }
  await cdp.detach();
  const saved = page.getByRole('button', { name: 'Use saved connection', exact: true });
  await saved.focus();
  await page.keyboard.press('Enter');
  await expect(acquisitionPanel(page).getByText(source, { exact: true })).toBeVisible();
});

test('H10 acquisition entry connects existing and newly saved pieces without duplicate item retries', async ({ page }) => {
  // GIVEN an acquisition and an existing item with a different, mistaken origin.
  await useAuthenticatedSession(page);
  const token = crypto.randomUUID();
  const first = await createPiece(page, `Original ${token}`);
  const existing = await createPiece(page, `Existing ${token}`);
  const target = await createOrigin(page, first.id, `Chosen fair ${token}`);
  const previous = await createOrigin(page, existing.id, `Mistaken fair ${token}`);
  await page.goto(`/inventory/${first.id}`);
  await acquisitionPanel(page).getByRole('link', { name: 'View acquisition', exact: true }).click();
  // WHEN choosing an existing piece THEN both old and intended contexts are reviewed before replacement.
  await page.getByRole('button', { name: 'Connect existing piece', exact: true }).click();
  await page.getByRole('searchbox', { name: 'Search active pieces', exact: true }).fill(existing.name);
  await page.getByRole('button', { name: 'Search pieces', exact: true }).click();
  await page.getByRole('button', { name: `Select ${existing.name}`, exact: true }).click();
  await expect(page.getByText(`Mistaken fair ${token}`, { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Save connection', exact: true }).click();
  await expect(page.getByRole('link', { name: existing.name, exact: true })).toBeVisible();
  expect((await page.request.get(`/api/acquisitions/${previous.acquisition.id}`)).status()).toBe(200);
  // WHEN opening a sibling and returning THEN the shared acquisition origin is retained.
  await page.getByRole('link', { name: existing.name, exact: true }).click();
  await page.getByRole('link', { name: 'Back to acquisition', exact: true }).click();
  await expect(page.getByRole('link', { name: first.name, exact: true })).toBeVisible();
  // GIVEN a new piece saved from this acquisition WHEN its connection fails THEN only that connection retries.
  await page.getByRole('button', { name: 'Record a new piece', exact: true }).click();
  const newName = `Newly recorded ${token}`;
  await page.getByLabel('Name', { exact: true }).fill(newName);
  let creations = 0;
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/items' && request.method() === 'POST') creations++; });
  await page.getByRole('button', { name: 'Save item', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Review connection', exact: true })).toBeVisible();
  await page.route('**/api/items/*/acquisition-link', route => route.fulfill({ status: 503 }), { times: 1 });
  await page.getByRole('button', { name: 'Save connection', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Piece saved; acquisition connection not confirmed');
  await page.getByRole('button', { name: 'Retry connection save', exact: true }).click();
  await expect(page.getByRole('link', { name: newName, exact: true })).toBeVisible();
  expect(creations).toBe(1);
  const savedPieces = await (await page.request.get(`/api/acquisitions/${target.acquisition.id}/items`)).json();
  expect(savedPieces.items.filter((piece: { name: string }) => piece.name === newName)).toHaveLength(1);
});
