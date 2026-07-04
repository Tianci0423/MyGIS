using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Mapsui;

namespace GeoVision.Services
{
    public static class RasterClipService
    {
        public static async Task RunExtentAsync(
            string rasterPath,
            string outputPath,
            MRect bounds)
        {
            await RunAsync(
                rasterPath,
                outputPath,
                "extent",
                bounds,
                null);
        }

        public static async Task RunCutlineAsync(
            string rasterPath,
            string outputPath,
            string cutlineJsonPath)
        {
            await RunAsync(
                rasterPath,
                outputPath,
                "cutline",
                null,
                cutlineJsonPath);
        }

        private static async Task RunAsync(
            string rasterPath,
            string outputPath,
            string mode,
            MRect? bounds,
            string? cutlineJsonPath)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string pythonPath = Path.Combine(baseDir, "python_env", "runtime", "python", "python.exe");
            if (!File.Exists(pythonPath))
                pythonPath = "python";

            string scriptPath = Path.Combine(baseDir, "Scripts", "raster_clip.py");
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("找不到影像裁剪脚本。", scriptPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
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
            startInfo.ArgumentList.Add(rasterPath);
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("--mode");
            startInfo.ArgumentList.Add(mode);

            if (bounds != null)
            {
                startInfo.ArgumentList.Add("--bounds");
                startInfo.ArgumentList.Add(bounds.MinX.ToString("R", CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add(bounds.MinY.ToString("R", CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add(bounds.MaxX.ToString("R", CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add(bounds.MaxY.ToString("R", CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(cutlineJsonPath))
            {
                startInfo.ArgumentList.Add("--cutline-json");
                startInfo.ArgumentList.Add(cutlineJsonPath);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("无法启动影像裁剪进程。");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                string message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(message)
                        ? $"影像裁剪失败，退出码 {process.ExitCode}。"
                        : message.Trim());
            }

            if (!File.Exists(outputPath))
                throw new FileNotFoundException("裁剪进程结束，但没有生成输出文件。", outputPath);
        }
    }
}
