"""Shared SEFM calculations and plotting. No legacy scripts are executed."""
from __future__ import annotations

import calendar
import csv
import hashlib
import json
import math
import re
from datetime import date, timedelta
from pathlib import Path

import numpy as np

BASE = Path(__file__).resolve().parent


def region_key(path):
    match = re.match(r"(\d+)", path.name)
    return (int(match[1]) if match else 10000, path.name)


def regions(root=BASE):
    return sorted((p for p in Path(root).iterdir() if p.is_dir() and
                   ((p / 'input/observations.csv').exists() or (p / 'yobs.txt').exists())),
                  key=region_key)


def read_input(path):
    with Path(path).open(newline='', encoding='utf-8-sig') as stream:
        reader = csv.DictReader(stream)
        if reader.fieldnames != ['Year', 'DOY']:
            raise ValueError(f'{path}: expected exactly the columns Year,DOY')
        records = []
        for line, row in enumerate(reader, 2):
            try:
                year_float, doy = float(row['Year']), float(row['DOY'])
                year = int(year_float)
                if year != year_float or not 1 <= year <= 9998:
                    raise ValueError('invalid year')
                if not math.isfinite(doy) or not 1 <= doy <= (366 if calendar.isleap(year) else 365):
                    raise ValueError('DOY outside calendar year')
            except (ValueError, TypeError, OverflowError) as exc:
                raise ValueError(f'{path}, row {line}: {exc}') from exc
            records.append((year, doy))
    if len(records) < 5:
        raise ValueError(f'{path}: at least five annual observations required')
    years, obs = np.asarray(records).T
    if not np.all(np.diff(years) == 1):
        raise ValueError(f'{path}: years must be unique, ascending and consecutive; missing years cannot be interpolated silently')
    return years.astype(int), obs


def spectrum(obs):
    n = len(obs)
    freq = np.fft.rfftfreq(n)[1:]
    power = np.abs(np.fft.rfft(obs)[1:]) ** 2
    if np.ptp(obs) == 0:
        return freq, power, None
    return freq, power, float(1 / freq[np.argmax(power)])


def smooth(obs, window, expanding=True):
    """Two-year mean at year t uses observations t-1 and t, then smooth means."""
    if not isinstance(window, int) or window < 1:
        raise ValueError('rolling window must be a positive integer')
    means = (np.asarray(obs[:-1]) + obs[1:]) / 2
    rolling = np.full(len(obs), np.nan)
    for i in range(len(means)):
        if expanding or i + 1 >= window:
            rolling[i + 1] = np.mean(means[max(0, i + 1 - window):i + 1])
    return np.r_[np.nan, means], rolling


def legacy_predictions(yz):
    """Preserve original coefficient updates, including their unusual nesting.

    The repeated outer loop produces identical results, so run it once.
    Returns all L+1 recursive predictions, allowing exact legacy comparison.
    """
    yz = np.asarray(yz, dtype=float)
    n = len(yz)
    order = n // 2
    wk1, wk2 = np.zeros(n), np.zeros(n)
    wk1[0], wk2[-1] = yz[0], yz[-1]
    for j in range(1, n - 1):
        wk1[j], wk2[j - 1] = yz[j], yz[j]
    cof, wkm = np.zeros(order), np.zeros(order)
    for k in range(order):
        num = den = 0.0
        for j in range(n - k):
            num += wk1[j] * wk2[j]
            den += wk1[j] ** 2 + wk2[j] ** 2
        cof[k] = 2 * num / den if den else 0.0
        for i in range(k):
            cof[i] = wkm[i] - cof[k] * wkm[k - i - 1]
        for i in range(k + 1):
            wkm[i] = cof[i]
            for j in range(n - k - 1):
                wk1[j] -= wkm[k] * wk2[j]
                wk2[j] = wk2[j + 1] - wkm[k] * wk1[j + 1]
    reg = yz[::-1][:order].copy()
    prediction = []
    for _ in range(n + 1):
        value = 0.0
        for k in range(order):
            value += cof[k] * reg[k]
        reg[1:] = reg[:-1].copy()
        reg[0] = value
        prediction.append(value)
    return np.asarray(prediction)


def calendar_value(year, value):
    """Round half up once for a consistent integer DOY and calendar date."""
    rounded = int(math.floor(value + 0.5))
    valid = 1 <= rounded <= (366 if calendar.isleap(year) else 365)
    return dict(doy=float(value), rounded_doy=rounded,
                date=(date(year, 1, 1) + timedelta(days=rounded - 1)).isoformat() if valid else None,
                valid=valid)


