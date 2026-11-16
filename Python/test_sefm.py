"""Numerical regression and input-contract checks: python -m unittest -v."""
import json
import csv
import tempfile
import unittest
from pathlib import Path
import numpy as np
from sefm import BASE, calendar_value, calculate, legacy_predictions, read_input, regions, smooth, spectrum


class ForecastTests(unittest.TestCase):
    def test_legacy_regression_all_regions(self):
        for folder in regions():
            with self.subTest(region=folder.name):
                baseline=json.loads((BASE/'reference/legacy_regression.json').read_text(encoding='utf-8'))[folder.name]
                values=np.loadtxt(folder/'input/legacy_rolling_mean.csv',ndmin=1)
                np.testing.assert_allclose(legacy_predictions(values),baseline['predictions'],rtol=1e-12,atol=1e-9)
                r,_=calculate(folder,'legacy','legacy')
                self.assertAlmostEqual(r['quadratic_fft']['doy'],baseline['quadratic'],places=8)
                self.assertAlmostEqual(r['ofbm']['doy'],baseline['predictions'][-1],places=8)

    def test_smoothing_alignment(self):
        means,rolling=smooth(np.array([10.,20.,30.,40.]),2)
        np.testing.assert_allclose(means[1:],[15,25,35])
        np.testing.assert_allclose(rolling[1:],[15,20,30])
        self.assertTrue(np.isnan(rolling[0]))

    def test_fft_known_period(self):
        obs=200+10*np.cos(2*np.pi*np.arange(40)/5)
        self.assertAlmostEqual(spectrum(obs)[2],5)
        self.assertIsNone(spectrum(np.full(10,200.))[2])

    def test_next_year_targets(self):
        r,arrays=calculate(BASE/'1-Peel Sound Area')
        rolling=arrays[3];values=rolling[np.isfinite(rolling)]
        self.assertEqual(r['forecast_year'],2026)
        self.assertAlmostEqual(r['ofbm']['doy'],legacy_predictions(values)[0])
        self.assertAlmostEqual(r['quadratic_fft']['doy'],np.polyval(arrays[-1],len(values)+1))

    def test_calendar_and_rounding(self):
        self.assertEqual(calendar_value(2024,60)['date'],'2024-02-29')
        self.assertEqual(calendar_value(2025,60)['date'],'2025-03-01')
        self.assertEqual(calendar_value(2026,200.5)['rounded_doy'],201)
        self.assertFalse(calendar_value(2026,366)['valid'])

    def test_reject_bad_input(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'input.csv'
            for rows in [[(2020,200),(2021,200),(2021,201),(2023,200),(2024,200)],
                         [(2020,200),(2021,float('nan')),(2022,200),(2023,200),(2024,200)],
                         [(2020,200),(2021,366),(2022,200),(2023,200),(2024,200)]]:
                with path.open('w',newline='') as f:
                    w=csv.writer(f);w.writerow(['Year','DOY']);w.writerows(rows)
                with self.assertRaises(ValueError):read_input(path)


if __name__=='__main__':unittest.main()
