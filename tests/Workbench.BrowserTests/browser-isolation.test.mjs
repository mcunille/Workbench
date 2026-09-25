import test from 'node:test';
import assert from 'node:assert/strict';
import { browserOwner, sessionPath, uiWorkers } from './browser-isolation.mjs';

test('live and destructive auth sessions have distinct owners and private cache names', () => {
  // GIVEN a valid current browser run and its ordinary and destructive owners.
  const run = 'browser-0123456789ab';
  const paths = [];
  // WHEN obtaining both independent ordinary sessions and the auth session.
  for (const [owner, session] of [['live-0', 'primary'], ['live-0', 'secondary'], ['auth', 'primary']]) {
    paths.push(sessionPath('/tmp', run, owner, session));
  }
  // THEN neither the account nor a cookie file can cross the owner boundary.
  assert.equal(new Set(paths).size, 3);
  assert.notEqual(browserOwner('live-0').email, browserOwner('auth').email);
  assert.throws(() => sessionPath('/tmp', '../outside', 'live-0', 'primary'));
  assert.throws(() => sessionPath('/tmp', run, '../auth', 'primary'));
  assert.throws(() => sessionPath('/tmp', run, 'live-0', '../primary'));
});

test('UI concurrency is explicit and bounded independently of live mutation execution', () => {
  // GIVEN the default and supported measured comparison.
  // WHEN resolving concurrency THEN only one or two UI workers are accepted.
  assert.equal(uiWorkers(), 1);
  assert.equal(uiWorkers('2'), 2);
  for (const invalid of ['0', '3', '50%', '-1', 'garbage']) assert.throws(() => uiWorkers(invalid));
});