def calculate(folder, smoothing='fft', timing='next-year'):
    folder = Path(folder)
    source = folder / 'input/observations.csv'
    years, obs = read_input(source)
    meta_path = folder / 'input/region.json'
    meta = json.loads(meta_path.read_text(encoding='utf-8')) if meta_path.exists() else {}
    freq, power, peak = spectrum(obs)
    warnings = list(meta.get('warnings', []))
    if smoothing == 'legacy':
        rolling_values = np.loadtxt(folder / 'input/legacy_rolling_mean.csv', ndmin=1)
        if int(years[-1]) != meta.get('migration_last_year') or hashlib.sha256(source.read_bytes()).hexdigest() != meta.get('input_sha256'):
            raise ValueError('legacy mode requires the unchanged migrated input; legacy_rolling_mean.csv is a historical snapshot')
        if len(rolling_values) > len(years):
            raise ValueError('legacy smoothing has more values than observation years')
        means, _ = smooth(obs, 1)
        rolling = np.r_[np.full(len(obs) - len(rolling_values), np.nan), rolling_values]
        window = meta.get('workbook_window')
        warnings.append('Legacy rolling-mean snapshot used; values are not recomputed.')
    else:
        if smoothing == 'workbook':
            window = meta.get('workbook_window')
            if not window:
                raise ValueError('no unambiguous contiguous workbook window; select fft smoothing')
        else:
            window = max(1, int(math.floor(peak + 0.5))) if peak else 1
        means, rolling = smooth(obs, window, expanding=True)
        rolling_values = rolling[np.isfinite(rolling)]
    if len(rolling_values) < 3:
        raise ValueError('at least three smoothed values are required')
    n = len(rolling_values)
    x = np.arange(1, n + 1)
    fit = np.polyfit(x, rolling_values, 2)
    recursive = legacy_predictions(rolling_values)
    # Historical programs evaluate the polynomial at L and recursive output at L.
    quadratic = float(np.polyval(fit, n if timing == 'legacy' else n + 1))
    ofbm = float(recursive[-1] if timing == 'legacy' else recursive[0])
    if not np.all(np.isfinite([quadratic, ofbm])):
        raise ValueError('non-finite forecast produced by legacy filter')
    if peak and peak >= len(obs) - 1:
        warnings.append('Dominant FFT period spans nearly the entire record; smoothing uses expanding averages throughout.')
    warnings.append('FFT label refers to a quadratic forecast after FFT-selected smoothing, not Fourier extrapolation.')
    warnings.append('OFBM retains the legacy coefficient algorithm; this implementation does not establish forecast skill.')
    result = dict(region=meta.get('region', re.sub(r'^\d+[\s-]*', '', folder.name)),
                  folder=folder.name, forecast_year=int(years[-1] + 1),
                  observation_start=int(years[0]), observation_end=int(years[-1]),
                  observations=len(obs), smoothing=smoothing, timing=timing,
                  fft_peak_period_years=peak, rolling_window_years=window,
                  quadratic_fft=calendar_value(int(years[-1] + 1), quadratic),
                  ofbm=calendar_value(int(years[-1] + 1), ofbm), warnings=warnings,
                  input_sha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                  history=dict(years=years.tolist(), observations=obs.tolist(),
                               mean=[float(v) if np.isfinite(v) else None for v in means],
                               rolling_mean=[float(v) if np.isfinite(v) else None for v in rolling],
                               quadratic_fit=[None] * (len(years)-n) + np.polyval(fit,x).tolist()))
    for key in ('quadratic_fft', 'ofbm'):
        if not result[key]['valid']:
            warnings.append(f'{key}: forecast lies outside the forecast calendar year; no date is assigned.')
    return result, (years, obs, means, rolling, freq, power, fit)


def report_text(result):
    r = result
    lines = ['Statistical Events Forecasting Model', r['region'],
             f"Forecast year: {r['forecast_year']}",
             f"Observed years: {r['observation_start']}-{r['observation_end']} ({r['observations']} observations)",
             f"Smoothing: {r['smoothing']}; timing: {r['timing']}",
             f"FFT peak period: {r['fft_peak_period_years']} years; rolling window: {r['rolling_window_years']} years"]
    for key, title in [('quadratic_fft', 'Quadratic / legacy FFT'), ('ofbm', 'Optimal Filtering-Based Model (OFBM)')]:
        v = r[key]
        lines.append(f"{title}: DOY {v['doy']:.3f}; rounded DOY {v['rounded_doy']}; {v['date'] or 'OUTSIDE CALENDAR YEAR'}")
    lines += ['', 'Method notes:'] + ['- ' + w for w in r['warnings']]
    return '\n'.join(lines) + '\n'


