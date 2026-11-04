import { test, expect } from '@playwright/test';
import { createHash } from 'node:crypto';
import { startNative } from './native.mjs';

test('durable failures are visible on peers; bounded inert preview and concurrent replay preserve offloaded bytes', async ({ browser, request }, info) => {
  const cluster = await startNative(info.outputPath('dlq-runtime.log'), 'dlq');
  const page = await browser.newPage({ viewport: { width: 900, height: 900 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await expect.poll(async () => {
      cluster.check();
      try { return (await request.get(`${cluster.url}/ready`)).status(); } catch { return 0; }
    }, { timeout: 90_000 }).toBe(200);
    const body = Buffer.from('<script>window.__dlqExecuted=true</script>\n' + 'x'.repeat(2 * 1024 * 1024));
    const accepted = await request.post(`${cluster.url}/async-function/fibonacci1/work?original=query`, {
      data: body, headers: { 'Content-Type': 'text/html' },
    });
    expect(accepted.status()).toBe(202);
    const original = accepted.headers()['slimfaas-element-id'];
    let messages;
    await expect.poll(async () => {
      const response = await request.get(`${cluster.peerUrl}/data/dead-letters/fibonacci1/messages`);
      messages = await response.json(); return messages.Items?.length;
    }).toBe(1);
    const message = messages.Items[0];
    expect(message.OriginalElementId).toBe(original);
    expect(message.FailureReason).toBe('NonRetryableResponse');
    expect(message.Attempts).toBe(1);
    expect(message.Request.BodyLength).toBe(body.length);
    await page.goto(`${cluster.peerUrl}/#/dead-letters/fibonacci1/messages/${message.Id}`);
    await expect(page.getByRole('heading', { name: `Message ${message.Id}`, exact: true })).toBeVisible();
    expect(await page.evaluate(() => window.__dlqExecuted)).toBeUndefined();
    await page.getByRole('button', { name: 'Preview body (64 KiB maximum)', exact: true }).click();
    await expect(page.getByText('Preview truncated at 64 KiB. Download for the complete body.')).toBeVisible();
    const preview = await page.locator('.dead-letters__preview').last().textContent();
    expect(Buffer.byteLength(preview)).toBe(65536);
    expect(await page.evaluate(() => window.__dlqExecuted)).toBeUndefined();
    const downloaded = await request.get(`${cluster.peerUrl}/data/dead-letters/fibonacci1/messages/${message.Id}/body`);
    expect(await downloaded.body()).toEqual(body);
    expect((await request.post(`${cluster.url}/function/fibonacci1/fix`)).status()).toBe(200);
    const replayUrl = `${cluster.peerUrl}/data/dead-letters/fibonacci1/messages/${message.Id}/requeue`;
    const replay = await Promise.all([request.post(replayUrl), request.post(replayUrl)]);
    expect(replay.map(response => response.status())).toEqual([202, 202]);
    const receipts = await Promise.all(replay.map(response => response.json()));
    expect(receipts[0]).toEqual(receipts[1]);
    expect(receipts[0].ElementId).not.toBe(original);
    await expect.poll(async () => (await (await request.get(`${cluster.url}/data/dead-letters/fibonacci1`)).json()).Count).toBe(0);
    let last;
    await expect.poll(async () => {
      last = await (await request.get(`${cluster.url}/function/fibonacci1/last`)).json();
      return last.elementId;
    }).toBe(receipts[0].ElementId);
    expect(last).toMatchObject({ method: 'POST', url: '/work?original=query', length: body.length,
      sha256: createHash('sha256').update(body).digest('hex'), attempt: '1' });
    expect(errors).toEqual([]);
    await page.screenshot({ path: info.outputPath('dead-letters.png'), fullPage: true });
  } finally { await page.close(); await cluster.close(); }
});
