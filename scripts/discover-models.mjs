// discover-models.mjs
//
// Generates bookmarks.json for the HoloLens app by looking up each product
// on IKEA's public search API, then using katana (https://github.com/projectdiscovery/katana)
// in headless mode to discover its direct .glb 3D model URL, so the app
// never has to scrape IKEA's product pages at runtime. IKEA renders its
// "View in 3D" model-viewer client-side via JavaScript and fetches the
// model over an XHR/fetch request as soon as the page loads, so a plain
// HTML fetch can't see it -- but a headless browser crawl can observe that
// network request directly, which is what katana's `-headless` mode does.
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
const MODELS_DIRECTORY = 'Models';
const LOCALE = 'us/en';
const SEARCH_API = `https://sik.search.blue.cdtapps.com/${LOCALE}/search-result-page`;
const DECODED_MODEL_BASE_URL = 'https://raw.githubusercontent.com/turbolego/HololensIKEA/main/Models';
const USER_AGENT = 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/131 Safari/537.36 HoloLensIKEA-Bookmarks/1.0';
const ROTERA_CLIENT_ID = '4863e7d2-1428-4324-890b-ae5dede24fc6';

// Bookmarked products: a display name, the series name IKEA returns for a
// matching search result, and a search query likely to surface a real,
// single-part (non-combination) product from that series.
const PRODUCTS = [
    { name: "BILLY Bookcase", series: "BILLY", query: "billy bookcase" },
    { name: "KALLAX Shelf Unit", series: "KALLAX", query: "kallax shelf unit" },
    { name: "HEMNES Bookcase", series: "HEMNES", query: "hemnes bookcase" },
    { name: "BESTÅ Frame", series: "BESTÅ", query: "besta frame" },
    { name: "IVAR Cabinet", series: "IVAR", query: "ivar cabinet" },
    { name: "LACK Wall Shelf", series: "LACK", query: "lack wall shelf" },
    { name: "TROFAST Frame", series: "TROFAST", query: "trofast frame" },
    { name: "EKET Cabinet", series: "EKET", query: "eket cabinet" },
    { name: "MALM Dressing Table", series: "MALM", query: "malm dressing table" },
    { name: "BJÖRKSNÄS Nightstand", series: "BJÖRKSNÄS", query: "bjorksnas nightstand" },
];

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

function loadExistingBookmarks() {
    if (!fs.existsSync(OUTPUT_FILE)) return new Map();

    try {
        return new Map(JSON.parse(fs.readFileSync(OUTPUT_FILE, 'utf8'))
            .filter(bookmark => bookmark?.name && bookmark?.glbUrl)
            .map(bookmark => [bookmark.name, bookmark]));
    } catch (error) {
        console.warn(`Could not read existing bookmarks: ${error.message}`);
        return new Map();
    }
}

function decodeGlb(articleNumber, sourceUrl) {
    const outputPath = path.join(MODELS_DIRECTORY, `${articleNumber}.glb`);
    fs.mkdirSync(MODELS_DIRECTORY, { recursive: true });
    const sourcePath = path.join(os.tmpdir(), `ikea-${articleNumber}.glb`);

    try {
        const download = spawnSync('curl', ['-fsSL', sourceUrl, '-o', sourcePath], { stdio: 'inherit', timeout: 60000 });
        if (download.status !== 0) return null;

        // Reading and writing with glTF Transform decodes KHR_draco_mesh_compression.
        const convert = spawnSync('npx', ['--no-install', 'gltf-transform', 'copy', sourcePath, outputPath], { stdio: 'inherit', timeout: 120000 });
        return convert.status === 0 ? `${DECODED_MODEL_BASE_URL}/${articleNumber}.glb` : null;
    } finally {
        fs.rmSync(sourcePath, { force: true });
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

async function discoverModels() {
    console.log(`Discovering ${PRODUCTS.length} bookmarked IKEA products...`);

    const existingBookmarks = loadExistingBookmarks();
    const bookmarks = [];
    let resolvedCount = 0;

    for (const candidate of PRODUCTS) {
        const found = await findProduct(candidate.series, candidate.query);
        if (!found) {
            console.warn(`[skip] ${candidate.name}: no single-part "${candidate.series}" product found for query "${candidate.query}"`);
            continue;
        }

        const bookmark = { name: candidate.name, url: found.pipUrl };
        let glbUrl = findGlbUrlWithKatana(found.pipUrl);
        if (!glbUrl) {
            // The viewer is now lazy-loaded and may not issue an XHR during a
            // page crawl. Ask the same public Rotera API used by the viewer.
            glbUrl = await findGlbUrlFromRotera(found.articleNumber);
            if (glbUrl) console.log(`[fallback] ${candidate.name}: found GLB through IKEA Rotera API`);
        }

        if (glbUrl) {
            const decodedUrl = decodeGlb(found.articleNumber, glbUrl);
            if (decodedUrl) {
                console.log(`[ok]   ${candidate.name}: ${found.pipUrl} -> ${decodedUrl}`);
                bookmark.glbUrl = decodedUrl;
                bookmark.sourceGlbUrl = glbUrl;
                resolvedCount++;
            } else {
                console.warn(`[miss] ${candidate.name}: could not decode the discovered GLB`);
            }
        } else {
            console.warn(`[miss] ${candidate.name}: found ${found.pipUrl} but katana observed no .glb request`);
            const existing = existingBookmarks.get(candidate.name);
            if (existing?.glbUrl) {
                bookmark.glbUrl = existing.glbUrl;
                bookmark.sourceGlbUrl = existing.sourceGlbUrl;
                console.log(`[keep] ${candidate.name}: retaining last verified GLB URL`);
            }
        }

        // A page without a model is not useful to the HoloLens app. Keep the
        // existing verified entry when discovery misses, but do not create a
        // new page-only bookmark (for example, a discontinued KALLAX result).
        if (!bookmark.glbUrl) {
            console.warn(`[skip] ${candidate.name}: omitting bookmark because no GLB is available`);
            continue;
        }
        bookmarks.push(bookmark);
    }

    const outputPath = path.join(process.cwd(), OUTPUT_FILE);
    if (bookmarks.length === 0) {
        throw new Error('Katana did not discover any GLB URLs and no verified bookmarks are available to preserve.');
    }

    fs.writeFileSync(outputPath, JSON.stringify(bookmarks, null, 2) + '\n');
    console.log(`Saved ${bookmarks.length} bookmarks to ${outputPath} (${resolvedCount} with a verified 3D model).`);

    return bookmarks;
}



// Run the generation
discoverModels().catch(error => {
    console.error('Generation failed:', error);
    process.exit(1);
});
