using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace Sefm;

public sealed record Prediction(double Doy, int RoundedDoy, string? Date, bool Valid);
public sealed record History(int[] Years, double[] Observations, double?[] Mean,
    double?[] RollingMean, double?[] QuadraticFit);
public sealed record Forecast(string Region, string Folder, int ForecastYear, int ObservationStart,
    int ObservationEnd, int Observations, string Smoothing, string Timing, double? FftPeakPeriodYears,
    int? RollingWindowYears, Prediction QuadraticFft, Prediction Ofbm, List<string> Warnings,
    string InputSha256, History History);
public sealed record Calculation(Forecast Result, double[] Frequency, double[] Power,
    double[] Polynomial, double[] RecursivePredictions);
public sealed record Series(string Name, double[] X, double[] Y, string Color, string Marker = "none");

public static class Model
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true, PropertyNameCaseInsensitive = true
    };
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static readonly UTF8Encoding Utf8 = new(false);
    public static string Number(double x) => x.ToString("G17", Inv);
    public static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    public static void SaveJson<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json), Utf8);
    public static T LoadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"Empty JSON document: {path}");
    public static string[] Regions(string root) => Directory.GetDirectories(root)
        .Where(d => File.Exists(Path.Combine(d, "input", "observations.csv")))
        .OrderBy(d => int.TryParse(Regex.Match(Path.GetFileName(d), @"^\d+").Value, out int n) ? n : int.MaxValue)
        .ThenBy(Path.GetFileName, StringComparer.Ordinal).ToArray();

    public static (int[] Years, double[] Obs) ReadInput(string path)
    {
        using var csv = new TextFieldParser(path, Encoding.UTF8) { TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        csv.SetDelimiters(",");
        if (!(csv.ReadFields() ?? []).SequenceEqual(new[] { "Year", "DOY" }))
            throw new InvalidDataException($"{path}: expected exactly the columns Year,DOY");
        var years = new List<int>(); var obs = new List<double>();
        while (!csv.EndOfData)
        {
            long row = csv.LineNumber;
            var fields = csv.ReadFields() ?? [];
            if (fields.Length != 2 || !double.TryParse(fields[0], NumberStyles.Float, Inv, out double yf)
                || !double.IsFinite(yf) || yf != Math.Truncate(yf) || yf < 1 || yf > 9998
                || !double.TryParse(fields[1], NumberStyles.Float, Inv, out double doy)
                || !double.IsFinite(doy))
                throw new InvalidDataException($"{path}, row {row}: invalid year or DOY");
            int year = (int)yf;
            if (doy < 1 || doy > (DateTime.IsLeapYear(year) ? 366 : 365))
                throw new InvalidDataException($"{path}, row {row}: DOY outside calendar year");
            if (years.Count > 0 && year != years[^1] + 1)
                throw new InvalidDataException($"{path}, row {row}: years must be unique, ascending and consecutive");
            years.Add(year); obs.Add(doy);
        }
        if (years.Count < 5) throw new InvalidDataException($"{path}: at least five annual observations required");
        return (years.ToArray(), obs.ToArray());
    }

    // Mixed-radix Cooley-Tukey FFT, with a direct transform for prime factors.
    // Annual records are small; this handles arbitrary lengths without padding.
    public static Complex[] Fft(Complex[] input)
    {
        int n = input.Length;
        if (n <= 1) return input.ToArray();
        int factor = 2;
        while (factor * factor <= n && n % factor != 0) factor++;
        if (n % factor != 0) factor = n;
        var result = new Complex[n];
        if (factor == n)
        {
            for (int k = 0; k < n; k++)
                for (int j = 0; j < n; j++)
                    result[k] += input[j] * Complex.FromPolarCoordinates(1, -2 * Math.PI * j * k / n);
            return result;
        }
        int m = n / factor;
        var sub = new Complex[factor][];
        for (int r = 0; r < factor; r++)
            sub[r] = Fft(Enumerable.Range(0, m).Select(j => input[r + factor * j]).ToArray());
        for (int k = 0; k < n; k++)
            for (int r = 0; r < factor; r++)
                result[k] += sub[r][k % m] * Complex.FromPolarCoordinates(1, -2 * Math.PI * r * k / n);
        return result;
    }
    public static (double[] Freq, double[] Power, double? Peak) Spectrum(double[] obs)
    {
        var fft = Fft(obs.Select(x => new Complex(x, 0)).ToArray());
        var freq = Enumerable.Range(1, obs.Length / 2).Select(k => (double)k / obs.Length).ToArray();
        var power = Enumerable.Range(1, obs.Length / 2).Select(k => fft[k].Real * fft[k].Real + fft[k].Imaginary * fft[k].Imaginary).ToArray();
        double? peak = obs.Max() == obs.Min() ? null : 1 / freq[Array.IndexOf(power, power.Max())];
        return (freq, power, peak);
    }
    public static (double?[] Mean, double?[] Rolling) Smooth(double[] obs, int window)
    {
        if (window < 1) throw new ArgumentOutOfRangeException(nameof(window));
        var means = new double?[obs.Length]; var rolling = new double?[obs.Length];
        for (int i = 1; i < obs.Length; i++)
        {
            means[i] = (obs[i - 1] + obs[i]) / 2;
            double sum = 0; int first = Math.Max(1, i - window + 1);
            for (int j = first; j <= i; j++) sum += means[j]!.Value;
            rolling[i] = sum / (i - first + 1);
        }
        return (means, rolling);
    }
    // Preserve the supplied Python filter's buffer initialization and nested updates.
    public static double[] LegacyPredictions(double[] yz)
    {
        int n = yz.Length, order = n / 2;
        var wk1 = new double[n]; var wk2 = new double[n];
        wk1[0] = yz[0]; wk2[^1] = yz[^1];
        for (int j = 1; j < n - 1; j++) { wk1[j] = yz[j]; wk2[j - 1] = yz[j]; }
        var cof = new double[order]; var wkm = new double[order];
        for (int k = 0; k < order; k++)
        {
            double num = 0, den = 0;
            for (int j = 0; j < n - k; j++)
            { num += wk1[j] * wk2[j]; den += wk1[j] * wk1[j] + wk2[j] * wk2[j]; }
            cof[k] = den != 0 ? 2 * num / den : 0;
            for (int i = 0; i < k; i++) cof[i] = wkm[i] - cof[k] * wkm[k - i - 1];
            for (int i = 0; i <= k; i++)
            {
                wkm[i] = cof[i];
                for (int j = 0; j < n - k - 1; j++)
                { wk1[j] -= wkm[k] * wk2[j]; wk2[j] = wk2[j + 1] - wkm[k] * wk1[j + 1]; }
            }
        }
        var reg = yz.Reverse().Take(order).ToArray(); var prediction = new double[n + 1];
        for (int j = 0; j <= n; j++)
        {
            double value = 0;
            for (int k = 0; k < order; k++) value += cof[k] * reg[k];
            for (int k = order - 1; k > 0; k--) reg[k] = reg[k - 1];
            reg[0] = value; prediction[j] = value;
        }
        return prediction;
    }
    // Scaled quadratic least squares with twice-reorthogonalized QR, avoiding
    // ill-conditioned normal equations. Return coefficients in the original index.
    public static double[] Polyfit(double[] y)
    {
        int n = y.Length; double center = (n + 1.0) / 2, scale = Math.Max(1, (n - 1.0) / 2);
        var q = new double[3][]; var r = new double[3, 3];
        for (int col = 0; col < 3; col++)
        {
            var v = Enumerable.Range(1, n).Select(i => Math.Pow((i - center) / scale, 2 - col)).ToArray();
            for (int pass = 0; pass < 2; pass++)
                for (int prev = 0; prev < col; prev++)
                {
                    double dot = 0; for (int i = 0; i < n; i++) dot += q[prev][i] * v[i];
                    r[prev, col] += dot; for (int i = 0; i < n; i++) v[i] -= dot * q[prev][i];
                }
            double norm = Math.Sqrt(v.Sum(x => x * x));
            if (norm < 1e-14) throw new InvalidDataException("Quadratic fit is rank deficient");
            r[col, col] = norm; q[col] = v.Select(x => x / norm).ToArray();
        }
        var b = new double[3];
        for (int k = 2; k >= 0; k--)
        {
            double dot = 0; for (int i = 0; i < n; i++) dot += q[k][i] * y[i];
            for (int j = k + 1; j < 3; j++) dot -= r[k, j] * b[j];
            b[k] = dot / r[k, k];
        }
        return [b[0] / (scale * scale), b[1] / scale - 2 * b[0] * center / (scale * scale),
            b[2] - b[1] * center / scale + b[0] * center * center / (scale * scale)];
    }
    public static double Polyval(double[] p, double x) => (p[0] * x + p[1]) * x + p[2];
    public static Prediction CalendarValue(int year, double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue + 1 || value > int.MaxValue - 1)
            throw new InvalidDataException("Non-finite or unrepresentable forecast");
        int rounded = (int)Math.Floor(value + 0.5);
        bool valid = rounded >= 1 && rounded <= (DateTime.IsLeapYear(year) ? 366 : 365);
        return new(value, rounded, valid ? new DateTime(year, 1, 1).AddDays(rounded - 1).ToString("yyyy-MM-dd", Inv) : null, valid);
    }
    public static Calculation Calculate(string folder, string smoothing = "fft", string timing = "next-year")
    {
        string input = Path.Combine(folder, "input", "observations.csv"), metaPath = Path.Combine(folder, "input", "region.json");
        var (years, obs) = ReadInput(input);
        using var metadata = JsonDocument.Parse(File.Exists(metaPath) ? File.ReadAllText(metaPath) : "{}");
        var meta = metadata.RootElement;
        var warnings = meta.TryGetProperty("warnings", out var w) ? w.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
        var (freq, power, peak) = Spectrum(obs);
        double?[] means, rolling; int? window;
        if (smoothing == "legacy")
        {
            if (!meta.TryGetProperty("migration_last_year", out var my) || my.GetInt32() != years[^1]
                || !meta.TryGetProperty("input_sha256", out var hash) || hash.GetString() != Sha(input))
                throw new InvalidDataException("Legacy mode requires unchanged migrated input");
            var snapshot = File.ReadAllLines(Path.Combine(folder, "input", "legacy_rolling_mean.csv"))
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => double.Parse(s, Inv)).ToArray();
            if (snapshot.Length > years.Length || snapshot.Any(v => !double.IsFinite(v))) throw new InvalidDataException("Invalid legacy snapshot");
            (means, _) = Smooth(obs, 1);
            rolling = new double?[years.Length];
            for (int i = 0; i < snapshot.Length; i++) rolling[years.Length - snapshot.Length + i] = snapshot[i];
            window = meta.TryGetProperty("workbook_window", out var ww) && ww.ValueKind == JsonValueKind.Number ? ww.GetInt32() : null;
            warnings.Add("Legacy rolling-mean snapshot used; values are not recomputed.");
        }
        else
        {
            if (smoothing == "workbook")
            {
                if (!meta.TryGetProperty("workbook_window", out var ww) || ww.ValueKind != JsonValueKind.Number || ww.GetInt32() < 1)
                    throw new InvalidDataException("No unambiguous contiguous workbook window; select fft smoothing");
                window = ww.GetInt32();
            }
            else if (smoothing == "fft") window = peak.HasValue ? Math.Max(1, (int)Math.Floor(peak.Value + 0.5)) : 1;
            else throw new ArgumentException("Smoothing must be fft, workbook or legacy");
            (means, rolling) = Smooth(obs, window!.Value);
        }
        if (timing != "next-year" && timing != "legacy") throw new ArgumentException("Timing must be next-year or legacy");
        var values = rolling.Where(x => x.HasValue).Select(x => x!.Value).ToArray(); int n = values.Length;
        if (n < 3) throw new InvalidDataException("At least three smoothed values required");
        var poly = Polyfit(values); var recursive = LegacyPredictions(values);
        var quadratic = CalendarValue(years[^1] + 1, Polyval(poly, timing == "legacy" ? n : n + 1));
        var ofbm = CalendarValue(years[^1] + 1, timing == "legacy" ? recursive[^1] : recursive[0]);
        if (peak.HasValue && peak >= obs.Length - 1) warnings.Add("Dominant FFT period spans nearly the entire record; smoothing uses expanding averages throughout.");
        warnings.Add("FFT label refers to a quadratic forecast after FFT-selected smoothing, not Fourier extrapolation.");
        warnings.Add("OFBM retains the legacy coefficient algorithm; this implementation does not establish forecast skill.");
        if (!quadratic.Valid) warnings.Add("quadratic_fft: forecast lies outside the forecast calendar year; no date is assigned.");
        if (!ofbm.Valid) warnings.Add("ofbm: forecast lies outside the forecast calendar year; no date is assigned.");
        var fit = new double?[years.Length];
        for (int i = 0; i < n; i++) fit[years.Length - n + i] = Polyval(poly, i + 1);
        string name = meta.TryGetProperty("region", out var label) ? label.GetString()! : Regex.Replace(Path.GetFileName(folder), @"^\d+[\s-]*", "");
        return new(new(name, Path.GetFileName(folder), years[^1] + 1, years[0], years[^1], years.Length,
            smoothing, timing, peak, window, quadratic, ofbm, warnings, Sha(input), new(years, obs, means, rolling, fit)), freq, power, poly, recursive);
    }
    public static string Text(Forecast r) => $"Statistical Events Forecasting Model\n{r.Region}\nForecast year: {r.ForecastYear}\n"
        + $"Observed years: {r.ObservationStart}-{r.ObservationEnd} ({r.Observations} observations)\n"
        + $"Smoothing: {r.Smoothing}; timing: {r.Timing}\nFFT peak period: {r.FftPeakPeriodYears?.ToString("G8", Inv) ?? "none"} years; rolling window: {r.RollingWindowYears} years\n"
        + $"Quadratic / legacy FFT: DOY {r.QuadraticFft.Doy.ToString("F3", Inv)}; rounded DOY {r.QuadraticFft.RoundedDoy}; {r.QuadraticFft.Date ?? "OUTSIDE CALENDAR YEAR"}\n"
        + $"Optimal Filtering-Based Model (OFBM): DOY {r.Ofbm.Doy.ToString("F3", Inv)}; rounded DOY {r.Ofbm.RoundedDoy}; {r.Ofbm.Date ?? "OUTSIDE CALENDAR YEAR"}\n\nMethod notes:\n"
        + string.Join("\n", r.Warnings.Select(x => "- " + x)) + "\n";
    public static string Csv(string? value) => value is null ? "" : value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    public static List<Series> HistorySeries(Forecast r, bool model = false, bool forecasts = true)
    {
        var list = new List<Series>(); var x = r.History.Years.Select(y => (double)y).ToArray();
        void Add(string name, double?[] values, string color)
        {
            var valid = Enumerable.Range(0, x.Length).Where(i => values[i].HasValue).ToArray();
            list.Add(new(name, valid.Select(i => x[i]).ToArray(), valid.Select(i => values[i]!.Value).ToArray(), color));
        }
        if (!model)
        { list.Add(new("Observed event DOY", x, r.History.Observations, "94A3B8")); Add("Two-year mean (current + preceding year)", r.History.Mean, "0284C7"); }
        Add($"Rolling mean of two-year means ({r.RollingWindowYears}-year window)", r.History.RollingMean, "0F766E");
        if (model) Add("Quadratic fit", r.History.QuadraticFit, "B45309");
        if (forecasts)
        {
            list.Add(new($"Quadratic / FFT: DOY {r.QuadraticFft.RoundedDoy} ({r.QuadraticFft.Date ?? "outside year"})", [r.ForecastYear], [r.QuadraticFft.Doy], "B45309", "diamond"));
            list.Add(new($"OFBM: DOY {r.Ofbm.RoundedDoy} ({r.Ofbm.Date ?? "outside year"})", [r.ForecastYear], [r.Ofbm.Doy], "BE123C", "star"));
        }
        return list;
    }
}
