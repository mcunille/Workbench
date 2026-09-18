import { expect, test, type Page } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';

test.setTimeout(120_000);

async function selectRecovery(page: Page, label: string, retained: string) {
  const text = page.getByRole('textbox', { name: `${label} recovery text`, exact: true });
  await expect(text).toContainText(retained);
  await expect(text).toHaveAttribute('readonly', '');
  await text.focus();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: `Select ${label.toLowerCase()} text`, exact: true })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(text).toBeFocused();
  expect(await text.evaluate((node: HTMLTextAreaElement) => node.selectionStart === 0 && node.selectionEnd === node.value.length)).toBe(true);
}

test('an interrupted item save keeps submitted details keyboard-selectable', async ({ page }) => {
  // GIVEN a collector entering a new item on a phone-sized screen.
  await useAuthenticatedSession(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('link', { name: 'Add item', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill('Retained sapphire');
  await page.getByLabel('Notes (optional)', { exact: true }).fill('Keep this research.');
  let writes = 0;
  await page.route('**/api/beta/items', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    writes++;
    await route.fulfill({ status: 503, json: { title: 'Temporarily unavailable' } });
  });
  // WHEN the save response is unavailable.
  await page.getByRole('button', { name: 'Save item', exact: true }).click();
  // THEN retained details can be selected without unlocking or resending the submission.
  await expect(page.getByLabel('Name', { exact: true })).toBeDisabled();
  await selectRecovery(page, 'Item', 'Keep this research.');
  await page.getByRole('button', { name: 'Retry save', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Retry save', exact: true })).toBeEnabled();
  expect(writes).toBe(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

for (const kind of ['draft', 'supplier'] as const) test(`a ${kind} conflict followed by an unavailable read keeps edits keyboard-selectable`, async ({ page }) => {
  // GIVEN a saved record with local edits awaiting reconciliation.
  await useAuthenticatedSession(page);
  const draft = kind === 'draft';
  const path = draft ? 'purchase-orders' : 'suppliers';
  const field = draft ? 'Title' : 'Supplier name';
  const save = draft ? 'Save draft' : 'Save supplier';
  await page.goto(`/${path}/new`);
  await page.getByLabel(field, { exact: true }).fill(`Recovery ${kind}`);
  await page.getByRole('button', { name: save, exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/${path}/[a-f0-9-]{36}$`));
  await expect(page.getByLabel(field, { exact: true })).toBeEnabled();
  await page.getByLabel(field, { exact: true }).fill(`Retained ${kind} edits`);
  const id = page.url().split('/').at(-1);
  let writes = 0;
  await page.route(`**/api/beta/${draft ? 'purchase-order-drafts' : 'suppliers'}/${id}`, async route => {
    const write = route.request().method() === 'PUT';
    if (write) writes++;
    await route.fulfill({ status: write ? 409 : 503, json: { code: write ? `${kind}_version_conflict` : 'temporarily_unavailable' } });
  });
  if (draft) await page.route(`**/api/beta/purchase-orders/${id}`, async route => {
    await route.fulfill({ status: 503, json: { title: 'Temporarily unavailable' } });
  });
  // WHEN a version conflict is followed by a failed recovery read.
  await page.getByRole('button', { name: save, exact: true }).click();
  // THEN frozen local edits remain selectable and further saves stay disabled.
  await expect(page.getByText('Workbench has been updated. Reload required.', { exact: true })).toHaveCount(0);
  await expect(page.getByLabel(field, { exact: true })).toBeDisabled();
  await selectRecovery(page, draft ? 'Purchase draft' : 'Supplier', `Retained ${kind} edits`);
  await expect(page.getByRole('button', { name: save, exact: true })).toBeDisabled();
  expect(writes).toBe(1);
});

test('an interrupted beta request preserves purchase edits and retries the exact submission', async ({ page }) => {
  // GIVEN an authenticated owner editing a purchase draft while the service is temporarily unavailable.
  await useAuthenticatedSession(page);
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill('Retain my purchase draft');
  await page.getByLabel('Notes', { exact: true }).fill('Ask the supplier about the blue stones.');
  let saves = 0;
  let original: string | null;
  await page.route('**/api/beta/purchase-order-drafts', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    saves++;
    if (saves === 1) original = route.request().postData();
    else expect(route.request().postData()).toBe(original);
    // The single beta API requires no client revision header.
    expect(route.request().headers()['x-workbench-api-revision']).toBeUndefined();
    await route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({
      title: 'Temporarily unavailable', code: 'temporarily_unavailable',
    }) });
  });

  // WHEN the owner's save receives the unavailable-service response.
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();

  // THEN the existing recovery explains the uncertain result and the same unsaved form remains on screen.
  await expect(page.getByText('Workbench has been updated. Reload required.', { exact: true })).toHaveCount(0);
  await expect(page.getByText('We couldn’t confirm your save.', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Title', { exact: true })).toHaveValue('Retain my purchase draft');
  await expect(page.getByLabel('Notes', { exact: true })).toHaveValue('Ask the supplier about the blue stones.');
  await expect(page).toHaveURL(/\/purchase-orders\/new$/);

  // AND keyboard users can select all retained edits without unlocking the original form.
  const recovery = page.getByRole('textbox', { name: 'Purchase draft recovery text' });
  const selectText = page.getByRole('button', { name: 'Select purchase draft text' });
  await recovery.focus();
  await page.keyboard.press('Tab');
  await expect(selectText).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(recovery).toBeFocused();
  await expect(recovery).toHaveAttribute('readonly', '');
  await expect(page.getByLabel('Notes', { exact: true })).toBeDisabled();
  const selection = await recovery.evaluate((node: HTMLTextAreaElement) => ({ value: node.value, start: node.selectionStart, end: node.selectionEnd }));
  expect(selection.value).toContain('Ask the supplier about the blue stones.');
  expect(selection.start).toBe(0);
  expect(selection.end).toBe(selection.value.length);
  // WHEN retry is requested THEN the original submission is sent again to check its result.
  const subsequentWrites: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/beta/') && !['GET', 'HEAD', 'OPTIONS'].includes(request.method())) {
      subsequentWrites.push(request.method());
    }
  });
  await page.getByRole('button', { name: 'Check and retry', exact: true }).click();
  await expect(page.getByText('We couldn’t confirm your save.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Check and retry', exact: true })).toBeEnabled();
  expect(subsequentWrites).toContain('POST');
  expect(saves).toBe(2);
  await expect(page.getByLabel('Notes', { exact: true })).toHaveValue('Ask the supplier about the blue stones.');
});
