// Render the portrait cut entirely from the bundled mark, footage and observations.
import { chromium } from 'playwright-core';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const directory = process.argv[2];
const { scenes, fps, observed } = JSON.parse(await readFile(path.join(directory, 'plan.json'), 'utf8'));
const board = await readFile(new URL('board.html', import.meta.url), 'utf8');
const logo = await readFile(new URL('slimfaas.svg', import.meta.url));
const frames = path.join(directory, 'frames');
await mkdir(frames);
const metrics = {
  idle: [[0, 'ready workers']],
  wake: [['0 → 1', 'ready worker'], [observed.syncResponse.result, 'HTTP result']],
  rush: [[observed.asyncAccepted, 'accepted'], [observed.peakReadyWorkers, 'workers at peak']],
  quiet: [[observed.asyncCompletedWithResult4807526976, 'completed'], [observed.finalReadyWorkers, 'workers after idle']],
};

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1080, height: 1350 }, deviceScaleFactor: 1 });
  await page.setContent(board);
  if (scenes.some(scene => scene.house)) {
    const interior = await readFile(new URL('house/scene.html', import.meta.url), 'utf8');
    await page.locator('.film__story').evaluate((story, html) => story.insertAdjacentHTML('beforebegin', html), interior);
    await page.addScriptTag({ path: fileURLToPath(new URL('house/animation.js', import.meta.url)) });
  }
  await page.evaluate(async base64 => {
    const mark = document.querySelector('.film__logo');
    mark.src = 'data:image/svg+xml;base64,' + base64;
    await mark.decode();
  }, logo.toString('base64'));

  let movieFrame = 0;
  for (const scene of scenes) {
    const stats = metrics[scene.key];
    await page.evaluate(({ scene, stats, result }) => {
      const field = name => document.querySelector('.film__' + name);
      document.querySelector('main').className = 'film film--' + scene.key;
      if (field('house')) field('house').hidden = !scene.house;
      field('story').hidden = !stats;
      field('case').hidden = scene.key !== 'cost';
      field('close').hidden = scene.key !== 'try';
      if (scene.house) {
        document.querySelector('.house-film__title').textContent = scene.headline;
      } else if (stats) {
        field('headline').textContent = scene.headline;
        field('detail').textContent = scene.detail;
        field('source').textContent = scene.key === 'quiet' ? 'Kubernetes · time-lapse' : 'Kubernetes recording';
        field('metrics').replaceChildren();
        for (const [value, unit] of stats) {
          const metric = document.createElement('div');
          metric.className = 'film__metric';
          for (const [name, text] of [['value', value], ['unit', unit]]) {
            const item = document.createElement('span');
            item.className = 'film__' + name;
            item.textContent = text;
            metric.append(item);
          }
          field('metrics').append(metric);
        }
      } else if (scene.key === 'cost') {
        field('case-title').textContent = scene.headline;
      } else if (scene.key === 'try') {
        field('close-title').textContent = scene.headline;
        field('result').textContent = '200 OK  ·  ' + JSON.stringify({ result });
      } else {
        throw new Error('Unknown scene ' + scene.key);
      }
      const content = document.querySelector('section:not([hidden])');
      if (content.getBoundingClientRect().bottom > field('captions').getBoundingClientRect().top ||
          (stats && field('detail').getBoundingClientRect().bottom > field('monitor').getBoundingClientRect().top)) {
        throw new Error('Content overlaps in ' + scene.key);
      }
    }, { scene, stats, result: observed.syncResponse.result });

    let previousImage = 0;
    for (let i = 0; i < scene.frames; i++, movieFrame++) {
      const t = i / fps;
      let image;
      if (scene.images) {
        const holdFrames = Math.round(fps * (scene.key === 'quiet' ? 1.75 : 0.5));
        const progress = Math.min(1, i / Math.max(1, scene.frames - holdFrames - 1));
        const shot = 1 + Math.round((scene.images - 1) * progress);
        if (shot !== previousImage) {
          const file = String(shot).padStart(4, '0') + '.jpg';
          image = (await readFile(path.join(directory, scene.key, file))).toString('base64');
          previousImage = shot;
        }
      }
      const caption = scene.cues.find(cue => cue.start <= t && t < cue.end)?.text || '';
      await page.evaluate(async ({ image, caption, t, houseKey, progress }) => {
        if (houseKey) window.drawHouse(houseKey, progress, t);
        if (image) {
          const capture = document.querySelector('.film__capture');
          capture.src = 'data:image/jpeg;base64,' + image;
          await capture.decode();
        }
        const text = document.querySelector('.film__caption');
        text.textContent = caption;
        if (text.getBoundingClientRect().bottom > innerHeight - 38) throw new Error('Caption exceeds safe area');
        const section = document.querySelector('section:not([hidden])');
        section.style.transform = houseKey ? 'none' : 'translateY(' + (8 * Math.max(0, 1 - t / 0.3) ** 2) + 'px)';
      }, { image, caption, t, houseKey: scene.house ? scene.key : null, progress: i / Math.max(1, scene.frames - 1) });
      await page.screenshot({ path: path.join(frames, String(movieFrame).padStart(5, '0') + '.jpg'),
        type: 'jpeg', quality: 92 });
    }
    console.log(scene.key, (scene.frames / fps).toFixed(2) + 's');
  }
} finally {
  await browser.close();
}
