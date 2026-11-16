# Python implementation

This public package contains the Python source and its own regional CSV inputs. A virtual environment is not shipped. Create one and install dependencies before running:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe run_forecasts.py
.\.venv\Scripts\python.exe build_reports.py --year 2026 --pdf-only
```

For 2027, append each region's actual 2026 observation first and use `--year 2027` for reports. Full instructions: https://karagang.github.io/SEF-Statistical-Events-Forecasting-Model/guide.html

The retained JavaScript PowerPoint helper depends on the configured Codex presentation runtime. An independent alternative is the ready-to-run C# report writer: with the two downloaded folders side by side, run `.\CSharp\SEFM.exe reports --root .\Python --year 2027 --destination .\Python\reports` from their parent after Python forecasts complete. It uses the saved Python results.

The notes below describe the implementation and original local workflow. Setup requirements above apply to this public distribution.

---

# Standalone OneDrive project

This folder contains the latest shared SEFM programs, all 55 regional CSV inputs and 2026 outputs, the latest combined reports, and reference documents. It runs without the original D: project folders. Earlier program versions, temporary builds and review images are excluded.

Reports are saved inside this folder under `reports/output-YYYY`. Regional outputs stay beside their inputs in `output-YYYY`. The `reference` folder contains source documents, migration provenance, legacy numerical baselines and the existing PowerPoint validation receipt. The previously generated reports retain their original source-path citations as historical provenance.

Python dependencies are installed in the local `.venv`. PowerPoint generation also uses the Codex Node.js runtime and bundled presentation library installed on this computer; those installed runtime dependencies are not copied into OneDrive. This is a self-contained project, not a portable executable for computers without its dependencies.

`migrate_inputs.py` is retained as the one-time import utility. The CSV inputs have already been migrated; the old workbooks and old scripts are not needed for forecasting or tests. To import other original datasets, pass their location with `--root`.


The shared runner replaces manual copying between Excel and text files for all 55 regions. Original scripts, workbooks, text inputs and manually assembled reports are preserved.

## Run on Windows

From this directory, use the prepared environment:

```powershell
.\.venv\Scripts\python.exe run_forecasts.py
.\.venv\Scripts\python.exe build_reports.py --year 2026
```

The first command reads every region's `input/observations.csv`. The second collects the saved forecasts into `reports/output-2026`, with a PDF, PowerPoint and summary CSV. PowerPoint generation uses the Codex bundled Node.js presentation library. `--pdf-only` generates a PDF without that library. `SEFM_NODE`, `SEFM_NODE_MODULES`, `SEFM_PRESENTATION_SKILL`, and `SEFM_PYTHON` can override its runtime paths.

For a fresh Python installation:

```powershell
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

Select a region with its exact folder name:

```powershell
.\.venv\Scripts\python.exe run_forecasts.py --region "1-Peel Sound Area"
.\.venv\Scripts\python.exe build_reports.py --year 2026 --region "1-Peel Sound Area"
```

## Annual update

1. Append the new observed year and DOY to each region's `input/observations.csv`. Keep the header `Year,DOY`. DOY means January 1 = 1, including leap day when applicable.
2. Run `run_forecasts.py`. The forecast year is the last observed year plus one. No edit to `year.txt`, means, scripts or workbook formulas is needed.
3. Run `build_reports.py --year YYYY` for that forecast year.

Years must be ascending, consecutive and unique. Missing or invalid observations cause an explicit regional failure; the runner continues with other regions and exits with a failure status. See `forecast_run_errors.json`. Report assembly rejects missing, stale or mixed-method results. Re-running forecasts replaces files with the same names inside that year's generated output folders. Earlier years and original files are retained. A repeated PowerPoint build uses a timestamped filename to preserve the previous deck.

## Regional products

Each `output-YYYY` contains `series.csv` (Year, DOY, Mean, RollingMean), `forecasts.csv`, `forecast.json`, `forecast.txt`, and five plots in PNG and PDF: history with both forecasts, observations and means, power spectrum, periodogram, and model forecasts. The forecast year is kept in the forecast files, rather than added as a fictitious observation in `series.csv`.

The PDF report has a summary and three pages per region. The PowerPoint has three slides per region plus a cover. History and forecast charts are editable; spectral diagnostics are embedded images. Numerical data and method notes remain available in each region's output and in slide notes. Reports are newly assembled presentations of the supplied products, not copies of the old manual slide layout.

