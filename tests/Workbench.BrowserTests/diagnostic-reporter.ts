import type { FullResult, Reporter, TestCase, TestResult } from '@playwright/test/reporter';
import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { basename, dirname, resolve } from 'node:path';

// Run before the line reporter: raw attachments remain local, but must not be
// advertised as CI evidence. The fixture prints the retained safe paths itself.
export default class DiagnosticReporter implements Reporter {
  private readonly results: Array<{
    id: string; file: string; line: number; column: number;
    retry: number; status: TestResult['status']; expectedStatus: TestCase['expectedStatus']; duration: number;
  }> = [];

  constructor(private readonly options: { outputFile?: string } = {}) {}

  onTestEnd(test: TestCase, result: TestResult) {
    result.attachments = [];
    // Allowlist timing/identity fields instead of serializing Playwright objects:
    // errors, stdout, annotations and titles can contain generated credentials.
    this.results.push({
      id: createHash('sha256').update(test.id).digest('hex').slice(0, 16),
      file: basename(test.location.file), line: test.location.line, column: test.location.column,
      retry: result.retry, status: result.status, expectedStatus: test.expectedStatus, duration: result.duration,
    });
  }

  async onEnd(result: FullResult) {
    if (!this.options.outputFile) return;
    const path = resolve(this.options.outputFile);
    await mkdir(dirname(path), { recursive: true });
    await writeFile(path, JSON.stringify({ status: result.status, duration: result.duration, tests: this.results }, null, 2));
  }
}
