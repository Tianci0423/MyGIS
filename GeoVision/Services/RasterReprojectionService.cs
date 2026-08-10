using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GeoVision.Services
{
    public sealed record RasterReprojectionRequest(
        string InputPath,
        string OutputPath,
        string TargetCrs,
        string Resampling,
        double? Resolution,
        bool LoadResult);

    public static class RasterReprojectionService
    {
        private static readonly Regex ProgressRegex = new(
            @"\bReproject:\s*(?<percent>\d+(?:\.\d+)?)%",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string GetPythonPath()
        {
            string bundled = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "python_env", "runtime", "python", "python.exe");
            return File.Exists(bundled) ? bundled : "python";
        }

        public static string GetScriptPath()
            => Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Scripts", "raster_reproject.py");

        public static async Task RunAsync(
            RasterReprojectionRequest request,
            Action<Process>? onProcessCreated = null,
            IProgress<int>? progress = null)
        {
            string scriptPath = GetScriptPath();
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("找不到影像重投影脚本。", scriptPath);

            string? outputDirectory = Path.GetDirectoryName(request.OutputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("输出路径没有有效的文件夹。");
            Directory.CreateDirectory(outputDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = GetPythonPath(),
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("--input");
            startInfo.ArgumentList.Add(request.InputPath);
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(request.OutputPath);
            startInfo.ArgumentList.Add("--target-crs");
            startInfo.ArgumentList.Add(request.TargetCrs);
            startInfo.ArgumentList.Add("--resampling");
            startInfo.ArgumentList.Add(request.Resampling);
            if (request.Resolution.HasValue)
            {
                startInfo.ArgumentList.Add("--resolution");
                startInfo.ArgumentList.Add(
                    request.Resolution.Value.ToString("R", CultureInfo.InvariantCulture));
            }

            using var process = new Process { StartInfo = startInfo };
            var output = new ProcessOutputBuffer();
            process.OutputDataReceived += (_, e) => AppendLine(output, e.Data, progress);
            process.ErrorDataReceived += (_, e) => AppendLine(output, e.Data, progress);

            if (!process.Start())
                throw new InvalidOperationException("无法启动影像重投影进程。");
            onProcessCreated?.Invoke(process);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(output.Length == 0
                    ? $"影像重投影失败，退出码 {process.ExitCode}。"
                    : output.ToString().Trim());
            }

            if (!File.Exists(request.OutputPath))
                throw new FileNotFoundException("重投影进程结束，但没有生成输出文件。", request.OutputPath);
        }

        private static void AppendLine(
            ProcessOutputBuffer output,
            string? line,
            IProgress<int>? progress)
        {
            output.AppendLine(line);
            if (line == null || progress == null)
                return;

            var match = ProgressRegex.Match(line);
            if (!match.Success ||
                !double.TryParse(
                    match.Groups["percent"].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double percent))
            {
                return;
            }

            progress.Report(Math.Clamp((int)Math.Round(percent), 0, 100));
        }
    }
}
