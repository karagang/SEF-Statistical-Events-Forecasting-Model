"""Run all regional forecasts, or select folders with --region."""
import argparse
import json
from pathlib import Path
from sefm import BASE, calculate, regions, write_outputs


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--root', type=Path, default=BASE)
    p.add_argument('--region', action='append', help='Exact region folder name; repeat to select several')
    p.add_argument('--smoothing', choices=['fft','workbook','legacy'], default='fft')
    p.add_argument('--timing', choices=['next-year','legacy'], default='next-year')
    args = p.parse_args()
    folders = regions(args.root)
    if args.region:
        unknown = set(args.region) - {d.name for d in folders}
        if unknown: p.error('Unknown region(s): ' + ', '.join(sorted(unknown)))
        folders = [d for d in folders if d.name in args.region]
    if not folders: p.error('No region folders found')
    errors = []
    for folder in folders:
        try:
            result, arrays = calculate(folder,args.smoothing,args.timing)
            out = write_outputs(folder,result,arrays)
            print(f"OK {folder.name}: {out.name}",flush=True)
        except Exception as exc:
            errors.append({'region':folder.name,'error':str(exc)})
            print(f'FAILED {folder.name}: {exc}',flush=True)
    (args.root/'forecast_run_errors.json').write_text(json.dumps(errors,indent=2),encoding='utf-8')
    print(f'{len(folders)-len(errors)}/{len(folders)} regions completed')
    return bool(errors)


if __name__ == '__main__':
    raise SystemExit(main())
