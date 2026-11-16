using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text;

namespace Sefm;

public static class Plots
{
    static readonly Color Ink = Color.FromArgb(18, 48, 71);
    public static void WriteOutputs(string folder, Calculation calc)
    {
        var r = calc.Result; string output = Path.Combine(folder, $"output-{r.ForecastYear}");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "forecast.txt"), Model.Text(r), Model.Utf8);
        var csv = new StringBuilder("Year,DOY,Mean,RollingMean\n");
        for (int i = 0; i < r.Observations; i++)
            csv.AppendLine($"{r.History.Years[i]},{Model.Number(r.History.Observations[i])},{(r.History.Mean[i].HasValue ? Model.Number(r.History.Mean[i]!.Value) : "")},{(r.History.RollingMean[i].HasValue ? Model.Number(r.History.RollingMean[i]!.Value) : "")}");
        File.WriteAllText(Path.Combine(output, "series.csv"), csv.ToString(), Model.Utf8);
        File.WriteAllText(Path.Combine(output, "forecasts.csv"), "Year,Method,DOY,RoundedDOY,Date\n"
            + $"{r.ForecastYear},quadratic_fft,{Model.Number(r.QuadraticFft.Doy)},{r.QuadraticFft.RoundedDoy},{r.QuadraticFft.Date}\n"
            + $"{r.ForecastYear},ofbm,{Model.Number(r.Ofbm.Doy)},{r.Ofbm.RoundedDoy},{r.Ofbm.Date}\n", Model.Utf8);
        Save(output, "history_forecast", r.Region, $"History and {r.ForecastYear} forecasts", Model.HistorySeries(r),
            "Observation / forecast year", "Event day of year (Jan 1 = 1)", true, forecastYear: r.ForecastYear);
        Save(output, "observations_means", r.Region, "Observed events and calculated means", Model.HistorySeries(r, forecasts: false),
            "Observation year", "Event day of year (Jan 1 = 1)", true);
        Save(output, "power_spectrum", r.Region, "FFT power spectrum of annual observed event dates",
            [new("FFT power", calc.Frequency, calc.Power, "0284C7")], "Frequency (cycles per year)", "FFT power (DOY squared)", false, true);
        var order = Enumerable.Range(0, calc.Frequency.Length).Reverse().ToArray();
        Save(output, "periodogram", r.Region, "FFT periodogram" + (r.FftPeakPeriodYears.HasValue ? $" - dominant period: {r.FftPeakPeriodYears.Value.ToString("F2", Model.Inv)} years" : " - no dominant period"),
            [new("FFT power", order.Select(i => 1 / calc.Frequency[i]).ToArray(), order.Select(i => calc.Power[i]).ToArray(), "0F766E")],
            "Period (years per cycle)", "FFT power (DOY squared)", false, true, peak: r.FftPeakPeriodYears);
        Save(output, "model_forecasts", r.Region, $"Smoothed series and {r.ForecastYear} model forecasts", Model.HistorySeries(r, true),
            "Observation / forecast year", "Event day of year (Jan 1 = 1)", true, forecastYear: r.ForecastYear);
        // Publish JSON last: report assembly treats it as the completion record.
        Model.SaveJson(Path.Combine(output, "forecast.json"), r);
    }
    public static void Save(string output, string stem, string title, string subtitle, List<Series> series,
        string xLabel, string yLabel, bool yearAxis, bool zeroY = false, int? forecastYear = null, double? peak = null)
    {
        const int width = 1600, height = 1000;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var titleFont = new Font("Arial", 32, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont = new Font("Arial", 25, FontStyle.Regular, GraphicsUnit.Pixel);
        using var font = new Font("Arial", 23, FontStyle.Regular, GraphicsUnit.Pixel);
        using var legendFont = new Font("Arial", 22, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Ink); using var muted = new SolidBrush(Color.FromArgb(71,85,105));
        g.DrawString(title, titleFont, ink, new RectangleF(115, 25, 1450, 84));
        g.DrawString(subtitle, subFont, muted, new RectangleF(115, 111, 1450, 50));
        float left = 135, top = 185, right = 1540, bottom = 715;
        var xx = series.SelectMany(s => s.X).ToArray(); var yy = series.SelectMany(s => s.Y).ToArray();
        double xmin = xx.Min(), xmax = xx.Max(), ymin = yy.Min(), ymax = yy.Max();
        if (yearAxis) { xmin -= 1; xmax += forecastYear.HasValue ? 2 : 1; }
        else { double pad = (xmax - xmin) * .02; xmin = Math.Max(0, xmin - pad); xmax += pad; }
        double ypad = Math.Max((ymax - ymin) * .09, zeroY ? 1 : 2);
        ymin = zeroY ? 0 : ymin - ypad; ymax += ypad;
        if (xmax <= xmin) xmax = xmin + 1;
        if (ymax <= ymin) ymax = ymin + 1;
        float X(double x) => left + (float)((x - xmin) / (xmax - xmin)) * (right - left);
        float Y(double y) => bottom - (float)((y - ymin) / (ymax - ymin)) * (bottom - top);
        using var grid = new Pen(Color.FromArgb(226,232,240), 1);
        using var axis = new Pen(Color.FromArgb(100,116,139), 1.5f);
        using var centered = new StringFormat { Alignment = StringAlignment.Center };
        using var alignedRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        for (int i = 0; i <= 6; i++)
        {
            double xv = xmin + (xmax - xmin) * i / 6, yv = ymin + (ymax - ymin) * i / 6;
            if (yearAxis) xv = Math.Round(xv);
            float px = X(xv), py = Y(yv);
            g.DrawLine(grid, px, top, px, bottom); g.DrawLine(grid, left, py, right, py);
            string xt = yearAxis ? xv.ToString("0", Model.Inv) : xv.ToString("0.###", Model.Inv);
            string yt = Math.Abs(yv) >= 1000000 ? yv.ToString("0.0E+0", Model.Inv) : yv.ToString("0.#", Model.Inv);
            g.DrawString(xt, font, muted, new RectangleF(px - 90, bottom + 12, 180, 40), centered);
            g.DrawString(yt, font, muted, new RectangleF(0, py - 20, left - 12, 40), alignedRight);
        }
        g.DrawLine(axis, left, top, left, bottom); g.DrawLine(axis, left, bottom, right, bottom);
        if (forecastYear.HasValue || peak.HasValue)
        {
            using var dashed = new Pen(Color.FromArgb(100,116,139), 1.7f) { DashStyle = DashStyle.Dash };
            float px = X(peak ?? (forecastYear!.Value - .5)); g.DrawLine(dashed, px, top, px, bottom);
        }
        foreach (var s in series)
        {
            Color color = ColorTranslator.FromHtml("#" + s.Color); using var pen = new Pen(color, s.Name.StartsWith("Rolling") ? 4 : 3);
            using var brush = new SolidBrush(color);
            if (s.Marker == "none")
            {
                if (s.X.Length > 1) g.DrawLines(pen, s.X.Zip(s.Y, (x,y) => new PointF(X(x),Y(y))).ToArray());
            }
            else for (int i = 0; i < s.X.Length; i++)
            {
                float x = X(s.X[i]), y = Y(s.Y[i]);
                if (s.Marker == "diamond") g.FillPolygon(brush, new PointF[]{new(x,y-12),new(x+12,y),new(x,y+12),new(x-12,y)});
                else
                {
                    var points = Enumerable.Range(0,10).Select(k => { double a = -Math.PI/2 + Math.PI*k/5; float r = k%2==0 ? 14 : 6; return new PointF(x+r*(float)Math.Cos(a),y+r*(float)Math.Sin(a)); }).ToArray();
                    g.FillPolygon(brush, points);
                }
            }
        }
        g.DrawString(xLabel, subFont, ink, new RectangleF(left, 774, right-left, 35), centered);
        var state = g.Save(); g.TranslateTransform(30, (top+bottom)/2); g.RotateTransform(-90);
        g.DrawString(yLabel, subFont, ink, new RectangleF(-350,-20,700,40), centered); g.Restore(state);
        for (int i = 0; i < series.Count; i++)
        {
            var s = series[i]; float y = 838 + i * 29;
            using var pen = new Pen(ColorTranslator.FromHtml("#"+s.Color), 4);
            g.DrawLine(pen, 135, y+12, 180, y+12); g.DrawString(s.Name, legendFont, muted, 195, y);
        }
        string png = Path.Combine(output, stem + ".png"); bitmap.Save(png, ImageFormat.Png);
        var pdf = new SimplePdf(); var page = pdf.AddPage(842, 526);
        page.Image(png, 0, 0, 842, 526); pdf.Save(Path.Combine(output, stem + ".pdf"));
    }
}

