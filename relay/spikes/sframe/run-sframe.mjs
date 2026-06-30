// Increment 4: SFrame (AES-GCM) over WebRTC Encoded Transform (Insertable Streams).
// Serves sframe-test.html over http://127.0.0.1 (a secure context) and drives it in headless
// Chrome. getUserMedia requires a secure context — file:// stopped qualifying in Chrome 138, so
// we serve over localhost (mirrors run-browser.mjs). Exits non-zero unless every frame round-trips.
// Run: npm install && node run-sframe.mjs   (needs a local Chrome/Edge)
import http from 'node:http'
import { readFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import puppeteer from 'puppeteer-core'

const here = dirname(fileURLToPath(import.meta.url))
const exe = process.env.CHROME || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'

const server = http.createServer(async (req, res) => {
  try {
    const body = await readFile(join(here, 'sframe-test.html'))
    res.writeHead(200, { 'content-type': 'text/html' })
    res.end(body)
  } catch { res.writeHead(404); res.end('nf') }
})
await new Promise(r => server.listen(0, r))
const port = server.address().port

const browser = await puppeteer.launch({
  executablePath: exe,
  headless: true,
  args: ['--no-sandbox', '--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--autoplay-policy=no-user-gesture-required']
})
let code = 1
try {
  const page = await browser.newPage()
  await page.goto(`http://127.0.0.1:${port}/sframe-test.html`)
  await page.waitForFunction('window.__result !== null', { timeout: 25000 })
  const result = await page.evaluate(() => window.__result)
  console.log('RESULT ' + JSON.stringify(result))
  // Real pass/fail: green only when every frame was encrypted -> forwarded -> decrypted byte-exact
  // AND differed on the wire (relay blind). Anything else (or an error) exits non-zero.
  code = result && result.green === true ? 0 : 1
} finally {
  await browser.close()
  server.close()
}
process.exit(code)
