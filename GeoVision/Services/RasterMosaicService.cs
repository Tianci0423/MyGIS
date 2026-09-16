using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GeoVision.Services
{
    public enum MosaicColorBalance
    {
        None,
        Linear,
        Histogram
    }

    public sealed record RasterMosaicRequest(
        string BasePath,
        string OverlayPath,
        string OutputPath,
        MosaicColorBalance ColorBalance,
        int FeatherDistance,
        string Resampling,
        bool LoadResult);

    public static class RasterMosaicService
    {
        private static readonly Regex ProgressRegex = new(
            @"\bMosaic:\s*(?<percent>\d+(?:\.\d+)?)%",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static async Task RunAsync(
            RasterMosaicRequest request,
            Action<Process>? onProcessCreated = null,
            IProgress<int>? progress = null)
        {
            string basePath = Path.GetFullPath(request.BasePath);
            string overlayPath = Path.GetFullPath(request.OverlayPath);
            string outputPath = Path.GetFullPath(request.OutputPath);
            if (!File.Exists(basePath) || !File.Exists(overlayPath))
                throw new FileNotFoundException("影像拼接输入文件不存在。", !File.Exists(basePath) ? basePath : overlayPath);
            if (string.Equals(outputPath, basePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outputPath, overlayPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("输出文件不能覆盖输入影像。");
            if (request.FeatherDistance < 0 || request.FeatherDistance > 1024)
                throw new ArgumentOutOfRangeException(nameof(request.FeatherDistance), "羽化距离必须在 0 到 1024 像元之间。");

            string scriptPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Scripts", "raster_mosaic.py");
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("找不到影像拼接脚本。", scriptPath);

            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("输出路径没有有效的文件夹。");
            Directory.CreateDirectory(outputDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = RasterReprojectionService.GetPythonPath(),
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add(scriptPath);
            AddArgument(startInfo, "--base", basePath);
            AddArgument(startInfo, "--overlay", overlayPath);
            AddArgument(startInfo, "--output", outputPath);
            AddArgument(startInfo, "--color-balance", request.ColorBalance switch
            {
                MosaicColorBalance.None => "none",
                MosaicColorBalance.Linear => "linear",
                _ => "histogram"
            });
            AddArgument(startInfo, "--feather-distance",
                request.FeatherDistance.ToString(CultureInfo.InvariantCulture));
            AddArgument(startInfo, "--resampling", request.Resampling);

            using var process = new Process { StartInfo = startInfo };
            var output = new ProcessOutputBuffer();
            process.OutputDataReceived += (_, e) => AppendLine(output, e.Data, progress);
            process.ErrorDataReceived += (_, e) => AppendLine(output, e.Data, progress);

            if (!process.Start())
                throw new InvalidOperationException("无法启动影像拼接进程。");
            onProcessCreated?.Invoke(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(output.Length == 0
                    ? $"影像拼接失败，退出码 {process.ExitCode}。"
                    : output.ToString().Trim());
            if (!File.Exists(outputPath))
                throw new FileNotFoundException("拼接进程结束，但没有生成输出影像。", outputPath);
        }

        private static void AddArgument(ProcessStartInfo info, string name, string value)
        {
            info.ArgumentList.Add(name);
            info.ArgumentList.Add(value);
        }

        private static void AppendLine(
            ProcessOutputBuffer output,
            string? line,
            IProgress<int>? progress)
        {
            output.AppendLine(line);
            if (line == null || progress == null)
                return;

            Match match = ProgressRegex.Match(line);
            if (match.Success && double.TryParse(
                    match.Groups["percent"].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double percent))
            {
                progress.Report(Math.Clamp((int)Math.Round(percent), 0, 100));
            }
        }
    }
}
