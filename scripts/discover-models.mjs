// discover-models.mjs
//
// Generates bookmarks.json for the HoloLens app by looking up each product
// on IKEA's public search API, then using katana (https://github.com/projectdiscovery/katana)
// in headless mode to verify that each page exposes a 3D model. The generated
// file intentionally contains only IKEA page bookmarks: GLBs are never
// downloaded or stored in this repository and are fetched by the HoloLens.
//
// Search:  https://sik.search.blue.cdtapps.com/{locale}/search-result-page?q={query}
//          Returns JSON with each match's real product-page URL (pipUrl),
//          which is more reliable than hardcoding article numbers that can
//          go stale as IKEA's catalog changes.
//
// Multi-part "combination"/set products (article numbers prefixed with a
// letter, e.g. ".../s29209829/") typically don't have a single 3D model and
// are skipped in favor of single-part products from the same series.

import fs from 'fs';
import os from 'os';
import path from 'path';
import { spawnSync } from 'child_process';

const OUTPUT_FILE = 'bookmarks.json';
const LOCALE = 'us/en';
const SEARCH_API = `https://sik.search.blue.cdtapps.com/${LOCALE}/search-result-page`;
const USER_AGENT = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/131 Safari/537.36 HoloLensIKEA-Bookmarks/1.0';
const ROTERA_CLIENT_ID = '4863e7d2-1428-4324-890b-ae5dede24fc6';

const CRAWL_SEED = 'https://www.ikea.com/us/en/cat/products-products/';
const MAX_NEW_BOOKMARKS = 25;

