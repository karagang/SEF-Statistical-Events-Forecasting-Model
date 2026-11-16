// Called by build_reports.py. Uses the Codex bundled presentation authoring library.
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {pathToFileURL} from 'node:url';
import {createRequire} from 'node:module';
const modules=process.env.SEFM_NODE_MODULES || path.join(os.homedir(),'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules');
process.env.RUNTIME_NODE_MODULES=modules;
const require=createRequire(path.join(modules,'__sefm__.cjs'));
const {Presentation,PresentationFile}=await import(pathToFileURL(require.resolve('@oai/artifact-tool')).href);
const [manifestArg,outputArg]=process.argv.slice(2);
const manifest=path.resolve(manifestArg),output=path.resolve(outputArg);
const rows=JSON.parse(await fs.readFile(manifest,'utf8'));
const pres=Presentation.create({slideSize:{width:1280,height:720}});
const native=[];
function text(slide,value,x,y,w,h,size=25,color='#123047'){
 const s=slide.shapes.add({geometry:'textbox',position:{left:x,top:y,width:w,height:h},fill:'none',line:{fill:'none',width:0}});
 s.text=value;s.text.style={fontFamily:'Arial',fontSize:size,color};return s;
}
function page(title,subtitle){
 const slide=pres.slides.add();slide.background.fill='#ffffff';
 text(slide,title,55,26,1170,75,32);
 text(slide,subtitle,55,105,1170,42,20,'#475569');
 text(slide,`SEFM ${rows[0].forecast_year}    ${pres.slides.items.length}`,55,680,1170,25,14,'#64748b');
 return slide;
}
function chart(slide,categories,series,title,x=60,y=165,w=1160,h=480){
 const cleaned=series.map(s=>{
  const valid=s.values.map((v,i)=>[v,i]).filter(([v])=>v!==null && Number.isFinite(v));
  return {...s,values:valid.map(([v])=>Number(v.toFixed(6))),xValues:valid.map(([,i])=>Number(categories[i])),marker:s.marker || {symbol:'none'},
   line:s.line || {fill:s.marker?.fill || '#475569',width:0}};
 });
 slide.charts.add('scatter',{position:{left:x,top:y,width:w,height:h},title,
 titleTextStyle:{typeface:'Arial',fontSize:20},series:cleaned,scatterOptions:{style:'lineWithMarkers'},
 hasLegend:true,legend:{position:'bottom',textStyle:{typeface:'Arial',fontSize:15}},displayBlanksAs:'gap',
 xAxis:{title:'Year',numberFormatCode:'0',min:Number(categories[0])-1,max:Number(categories.at(-1))+1,textStyle:{typeface:'Arial',fontSize:13}},
 yAxis:{title:'Event day of year (Jan 1 = 1)',numberFormatCode:'0',textStyle:{typeface:'Arial',fontSize:14}},
 chartFill:'#ffffff',plotAreaFill:'#ffffff'});
 native.push(pres.slides.items.length);
}
let slide=page('Statistical Events Forecasting Model',`${rows[0].forecast_year} Arctic season`);
text(slide,`${rows.length} regional event forecasts`,70,225,1100,65,42);
text(slide,'Historical statistical guidance for review against current conditions',70,325,1100,75,28);
text(slide,'FFT selects the smoothing period. The historical FFT forecast label refers to a quadratic fit. OFBM preserves the supplied filter algorithm.',70,445,1100,110,24);
slide.speakerNotes.text='Source: supplied regional CSV observations and forecast.json outputs. This report does not establish forecast skill.';
for(const r of rows){
 const h=r.history,fy=r.forecast_year;
 const subtitle=`${fy} forecast. ${r.smoothing} smoothing, ${r.rolling_window_years}-year window, ${r.timing} timing`;
 slide=page(r.region,subtitle);
 const cats=[...h.years,fy];
 const series=[
  {name:'Observed DOY',values:[...h.observations,null],line:{fill:'#94a3b8',width:1.5}},
  {name:'Two-year mean',values:[...h.mean,null],line:{fill:'#0284c7',width:2}},
  {name:'Rolling mean',values:[...h.rolling_mean,null],line:{fill:'#0f766e',width:2.5}},
  {name:`Quadratic / FFT ${r.quadratic_fft.date || 'outside year'}`,values:[...h.years.map(()=>null),r.quadratic_fft.doy],marker:{symbol:'diamond',size:11,fill:'#b45309'}},
  {name:`OFBM ${r.ofbm.date || 'outside year'}`,values:[...h.years.map(()=>null),r.ofbm.doy],marker:{symbol:'star',size:12,fill:'#be123c'}}];
 chart(slide,cats,series,'Observed events, means and forecasts');
 slide.speakerNotes.text=`Source: ${r._output}/forecast.json\n${r.warnings.join('\n')}`;
 slide=page(r.region,'FFT periodogram and power spectrum');
 // Diagnostic images preserve the exact numerical axes used by the forecast program.
 for(const [name,x] of [['power_spectrum',40],['periodogram',650]]){
  const bytes=await fs.readFile(path.join(r._output,name+'.png'));
  slide.images.add({blob:bytes.buffer.slice(bytes.byteOffset,bytes.byteOffset+bytes.byteLength),contentType:'image/png',alt:`${r.region}: ${name}`,fit:'contain',position:{left:x,top:180,width:590,height:360}});
 }
 text(slide,`Dominant period: ${r.fft_peak_period_years?.toFixed(2) ?? 'none'} years. Applied window: ${r.rolling_window_years} years.`,65,580,1140,60,24);
 slide.speakerNotes.text=`Source: ${r._output}. Diagnostic figures are embedded PNGs; the history and model charts are editable.`;
 slide=page(r.region,`Quadratic / FFT: ${r.quadratic_fft.date || 'outside year'}. OFBM: ${r.ofbm.date || 'outside year'}`);
 const fitSeries={name:'Quadratic fit',values:[...(h.quadratic_fit || h.years.map(()=>null)),null],line:{fill:'#b45309',width:2}};
 chart(slide,cats,[series[2],fitSeries,series[3],series[4]],'Smoothed events and forecast dates');
 slide.speakerNotes.text=`Source: ${r._output}/forecast.json\n${r.warnings.join('\n')}`;
 console.log(`Prepared ${r.folder}`);
}
const build=path.dirname(manifest);await fs.mkdir(build,{recursive:true});
// PNG previews are retained privately for visual review.
for(const [i,s] of (process.env.SEFM_RENDER_SLIDES === '1' ? pres.slides.items.entries() : [])){
 const png=await pres.export({slide:s,format:'png',scale:0.8});
 await fs.writeFile(path.join(build,`slide-${String(i+1).padStart(3,'0')}.png`),new Uint8Array(await png.arrayBuffer()));
}
const draft=path.join(build,'candidate.pptx');await(await PresentationFile.exportPptx(pres)).save(draft);
const skill=process.env.SEFM_PRESENTATION_SKILL || path.join(os.homedir(),'.codex/plugins/cache/openai-primary-runtime/presentations/26.1111.10930/skills/presentations');
const {finalizePresentation}=await import(pathToFileURL(path.join(skill,'container_tools/artifact_tool_utils.mjs')).href);
// The finalizer intentionally does not overwrite. Preserve earlier reports with timestamped names.
let deliveryPath=output;
try{await fs.access(deliveryPath);deliveryPath=output.replace(/\.pptx$/,`-${Date.now()}.pptx`);}catch{}
// The snapshot validator uses hard links, unavailable on some data drives.
// Stage validation on the system temporary volume, then copy the validated deck.
const validationWorkspace=await fs.mkdtemp(path.join(os.tmpdir(),'sefm-validate-'));
const validationCandidate=path.join(validationWorkspace,'candidate.pptx');await fs.copyFile(draft,validationCandidate);
const localOutput=path.join(validationWorkspace,'final');await fs.mkdir(localOutput,{recursive:true});
const finalPath=path.join(localOutput,`SEFM-${rows[0].forecast_year}.pptx`);
await finalizePresentation({workspaceDir:validationWorkspace,candidatePath:validationCandidate,finalPath,
 pythonExecutable:process.env.SEFM_PYTHON || path.join(os.homedir(),'.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'),
 integrityValidatorPath:path.join(skill,'container_tools/inspect_presentation_package_integrity.py'),
 layoutValidatorPath:path.join(skill,'container_tools/inspect_presentation_layout_geometry.py'),
 layoutArgs:['--expected-slide-size-emu','12192000,6858000','--validate-heading-fit'],
 requiredNativeChartOwnerSlides:[...new Set(native)],materializeLiteralChartWorkbooks:true,
 fontPolicy:{basis:'design',families:['Arial']},verifyArtifactToolImport:true,
 receiptPath:path.join(validationWorkspace,'deck.validation.json')});
await fs.copyFile(path.join(validationWorkspace,'deck.validation.json'),path.join(build,'deck.validation.json'));
await fs.copyFile(finalPath,deliveryPath);
console.log(`Created ${deliveryPath}`);
