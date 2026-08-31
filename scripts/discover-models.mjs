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
const LOCALE = 'us/en';
const SEARCH_API = `https://sik.search.blue.cdtapps.com/${LOCALE}/search-result-page`;

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
    const url = `${SEARCH_API}?q=${encodeURIComponent(query)}&size=10`;
    const res = await fetch(url, { headers: { 'User-Agent': 'Mozilla/5.0 HoloLensIKEA-Bookmarks/1.0' } });
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
 * Visits a product page with katana's headless crawler and returns the
 * first .glb URL observed in the page's network traffic, or null if none
 * was seen. Crawling is capped to a single page. The DOM-content-loaded
 * strategy waits for a bounded period of client-side rendering without using
 * crawl-duration, which prematurely cancels IKEA's async model request.
 */
function findGlbUrlWithKatana(pageUrl) {
    const outFile = path.join(os.tmpdir(), `katana-${Date.now()}-${Math.random().toString(36).slice(2)}.jsonl`);
    const args = [
        '-u', pageUrl,
        '-headless', '-no-sandbox',
        '-depth', '1',
        '-page-load-strategy', 'domcontentloaded',
        '-dom-wait-time', '10',
        '-extension-match', 'glb',
        '-jsonl',
        '-output', outFile,
        '-silent',
        '-timeout', '20',
    ];

    // Note: -crawl-duration is intentionally not used here -- it was found to
    // cut the headless session short before the page's async .glb request
    // (fired by IKEA's model-viewer after load) had a chance to complete.
    const result = spawnSync('katana', args, { stdio: 'ignore', timeout: 60000 });
    if (result.error) {
        console.warn(`  katana failed to run: ${result.error.message}`);
        return null;
    }

    if (!fs.existsSync(outFile)) return null;
    const content = fs.readFileSync(outFile, 'utf8');
    fs.rmSync(outFile, { force: true });

    for (const line of content.split('\n')) {
        const trimmed = line.trim();
        if (!trimmed) continue;
        try {
            const record = JSON.parse(trimmed);
            const endpoint = record?.request?.endpoint;
            if (endpoint && /\.glb(\?|$)/i.test(endpoint)) return endpoint;
        } catch {
            // Not a JSON line (e.g. a stray log line); ignore.
        }
    }
    return null;
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
        const glbUrl = findGlbUrlWithKatana(found.pipUrl);

        if (glbUrl) {
            console.log(`[ok]   ${candidate.name}: ${found.pipUrl} -> ${glbUrl}`);
            bookmark.glbUrl = glbUrl;
            resolvedCount++;
        } else {
            console.warn(`[miss] ${candidate.name}: found ${found.pipUrl} but katana observed no .glb request`);
            const existing = existingBookmarks.get(candidate.name);
            if (existing?.glbUrl) {
                bookmark.glbUrl = existing.glbUrl;
                console.log(`[keep] ${candidate.name}: retaining last verified GLB URL`);
            }
        }

        bookmarks.push(bookmark);
    }

    const outputPath = path.join(process.cwd(), OUTPUT_FILE);
    if (resolvedCount === 0 && existingBookmarks.size === 0) {
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