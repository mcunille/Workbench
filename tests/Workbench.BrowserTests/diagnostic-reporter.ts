import type { Reporter, TestCase, TestResult } from '@playwright/test/reporter';

// Run before the line reporter: raw attachments remain local, but must not be
// advertised as CI evidence. The fixture prints the retained safe paths itself.
export default class DiagnosticReporter implements Reporter {
  onTestEnd(_test: TestCase, result: TestResult) {
    result.attachments = [];
  }
}
