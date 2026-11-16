# Regional observations

`observations.csv` combines the 2,741 Year/DOY observations across all 55 regions. The columns are Region, Year, DOY. DOY uses January 1 = 1; leap years permit 366. Each region's record has its own starting year, and the supplied records end in 2025.

The actual program inputs are the independent `Python/[region]/input/observations.csv` and `CSharp/[region]/input/observations.csv` files with exactly the header Year,DOY. Update the chosen implementation's regional files for a new run. The combined table is a distribution/exploration copy, not an input read by the runners.

Means and forecasts are derived outputs. Metadata and historical rolling-mean snapshots remain beside the input CSVs. The original workbook/text discrepancy for Lake Melville VCPI is documented in its region.json warning. No example 2026 observation has been added to these records.
