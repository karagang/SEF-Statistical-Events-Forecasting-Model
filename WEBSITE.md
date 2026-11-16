# Project website

The site is static HTML/CSS/JavaScript in `docs/`, published by GitHub Pages from `main` and `/docs`. `.nojekyll` keeps it buildless. There are no trackers, third-party web fonts, remote runtime scripts, accounts or databases.

## Files

- `docs/index.html`: project overview, software links, example explorer, regional table and annual guide.
- `docs/guide.html`: installation and annual-update procedure, including Python-to-C# report compatibility.
- `docs/methodology.html`: calculation choices, validation and limitations.
- `docs/styles.css`, `docs/app.js`: responsive layout and local interactions.
- `docs/assets/regions.json`: archived 2026 results used by the example explorer.
- `docs/assets/*.png`: actual C# output images.
- `docs/downloads`: the supplied paper and combined input CSV.

## Refreshing examples

Generate a consistent run locally first. For a new example year, replace the archived regions JSON from the saved regional forecast.json files, update the gallery images, years, labels and report/download links, and check the browser on desktop and mobile. Update the repository's combined `data/observations.csv` and site download copy together. The website is an archived example viewer and does not run the forecast model in the browser.

Source and input files belong in Git. Do not commit virtual environments, credentials, local validation receipts, executable binaries, or build caches. Provide the ready-to-run C# executable and large software/report packages as GitHub release assets. The .gitignore documents generated directories to exclude.