PowerPoint chart values use six decimal places to fit Excel's embedded-workbook precision. Full calculated precision is retained in the regional JSON and CSV outputs. The bundled PowerPoint image renderer currently crashes on this Windows runtime. Package, layout and chart-data validation are performed, but the resulting deck needs visual review in PowerPoint. Set `SEFM_RENDER_SLIDES=1` to request private PNG previews on a runtime with working rendering.

## Calculation choices and differences from the original programs

* **Two-year mean:** `(DOY[t-1] + DOY[t]) / 2`, stored against year `t`. The workbooks and user notes establish this preprocessing step. It is the latest two available observations at year `t`, not two observations excluding `t`. The supplied paper's Methodology describes an FFT-selected moving average of observation dates but does not explicitly prescribe a two-year pre-average. We retain the workbooks' two-stage smoothing for continuity.
* **Default smoothing, selected by the user:** FFT the observed annual series, exclude the zero-frequency bin, choose the largest-power bin and invert its frequency. Round that period to the nearest whole year, with halves rounded upward. Apply that window to the two-year means. Use expanding averages before the window fills, without rounding intermediate values. The first year has neither a two-year mean nor a rolling mean. With a record-length period the complete series remains in the expanding stage; the program flags that condition. A constant series has no peak and uses a one-year window.
* **Forecast timing:** The default quadratic evaluates the next annual index `L+1`, and OFBM uses the first recursive prediction. Original programs evaluate the quadratic at the final historical index `L` and select recursive prediction `xp[L]`, which is `L+1` steps ahead. These original choices can be reproduced with `--timing legacy`.
* **Meaning of FFT forecast:** The supplied code uses a quadratic polynomial on the smoothed series for its so-called FFT forecast. This implementation preserves that method and labels it `Quadratic / legacy FFT`. It does not claim to perform Fourier extrapolation.
* **OFBM coefficients:** The supplied coefficient algorithm is retained exactly, including its unusual nested updates and buffer initialization. An outer loop that recalculates the identical result is removed. This preserves reproducibility, but does not establish that the translated filter implements the published equations correctly. Scientific revalidation and retrospective forecast-skill assessment are separate work.
* **Dates:** Raw fractional forecast DOYs are retained. Calendar dates and displayed integer DOYs use one shared half-up rounding rule. Predictions outside the forecast year's calendar range are flagged and receive no calendar date.
* **Spectrum:** The implementation uses the real FFT's positive bins, including Nyquist for even records, equivalent to the original spectral peak calculation.

Workbook smoothing can be selected with `--smoothing workbook`. This uses the contiguous window found in column E but applies consistent expanding averages to the complete data record, so it need not reproduce workbooks with omitted early rows or malformed formulas. A workbook with no unambiguous window is rejected in that mode.

Exact original forecast arithmetic on the retained `input/legacy_rolling_mean.csv` snapshot is available with:

```powershell
.\.venv\Scripts\python.exe run_forecasts.py --smoothing legacy --timing legacy
```

This replaces that year's generated outputs with compatibility results. Run the default command again to restore FFT-window results. Legacy mode requires unchanged migrated CSV inputs, as the legacy smoothing snapshot is not updated automatically.

## Migration and audit

`migrate_inputs.py` reads columns A (Year) and C (Obs) from each workbook and creates the input CSV and `region.json` metadata. It skips existing CSVs so annual edits are not overwritten. `migration_audit.json` records sources and warnings. Most workbook observations match `yobs.txt`; Lake Melville VCPI differs, and the workbook is used as requested. Region-specific start years are read from the source records, correcting hardcoded plotting-year discrepancies in some originals.

The source audit found identical numerical forecast sections across all 55 scripts, but different starting years, rolling windows and excluded early smoothing rows. In particular the Cape Lisburne open drift workbook uses comma-separated endpoint averages in column E, rather than contiguous ranges. The default FFT mode avoids inheriting that formula issue. The original files remain available for comparison.

## Verification

```powershell
.\.venv\Scripts\python.exe -m unittest -v
```

Tests compare compatibility mode with numerical baselines extracted from the original scripts during relocation. The baselines include the original script names and hashes, so the old scripts are not required to run this version. Additional tests check an FFT with a known period, expanding-window alignment, forecast indices, leap years, rounding, and invalid inputs. Numerical agreement with old code is a regression check, not evidence of predictive accuracy.
