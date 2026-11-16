using System.IO.Compression;
using System.Security;
using System.Text;

namespace Sefm;

public static class Reports
{
    public sealed record Item(Forecast Forecast, string Output);
    public static void Build(Options options)
    {
        if(!options.Year.HasValue)throw new ArgumentException("Reports require --year YYYY (or use the all command)");
        int year=options.Year.Value;var records=new List<Item>();var errors=new List<string>();
        foreach(string region in options.Selected())
        {
            try
            {
                string source=Path.Combine(region,"input","observations.csv");var (years,_)=Model.ReadInput(source);
                if(years[^1]+1!=year)throw new InvalidDataException($"Input implies forecast year {years[^1]+1}, not {year}");
                string output=Path.Combine(region,$"output-{year}");var r=Model.LoadJson<Forecast>(Path.Combine(output,"forecast.json"));
                if(r.ForecastYear!=year||r.Folder!=Path.GetFileName(region))throw new InvalidDataException("Wrong forecast year or region in saved output");
                if(r.InputSha256!=Model.Sha(source))throw new InvalidDataException("Stale forecast; rerun forecasts after editing observations");
                foreach(string plot in new[]{"history_forecast","periodogram","model_forecasts","power_spectrum"})
                    if(!File.Exists(Path.Combine(output,plot+".png")))throw new InvalidDataException("Missing plot: "+plot);
                records.Add(new(r,output));
            }
            catch(Exception e){errors.Add(Path.GetFileName(region)+": "+e.Message);}
        }
        if(errors.Count>0)throw new InvalidDataException("Cannot build a complete report:\n"+string.Join("\n",errors));
        if(records.Select(r=>(r.Forecast.Smoothing,r.Forecast.Timing)).Distinct().Count()!=1)
            throw new InvalidDataException("Mixed smoothing/timing settings; rerun all selected regions consistently");
        string target=Path.Combine(options.Destination??Path.Combine(options.Root,"reports"),$"output-{year}");Directory.CreateDirectory(target);
        var csv=new StringBuilder("Region,ForecastYear,FFT_PeakYears,WindowYears,Quadratic_DOY,Quadratic_Date,OFBM_DOY,OFBM_Date\n");
        foreach(var item in records)
        {
            var r=item.Forecast;csv.AppendLine(string.Join(",",new[]{Model.Csv(r.Region),year.ToString(),r.FftPeakPeriodYears?.ToString("G17",Model.Inv)??"",r.RollingWindowYears?.ToString()??"",Model.Number(r.QuadraticFft.Doy),r.QuadraticFft.Date??"",Model.Number(r.Ofbm.Doy),r.Ofbm.Date??""}));
        }
        File.WriteAllText(Path.Combine(target,"forecast_summary.csv"),csv.ToString(),Model.Utf8);
        string pdf=Path.Combine(target,$"SEFM-{year}.pdf"),pptx=Path.Combine(target,$"SEFM-{year}.pptx");
        MakePdf(records,pdf);Console.WriteLine("Created "+pdf);
        PowerPoint.Write(records,pptx);Console.WriteLine("Created "+pptx);
    }
    static void MakePdf(List<Item> records,string output)
    {
        int year=records[0].Forecast.ForecastYear;var pdf=new SimplePdf();var cover=pdf.AddPage();
        cover.Text("Statistical Events Forecasting Model",45,60,26,true);cover.Text($"{year} Arctic season",45,108,23,true);
        cover.Text($"{records.Count} regional event forecasts",45,178,18,true);
        cover.Text("Historical statistical guidance for review against current conditions.",45,225,14);
        cover.Text("FFT selects the smoothing period. The historical FFT forecast label refers to a quadratic fit.",45,268,12);
        cover.Text("OFBM preserves the coefficient algorithm in the supplied programs.",45,293,12);
        cover.Text("No forecast accuracy claim is made. Review regional method notes before operational use.",45,318,12);
        cover.Text("Calendar dates and integer DOYs use the same half-up rounding rule.",45,367,12);
        cover.Text("Early rolling means expand until the selected window fills.",45,392,12);
        int pageNumber=1;Footer(cover,pageNumber++);
        for(int start=0;start<records.Count;start+=14)
        {
            var page=pdf.AddPage();page.Text($"{year} forecast dates",40,30,22,true);
            page.Text("Area / event",40,83,12,true);page.Text("Quadratic / FFT",570,83,12,true);page.Text("OFBM",708,83,12,true);page.Line(40,105,802,105);
            int i=0;foreach(var item in records.Skip(start).Take(14))
            {
                var r=item.Forecast;double y=116+29*i++;page.Text(r.Region,40,y,10);
                page.Text(r.QuadraticFft.Date??"Outside year",570,y,10);page.Text(r.Ofbm.Date??"Outside year",708,y,10);page.Line(40,y+23,802,y+23);
            }
            Footer(page,pageNumber++);
        }
        foreach(var item in records)
            foreach(string plot in new[]{"history_forecast","periodogram","model_forecasts"})
            {
                var r=item.Forecast;var page=pdf.AddPage();page.Text(r.Region,40,20,15,true);
                page.Text($"{year} forecast. {r.Smoothing} smoothing. {r.RollingWindowYears}-year window. {r.Timing} timing.",40,46,10);
                page.Image(Path.Combine(item.Output,plot+".png"),35,75,772,482.5);
                Footer(page,pageNumber++);
            }
        pdf.Save(output);
    }
    static void Footer(SimplePdf.Page page,int number){page.Text("SEFM - C# - Historical statistical guidance",40,572,9,false,"64748B");page.Text(number.ToString(),785,572,9,false,"64748B");}
}

