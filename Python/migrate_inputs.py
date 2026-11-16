"""One-time non-destructive migration of workbook Year/Obs columns into CSV."""
import argparse
import ast
import csv
import hashlib
import json
import re
import numpy as np
import openpyxl
from sefm import BASE, read_input, regions


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--root',type=__import__('pathlib').Path,default=BASE)
    args=p.parse_args(); audit=[]
    for folder in regions(args.root):
        dest=folder/'input/observations.csv'
        if dest.exists():
            print(f'SKIP existing {dest}'); continue
        books=list(folder.glob('*.xlsx'))
        if len(books)!=1: raise ValueError(f'{folder}: expected one workbook')
        wb=openpyxl.load_workbook(books[0],data_only=True)
        ws=wb.active
        data=[(int(r[0]),float(r[2])) for r in ws.values if isinstance(r[0],(int,float)) and isinstance(r[2],(int,float))]
        wb.close()
        formulas=openpyxl.load_workbook(books[0],data_only=False)
        windows=set()
        for row in formulas.active.values:
            for a,b in re.findall(r'D(\d+):D(\d+)',str(row[4]) if len(row)>4 else '', re.I):
                windows.add(int(b)-int(a)+1)
        formulas.close()
        warnings=[]; legacy=np.loadtxt(folder/'yobs.txt',ndmin=1)
        if len(legacy)!=len(data) or not np.allclose(legacy,[v for _,v in data]):
            warnings.append('Workbook observations differ from yobs.txt; CSV uses the workbook as requested.')
        script=next(folder.glob('test*.py'))
        prints=[]
        for node in ast.walk(ast.parse(script.read_text(encoding='utf-8-sig'))):
            if isinstance(node,ast.Call) and isinstance(node.func,ast.Name) and node.func.id=='print' and node.args and isinstance(node.args[0],ast.Constant) and isinstance(node.args[0].value,str):
                prints.append(node.args[0].value)
        label=re.sub(r'^\d+[\s-]*','',folder.name)
        # Folder names are authoritative: some original print labels were copied
        # from unrelated areas (Mackenzie Bay and Southern Davis Strait).
        dest.parent.mkdir(exist_ok=True)
        with dest.open('w',newline='',encoding='utf-8') as stream:
            writer=csv.writer(stream); writer.writerow(['Year','DOY']); writer.writerows(data)
        read_input(dest)
        meta=dict(region=label,source_workbook=books[0].name,source_columns='A (Year), C (Obs)',
                  migration_last_year=data[-1][0],workbook_window=next(iter(windows)) if len(windows)==1 else None,
                  input_sha256=hashlib.sha256(dest.read_bytes()).hexdigest(),warnings=warnings)
        (dest.parent/'region.json').write_text(json.dumps(meta,indent=2),encoding='utf-8')
        audit.append(dict(folder=folder.name,**meta))
        print(f'CREATED {folder.name}: {len(data)} observations',flush=True)
    if audit:
        (args.root/'migration_audit.json').write_text(json.dumps(audit,indent=2),encoding='utf-8')


if __name__=='__main__': main()
