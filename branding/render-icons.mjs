// Renders branding/kna.svg to PNGs with headless Chromium (Playwright), then
// branding/pack-icons.py packs them into .ico (Windows) and .icns (macOS).
//
//   node branding/render-icons.mjs && python3 branding/pack-icons.py
import { createRequire } from "node:module";
import { readFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
// Works with a project-local or a global Playwright install.
const { chromium } = createRequire(join(process.cwd(), "noop.js"))("playwright");
const svg = readFileSync(join(here, "kna.svg"), "utf8");
const out = join(here, "png");
mkdirSync(out, { recursive: true });

const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 180, 192, 256, 512, 1024];
const browser = await chromium.launch();
const page = await browser.newPage({ deviceScaleFactor: 1 });
for (const s of sizes) {
  await page.setViewportSize({ width: s, height: s });
  await page.setContent(
    `<html><body style="margin:0;background:transparent">` +
    svg.replace("<svg ", `<svg style="display:block;width:${s}px;height:${s}px" `) +
    `</body></html>`);
  await page.screenshot({ path: join(out, `kna-${s}.png`), omitBackground: true, clip: { x: 0, y: 0, width: s, height: s } });
}
await browser.close();
console.log(`rendered ${sizes.length} sizes into ${out}`);
