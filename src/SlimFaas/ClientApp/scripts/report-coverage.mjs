// Remap Chromium's V8 ranges using the Vite source map produced for this test build.
import { readFile, readdir } from 'node:fs/promises';
import { basename, relative, resolve, sep } from 'node:path';
import v8ToIstanbul from 'v8-to-istanbul';
import coverage from 'istanbul-lib-coverage';
import report from 'istanbul-lib-report';
import reports from 'istanbul-reports';

const directory = process.env.DASHBOARD_COVERAGE_DIR;
const sources = resolve('src') + sep;
const map = coverage.createCoverageMap({});
for (const file of await readdir(directory)) {
  if (!file.endsWith('.json')) continue;
  for (const entry of JSON.parse(await readFile(resolve(directory, file), 'utf8'))) {
    const asset = resolve('../wwwroot/assets', basename(new URL(entry.url).pathname));
    const converter = v8ToIstanbul(asset);
    await converter.load();
    converter.applyCoverage(entry.functions);
    for (const [name, result] of Object.entries(converter.toIstanbul())) {
      if (name.startsWith(sources)) map.addFileCoverage(result);
    }
    converter.destroy();
  }
}
if (!map.files().length) throw new Error('No dashboard source files in Chromium coverage.');
const projectRoot = resolve('../../..');
reports.create('lcovonly', { file: 'browser.info', projectRoot }).execute(report.createContext({ dir: directory, coverageMap: map }));
console.log('Wrote coverage for', map.files().length, 'dashboard files under', relative(projectRoot, sources));
