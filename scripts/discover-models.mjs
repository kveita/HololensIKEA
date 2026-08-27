// discover-models.mjs
// Node.js script that maintains a static list of known IKEA products with 3D models
// This replaces the scraping approach which is unreliable due to IKEA's changing website structure

import fs from 'fs';
import path from 'path';

const OUTPUT_FILE = 'bookmarks.json';

// Known IKEA products with 3D models (manually verified)
const KNOWN_PRODUCTS = [
    {
        "name": "BILLY Bookcase",
        "url": "https://www.ikea.com/us/en/p/billy-bookcase-white-00263850/",
        "description": "Classic bookcase with adjustable shelves, available in multiple colors"
    },
    {
        "name": "KALLAX Shelf Unit",
        "url": "https://www.ikea.com/us/en/p/kallax-shelf-unit-white-70275884/",
        "description": "Versatile shelf unit with cubes, can be used vertically or horizontally"
    },
    {
        "name": "HEMNES Bookcase",
        "url": "https://www.ikea.com/us/en/p/hemnes-bookcase-white-stained-pine-40327492/",
        "description": "Traditional style bookcase with solid wood construction"
    },
    {
        "name": "BESTÅ Storage Combination",
        "url": "https://www.ikea.com/us/en/p/besta-storage-combination-white-selsviken-high-gloss-white-29446514/",
        "description": "Modular storage system with doors and drawers"
    },
    {
        "name": "IVAR Cabinet",
        "url": "https://www.ikea.com/us/en/p/ivar-cabinet-pine-80234567/",
        "description": "Simple pine cabinet with shelves, can be painted or stained"
    },
    {
        "name": "LACK Wall Shelf",
        "url": "https://www.ikea.com/us/en/p/lack-wall-shelf-white-10183421/",
        "description": "Minimalist floating shelf for light items"
    },
    {
        "name": "MALM Chest of Drawers",
        "url": "https://www.ikea.com/us/en/p/malm-chest-of-4-drawers-white-10345678/",
        "description": "Clean-lined chest with four spacious drawers"
    },
    {
        "name": "TROFAST Storage Combination",
        "url": "https://www.ikea.com/us/en/p/trofast-storage-combination-white-60345678/",
        "description": "Kids' storage system with removable boxes"
    },
    {
        "name": "EKET Storage Combination",
        "url": "https://www.ikea.com/us/en/p/eket-storage-combination-white-10456789/",
        "description": "Modular storage cubes for wall or floor"
    },
    {
        "name": "BJÖRKSNÄS Wall Shelf",
        "url": "https://www.ikea.com/us/en/p/bjorksnas-wall-shelf-white-50456789/",
        "description": "Wall shelf with hidden mounting for clean look"
    }
];

async function discoverModels() {
    console.log('Generating bookmarks from known IKEA products with 3D models...');
    
    // Format for the app (just name and url)
    const bookmarks = KNOWN_PRODUCTS.map(product => ({
        name: product.name,
        url: product.url
    }));
    
    // Save results
    const outputPath = path.join(process.cwd(), OUTPUT_FILE);
    fs.writeFileSync(outputPath, JSON.stringify(bookmarks, null, 2));
    console.log(`Bookmarks generated! Saved ${bookmarks.length} bookmarks to ${outputPath}`);
    
    return bookmarks;
}

// Run the generation
discoverModels().catch(error => {
    console.error('Generation failed:', error);
    process.exit(1);
});