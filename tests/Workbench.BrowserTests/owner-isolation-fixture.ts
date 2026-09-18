import { expect, type Page } from './diagnostic-fixture';
import { lifecycle } from './restoration-fixture';

async function createOwnedItem(page: Page) {
  const csrf = await (await page.request.get('/api/beta/auth/antiforgery')).json();
  const response = await page.request.post('/api/beta/items', {
    headers: { 'X-Workbench-Api-Revision': 'beta-3', 'X-CSRF-TOKEN': csrf.requestToken },
    data: { creationRequestId: crypto.randomUUID(), name: `Owner isolation ${crypto.randomUUID()}`, notes: null, location: null },
  });
  expect(response.status()).toBe(201);
  return response.json();
}

// Called inside the existing real-auth/revocation journey. Both pages are already
// authenticated: ordinary live cookies are reused, and auth retains its real UI login.
export async function verifyOwnerDataIsolation(live: Page, auth: Page) {
  // GIVEN two genuine operator-provisioned tenants and independently authenticated cookie jars.
  const liveIdentity = await (await live.request.get('/api/beta/auth/me')).json();
  const authIdentity = await (await auth.request.get('/api/beta/auth/me')).json();
  expect(liveIdentity.userId !== authIdentity.userId).toBe(true);
  const liveItem = await createOwnedItem(live);
  const authItem = await createOwnedItem(auth);
  // WHEN each authenticated owner reads its own and the other owner's item.
  const statuses = await Promise.all([
    live.request.get(`/api/beta/items/${liveItem.id}`),
    live.request.get(`/api/beta/items/${authItem.id}`),
    auth.request.get(`/api/beta/items/${authItem.id}`),
    auth.request.get(`/api/beta/items/${liveItem.id}`),
  ]);
  // THEN successful owner reads and hidden foreign records establish actual tenant separation.
  expect(statuses.map(response => response.status())).toEqual([200, 404, 200, 404]);
  await lifecycle(live, liveItem.id, 'archive', liveItem.version);
  await lifecycle(auth, authItem.id, 'archive', authItem.version);
  return async () => {
    // THEN revoking the auth owner's session leaves the cached ordinary session usable.
    expect((await live.request.get('/api/beta/auth/me')).status()).toBe(200);
    expect((await live.request.get(`/api/beta/items/${liveItem.id}`)).status()).toBe(200);
  };
}