/** Extracts the 8-digit article number from a single-part IKEA product page URL, or null. */
function extractArticleNumber(pipUrl) {
    const match = pipUrl.match(/-(\d{8})\/?(?:[?#].*)?$/);
    return match ? match[1] : null;
}

/** Searches IKEA's product search API and returns the first single-part match from the given series. */
async function findProduct(series, query) {
    const url = `${SEARCH_API}?q=${encodeURIComponent(query)}&size=50`;
    const res = await fetch(url, { headers: { 'User-Agent': USER_AGENT } });
    if (!res.ok) {
        console.warn(`  Search request failed (HTTP ${res.status}) for query "${query}"`);
        return null;
    }
    const data = await res.json();
    const items = data?.searchResultPage?.products?.main?.items ?? [];
    for (const item of items) {
        const product = item.product;
        if (!product?.pipUrl) continue;
        if (product.name?.toUpperCase() !== series.toUpperCase()) continue;
        const articleNumber = extractArticleNumber(product.pipUrl);
        if (!articleNumber) continue; // multi-part "combination"/set product; skip
        return { pipUrl: product.pipUrl, articleNumber };
    }
    return null;
}

/**
 * Katana's XHR records have changed shape between releases (request.endpoint,
 * request.url, and top-level url have all appeared). Enable XHR extraction and
 * inspect every URL-valued field rather than depending on one record shape.
 */
function modelUrlFromValue(value) {
    if (typeof value !== 'string' || !/^https?:\/\//i.test(value)) return null;
    let decoded = value;
    try { decoded = decodeURIComponent(value); } catch { /* keep original */ }
    return /\.glb(?:[?#]|$)/i.test(decoded) ? decoded : null;
}

function glbUrlsInRecord(record) {
    const urls = [];
    const visit = value => {
        if (typeof value === 'string') {
            let decoded = value;
            try { decoded = decodeURIComponent(value); } catch { /* keep original */ }
            if (/https?:\/\/[^\s"']+\.glb(?:[?#]|$)/i.test(decoded)) urls.push(decoded);
        } else if (Array.isArray(value)) value.forEach(visit);
        else if (value && typeof value === 'object') Object.values(value).forEach(visit);
    };
    visit(record);
    return [...new Set(urls)];
}

function findGlbUrlWithKatana(pageUrl) {
    const outFile = path.join(os.tmpdir(), `katana-${Date.now()}-${Math.random().toString(36).slice(2)}.jsonl`);
    const args = [
        '-u', pageUrl,
        '-headless', '-no-sandbox', '-disable-update-check',
        '-depth', '1',
        '-page-load-strategy', 'domcontentloaded',
        '-dom-wait-time', '15',
        '-xhr',
        '-jsonl', '-output', outFile, '-silent',
        '-timeout', '30', '-retry', '2',
        '-H', `User-Agent: ${USER_AGENT}`,
    ];

    // Note: -crawl-duration is intentionally not used here -- it was found to
    // cut the headless session short before the page's async .glb request
    // (fired by IKEA's model-viewer after load) had a chance to complete.
    const result = spawnSync('katana', args, { stdio: ['ignore', 'ignore', 'pipe'], timeout: 90000, encoding: 'utf8' });
    if (result.error) {
        console.warn(`  katana failed to run: ${result.error.message}`);
        return null;
    }

    try {
        if (!fs.existsSync(outFile)) return null;
        const content = fs.readFileSync(outFile, 'utf8');
        for (const line of content.split('\n')) {
            if (!line.trim()) continue;
            try {
                const urls = glbUrlsInRecord(JSON.parse(line));
                if (urls.length) return urls[0];
            } catch {
                // Ignore non-JSON diagnostics.
            }
        }
        return null;
    } finally {
        fs.rmSync(outFile, { force: true });
    }
}

function crawlProductPages() {
    const outFile = path.join(os.tmpdir(), `katana-products-${Date.now()}.jsonl`);
    const args = [
        '-u', CRAWL_SEED, '-headless', '-no-sandbox', '-disable-update-check',
        '-depth', '2', '-page-load-strategy', 'domcontentloaded', '-dom-wait-time', '8',
        '-jsonl', '-output', outFile, '-silent', '-timeout', '15', '-retry', '1',
        '-H', `User-Agent: ${USER_AGENT}`,
    ];
    try {
        const result = spawnSync('katana', args, { stdio: ['ignore', 'ignore', 'pipe'], timeout: 120000 });
        if (result.error) {
            console.warn(`  Katana product crawl failed: ${result.error.message}`);
            return [];
        }
        if (!fs.existsSync(outFile)) return [];
        const pages = new Set();
        for (const line of fs.readFileSync(outFile, 'utf8').split('\n')) {
            try {
                const record = JSON.parse(line);
                const values = [];
                const visit = value => {
                    if (typeof value === 'string') values.push(value);
                    else if (Array.isArray(value)) value.forEach(visit);
                    else if (value && typeof value === 'object') Object.values(value).forEach(visit);
                };
                visit(record);
                for (const value of values) {
                    const url = value.replace(/\\\//g, '/');
                    if (/^https:\/\/www\.ikea\.com\/us\/en\/p\/[^?#]+-\d{8}\/?(?:[?#].*)?$/i.test(url)) {
                        pages.add(url.split(/[?#]/)[0]);
                    }
                }
            } catch { /* Ignore non-JSON output. */ }
        }
        return [...pages];
    } finally {
        fs.rmSync(outFile, { force: true });
    }
}

function loadExistingBookmarks() {
    if (!fs.existsSync(OUTPUT_FILE)) return new Map();

    try {
        return new Map(JSON.parse(fs.readFileSync(OUTPUT_FILE, 'utf8'))
            .filter(bookmark => bookmark?.name && bookmark?.url)
            .map(bookmark => [bookmark.name, bookmark]));
    } catch (error) {
        console.warn(`Could not read existing bookmarks: ${error.message}`);
        return new Map();
    }
}

async function findGlbUrlFromRotera(articleNumber) {
    const endpoint = `https://web-api.ikea.com/${LOCALE}/rotera/data/model/${articleNumber}/`;
    try {
        const response = await fetch(endpoint, {
            headers: {
                Accept: 'application/json;version=2',
                'x-client-id': ROTERA_CLIENT_ID,
                'User-Agent': USER_AGENT,
            },
        });
        if (!response.ok) return null;
        const body = await response.text();
        if (!body.trim()) return null;
        const modelUrl = JSON.parse(body)?.modelUrl;
        return modelUrlFromValue(modelUrl);
    } catch (error) {
        console.warn(`  Rotera model lookup failed for ${articleNumber}: ${error.message}`);
        return null;
    }
}

async function mapWithConcurrency(items, concurrency, worker) {
    const results = new Array(items.length);
    let next = 0;
    async function consume() {
        while (true) {
            const index = next++;
            if (index >= items.length) return;
            results[index] = await worker(items[index], index);
        }
    }
    await Promise.all(Array.from({ length: Math.min(concurrency, items.length) }, consume));
    return results;
}

async function discoverModels() {
    const existingBookmarks = loadExistingBookmarks();
    const bookmarks = [];
    let resolvedCount = 0;
    const knownUrls = new Set();
    const knownArticles = new Set();

    console.log(`Phase 1: validating ${existingBookmarks.size} existing IKEA bookmarks...`);
    const existingResults = await mapWithConcurrency([...existingBookmarks.values()], 8, async bookmark => {
        const articleNumber = extractArticleNumber(bookmark.url);
        if (!articleNumber) return { bookmark, articleNumber: null, glbUrl: null };
        const glbUrl = await findGlbUrlFromRotera(articleNumber);
        return { bookmark, articleNumber, glbUrl };
    });
    for (const result of existingResults) {
        if (!result.articleNumber) continue;
        knownUrls.add(result.bookmark.url.split(/[?#]/)[0]);
        knownArticles.add(result.articleNumber);
        if (result.glbUrl) {
            bookmarks.push({ name: result.bookmark.name, url: result.bookmark.url });
            resolvedCount++;
            console.log(`[keep] ${result.bookmark.name}: existing ID ${result.articleNumber} has a GLB`);
        } else console.warn(`[drop] ${result.bookmark.name}: ID ${result.articleNumber} has no GLB`);
    }

    console.log(`Phase 2: crawling IKEA for new product pages (skipping ${knownArticles.size} existing articles)...`);
    const candidatesByArticle = new Map(crawlProductPages().map(url => ({
        url: url.split(/[?#]/)[0],
        articleNumber: extractArticleNumber(url),
    })).filter(candidate => candidate.articleNumber &&
        !knownUrls.has(candidate.url) && !knownArticles.has(candidate.articleNumber))
        .map(candidate => [candidate.articleNumber, candidate]));
    const candidates = [...candidatesByArticle.values()];
    console.log(`Found ${candidates.length} new candidate IKEA article IDs; validating in parallel...`);
    const newResults = await mapWithConcurrency(candidates, 8, async candidate => ({
        ...candidate,
        glbUrl: await findGlbUrlFromRotera(candidate.articleNumber),
    }));
    for (const candidate of newResults) {
        if (!candidate.glbUrl) continue;
        const normalizedUrl = candidate.url;
        const articleNumber = candidate.articleNumber;
        const slug = normalizedUrl.match(/\/p\/([^/]+)-\d{8}\/?$/i)?.[1] || articleNumber;
        const name = slug.replace(/-/g, ' ').replace(/\b\w/g, char => char.toUpperCase());
        bookmarks.push({ name, url: normalizedUrl });
        knownUrls.add(normalizedUrl);
        knownArticles.add(articleNumber);
        resolvedCount++;
        console.log(`[new]  ${name}: ${normalizedUrl}`);
        if (bookmarks.length >= existingBookmarks.size + MAX_NEW_BOOKMARKS) break;
    }

    const outputPath = path.join(process.cwd(), OUTPUT_FILE);
    if (bookmarks.length === 0) {
        throw new Error('No IKEA product pages with 3D models were discovered.');
    }

    fs.writeFileSync(outputPath, JSON.stringify(bookmarks, null, 2) + '\n');
    console.log(`Saved ${bookmarks.length} page bookmarks (${resolvedCount} with a verified IKEA GLB).`);

    return bookmarks;
}



// Run the generation
discoverModels().catch(error => {
    console.error('Generation failed:', error);
    process.exit(1);
});
