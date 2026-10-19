import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { load } = require('../../src/Workbench.Client/node_modules/js-yaml');
const workflow = load(readFileSync(process.argv[2], 'utf8'));
// GIVEN an executable candidate graph WHEN inspected as YAML THEN preparation fans out to three isolated consumers.
assert.deepEqual(workflow.permissions, { contents: 'read' });
for (const id of ['prepare', 'release', 'server-a', 'server-b', 'verify', 'local-setup-contracts', 'deployment-contracts', 'container-smoke', 'design-tooling-contracts', 'required-gate']) {
  assert.ok(workflow.jobs[id], `Missing required job ${id}`);
}
for (const id of ['release', 'server-a', 'server-b']) {
  const job = workflow.jobs[id];
  assert.equal(job['runs-on'], 'ubuntu-latest');
  assert.deepEqual(job.needs, ['prepare']);
  const download = job.steps.find(step => step.uses?.startsWith('actions/download-artifact@'));
  assert.equal(download?.with?.['artifact-ids'], '${{ needs.prepare.outputs.bundle-id }}');
}
// GIVEN server hosts WHEN their commands execute THEN no other verification workload or build shares their CPU.
for (const [id, host] of [['server-a', 'A'], ['server-b', 'B']]) {
  const job = workflow.jobs[id];
  const commands = job.steps.map(step => step.run ?? '').join('\n');
  assert.match(commands, new RegExp(`-Phase Server .*?-HostId ${host}(?:\\s|$)`));
  assert.doesNotMatch(commands, /dotnet\s+(?:build|publish|test)|npm\s|playwright|test-publish|smoke-container/);
  assert.ok(!job.strategy, 'Explicit server jobs must not fail-fast their siblings');
  assert.ok(job.steps.some(step => step.if === '${{ always() }}' && step.uses?.startsWith('actions/upload-artifact@')));
}
// GIVEN a failure or cancellation THEN the strict aggregator remains scheduled and every independent gate remains required.
assert.deepEqual(workflow.jobs.verify.needs, ['prepare', 'release', 'server-a', 'server-b']);
assert.equal(workflow.jobs.verify.if, '${{ always() }}');
assert.ok(workflow.jobs.verify.steps.some(step => /-Phase Aggregate/.test(step.run ?? '')));
assert.equal(workflow.jobs['required-gate'].name, 'Required verification gate');
assert.equal(workflow.jobs['required-gate'].if, '${{ always() }}');
assert.deepEqual(workflow.jobs['required-gate'].needs, ['local-setup-contracts', 'deployment-contracts', 'verify', 'container-smoke']);
for (const job of Object.values(workflow.jobs)) {
  for (const step of job.steps ?? []) {
    if (!step.uses) continue;
    assert.match(step.uses, /^[\w.-]+\/[\w./-]+@[a-f0-9]{40}$/);
    if (step.uses.startsWith('actions/checkout@')) assert.equal(step.with?.['persist-credentials'], false);
  }
}
console.log('Distributed workflow graph and isolation contracts passed.');