def write_outputs(folder, result, arrays):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib.ticker import MaxNLocator

    out = Path(folder) / f"output-{result['forecast_year']}"
    out.mkdir(exist_ok=True)
    (out / 'forecast.json').write_text(json.dumps(result, indent=2, allow_nan=False), encoding='utf-8')
    (out / 'forecast.txt').write_text(report_text(result), encoding='utf-8')
    years, obs, means, rolling, freq, power, fit = arrays
    with (out / 'series.csv').open('w', newline='', encoding='utf-8') as stream:
        writer = csv.writer(stream)
        writer.writerow(['Year', 'DOY', 'Mean', 'RollingMean'])
        writer.writerows([[int(y)] + [float(v) if np.isfinite(v) else '' for v in (o,m,r)] for y,o,m,r in zip(years,obs,means,rolling)])
    with (out / 'forecasts.csv').open('w', newline='', encoding='utf-8') as stream:
        writer = csv.writer(stream); writer.writerow(['Year','Method','DOY','RoundedDOY','Date'])
        for method in ('quadratic_fft','ofbm'):
            v=result[method]; writer.writerow([result['forecast_year'],method,v['doy'],v['rounded_doy'],v['date']])
    plt.rcParams.update({'font.size':11, 'axes.spines.top':False, 'axes.spines.right':False,
                         'axes.titleweight':'bold', 'axes.labelcolor':'#334155', 'grid.alpha':0.22})
    title = result['region']
    def new(subtitle):
        fig, ax = plt.subplots(figsize=(12,6.5), layout='constrained')
        ax.set_title(title + '\n' + subtitle, loc='left', fontsize=14, pad=15)
        ax.grid(True); return fig, ax
    def save(fig, name):
        fig.savefig(out / (name+'.png'), dpi=160)
        fig.savefig(out / (name+'.pdf'))
        plt.close(fig)
    def historical(ax):
        ax.plot(years, obs, color='#94a3b8', lw=1.4, label='Observed event DOY')
        ax.plot(years, means, color='#0284c7', lw=1.8, label='Two-year mean (current + preceding year)')
        ax.plot(years, rolling, color='#0f766e', lw=2.3, label=f"Rolling mean of two-year means ({result['rolling_window_years']} yr window)")
        ax.set(xlabel='Observation / forecast year', ylabel='Event day of year (DOY, Jan 1 = 1)')
        ax.xaxis.set_major_locator(MaxNLocator(integer=True))
    def forecasts(ax):
        fy=result['forecast_year']
        for key, label, color, marker in [('quadratic_fft','Quadratic / legacy FFT','#b45309','D'),('ofbm','OFBM','#be123c','*')]:
            v=result[key]
            ax.scatter([fy],[v['doy']],s=110,c=color,marker=marker,zorder=5,
                       label=f"{label}: DOY {v['rounded_doy']} ({v['date'] or 'invalid date'})")
        ax.axvline(fy-0.5,color='#64748b',ls='--',lw=1)
        ax.set_xlim(years[0]-1,fy+2)
    fig,ax=new(f"History and {result['forecast_year']} forecasts")
    historical(ax); forecasts(ax)
    ax.legend(loc='best',fontsize=8.5); save(fig,'history_forecast')
    fig,ax=new('Observed events and calculated means')
    historical(ax); ax.legend(fontsize=9); save(fig,'observations_means')
    fig,ax=new('FFT power spectrum of annual observed event dates')
    ax.plot(freq,power,color='#0284c7'); ax.set(xlabel='Frequency (cycles per year)',ylabel='FFT power (DOY squared)')
    save(fig,'power_spectrum')
    fig,ax=new('FFT periodogram')
    period=1/freq; ax.plot(period[::-1],power[::-1],color='#0f766e')
    peak=result['fft_peak_period_years']
    if peak:
        ax.axvline(peak,color='#b45309',ls='--',label=f'Dominant period: {peak:.2f} years'); ax.legend()
    ax.set(xlabel='Period (years per cycle)',ylabel='FFT power (DOY squared)'); save(fig,'periodogram')
    fig,ax=new(f"Smoothed series and {result['forecast_year']} model forecasts")
    valid=np.isfinite(rolling); x=np.arange(1,int(valid.sum())+1)
    ax.plot(years[valid],rolling[valid],color='#0f766e',label='Rolling mean')
    ax.plot(years[valid],np.polyval(fit,x),color='#b45309',ls='--',label='Quadratic fit')
    forecasts(ax); ax.set(xlabel='Observation / forecast year',ylabel='Event day of year (DOY)'); ax.legend(fontsize=9)
    save(fig,'model_forecasts')
    return out
