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

for (const language of ['en', 'fr']) {
  test(`the ${language} film maps the windows to pods and annualizes the monthly saving`, async t => {
    const french = language === 'fr';
    const currency = new Intl.NumberFormat(french ? 'fr-FR' : 'en-IE', {style: 'currency', currency: 'EUR', maximumFractionDigits: 0});
    const hours = value => value.toFixed(1).replace('.', french ? ',' : '.') + ' h';
    const browser = await chromium.launch();
    t.after(() => browser.close());
    const page = await browser.newPage({ viewport: { width: 1080, height: 1350 } });
    const [board, interior, chapters, penguin] = await Promise.all(['../board.html', 'scene.html', 'chapters.html', 'penguin.html']
      .map(file => readFile(new URL(file, import.meta.url), 'utf8')));
    await page.setContent(board);
    await page.evaluate(lang => { document.documentElement.lang = lang; }, language);
    await page.locator('main').evaluate((main, html) => main.insertAdjacentHTML('beforeend', html), interior);
    await page.locator('.house-film').evaluate((film, html) => {
      film.hidden = false;
      film.insertAdjacentHTML('beforeend', html);
    }, chapters + penguin);
    for (const script of ['animation.js', 'chapters.js']) {
      await page.addScriptTag({path: fileURLToPath(new URL(script, import.meta.url))});
    }
    const scenes = JSON.parse(await readFile(new URL(french ? 'fr/scenes.json' : 'scenes.json', import.meta.url), 'utf8'));
    async function frame(key, progress, mouth = 0) {
      const scene = scenes.find(item => item.key === key);
      return page.evaluate(({scene, progress, mouth}) => {
        scene.cues = [{start: 0, end: 2}, {start: 2, end: 5}];
        window.setHouseChapter(scene);
        window.drawHouseChapter(scene.key, scene.visual, progress, progress * 6, progress * 6, mouth);
        const gate = document.querySelector('#podDoor path').getBoundingClientRect();
        const windows = [...document.querySelector('#podWindows').children];
        const displayed = windows.filter(el => el.style.display !== 'none');
        return {
          shown: displayed.length,
          lit: displayed.filter(el => el.dataset.lit === 'true').length,
          door: [gate.x, gate.y],
          firstWindow: ['x', 'y', 'width', 'height'].map(k => windows[0].getBoundingClientRect()[k]),
          originalWindow: ['x', 'y', 'width', 'height'].map(k =>
            document.querySelector('#rooms > g > rect:nth-child(2)').getBoundingClientRect()[k]),
          readings: [...document.querySelectorAll('[data-pod]')].slice(0,4).map(el => el.textContent),
          monthly: [...document.querySelectorAll('[data-month]')].map(el => el.textContent),
          annual: document.querySelector('[data-year="saved"]').textContent,
          months: document.querySelectorAll('.house-film__month--saved').length,
          intro: document.querySelector('.house-film__promise').textContent,
          beak: document.querySelector('#penguinBeak').getAttribute('transform'),
          visiblePitches: [...document.querySelectorAll('[data-pitch]')].filter(el => !el.hidden).map(el => el.dataset.pitch)
        };
      }, {scene, progress, mouth});
    }
    const house = await frame('mapping', 1);
    assert.deepEqual(house.firstWindow, house.originalWindow);
    assert.equal(house.lit, 8);
    assert.deepEqual((await frame('podbill', .44)).readings, ['8', currency.format(80), hours(.5), currency.format(40)]);
    assert.deepEqual((await frame('podbill', 1)).readings, ['8', currency.format(80), hours(1), currency.format(80)]);
    const asleep = await frame('sleeping', 1);
    assert.equal(asleep.lit, 0);
    assert.deepEqual(asleep.readings, ['0', currency.format(0), hours(2), currency.format(80)]);
    assert.equal((await frame('arrival', .28)).lit, 1);
    assert.equal((await frame('arrival', 1)).lit, 8);
    for (const [key, count] of [['tower', 32], ['palace', 96]]) {
      const drawing = await frame(key, 1);
      assert.equal(drawing.lit, count);
      assert.equal(drawing.shown, count);
      assert.deepEqual(drawing.door, house.door);
      assert.deepEqual(drawing.readings, [String(count), currency.format(count*10), hours(2), currency.format(80)]);
      const middle = await frame(key, .5);
      assert.ok(middle.lit > (key === 'tower' ? 8 : 32) && middle.lit < count);
    }
    for (const key of ['trial', 'toolbox', 'savings', 'year']) {
      assert.deepEqual((await frame(key, 1)).visiblePitches, [key]);
    }
    const close = await frame('savings', 1);
    assert.deepEqual(close.monthly, [currency.format(4000) + (french ? ' à payer' : ' spend'),
      currency.format(6000) + (french ? ' économisés' : ' saved')]);
    assert.ok(close.intro.includes(french ? 'Réduisez' : 'Reduce'));
    for (const [progress, months] of [[0, 0], [1/6, 6], [1, 12]]) {
      const year = await frame('year', progress);
      assert.equal(year.months, months);
      assert.equal(year.annual, currency.format(6000 * months));
    }
    assert.notEqual(close.beak, (await frame('savings', 1, 1)).beak);
  });
}
