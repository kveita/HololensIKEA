# IKEA 3D Model Bookmarks

This directory contains the bookmarks system for the HololensIKEA app, which provides a curated list of IKEA products with 3D models.

## Overview

The bookmarks system replaces the previous dynamic scraping approach with a static, maintainable list of known IKEA products that have 3D models available.

## Files

- `bookmarks.json` - The main bookmarks file containing product names and URLs
- `scripts/discover-models.mjs` - Node.js script that generates the bookmarks file
- `.github/workflows/update-bookmarks.yml` - GitHub Actions workflow for automated updates

## How It Works

1. **Static Bookmark List**: The `bookmarks.json` file contains a curated list of IKEA products that are known to have 3D models.

2. **Generation Script**: The `discover-models.mjs` script generates the bookmarks file from a known list of products.

3. **Automated Updates**: The GitHub Actions workflow runs every Sunday to regenerate the bookmarks file and commit any changes.

## Adding New Products

To add a new IKEA product to the bookmarks:

1. Edit the `scripts/discover-models.mjs` file
2. Add the product to the `KNOWN_PRODUCTS` array with:
   - `name`: Product name
   - `url`: Full IKEA product URL
   - `description`: Optional description
3. Run `node scripts/discover-models.mjs` to regenerate the bookmarks
4. Commit the changes

## App Integration

The HoloLens app reads the `bookmarks.json` file and displays the list of products in the UI. When a user selects a bookmark, the app loads the product using the URL.

## Manual Update

To manually update the bookmarks:

```bash
npm install
node scripts/discover-models.mjs
```

## GitHub Actions

The workflow runs automatically every Sunday at midnight UTC. You can also trigger it manually through the GitHub Actions interface.

## Troubleshooting

If the script fails:
- Check that Node.js is installed
- Ensure all dependencies are installed (`npm install`)
- Verify the product URLs are correct and accessible
- Check the GitHub Actions logs for errors

## Future Improvements

- Add validation to ensure URLs are accessible
- Implement a backup system for the bookmarks file
- Add more products to the known list
- Implement a way to test if products still have 3D models