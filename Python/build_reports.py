"""Collect saved region forecasts into a PDF and an editable PowerPoint deck."""
import argparse
import csv
import json
import os
import shutil
import subprocess
from pathlib import Path
from xml.sax.saxutils import escape

from sefm import BASE, region_key


def make_pdf(records, destination):
    from reportlab.lib import colors
    from reportlab.lib.pagesizes import landscape, A4
    from reportlab.lib.styles import getSampleStyleSheet
    from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Image, PageBreak, Table, TableStyle
    styles=getSampleStyleSheet()
    styles['Title'].textColor=colors.HexColor('#123047')
    story=[Paragraph(f"Statistical Events Forecasting Model<br/>{records[0]['forecast_year']} Arctic season",styles['Title']),Spacer(1,24),
           Paragraph(f"{len(records)} regional event forecasts",styles['Heading2']),
           Paragraph('Forecast guidance from historical event dates. Review against current conditions before operational use.',styles['BodyText']),Spacer(1,18),
           Paragraph('The quadratic forecast retains the historical FFT label. FFT selects the smoothing window; the forecast itself is a polynomial extrapolation. OFBM preserves the coefficient updates in the supplied scripts. No forecast accuracy claim is made.',styles['BodyText']),Spacer(1,18),
           Paragraph('Dates and whole-day DOY values use the same rounding rule. Early rolling means use all available two-year means until the selected window fills. Regional pages state smoothing and forecast timing.',styles['BodyText'])]
    for start in range(0,len(records),14):
        story += [PageBreak(),Paragraph('Forecast dates',styles['Title'])]
        data=[['Area / event','Quadratic / FFT','OFBM']]
        for r in records[start:start+14]:
            data.append([Paragraph(escape(r['region']),styles['BodyText']),r['quadratic_fft']['date'] or 'Outside year',r['ofbm']['date'] or 'Outside year'])
        table=Table(data,colWidths=[440,130,130],repeatRows=1)
        table.setStyle(TableStyle([('BACKGROUND',(0,0),(-1,0),colors.HexColor('#123047')),('TEXTCOLOR',(0,0),(-1,0),colors.white),('VALIGN',(0,0),(-1,-1),'MIDDLE'),('BOTTOMPADDING',(0,0),(-1,-1),8),('TOPPADDING',(0,0),(-1,-1),8),('LINEBELOW',(0,1),(-1,-1),0.3,colors.HexColor('#cbd5e1'))]))
        story.append(table)
    for r in records:
        folder=Path(r['_output'])
        for name in ['history_forecast','periodogram','model_forecasts']:
            story += [PageBreak(),Paragraph(escape(r['region']),styles['Heading2']),
                      Paragraph(f"{r['forecast_year']} forecast | {r['smoothing']} smoothing | {r['rolling_window_years']}-year window | {r['timing']} timing",styles['BodyText']),Spacer(1,8),
                      Image(str(folder/(name+'.png')),width=700,height=379)]
            if name=='history_forecast':
                story.append(Paragraph('Quadratic / FFT: '+str(r['quadratic_fft']['date'])+'; OFBM: '+str(r['ofbm']['date']),styles['BodyText']))
            if name=='periodogram':
                story.append(Paragraph('Full frequency spectrum is saved in the regional output folder as power_spectrum.png.',styles['BodyText']))
    def footer(canvas,doc):
        canvas.setFont('Helvetica',9);canvas.setFillColor(colors.HexColor('#64748b'))
        canvas.drawString(40,20,'SEFM | Historical statistical guidance');canvas.drawRightString(800,20,str(doc.page))
    SimpleDocTemplate(str(destination),pagesize=landscape(A4),rightMargin=40,leftMargin=40,topMargin=32,bottomMargin=36).build(story,onFirstPage=footer,onLaterPages=footer)


def find_node():
    configured=os.environ.get('SEFM_NODE')
    return configured or shutil.which('node') or str(Path.home()/'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe')


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--root',type=Path,default=BASE)
    p.add_argument('--year',type=int,required=True)
    p.add_argument('--destination',type=Path,default=BASE/'reports')
    p.add_argument('--region',action='append')
    p.add_argument('--pdf-only',action='store_true')
    args=p.parse_args()
    # Derive expected membership from input folders so missing forecasts cannot disappear.
    from sefm import regions, read_input
    records=[]; missing=[]
    for folder in regions(args.root):
        if args.region and folder.name not in args.region: continue
        source=folder/'input/observations.csv'
        if not source.exists(): missing.append(f'{folder.name}: no input CSV');continue
        years,_=read_input(source)
        path=folder/f'output-{args.year}/forecast.json'
        if int(years[-1])+1 != args.year:
            missing.append(f'{folder.name}: input implies {int(years[-1])+1}, not {args.year}');continue
        if not path.exists():missing.append(str(path));continue
        r=json.loads(path.read_text(encoding='utf-8'))
        import hashlib
        if r['input_sha256']!=hashlib.sha256(source.read_bytes()).hexdigest():
            missing.append(f'{folder.name}: stale forecast; rerun forecasts');continue
        if r['forecast_year']!=args.year:raise ValueError(f'Wrong year in {path}')
        r['_output']=str(path.parent.resolve());records.append(r)
    if args.region:
        unknown=set(args.region)-{r['folder'] for r in records}
        if unknown:missing.append('Requested regions unavailable: '+', '.join(sorted(unknown)))
    if missing:raise ValueError('Cannot build a complete report:\n'+'\n'.join(missing))
    if not records:raise ValueError('No forecasts found')
    settings={(r['smoothing'],r['timing']) for r in records}
    if len(settings)>1:raise ValueError('Mixed smoothing/timing settings; rerun all selected regions consistently')
    target=args.destination/f'output-{args.year}';target.mkdir(parents=True,exist_ok=True)
    with (target/'forecast_summary.csv').open('w',newline='',encoding='utf-8') as stream:
        writer=csv.writer(stream);writer.writerow(['Region','ForecastYear','FFT_PeakYears','WindowYears','Quadratic_DOY','Quadratic_Date','OFBM_DOY','OFBM_Date'])
        for r in records:writer.writerow([r['region'],args.year,r['fft_peak_period_years'],r['rolling_window_years'],r['quadratic_fft']['doy'],r['quadratic_fft']['date'],r['ofbm']['doy'],r['ofbm']['date']])
    private=args.root/'_report_build';private.mkdir(exist_ok=True)
    manifest=private/f'manifest-{args.year}.json';manifest.write_text(json.dumps(records,indent=2),encoding='utf-8')
    pdf=target/f'SEFM-{args.year}.pdf'
    make_pdf(records,pdf);print(f'Created {pdf}',flush=True)
    if not args.pdf_only:
        subprocess.run([find_node(),str(BASE/'report_deck.mjs'),str(manifest.resolve()),str((target/f'SEFM-{args.year}.pptx').resolve())],check=True)


if __name__=='__main__':main()
