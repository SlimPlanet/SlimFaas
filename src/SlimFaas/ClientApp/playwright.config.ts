import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  workers: 1,
  fullyParallel: false,
  timeout: 120_000,
  expect: { timeout: 30_000 },
  retries: 0,
  outputDir: '../../../artifacts/traffic-e2e/results',
  reporter: [['list'], ['html', { outputFolder: '../../../artifacts/traffic-e2e/report', open: 'never' }]],
  use: { browserName: 'chromium', viewport: { width: 1920, height: 1080 }, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
});
