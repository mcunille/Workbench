import { join } from 'node:path';

export function uiWorkers(value = '1') {
  if (!['1', '2'].includes(value)) throw new Error('Browser UI workers must be 1 or 2.');
  return Number(value);
}

export function browserOwner(owner = 'live-0') {
  if (!['live-0', 'auth'].includes(owner)) throw new Error('Unknown browser test owner.');
  return { namespace: owner, email: `browser-${owner}@example.test` };
}

export function sessionPath(root, run, owner, session) {
  if (!/^browser-[a-f0-9]{12}$/.test(run ?? '')) throw new Error('Browser run identity is required.');
  if (!['primary', 'secondary'].includes(session)) throw new Error('Unknown browser session.');
  return join(root, run, `${browserOwner(owner).namespace}-${session}-cookies.json`);
}

export function createApiGuard() {
  let unexpected = 0;
  return {
    // Do not retain URLs, bodies, cookies or headers in diagnostic state.
    async handle(route) {
      unexpected++;
      await route.abort('blockedbyclient');
    },
    assertClean() {
      if (unexpected) throw new Error(`Intercepted UI attempted ${unexpected} undeclared API requests.`);
    },
  };
}
