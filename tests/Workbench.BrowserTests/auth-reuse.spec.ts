import { browserBaseUrl } from './browser-environment';
import { expect, test } from '@playwright/test';
import { photoSignIn } from './photo-fixture';

async function sessionId(page: import('@playwright/test').Page) {
  const response = await page.request.get('/api/auth/sessions');
  expect(response.ok()).toBe(true);
  const sessions = await response.json() as { id: string; isCurrent: boolean }[];
  const current = sessions.find(session => session.isCurrent);
  expect(current).toBeDefined();
  return current!.id;
}

test('ordinary contexts reuse authentication while competing contexts have distinct sessions', async ({ browser }) => {
  // GIVEN two independently authenticated fixture sessions.
  const contexts = await Promise.all(Array.from({ length: 3 }, () =>
    browser.newContext({ baseURL: browserBaseUrl })));
  try {
    const pages = await Promise.all(contexts.map(context => context.newPage()));
    await photoSignIn(pages[0]);
    await photoSignIn(pages[1], 'secondary');
    const primary = await sessionId(pages[0]);
    const secondary = await sessionId(pages[1]);
    expect(secondary).not.toBe(primary);

    // WHEN another fresh cookie jar opens an ordinary scenario.
    let logins = 0;
    pages[2].on('request', request => {
      if (new URL(request.url()).pathname === '/api/auth/login') logins++;
    });
    await photoSignIn(pages[2]);
    const reused = await sessionId(pages[2]);

    // THEN it uses the primary session without spending another login permit.
    expect(logins).toBe(0);
    expect(reused).toBe(primary);
  } finally {
    await Promise.all(contexts.map(context => context.close()));
  }
});
