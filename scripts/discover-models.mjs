// discover-models.mjs
//
// Generates bookmarks.json for the HoloLens app by looking up each product
// on IKEA's public search API, then resolving its direct .glb 3D model URL
// so the app never has to scrape IKEA's product pages at runtime (IKEA
// renders its "View in 3D" model-viewer client-side via JavaScript, so a
// plain HTML fetch can't find the model URL there).
//
// Two IKEA endpoints make this possible without a browser:
//  - Search:     https://sik.search.blue.cdtapps.com/{locale}/search-result-page?q={query}
//                Returns JSON with each match's real product-page URL
//                (pipUrl), which is more reliable than hardcoding article
//                numbers that can go stale as IKEA's catalog changes.
//  - 3D model:   https://web-api.ikea.com/{locale}/rotera/static/models/{articleNumber}-mini.glb
//                where {articleNumber} is the trailing numeric id in the
//                product page URL, e.g.
//                https://www.ikea.com/us/en/p/billy-bookcase-oak-effect-10508932/
//                -> https://web-api.ikea.com/us/en/rotera/static/models/10508932-mini.glb
//
// Multi-part "combination"/set products (article numbers prefixed with a
// letter, e.g. ".../s29209829/") don't have a single rotera model and are
// skipped in favor of single-part products from the same series.

import fs from 'fs';
import path from 'path';

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

function buildGlbUrl(articleNumber) {
    return `https://web-api.ikea.com/${LOCALE}/rotera/static/models/${articleNumber}-mini.glb`;
}

/** Returns true if the URL resolves to a real, fetchable resource. */
async function verifyUrlExists(url) {
    try {
        const res = await fetch(url, { method: 'HEAD' });
        if (res.ok) return true;
        // Some CDNs don't support HEAD; fall back to a ranged GET.
        if (res.status === 405 || res.status === 501) {
            const getRes = await fetch(url, { headers: { Range: 'bytes=0-0' } });
            return getRes.ok || getRes.status === 206;
        }
        return false;
    } catch (err) {
        console.warn(`  Request failed: ${err.message}`);
        return false;
    }
}

async function discoverModels() {
    console.log(`Discovering ${PRODUCTS.length} bookmarked IKEA products...`);

    const bookmarks = [];
    let resolvedCount = 0;

    for (const candidate of PRODUCTS) {
        const found = await findProduct(candidate.series, candidate.query);
        if (!found) {
            console.warn(`[skip] ${candidate.name}: no single-part "${candidate.series}" product found for query "${candidate.query}"`);
            continue;
        }

        const bookmark = { name: candidate.name, url: found.pipUrl };
        const glbUrl = buildGlbUrl(found.articleNumber);
        const exists = await verifyUrlExists(glbUrl);

        if (exists) {
            console.log(`[ok]   ${candidate.name}: ${found.pipUrl} -> ${glbUrl}`);
            bookmark.glbUrl = glbUrl;
            resolvedCount++;
        } else {
            console.warn(`[miss] ${candidate.name}: found ${found.pipUrl} but no 3D model at ${glbUrl}`);
        }

        bookmarks.push(bookmark);
    }

    const outputPath = path.join(process.cwd(), OUTPUT_FILE);
    fs.writeFileSync(outputPath, JSON.stringify(bookmarks, null, 2) + '\n');
    console.log(`Saved ${bookmarks.length} bookmarks to ${outputPath} (${resolvedCount} with a verified 3D model).`);

    return bookmarks;
}



// Run the generation
discoverModels().catch(error => {
    console.error('Generation failed:', error);
    process.exit(1);
});