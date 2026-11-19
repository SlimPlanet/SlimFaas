import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { chromium } from 'playwright-core';

test('the two departures show the same eight empty hours and different lighting bills', async t => {
  const browser = await chromium.launch();
  t.after(() => browser.close());
  const page = await browser.newPage();
  await page.setContent(await readFile(new URL('scene.html', import.meta.url), 'utf8'));
  await page.locator('.house-film').evaluate(section => { section.hidden = false; });
  await page.addScriptTag({ path: fileURLToPath(new URL('animation.js', import.meta.url)) });
  async function frame(key, progress) {
    return page.evaluate(({ key, progress }) => {
      window.drawHouse(key, progress, progress * 6);
      const outside = [...document.querySelector('#peopleOutside').children].filter(person =>
        person.style.display !== 'none' && person.getCTM().e < 1080);
      return {
        readings: [...document.querySelectorAll('[data-reading]')].map(el => el.textContent),
        litWindows: [...document.querySelector('#rooms').children].filter(room => Number(room.querySelector('g').getAttribute('opacity')) > .5).length,
        outside: outside.length,
        blossom: document.querySelector('#garden use').getAttribute('transform')
      };
    }, { key, progress });
  }
  const occupied = await frame('evening', 1);
  assert.equal(occupied.litWindows, 8);
  assert.equal(occupied.outside, 0);
  const departure = await frame('leave', .62);
  assert.ok(departure.outside > 0);
  assert.equal(departure.litWindows, 8);
  const halfway = await frame('bill', .465);
  assert.deepEqual(halfway.readings, ['8', '4.0', '1.92', '€0.48']);
  const waste = await frame('bill', 1);
  assert.deepEqual(waste.readings, ['8', '8.0', '3.84', '€0.96']);
  assert.notEqual(waste.blossom, occupied.blossom);
  const off = await frame('switch', 1);
  assert.deepEqual(off.readings, ['0', '8.0', '0.00', '€0.00']);
  assert.equal(off.litWindows, 0);
  assert.equal((await frame('return', 1)).litWindows, 1);
  assert.deepEqual(await frame('bill', 1), waste);
});
