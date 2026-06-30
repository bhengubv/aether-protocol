// Increment 6 (as a real, repo-reproducible test): bundle the js-libp2p BROWSER stack from source
// with esbuild, serve it from THIS directory (no external paths), boot it inside a real headless
// browser — the same runtime as txtMe's BlazorWebView — and assert the node actually started.
// Exit 0 only when the node reports status "started" with a PeerID; otherwise exit 1.
//
// Robust to transient Chrome/WebRTC startup hiccups under load: 60s boot budget + one retry.
import http from 'node:http'
import { readFile } from 'node:fs/promises'
import { extname, dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { build } from 'esbuild'
import puppeteer from 'puppeteer-core'

const here = dirname(fileURLToPath(import.meta.url))
const CHROME = process.env.CHROME || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'

// 1) Build the browser bundle fresh from source — proves reproducibility, no prebuilt artifact.
await build({
  entryPoints: [join(here, 'browser-node.mjs')],
  bundle: true,
  platform: 'browser',
  format: 'esm',
  outfile: join(here, 'aether-relay.bundle.js'),
  define: { global: 'globalThis' },
  logLevel: 'silent'
})

// 2) Serve this directory (browser-boot.html + the freshly built bundle).
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript' }
const server = http.createServer(async (req, res) => {
  try {
    const rel = req.url === '/' ? '/browser-boot.html' : req.url.split('?')[0]
    const body = await readFile(join(here, rel))
    res.writeHead(200, { 'content-type': types[extname(rel)] || 'application/octet-stream' })
    res.end(body)
  } catch { res.writeHead(404); res.end('nf') }
})
await new Promise(r => server.listen(0, r))
const port = server.address().port

const started = r => r && !r.error && r.status === 'started' && typeof r.peerId === 'string' && r.peerId.length > 0

// 3) Boot inside a real headless browser and assert the node started (one retry for transient hiccups).
async function bootOnce () {
  const browser = await puppeteer.launch({ executablePath: CHROME, headless: true, args: ['--no-sandbox'] })
  try {
    const page = await browser.newPage()
    const errs = []
    page.on('pageerror', e => errs.push(String(e)))
    await page.goto(`http://127.0.0.1:${port}/browser-boot.html`)
    await page.waitForFunction('window.__aether !== null', { timeout: 60000 })
    return { r: await page.evaluate(() => window.__aether), errs }
  } finally {
    await browser.close()
  }
}

let result = null
for (let attempt = 1; attempt <= 2 && !started(result); attempt++) {
  try {
    const { r, errs } = await bootOnce()
    result = r
    if (!started(r)) console.log(`attempt ${attempt}: not started -> ${JSON.stringify(r)} pageerrors=${JSON.stringify(errs)}`)
  } catch (e) {
    console.log(`attempt ${attempt}: ${String(e)}`)
  }
}
server.close()

if (started(result)) {
  console.log('RESULT ' + JSON.stringify(result))
  console.log('GREEN - js-libp2p browser stack bundled from source + booted in a real browser')
  process.exit(0)
}
console.log('RED - browser node did not start after retries')
process.exit(1)
