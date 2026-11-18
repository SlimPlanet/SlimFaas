// The native browser suite can export coverage against the same dashboard bundle it exercises.
import { test as base } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

export const coverageDirectory = process.env.DASHBOARD_COVERAGE_DIR;
export async function startCoverage(page) {
  if (coverageDirectory) await page.coverage.startJSCoverage({ resetOnNavigation: false });
}
export async function saveCoverage(page) {
  if (!coverageDirectory) return;
  const entries = (await page.coverage.stopJSCoverage()).filter(entry => /\/assets\/[^/]+\.js$/.test(entry.url));
  await mkdir(coverageDirectory, { recursive: true });
  await writeFile(join(coverageDirectory, randomUUID() + '.json'),
    JSON.stringify(entries.map(({ url, functions }) => ({ url, functions }))));
}
export const test = base.extend({
  page: async ({ page }, use) => {
    await startCoverage(page);
    try { await use(page); } finally { await saveCoverage(page); }
  },
});
export { expect } from '@playwright/test';
