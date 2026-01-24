import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const root = path.resolve(__dirname, "..");
const dist = path.join(root, "ClientApp", "dist");
const target = path.join(root, "wwwroot");

async function exists(p) {
  try { await fs.stat(p); return true; } catch { return false; }
}

async function rmDir(p) {
  if (await exists(p)) {
    await fs.rm(p, { recursive: true, force: true });
  }
}

async function copyDir(src, dst) {
  await fs.mkdir(dst, { recursive: true });
  const entries = await fs.readdir(src, { withFileTypes: true });
  for (const e of entries) {
    const s = path.join(src, e.name);
    const d = path.join(dst, e.name);
    if (e.isDirectory()) await copyDir(s, d);
    else if (e.isFile()) await fs.copyFile(s, d);
  }
}

async function main() {
  if (!(await exists(dist))) {
    console.error(`❌ Build output not found: ${dist}`);
    console.error("Run: npm --prefix ClientApp run build");
    process.exit(1);
  }
  await rmDir(target);
  await copyDir(dist, target);
  console.log(`✅ Copied ClientApp/dist -> wwwroot`);
}

main().catch((e) => {
  console.error("❌ Copy failed:", e);
  process.exit(1);
});
