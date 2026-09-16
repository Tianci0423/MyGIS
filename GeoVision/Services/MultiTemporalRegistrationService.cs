using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GeoVision.Services
{
    public enum RegistrationOutputMode
    {
        GeoreferenceOnly,
        ResampleToReferenceGrid
    }

    public sealed record MultiTemporalRegistrationRequest(
        string MovingPath,
        string ReferencePath,
        string OutputPath,
        double[] CorrectedGeoTransform,
        RegistrationOutputMode OutputMode,
        string Resampling,
        bool LoadResult);

    public static class MultiTemporalRegistrationService
    {
        private static readonly Regex ProgressRegex = new(
            @"\bRegister:\s*(?<percent>\d+(?:\.\d+)?)%",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static async Task RunAsync(
            MultiTemporalRegistrationRequest request,
            Action<Process>? onProcessCreated = null,
            IProgress<int>? progress = null)
        {
            if (request.CorrectedGeoTransform.Length != 6)
                throw new ArgumentException("配准仿射变换必须包含 6 个参数。");
            if (request.CorrectedGeoTransform.Any(value => !double.IsFinite(value)))
                throw new ArgumentException("配准仿射变换包含无效数字。");

            string movingPath = Path.GetFullPath(request.MovingPath);
            string referencePath = Path.GetFullPath(request.ReferencePath);
            string outputPath = Path.GetFullPath(request.OutputPath);
            if (!File.Exists(movingPath) || !File.Exists(referencePath))
                throw new FileNotFoundException("多时相配准输入影像不存在。", !File.Exists(movingPath) ? movingPath : referencePath);
            if (string.Equals(outputPath, movingPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outputPath, referencePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("输出文件不能覆盖输入影像。");

            string scriptPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Scripts", "multi_temporal_register.py");
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("找不到多时相影像配准脚本。", scriptPath);

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
            AddArgument(startInfo, "--moving", movingPath);
            AddArgument(startInfo, "--reference", referencePath);
            AddArgument(startInfo, "--output", outputPath);
            AddArgument(startInfo, "--mode",
                request.OutputMode == RegistrationOutputMode.GeoreferenceOnly ? "georef" : "resample");
            AddArgument(startInfo, "--resampling", request.Resampling);
            startInfo.ArgumentList.Add("--geotransform");
            foreach (double value in request.CorrectedGeoTransform)
                startInfo.ArgumentList.Add(value.ToString("R", CultureInfo.InvariantCulture));

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => AppendLine(output, e.Data, progress);
            process.ErrorDataReceived += (_, e) => AppendLine(output, e.Data, progress);

            if (!process.Start())
                throw new InvalidOperationException("无法启动多时相影像配准进程。");
            onProcessCreated?.Invoke(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(output.Length == 0
                    ? $"多时相影像配准失败，退出码 {process.ExitCode}。"
                    : output.ToString().Trim());
            if (!File.Exists(outputPath))
                throw new FileNotFoundException("配准进程结束，但没有生成输出影像。", outputPath);
        }

        private static void AddArgument(ProcessStartInfo info, string name, string value)
        {
            info.ArgumentList.Add(name);
            info.ArgumentList.Add(value);
        }

        private static void AppendLine(StringBuilder output, string? line, IProgress<int>? progress)
        {
            if (!string.IsNullOrWhiteSpace(line))
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
