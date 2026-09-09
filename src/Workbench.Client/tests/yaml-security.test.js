// @vitest-environment node
import { createRequire } from 'node:module';
import { describe, expect, it } from 'vitest';

const require = createRequire(import.meta.url);
const { parseYaml } = require('@redocly/openapi-core');

describe('OpenAPI tooling YAML merge budget (GHSA-2883-xcg3-v3hh)', () => {
  it.each(['[{}, {}, {}]', '[&empty {}, *empty, *empty]'])(
    'rejects repeated empty merge sources: %s',
    (sources) => {
      // GIVEN empty mappings whose repeated merges exceed the configured budget
      const source = `sources: &sources ${sources}\ntargets:\n${'  - <<: *sources\n'.repeat(4)}`;

      // WHEN the OpenAPI toolchain parses the YAML
      // THEN empty source mappings consume the merge budget
      expect(() => parseYaml(source, { maxTotalMergeKeys: 10 })).toThrow(/maxTotalMergeKeys/);
    },
  );

  it('preserves ordinary merges and explicit property precedence', () => {
    // GIVEN a normal YAML document with defaults and an explicit override
    const source = 'defaults: &defaults {type: string, description: original}\nproperty:\n  <<: *defaults\n  description: override\n';

    // WHEN the OpenAPI toolchain parses the YAML
    const document = parseYaml(source);

    // THEN defaults are merged and explicit values retain precedence
    expect(document.property).toEqual({ type: 'string', description: 'override' });
  });
});