// Minimal PDF 1.4 writer: searchable Base-14 text and standard JPEG image streams.
// All streams have byte lengths and an explicit xref table; no external PDF runtime.
public sealed class SimplePdf
{
    public sealed class Page(double width, double height)
    {
        internal readonly StringBuilder Commands = new();
        internal readonly List<(string Path, string Name)> Images = [];
        internal readonly double Width = width, Height = height;
        static string N(double v) => v.ToString("0.###", Model.Inv);
        static string Esc(string text) => new string(text.Select(c => c <= 255 ? c : '-').ToArray()).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        public void Text(string text, double x, double y, double size = 12, bool bold = false, string color = "123047")
        {
            double r=Convert.ToInt32(color[..2],16)/255.0,g=Convert.ToInt32(color[2..4],16)/255.0,b=Convert.ToInt32(color[4..6],16)/255.0;
            Commands.AppendLine($"BT /{(bold?"F2":"F1")} {N(size)} Tf {N(r)} {N(g)} {N(b)} rg 1 0 0 1 {N(x)} {N(Height-y-size)} Tm ({Esc(text)}) Tj ET");
        }
        public void Image(string path, double x, double y, double width, double height)
        {
            string name="Im"+(Images.Count+1); Images.Add((path,name));
            Commands.AppendLine($"q {N(width)} 0 0 {N(height)} {N(x)} {N(Height-y-height)} cm /{name} Do Q");
        }
        public void Line(double x1,double y1,double x2,double y2) => Commands.AppendLine($"0.8 0.84 0.88 RG 0.4 w {N(x1)} {N(Height-y1)} m {N(x2)} {N(Height-y2)} l S");
    }
    readonly List<Page> pages=[];
    public Page AddPage(double width=842,double height=595) {var p=new Page(width,height);pages.Add(p);return p;}
    public void Save(string destination)
    {
        var objects = new List<byte[]> {Array.Empty<byte>(),Array.Empty<byte>(), Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")};
        int Add(string s) {objects.Add(Encoding.Latin1.GetBytes(s));return objects.Count;}
        int StreamObject(string dictionary, byte[] bytes)
        {
            using var mem=new MemoryStream(); mem.Write(Encoding.ASCII.GetBytes($"<< {dictionary} /Length {bytes.Length} >>\nstream\n"));mem.Write(bytes);mem.Write(Encoding.ASCII.GetBytes("\nendstream")); objects.Add(mem.ToArray());return objects.Count;
        }
        var pageNumbers=new List<int>();
        foreach(var page in pages)
        {
            var imageRefs=new List<string>();
            foreach(var (path,name) in page.Images)
            {
                using var image=new Bitmap(path);using var jpeg=new MemoryStream(); image.Save(jpeg,ImageFormat.Jpeg);
                int id=StreamObject($"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode",jpeg.ToArray());imageRefs.Add($"/{name} {id} 0 R");
            }
            int content=StreamObject("",Encoding.Latin1.GetBytes(page.Commands.ToString()));
            pageNumbers.Add(Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width.ToString(Model.Inv)} {page.Height.ToString(Model.Inv)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> /XObject << {string.Join(" ",imageRefs)} >> >> /Contents {content} 0 R >>"));
        }
        objects[0]=Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
        objects[1]=Encoding.ASCII.GetBytes($"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(" ",pageNumbers.Select(n=>$"{n} 0 R"))}] >>");
        using var file=File.Create(destination);file.Write(Encoding.Latin1.GetBytes("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n"));var offsets=new List<long>{0};
        for(int i=0;i<objects.Count;i++){offsets.Add(file.Position);file.Write(Encoding.ASCII.GetBytes($"{i+1} 0 obj\n"));file.Write(objects[i]);file.Write(Encoding.ASCII.GetBytes("\nendobj\n"));}
        long xref=file.Position;var tail=new StringBuilder($"xref\n0 {objects.Count+1}\n0000000000 65535 f \n");
        foreach(long offset in offsets.Skip(1))tail.AppendLine(offset.ToString("D10",Model.Inv)+" 00000 n ");
        tail.Append($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");file.Write(Encoding.ASCII.GetBytes(tail.ToString()));
    }
}
