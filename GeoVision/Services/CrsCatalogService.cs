using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GeoVision.Services
{
    public sealed record CrsCatalogEntry(string Code, string Name, string Category)
    {
        public string Identifier => $"EPSG:{Code}";
        public string CategoryDisplay => Category == "geographic" ? "地理坐标系" : "投影坐标系";
        public string SearchText => $"{Code} {Name} {CategoryDisplay}";
    }

    public static class CrsCatalogService
    {
        private static readonly object CacheLock = new();
        private static Task<IReadOnlyList<CrsCatalogEntry>>? _catalogTask;

        public static IReadOnlyList<CrsCatalogEntry> CommonEntries { get; } =
        [
            new("4326", "WGS 84", "geographic"),
            new("4490", "China Geodetic Coordinate System 2000", "geographic"),
            new("4610", "Xian 1980", "geographic"),
            new("4214", "Beijing 1954", "geographic"),
            new("4269", "NAD83", "geographic"),
            new("4258", "ETRS89", "geographic"),
            new("3857", "WGS 84 / Pseudo-Mercator", "projected"),
            new("32647", "WGS 84 / UTM zone 47N", "projected"),
            new("32648", "WGS 84 / UTM zone 48N", "projected"),
            new("32649", "WGS 84 / UTM zone 49N", "projected"),
            new("32650", "WGS 84 / UTM zone 50N", "projected"),
            new("32651", "WGS 84 / UTM zone 51N", "projected"),
            new("32652", "WGS 84 / UTM zone 52N", "projected"),
            new("32653", "WGS 84 / UTM zone 53N", "projected"),
            new("4547", "CGCS2000 / 3-degree Gauss-Kruger CM 114E", "projected"),
            new("4548", "CGCS2000 / 3-degree Gauss-Kruger CM 117E", "projected"),
            new("4549", "CGCS2000 / 3-degree Gauss-Kruger CM 120E", "projected"),
            new("4550", "CGCS2000 / 3-degree Gauss-Kruger CM 123E", "projected")
        ];

        public static Task<IReadOnlyList<CrsCatalogEntry>> LoadAsync()
        {
            lock (CacheLock)
                return _catalogTask ??= LoadCoreAsync();
        }

        private static async Task<IReadOnlyList<CrsCatalogEntry>> LoadCoreAsync()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string scriptPath = Path.Combine(baseDirectory, "Scripts", "crs_catalog.py");
            string databasePath = Path.Combine(baseDirectory, "proj.db");
            if (!File.Exists(scriptPath) || !File.Exists(databasePath))
                return CommonEntries;

            var startInfo = new ProcessStartInfo
            {
                FileName = RasterReprojectionService.GetPythonPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("--database");
            startInfo.ArgumentList.Add(databasePath);

            try
            {
                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                    return CommonEntries;

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                string json = await outputTask;
                _ = await errorTask;
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                    return CommonEntries;

                var entries = JsonSerializer.Deserialize<List<CrsCatalogEntry>>(json);
                return entries is { Count: > 0 } ? entries : CommonEntries;
            }
            catch
            {
                return CommonEntries;
            }
        }
    }
}
