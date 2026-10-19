import { spawnSync } from 'node:child_process';
import { isAbsolute } from 'node:path';

// The reviewed installer pins this argument in the external hook; never search cwd or PATH here.
const gitExecutable = process.argv[2];

const limit = 20_000; // Tokens = bytes / 4; exactly 5,000 tokens is allowed.
const generatedPaths = new Set([
  'src/Workbench.Client/src/api/generated.ts',
  'src/Workbench.Client/openapi/Workbench.Server_beta.json',
]);
const lockfile = /(^|\/)(package-lock\.json|npm-shrinkwrap\.json|packages\.lock\.json|yarn\.lock|pnpm-lock\.yaml|bun\.lockb?|Cargo\.lock|poetry\.lock|uv\.lock|Gemfile\.lock|composer\.lock)$/;
const efMetadata = /^src\/Workbench\.Server\/Persistence\/Migrations\/[^/]+(?:\.Designer|ModelSnapshot)\.cs$/;
const binaryAsset = /\.(?:png|jpe?g|gif|webp|avif|ico|woff2?|ttf|otf|pdf|mp[34]|wav|ogg|zip|gz|7z)$/i;

function git(args, input) {
  if (!gitExecutable || !isAbsolute(gitExecutable)) throw new Error('Pass the trusted absolute Git executable recorded during installation.');
  const result = spawnSync(gitExecutable, ['-c', 'core.fsmonitor=false', ...args], {
    input, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024,
  });
  if (result.error || result.status !== 0) throw new Error(result.error?.message || result.stderr.trim());
  return result.stdout;
}

try {
  // Raw, NUL-delimited index entries preserve whitespace and avoid reading working files.
  // Disabling rename detection makes each destination an ordinary added staged blob.
  const entries = git(['diff', '--cached', '--raw', '-z', '--no-abbrev', '--no-renames',
    '--no-ext-diff', '--no-textconv', '--diff-filter=ACMT']).split('\0');
  const files = [];
  for (let i = 0; i < entries.length - 1; i += 2) {
    const [, mode, , oid] = entries[i].split(' ');
    const path = entries[i + 1];
    if (!/^100(?:644|755)$/.test(mode)) continue; // Gitlinks and symlinks are not authored file bodies.
    if (generatedPaths.has(path) || lockfile.test(path) || efMetadata.test(path) || binaryAsset.test(path)) continue;
    files.push({ path, oid });
  }
  if (files.length) {
    const sizes = git(['cat-file', '--batch-check=%(objectsize)'], files.map(f => f.oid).join('\n') + '\n').trim().split('\n');
    const violations = files.flatMap((file, i) => {
      const bytes = Number(sizes[i]);
      if (!Number.isSafeInteger(bytes) || bytes < 0) throw new Error('Unable to read a staged blob size.');
      return bytes > limit ? [{ ...file, bytes }] : [];
    });
    if (violations.length) {
      console.error('Commit blocked: staged authored files exceed 5,000 tokens (20,000 bytes; tokens = bytes / 4).');
      for (const { path, bytes } of violations) {
        console.error(`  ${JSON.stringify(path)}: ${bytes} bytes = ${bytes / 4} tokens`);
      }
      console.error('\nSplit each file by responsibility until every resulting file is at most 20,000 bytes:');
      console.error('  1. Move cohesive helpers, components, or domain operations into focused sibling files.');
      console.error('  2. Keep a small entry point; update imports, exports, and callers to use those files.');
      console.error('  3. Split tests by the behavior or subject they cover; split documents into linked topic files.');
      console.error('  4. Run affected checks, then git add the changed and new files and retry the commit.');
      process.exitCode = 1;
    }
  }
} catch (error) {
  console.error(`Unable to check staged files: ${error.message}`);
  process.exitCode = 1;
}
