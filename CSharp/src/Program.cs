namespace Sefm;

public sealed class Options
{
    public string Command = "help", Root = "", Smoothing = "fft", Timing = "next-year";
    public string? Destination, Source;
    public int? Year;
    public readonly List<string> Regions = [];
    public static Options Parse(string[] args)
    {
        var o=new Options {Command=args.Length==0?"menu":args[0].ToLowerInvariant()};
        for(int i=1;i<args.Length;i++)
        {
            string key=args[i];if(i+1>=args.Length)throw new ArgumentException($"Missing value for {key}");string value=args[++i];
            switch(key){case "--root":o.Root=Path.GetFullPath(value);break;case "--destination":o.Destination=Path.GetFullPath(value);break;case "--source":o.Source=Path.GetFullPath(value);break;
                case "--region":o.Regions.Add(value);break;case "--smoothing":o.Smoothing=value;break;case "--timing":o.Timing=value;break;
                case "--year":if(!int.TryParse(value,out int year)||year<2||year>9999)throw new ArgumentException("Invalid forecast year");o.Year=year;break;
                default:throw new ArgumentException($"Unknown option: {key}");}
        }
        if(o.Smoothing is not ("fft" or "workbook" or "legacy"))throw new ArgumentException("Smoothing must be fft, workbook or legacy");
        if(o.Timing is not ("next-year" or "legacy"))throw new ArgumentException("Timing must be next-year or legacy");
        if(o.Root.Length==0)
        {
            foreach(string start in new[]{AppContext.BaseDirectory,Environment.CurrentDirectory})
            {
                var d=new DirectoryInfo(start);
                while(d!=null){if(Model.Regions(d.FullName).Length>0){o.Root=d.FullName;break;}d=d.Parent;}
                if(o.Root.Length>0)break;
            }
            if(o.Root.Length==0)o.Root=AppContext.BaseDirectory;
        }
        return o;
    }
    public string[] Selected()
    {
        var all=Model.Regions(Root);var unknown=Regions.Except(all.Select(Path.GetFileName)).ToArray();
        if(unknown.Length>0)throw new ArgumentException("Unknown region(s): "+string.Join(", ",unknown));
        var selected=all.Where(d=>Regions.Count==0||Regions.Contains(Path.GetFileName(d))).ToArray();
        if(selected.Length==0)throw new InvalidDataException("No regional input folders found");return selected;
    }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var o=Options.Parse(args);
            if(o.Command=="menu")return Menu(o);
            return Dispatch(o);
        }
        catch(Exception e){Console.Error.WriteLine("ERROR: "+e.Message);return 1;}
    }
    static int Dispatch(Options o)
    {
        switch(o.Command)
        {
            case "forecast":return Forecast(o);
            case "reports":Reports.Build(o);return 0;
            case "all":
                int status=Forecast(o);if(status!=0)return status;
                var years=o.Selected().Select(d=>Model.ReadInput(Path.Combine(d,"input","observations.csv")).Years[^1]+1).Distinct().ToArray();
                if(years.Length!=1)throw new InvalidDataException("Selected regions imply different forecast years; update all selected inputs consistently");
                if(o.Year.HasValue&&o.Year!=years[0])throw new InvalidDataException("Requested report year differs from updated inputs");
                o.Year=years[0];Reports.Build(o);return 0;
            case "self-test":return SelfTests.Run(o.Root);
            case "import":Migration.Run(o);return 0;
            case "help":case "--help":case "-h":
                Console.WriteLine("SEFM - Standalone C# Windows application\n\n"
                    +"SEFM.exe forecast                  Forecast all regional inputs\n"
                    +"SEFM.exe reports --year 2027       Build PDF, PowerPoint and summary CSV\n"
                    +"SEFM.exe all                       Forecast and build reports automatically\n"
                    +"SEFM.exe self-test                 Run numerical and input validation tests\n\n"
                    +"SEFM.exe import --source PATH      Import original Excel/text datasets (optional)\n\n"
                    +"Options: --root PATH  --region \"Exact folder name\" (repeatable)\n"
                    +"         --smoothing fft|workbook|legacy  --timing next-year|legacy\n"
                    +"Reports: --year YYYY  --destination PATH\n\n"
                    +"Default: FFT-selected window with expanding averages; next-year forecast timing.\n"
                    +"With no arguments, an interactive menu opens. No Python, Office or Node.js is required.");return 0;
            default:throw new ArgumentException("Unknown command. Run SEFM.exe help");
        }
    }
    static int Forecast(Options o)
    {
        var folders=o.Selected();var errors=new List<object>();
        foreach(string d in folders)
        {
            try{var c=Model.Calculate(d,o.Smoothing,o.Timing);Plots.WriteOutputs(d,c);Console.WriteLine($"OK {Path.GetFileName(d)}: output-{c.Result.ForecastYear}");}
            catch(Exception e){errors.Add(new{region=Path.GetFileName(d),error=e.Message});Console.Error.WriteLine($"FAILED {Path.GetFileName(d)}: {e.Message}");}
        }
        Model.SaveJson(Path.Combine(o.Root,"forecast_run_errors.json"),errors);
        Console.WriteLine($"{folders.Length-errors.Count}/{folders.Length} regions completed");return errors.Count==0?0:1;
    }
    static int Menu(Options o)
    {
        Console.WriteLine("SEFM - Statistical Events Forecasting Model (C#)\nProject: "+o.Root);
        Console.WriteLine("\n1  Forecast all regions\n2  Build combined reports\n3  Forecast all regions and build reports\n4  Run verification tests\n5  Exit\n");
        Console.Write("Choose 1-5: ");var choice=Console.ReadLine();
        o.Command=choice switch{"1"=>"forecast","2"=>"reports","3"=>"all","4"=>"self-test",_=>"exit"};
        if(o.Command=="exit")return 0;
        if(o.Command=="reports"){Console.Write("Forecast year: ");if(!int.TryParse(Console.ReadLine(),out int year))throw new ArgumentException("Invalid forecast year");o.Year=year;}
        int result;
        try{result=Dispatch(o);}catch(Exception e){Console.Error.WriteLine("ERROR: "+e.Message);result=1;}
        Console.WriteLine("\nPress Enter to close.");Console.ReadLine();return result;
    }
}
