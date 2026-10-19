// @vitest-environment node
import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, it } from 'vitest';

const clientRoot = fileURLToPath(new URL('../', import.meta.url));

function lint(source: string) {
  const directory = mkdtempSync(join(tmpdir(), 'workbench-lint-'));
  try {
    const file = join(directory, 'Component.tsx');
    writeFileSync(file, source);
    const result = spawnSync(process.execPath, [
      join(clientRoot, 'node_modules/oxlint/bin/oxlint'),
      '--config', join(clientRoot, '.oxlintrc.json'), '--format', 'json', file,
    ], { cwd: clientRoot, encoding: 'utf8', timeout: 10_000 });
    if (result.error) throw result.error;
    return { status: result.status, ...JSON.parse(result.stdout) };
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

it('rejects unsafe TypeScript, conditional Hooks, and mixed Fast Refresh exports', () => {
  // GIVEN violations of the TypeScript, Hooks, and Fast Refresh contracts.
  // WHEN the installed CLI lints them using the real client configuration.
  const result = lint(`
    import { useState } from 'react';
    export const value: any = 1;
    export function Component({ enabled }: { enabled: boolean }) {
      if (enabled) useState(0);
      return <div />;
    }
    export function helper() { return 1; }
  `);
  // THEN lint fails with the independently relevant rule diagnostics.
  expect(result.status).toBe(1);
  expect(result.diagnostics.map((diagnostic: { code: string }) => diagnostic.code))
    .toEqual(expect.arrayContaining([
      'typescript(no-explicit-any)', 'react-hooks(rules-of-hooks)', 'react(only-export-components)',
    ]));
});

it('preserves Vite constant and compound exports and intentional dependency suppression', () => {
  // GIVEN exports allowed by Vite and the migrated, intentional effect suppression.
  // WHEN the real configuration lints a valid component module.
  const result = lint(`
    import { useEffect } from 'react';
    export const value = 1;
    export function Component({ name }: { name: string }) {
      useEffect(() => { console.log(name); }, []); // oxlint-disable-line react/exhaustive-deps
      return <div>{name}</div>;
    }
    export const Group = { Component };
  `);
  // THEN the compatibility options and suppression avoid false positives.
  expect(result.status).toBe(0);
  expect(result.diagnostics).toEqual([]);
});

it('rejects unused underscore names and values used only through typeof', () => {
  // GIVEN unused declarations that the previous TypeScript ESLint policy rejects.
  // WHEN the real linter checks underscore names and a value used only as a type.
  const result = lint(`
    const _unused = 1;
    const typeOnly = 1;
    export type Value = typeof typeOnly;
    export function helper(_argument: number) { return 1; }
  `);
  // THEN all three declarations remain errors rather than implicit exceptions.
  expect(result.status).toBe(1);
  expect(result.diagnostics).toHaveLength(3);
  for (const diagnostic of result.diagnostics) {
    expect(diagnostic.code).toBe('eslint(no-unused-vars)');
    expect(diagnostic.severity).toBe('error');
  }
});
