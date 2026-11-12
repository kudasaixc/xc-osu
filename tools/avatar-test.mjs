// Verify that avatar URLs always use local account IDs, even with imported official URLs.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright';
const browser = await chromium.launch({ executablePath: process.env.HOME + '/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome', headless: true, args: ['--no-sandbox'] });
try {
  const page = await browser.newPage();
  await page.route('https://avatar.test/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<!doctype html>' });
    if (path === '/core.js' || path === '/bbcode.js') return route.fulfill({ contentType: 'text/javascript', body: await readFile(new URL('../web/js' + path, import.meta.url)) });
    throw new Error('Unexpected request: ' + path);
  });
  await page.goto('https://avatar.test/');
  const actual = await page.evaluate(async () => {
    const { avatarUrl, avatar } = await import('/core.js');
    return [
      avatarUrl({ id: 2, avatar_url: 'https://a.ppy.sh/2' }),
      avatarUrl({ id: 2 }),
      avatarUrl({ id: 2, avatar_url: '/api/xc/avatar/2?v=3' }),
      avatarUrl({ id: 2, avatar_url: 'https://other.test/api/xc/avatar/2?v=3' }),
      avatarUrl({ id: 2, avatar_url: '/api/xc/avatar/8?v=3' }),
      avatarUrl(null),
      avatar({ id: 2, avatar_url: 'https://a.ppy.sh/2' }).includes('src="/api/xc/avatar/2"'),
    ];
  });
  assert.deepEqual(actual, ['/api/xc/avatar/2', '/api/xc/avatar/2', '/api/xc/avatar/2?v=3', '/api/xc/avatar/2', '/api/xc/avatar/2', '/api/xc/avatar/default', true]);
  console.log('Avatar routing: 7 browser checks passed');
} finally { await browser.close(); }
