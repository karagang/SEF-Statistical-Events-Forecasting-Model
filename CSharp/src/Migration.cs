using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sefm;

public static class Migration
{
    // Read cached numeric observations and literal Excel formulas without Excel.
    // Only input columns A (Year), C (Obs), and E (window provenance) are used.
    public static (int[] Years,double[] Obs,int? Window) ReadWorkbook(string path)
    {
        using var zip=ZipFile.OpenRead(path);
        XDocument Read(string name){using var stream=(zip.GetEntry(name)??throw new InvalidDataException("Missing XLSX part: "+name)).Open();return XDocument.Load(stream);}
        XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main",rel="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var workbook=Read("xl/workbook.xml");int active=(int?)workbook.Descendants(ns+"workbookView").FirstOrDefault()?.Attribute("activeTab")??0;
        var sheets=workbook.Descendants(ns+"sheet").ToArray();if(sheets.Length==0)throw new InvalidDataException("Workbook has no worksheets");
        string rid=(string?)sheets[Math.Min(active,sheets.Length-1)].Attribute(rel+"id")??throw new InvalidDataException("Worksheet has no relationship");
        var relationship=Read("xl/_rels/workbook.xml.rels").Root!.Elements().Single(x=>(string?)x.Attribute("Id")==rid);
        string target=(string)relationship.Attribute("Target")!;string part=target.StartsWith('/')?target.TrimStart('/'):"xl/"+target;
        var sheet=Read(part);var years=new List<int>();var observations=new List<double>();var windows=new HashSet<int>();
        foreach(var row in sheet.Descendants(ns+"row"))
        {
            var cells=row.Elements(ns+"c").ToDictionary(c=>Regex.Match((string?)c.Attribute("r")??"",@"^[A-Z]+").Value);
            if(cells.TryGetValue("A",out var a)&&cells.TryGetValue("C",out var c)
                &&((string?)a.Attribute("t") is null or "n")&&((string?)c.Attribute("t") is null or "n")
                &&double.TryParse(a.Element(ns+"v")?.Value,System.Globalization.NumberStyles.Float,Model.Inv,out double yf)
                &&double.TryParse(c.Element(ns+"v")?.Value,System.Globalization.NumberStyles.Float,Model.Inv,out double obs))
            {
                if(yf!=Math.Truncate(yf)||yf<1||yf>9998||!double.IsFinite(obs))throw new InvalidDataException("Invalid workbook Year/Obs value");
                years.Add((int)yf);observations.Add(obs);
            }
            if(cells.TryGetValue("E",out var e))
                foreach(Match m in Regex.Matches(e.Element(ns+"f")?.Value??"",@"D(\d+):D(\d+)",RegexOptions.IgnoreCase))
                    windows.Add(int.Parse(m.Groups[2].Value)-int.Parse(m.Groups[1].Value)+1);
        }
        if(years.Count<5)throw new InvalidDataException("Workbook has fewer than five numeric Year/Obs records");
        return(years.ToArray(),observations.ToArray(),windows.Count==1?windows.Single():null);
    }
    public static void Run(Options options)
    {
        if(options.Source==null||!Directory.Exists(options.Source))throw new ArgumentException("Import requires --source ORIGINAL_DATASET_FOLDER");
        Directory.CreateDirectory(options.Root);var audit=new List<object>();int created=0;
        foreach(string folder in Directory.GetDirectories(options.Source).OrderBy(Path.GetFileName,StringComparer.Ordinal))
        {
            string[] books=Directory.GetFiles(folder,"*.xlsx");if(books.Length==0)continue;
            if(books.Length!=1)throw new InvalidDataException($"{folder}: expected exactly one workbook");
            string destination=Path.Combine(options.Root,Path.GetFileName(folder),"input");
            if(Directory.Exists(destination)){Console.WriteLine("SKIP existing "+destination);continue;}
            var (years,obs,window)=ReadWorkbook(books[0]);var warnings=new List<string>();
            string yobs=Path.Combine(folder,"yobs.txt");
            if(File.Exists(yobs))
            {
                var old=Regex.Split(File.ReadAllText(yobs).Trim(),@"\s+").Select(s=>double.Parse(s,Model.Inv)).ToArray();
                if(old.Length!=obs.Length||old.Zip(obs).Any(p=>Math.Abs(p.First-p.Second)>1e-8))warnings.Add("Workbook observations differ from yobs.txt; CSV uses the workbook as requested.");
            }
            string temporary=Path.Combine(Path.GetTempPath(),"sefm-import-"+Guid.NewGuid());Directory.CreateDirectory(temporary);
            try
            {
                var csv=new StringBuilder("Year,DOY\n");for(int i=0;i<years.Length;i++)csv.AppendLine($"{years[i]},{Model.Number(obs[i])}");
                string source=Path.Combine(temporary,"observations.csv");File.WriteAllText(source,csv.ToString(),Model.Utf8);Model.ReadInput(source);
                var meta=new{region=Regex.Replace(Path.GetFileName(folder),@"^\d+[\s-]*",""),source_workbook=Path.GetFileName(books[0]),source_columns="A (Year), C (Obs)",migration_last_year=years[^1],workbook_window=window,input_sha256=Model.Sha(source),warnings};
                Model.SaveJson(Path.Combine(temporary,"region.json"),meta);
                string yz=Path.Combine(folder,"yz.txt");if(File.Exists(yz))File.Copy(yz,Path.Combine(temporary,"legacy_rolling_mean.csv"));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);Directory.CreateDirectory(destination);
                foreach(string file in Directory.GetFiles(temporary))File.Copy(file,Path.Combine(destination,Path.GetFileName(file)),false);
                audit.Add(new{folder=Path.GetFileName(folder),metadata=meta});created++;
                Console.WriteLine($"CREATED {Path.GetFileName(folder)}: {years.Length} observations");
            }
            finally{Directory.Delete(temporary,true);}
        }
        if(created>0)Model.SaveJson(Path.Combine(options.Root,"migration_audit.json"),audit);
        Console.WriteLine($"Imported {created} regions. Existing inputs were preserved.");
    }
}
