using System.Text.Json;

namespace Sefm;

public static class SelfTests
{
    public static int Run(string root)
    {
        int assertions=0,cases=0;double maxError=0;
        string fixtureRoot=Path.Combine(root,"reference","baseline_inputs");
        void Check(bool ok,string label){assertions++;if(!ok)throw new Exception("Test failed: "+label);}
        void Close(double actual,double expected,string label,double tolerance=1e-7)
        {double error=Math.Abs(actual-expected);maxError=Math.Max(maxError,error);Check(double.IsFinite(actual)&&error<=tolerance,label+$" ({actual:G17} versus {expected:G17})");}
        void ArrayClose(double?[] actual,JsonElement expected,string label)
        {
            Check(actual.Length==expected.GetArrayLength(),label+" length");int i=0;
            foreach(var e in expected.EnumerateArray())
            {if(e.ValueKind==JsonValueKind.Null)Check(actual[i]==null,label+" null alignment");else{Check(actual[i].HasValue,label+" present");Close(actual[i]!.Value,e.GetDouble(),label);}i++;}
        }
        using(var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"reference","python_baselines.json"))))
        {
            foreach(var entry in fixture.RootElement.GetProperty("cases").EnumerateArray())
            {
                string folder=entry.GetProperty("folder").GetString()!,smoothing=entry.GetProperty("smoothing").GetString()!,timing=entry.GetProperty("timing").GetString()!;
                var c=Model.Calculate(Path.Combine(fixtureRoot,folder),smoothing,timing);var expected=entry.GetProperty("result");var r=c.Result;
                Check(r.ForecastYear==expected.GetProperty("forecast_year").GetInt32(),folder+" forecast year");
                foreach(var (key,prediction) in new[]{("quadratic_fft",r.QuadraticFft),("ofbm",r.Ofbm)})
                {
                    var e=expected.GetProperty(key);Close(prediction.Doy,e.GetProperty("doy").GetDouble(),folder+" "+smoothing+" "+timing+" "+key);
                    Check(prediction.RoundedDoy==e.GetProperty("rounded_doy").GetInt32(),folder+" rounded day");
                    Check(prediction.Date==e.GetProperty("date").GetString(),folder+" calendar date");
                }
                Check(r.RollingWindowYears==(expected.GetProperty("rolling_window_years").ValueKind==JsonValueKind.Null?null:expected.GetProperty("rolling_window_years").GetInt32()),folder+" window");
                var ep=expected.GetProperty("fft_peak_period_years");if(ep.ValueKind!=JsonValueKind.Null)Close(r.FftPeakPeriodYears!.Value,ep.GetDouble(),folder+" FFT peak");
                var history=expected.GetProperty("history");ArrayClose(r.History.Mean,history.GetProperty("mean"),folder+" means");
                ArrayClose(r.History.RollingMean,history.GetProperty("rolling_mean"),folder+" rolling means");
                ArrayClose(r.History.QuadraticFit,history.GetProperty("quadratic_fit"),folder+" polynomial");
                cases++;
            }
        }
        using(var legacy=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"reference","legacy_regression.json"))))
        {
            foreach(var entry in legacy.RootElement.EnumerateObject())
            {
                var c=Model.Calculate(Path.Combine(fixtureRoot,entry.Name),"legacy","legacy");var expected=entry.Value.GetProperty("predictions").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
                Check(c.RecursivePredictions.Length==expected.Length,"Legacy vector length");for(int i=0;i<expected.Length;i++)Close(c.RecursivePredictions[i],expected[i],entry.Name+" legacy vector");
                Close(c.Result.QuadraticFft.Doy,entry.Value.GetProperty("quadratic").GetDouble(),entry.Name+" original quadratic");
            }
        }
        var known=Enumerable.Range(0,40).Select(i=>200+10*Math.Cos(2*Math.PI*i/5)).ToArray();Close(Model.Spectrum(known).Peak!.Value,5,"Known FFT period");
        Check(Model.Spectrum(Enumerable.Repeat(200.0,10).ToArray()).Peak==null,"Constant series has no peak");
        var (mean,rolling)=Model.Smooth([10,20,30,40],2);Check(mean[0]==null&&rolling[0]==null,"First-year alignment");
        for(int i=1;i<4;i++){Close(mean[i]!.Value,new double[]{0,15,25,35}[i],"Two-year mean");Close(rolling[i]!.Value,new double[]{0,15,20,30}[i],"Expanding mean");}
        Check(Model.CalendarValue(2024,60).Date=="2024-02-29","Leap day");Check(Model.CalendarValue(2025,60).Date=="2025-03-01","Ordinary year");
        Check(Model.CalendarValue(2026,200.5).RoundedDoy==201,"Half-up rounding");Check(!Model.CalendarValue(2026,366).Valid,"Out-of-year forecast");
        string temporary=Path.Combine(Path.GetTempPath(),"sefm-test-"+Guid.NewGuid());Directory.CreateDirectory(temporary);
        try
        {
            string input=Path.Combine(temporary,"bad.csv");
            foreach(string rows in new[]{"2020,200\n2021,200\n2021,201\n2023,200\n2024,200", "2020,200\n2021,NaN\n2022,200\n2023,200\n2024,200", "2020,200\n2021,366\n2022,200\n2023,200\n2024,200", "2020,200\n2022,200\n2023,200\n2024,200\n2025,200"})
            {
                File.WriteAllText(input,"Year,DOY\n"+rows,Model.Utf8);bool rejected=false;try{Model.ReadInput(input);}catch(InvalidDataException){rejected=true;}Check(rejected,"Reject invalid CSV");
            }
            string region=Path.Combine(temporary,"Test Region");Directory.CreateDirectory(Path.Combine(region,"input"));
            var first=Model.Regions(fixtureRoot)[0];string csv=File.ReadAllText(Path.Combine(first,"input","observations.csv")).TrimEnd();
            File.WriteAllText(Path.Combine(region,"input","observations.csv"),csv+"\n2026,204.0\n",Model.Utf8);
            var future=Model.Calculate(region);Check(future.Result.ForecastYear==2027,"Appending 2026 forecasts 2027");
            Check(future.Result.Observations==Model.ReadInput(Path.Combine(first,"input","observations.csv")).Years.Length+1,"New observation included");
        }
        finally{Directory.Delete(temporary,true);}
        Console.WriteLine($"PASS: {cases} Python comparison cases, original legacy vectors for 55 regions, and boundary/input tests.");
        Console.WriteLine($"Assertions: {assertions}; maximum numerical difference: {maxError.ToString("G6",Model.Inv)} days.");return 0;
    }
}