// Native Open XML presentation and embedded workbook writer. Uses only .NET ZIP/XML.
// Charts are editable scatter charts with independent year coordinates per series.
public static class PowerPoint
{
    const string A="http://schemas.openxmlformats.org/drawingml/2006/main",P="http://schemas.openxmlformats.org/presentationml/2006/main",R="http://schemas.openxmlformats.org/officeDocument/2006/relationships",C="http://schemas.openxmlformats.org/drawingml/2006/chart";
    const string RelNs="http://schemas.openxmlformats.org/package/2006/relationships",S="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static string E(string? s)=>SecurityElement.Escape(s??"")??"";
    static long Emu(double px)=>(long)Math.Round(px*9525);
    static string N(double n)=>n.ToString("0.######",Model.Inv);
    static string Head=>"<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>";
    static string Group=>"<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";
    static string Rel(string id,string type,string target)=>$"<Relationship Id=\"{id}\" Type=\"{R}/{type}\" Target=\"{E(target)}\"/>";
    static string Rels(IEnumerable<string> items)=>Head+$"<Relationships xmlns=\"{RelNs}\">{string.Join("",items)}</Relationships>";
    static string Transform(double x,double y,double w,double h)=>$"<a:xfrm><a:off x=\"{Emu(x)}\" y=\"{Emu(y)}\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm>";
    static string Text(int id,string text,double x,double y,double w,double h,double font=25,bool bold=false,string color="123047")
        =>$"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Text {id}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr>{Transform(x,y,w,h)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln><a:noFill/></a:ln></p:spPr><p:txBody><a:bodyPr wrap=\"square\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\"/><a:lstStyle/><a:p><a:pPr/><a:r><a:rPr lang=\"en-US\" sz=\"{(int)(font*75)}\" b=\"{(bold?1:0)}\"><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:latin typeface=\"Arial\"/></a:rPr><a:t>{E(text)}</a:t></a:r><a:endParaRPr lang=\"en-US\" sz=\"{(int)(font*75)}\"/></a:p></p:txBody></p:sp>";
    sealed class Slide
    {
        public readonly List<string> Elements=[],Relations=[];public string Notes="";int id=2;
        public void Text(string text,double x,double y,double w,double h,double size=25,bool bold=false,string color="123047")=>Elements.Add(PowerPoint.Text(id++,text,x,y,w,h,size,bold,color));
        public string Relationship(string type,string target){string rid="rId"+(Relations.Count+3);Relations.Add(Rel(rid,type,target));return rid;}
        public void Image(int imageId,double x,double y,double w,double h)
        {
            string rid=Relationship("image",$"../media/image{imageId}.png");int i=id++;
            Elements.Add($"<p:pic><p:nvPicPr><p:cNvPr id=\"{i}\" name=\"Diagnostic plot {i}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"{rid}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr>{Transform(x,y,w,h)}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");
        }
        public void Chart(int chartId)
        {
            string rid=Relationship("chart",$"../charts/chart{chartId}.xml");int i=id++;
            Elements.Add($"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{i}\" name=\"Editable event-date chart\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x=\"{Emu(55)}\" y=\"{Emu(170)}\"/><a:ext cx=\"{Emu(1170)}\" cy=\"{Emu(480)}\"/></p:xfrm><a:graphic><a:graphicData uri=\"{C}\"><c:chart xmlns:c=\"{C}\" r:id=\"{rid}\"/></a:graphicData></a:graphic></p:graphicFrame>");
        }
    }
    public static void Write(List<Reports.Item> records,string output)
    {
        var slides=new List<Slide>();var charts=new List<(List<Series> Series,string Title)>();var images=new List<string>();
        Slide Add(string title,string subtitle)
        {
            var slide=new Slide();slides.Add(slide);slide.Text(title,55,25,1170,80,32,true);slide.Text(subtitle,55,110,1170,45,20,false,"475569");
            slide.Text($"SEFM {records[0].Forecast.ForecastYear}    C#    {slides.Count}",55,680,1170,25,14,false,"64748B");return slide;
        }
        void Chart(Slide slide,List<Series> series,string title){charts.Add((series,title));slide.Chart(charts.Count);}
        var cover=Add("Statistical Events Forecasting Model",$"{records[0].Forecast.ForecastYear} Arctic season");
        cover.Text($"{records.Count} regional event forecasts",70,235,1120,70,42,true);cover.Text("Historical statistical guidance for review against current conditions",70,335,1120,85,28);
        cover.Text("FFT selects the smoothing period. The historical FFT label refers to a quadratic fit. OFBM preserves the supplied filter algorithm.",70,455,1120,115,24);
        cover.Notes="Source: this C# project's regional observations.csv and forecast.json files. This presentation makes no forecast-skill claim.";
        foreach(var item in records)
        {
            var r=item.Forecast;string note=$"Source: {r.Folder}/output-{r.ForecastYear}/forecast.json\n"+string.Join("\n",r.Warnings);
            var h=Add(r.Region,$"{r.ForecastYear} forecast. {r.Smoothing} smoothing, {r.RollingWindowYears}-year window, {r.Timing} timing");
            Chart(h,Model.HistorySeries(r),"Observed events, means and forecasts");h.Notes=note;
            var d=Add(r.Region,"FFT periodogram and power spectrum");
            images.Add(Path.Combine(item.Output,"power_spectrum.png"));d.Image(images.Count,45,195,585,366);
            images.Add(Path.Combine(item.Output,"periodogram.png"));d.Image(images.Count,650,195,585,366);
            d.Text($"Dominant period: {r.FftPeakPeriodYears?.ToString("F2",Model.Inv)??"none"} years. Applied window: {r.RollingWindowYears} years.",65,590,1140,50,23);
            d.Notes=note+"\nSpectral diagnostics are embedded images; history and model charts are editable.";
            var m=Add(r.Region,$"Quadratic / FFT: {r.QuadraticFft.Date??"outside year"}. OFBM: {r.Ofbm.Date??"outside year"}");
            Chart(m,Model.HistorySeries(r,true),"Smoothed events and forecast dates");m.Notes=note;
        }
        using var file=File.Create(output);using var zip=new ZipArchive(file,ZipArchiveMode.Create);
        void Put(string name,string xml){var entry=zip.CreateEntry(name,CompressionLevel.Optimal);using var stream=entry.Open();stream.Write(Model.Utf8.GetBytes(xml));}
        void Bytes(string name,byte[] bytes){var entry=zip.CreateEntry(name,CompressionLevel.Optimal);using var stream=entry.Open();stream.Write(bytes);}
        var types=new StringBuilder(Head+"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"xlsx\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet\"/>");
        void Type(string part,string type)=>types.Append($"<Override PartName=\"/{part}\" ContentType=\"{type}\"/>");
        Type("ppt/presentation.xml","application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml");
        Type("ppt/slideMasters/slideMaster1.xml","application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml");
        Type("ppt/slideLayouts/slideLayout1.xml","application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml");
        Type("ppt/notesMasters/notesMaster1.xml","application/vnd.openxmlformats-officedocument.presentationml.notesMaster+xml");
        Type("ppt/theme/theme1.xml","application/vnd.openxmlformats-officedocument.theme+xml");
        Put("_rels/.rels",Rels([Rel("rId1","officeDocument","ppt/presentation.xml")]));
        Put("ppt/presentation.xml",Head+$"<p:presentation xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:notesMasterIdLst><p:notesMasterId r:id=\"rId2\"/></p:notesMasterIdLst><p:sldIdLst>{string.Join("",Enumerable.Range(1,slides.Count).Select(i=>$"<p:sldId id=\"{255+i}\" r:id=\"rId{i+2}\"/>"))}</p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\" type=\"screen16x9\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/><p:defaultTextStyle/></p:presentation>");
        Put("ppt/_rels/presentation.xml.rels",Rels(new[]{Rel("rId1","slideMaster","slideMasters/slideMaster1.xml"),Rel("rId2","notesMaster","notesMasters/notesMaster1.xml")}.Concat(Enumerable.Range(1,slides.Count).Select(i=>Rel("rId"+(i+2),"slide",$"slides/slide{i}.xml")))));
        const string colorMap="<p:clrMap accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" bg1=\"lt1\" bg2=\"lt2\" folHlink=\"folHlink\" hlink=\"hlink\" tx1=\"dk1\" tx2=\"dk2\"/>";
        Put("ppt/slideMasters/slideMaster1.xml",Head+$"<p:sldMaster xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg><p:spTree>{Group}</p:spTree></p:cSld>{colorMap}<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>");
        Put("ppt/slideMasters/_rels/slideMaster1.xml.rels",Rels([Rel("rId1","slideLayout","../slideLayouts/slideLayout1.xml"),Rel("rId2","theme","../theme/theme1.xml")]));
        Put("ppt/slideLayouts/slideLayout1.xml",Head+$"<p:sldLayout xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\" type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\"><p:spTree>{Group}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        Put("ppt/slideLayouts/_rels/slideLayout1.xml.rels",Rels([Rel("rId1","slideMaster","../slideMasters/slideMaster1.xml")]));
        Put("ppt/notesMasters/notesMaster1.xml",Head+$"<p:notesMaster xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:cSld><p:spTree>{Group}</p:spTree></p:cSld>{colorMap}<p:notesStyle/></p:notesMaster>");
        Put("ppt/notesMasters/_rels/notesMaster1.xml.rels",Rels([Rel("rId1","theme","../theme/theme1.xml")]));
        Put("ppt/theme/theme1.xml",Theme());
        for(int i=1;i<=slides.Count;i++)
        {
            var slide=slides[i-1];Type($"ppt/slides/slide{i}.xml","application/vnd.openxmlformats-officedocument.presentationml.slide+xml");
            Type($"ppt/notesSlides/notesSlide{i}.xml","application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml");
            Put($"ppt/slides/slide{i}.xml",Head+$"<p:sld xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:cSld><p:spTree>{Group}{string.Join("",slide.Elements)}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
            Put($"ppt/slides/_rels/slide{i}.xml.rels",Rels(new[]{Rel("rId1","slideLayout","../slideLayouts/slideLayout1.xml"),Rel("rId2","notesSlide",$"../notesSlides/notesSlide{i}.xml")}.Concat(slide.Relations)));
            string paragraphs=string.Join("",slide.Notes.Split('\n').Select(s=>$"<a:p><a:r><a:rPr lang=\"en-US\" sz=\"1200\"><a:latin typeface=\"Arial\"/></a:rPr><a:t>{E(s)}</a:t></a:r></a:p>"));
            Put($"ppt/notesSlides/notesSlide{i}.xml",Head+$"<p:notes xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:cSld><p:spTree>{Group}<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Notes\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>{paragraphs}</p:txBody></p:sp></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>");
            Put($"ppt/notesSlides/_rels/notesSlide{i}.xml.rels",Rels([Rel("rId1","notesMaster","../notesMasters/notesMaster1.xml"),Rel("rId2","slide",$"../slides/slide{i}.xml")]));
        }
        for(int i=1;i<=images.Count;i++)Bytes($"ppt/media/image{i}.png",File.ReadAllBytes(images[i-1]));
        for(int i=1;i<=charts.Count;i++)
        {
            Type($"ppt/charts/chart{i}.xml","application/vnd.openxmlformats-officedocument.drawingml.chart+xml");
            Put($"ppt/charts/chart{i}.xml",ChartXml(charts[i-1].Series,charts[i-1].Title));
            Put($"ppt/charts/_rels/chart{i}.xml.rels",Rels([Rel("rId1","package",$"../embeddings/data{i}.xlsx")]));
            Bytes($"ppt/embeddings/data{i}.xlsx",Workbook(charts[i-1].Series));
        }
        types.Append("</Types>");Put("[Content_Types].xml",types.ToString());
    }
    static string Theme()
    {
        string[] names=["dk1","lt1","dk2","lt2","accent1","accent2","accent3","accent4","accent5","accent6","hlink","folHlink"];
        string[] colors=["123047","FFFFFF","475569","F1F5F9","0284C7","0F766E","B45309","BE123C","94A3B8","64748B","0284C7","B45309"];
        string palette=string.Join("",names.Zip(colors,(n,c)=>$"<a:{n}><a:srgbClr val=\"{c}\"/></a:{n}>"));
        string fill="<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>",line=$"<a:ln w=\"12700\">{fill}<a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>";
        return Head+$"<a:theme xmlns:a=\"{A}\" name=\"SEFM\"><a:themeElements><a:clrScheme name=\"SEFM\">{palette}</a:clrScheme><a:fontScheme name=\"Arial\"><a:majorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme><a:fmtScheme name=\"SEFM\"><a:fillStyleLst>{fill}{fill}{fill}</a:fillStyleLst><a:lnStyleLst>{line}{line}{line}</a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst>{fill}{fill}{fill}</a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
    }
    static string ChartText(string text,int size=1400)=>$"<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"{size}\"><a:solidFill><a:srgbClr val=\"475569\"/></a:solidFill><a:latin typeface=\"Arial\"/></a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr>";
    static string Title(string title,int size=1600)=>$"<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr sz=\"{size}\"><a:solidFill><a:srgbClr val=\"123047\"/></a:solidFill><a:latin typeface=\"Arial\"/></a:rPr><a:t>{E(title)}</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=\"0\"/></c:title>";
    static string Cache(double[] values)=>$"<c:numCache><c:formatCode>General</c:formatCode><c:ptCount val=\"{values.Length}\"/>{string.Join("",values.Select((v,i)=>$"<c:pt idx=\"{i}\"><c:v>{N(v)}</c:v></c:pt>"))}</c:numCache>";
    static string ChartXml(List<Series> series,string title)
    {
        var xml=new StringBuilder(Head+$"<c:chartSpace xmlns:c=\"{C}\" xmlns:a=\"{A}\" xmlns:r=\"{R}\"><c:date1904 val=\"0\"/><c:lang val=\"en-US\"/><c:roundedCorners val=\"0\"/><c:chart>{Title(title)}<c:autoTitleDeleted val=\"0\"/><c:plotArea><c:layout/><c:scatterChart><c:scatterStyle val=\"lineMarker\"/><c:varyColors val=\"0\"/>");
        for(int i=0;i<series.Count;i++)
        {
            var s=series[i];string xcol=Column(i*2),ycol=Column(i*2+1);int rows=s.X.Length+1;
            xml.Append($"<c:ser><c:idx val=\"{i}\"/><c:order val=\"{i}\"/><c:tx><c:strRef><c:f>Data!${ycol}$1</c:f><c:strCache><c:ptCount val=\"1\"/><c:pt idx=\"0\"><c:v>{E(s.Name)}</c:v></c:pt></c:strCache></c:strRef></c:tx><c:spPr><a:ln w=\"25400\">{(s.Marker=="none"?$"<a:solidFill><a:srgbClr val=\"{s.Color}\"/></a:solidFill>":"<a:noFill/>")}</a:ln></c:spPr><c:marker><c:symbol val=\"{s.Marker}\"/>");
            if(s.Marker!="none")xml.Append($"<c:size val=\"10\"/><c:spPr><a:solidFill><a:srgbClr val=\"{s.Color}\"/></a:solidFill><a:ln><a:solidFill><a:srgbClr val=\"{s.Color}\"/></a:solidFill></a:ln></c:spPr>");
            xml.Append($"</c:marker><c:xVal><c:numRef><c:f>Data!${xcol}$2:${xcol}${rows}</c:f>{Cache(s.X)}</c:numRef></c:xVal><c:yVal><c:numRef><c:f>Data!${ycol}$2:${ycol}${rows}</c:f>{Cache(s.Y)}</c:numRef></c:yVal><c:smooth val=\"0\"/></c:ser>");
        }
        var xs=series.SelectMany(s=>s.X).ToArray();var ys=series.SelectMany(s=>s.Y).ToArray();
        xml.Append("<c:axId val=\"100\"/><c:axId val=\"200\"/></c:scatterChart>");
        string Axis(int id,int cross,string position,string label,double min,double max,bool grid)=>$"<c:valAx><c:axId val=\"{id}\"/><c:scaling><c:orientation val=\"minMax\"/><c:max val=\"{N(max)}\"/><c:min val=\"{N(min)}\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"{position}\"/>{(grid?"<c:majorGridlines><c:spPr><a:ln w=\"6350\"><a:solidFill><a:srgbClr val=\"E2E8F0\"/></a:solidFill></a:ln></c:spPr></c:majorGridlines>":"")}{Title(label,1200)}<c:numFmt formatCode=\"0\" sourceLinked=\"0\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>{ChartText("",1050)}<c:crossAx val=\"{cross}\"/><c:crosses val=\"min\"/><c:crossBetween val=\"midCat\"/></c:valAx>";
        xml.Append(Axis(100,200,"b","Year",xs.Min()-1,xs.Max()+2,false));xml.Append(Axis(200,100,"l","Event day of year (Jan 1 = 1)",Math.Floor(ys.Min()-4),Math.Ceiling(ys.Max()+4),true));
        xml.Append($"</c:plotArea><c:legend><c:legendPos val=\"b\"/><c:layout/><c:overlay val=\"0\"/>{ChartText("",1050)}</c:legend><c:plotVisOnly val=\"1\"/><c:dispBlanksAs val=\"gap\"/></c:chart><c:spPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:ln><a:noFill/></a:ln></c:spPr>{ChartText("",1200)}<c:externalData r:id=\"rId1\"><c:autoUpdate val=\"0\"/></c:externalData></c:chartSpace>");return xml.ToString();
    }
    static string Column(int index){string result="";for(int n=index+1;n>0;n=(n-1)/26)result=(char)('A'+(n-1)%26)+result;return result;}
    static byte[] Workbook(List<Series> series)
    {
        using var memory=new MemoryStream();
        using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
        {
            void Put(string name,string text){using var writer=new StreamWriter(zip.CreateEntry(name).Open(),Model.Utf8);writer.Write(Head+text);}
            Put("[Content_Types].xml","<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Put("_rels/.rels",$"<Relationships xmlns=\"{RelNs}\">{Rel("rId1","officeDocument","xl/workbook.xml")}</Relationships>");
            Put("xl/workbook.xml",$"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels",$"<Relationships xmlns=\"{RelNs}\">{Rel("rId1","worksheet","worksheets/sheet1.xml")}</Relationships>");
            var sheet=new StringBuilder($"<worksheet xmlns=\"{S}\"><sheetData><row r=\"1\">");
            for(int i=0;i<series.Count;i++)for(int col=0;col<2;col++)sheet.Append($"<c r=\"{Column(2*i+col)}1\" t=\"inlineStr\"><is><t>{E(col==0?"Year":series[i].Name)}</t></is></c>");
            sheet.Append("</row>");
            for(int row=0;row<series.Max(s=>s.X.Length);row++)
            {sheet.Append($"<row r=\"{row+2}\">");for(int i=0;i<series.Count;i++)if(row<series[i].X.Length){sheet.Append($"<c r=\"{Column(i*2)}{row+2}\"><v>{N(series[i].X[row])}</v></c><c r=\"{Column(i*2+1)}{row+2}\"><v>{N(series[i].Y[row])}</v></c>");}sheet.Append("</row>");}
            sheet.Append("</sheetData></worksheet>");Put("xl/worksheets/sheet1.xml",sheet.ToString());
        }
        return memory.ToArray();
    }
}
