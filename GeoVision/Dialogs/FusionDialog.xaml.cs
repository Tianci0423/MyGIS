using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using GeoVision.Services;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace GeoVision.Dialogs
{
    public partial class FusionDialog : Window
    {
        private const int OutputBandCount = 4;
        private const int OutputBytesPerSample = 4; // Fusion model writes float32.
        internal const double DefaultFloatScale = 2047d;
        private const double RecommendedSpaceMultiplier = 1.20;
        private const long RecommendedSpacePaddingBytes = 512L * 1024L * 1024L;
        private static readonly Regex PercentRegex = new(
            @"(?<percent>\d+(?:\.\d+)?)%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public FusionRequest? Request { get; private set; }

        public FusionDialog(IReadOnlyList<RasterLayerInfo>? availableLayers = null)
        {
            InitializeComponent();

            if (availableLayers is { Count: > 0 })
            {
                foreach (var info in availableLayers)
                {
                    if (info.BandCount == OutputBandCount)
                        MsBox.Items.Add(info);
                    else if (info.BandCount == 1)
                        PanBox.Items.Add(info);
                }
            }
        }

        internal static string GetPythonExe()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string exe = Path.Combine(baseDir, "python_env", "runtime", "python", "python.exe");
            return File.Exists(exe) ? exe : "python";
        }

        internal static string GetScriptPath()
        {
            return Path.Combine(GetBundledFusionRoot(), "test.py");
        }

        internal static string GetModelPath()
        {
            return Path.Combine(GetBundledFusionRoot(), "bestGF7-gate.pth");
        }

        private static string GetBundledFusionRoot()
        {
            string bundledRoot = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "python_env",
                "runtime",
                "fusion_model");
            if (Directory.Exists(bundledRoot))
                return bundledRoot;

            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "python_env",
                "runtime",
                "fusion_model"));
        }

        public static async Task RunInferenceAsync(
            FusionRequest request,
            Action<Process>? onProcessCreated = null,
            IProgress<int>? progress = null)
        {
            string outputFullPath = Path.GetFullPath(request.OutputPath);
            string? outputDir = Path.GetDirectoryName(outputFullPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
                Directory.CreateDirectory(outputDir);

            string fusionDir = Path.GetDirectoryName(request.ScriptPath) ?? Environment.CurrentDirectory;
            var startInfo = new ProcessStartInfo
            {
                FileName = request.PythonPath,
                WorkingDirectory = fusionDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.Environment["PYTHONPATH"] = fusionDir;

            startInfo.ArgumentList.Add(request.ScriptPath);
            startInfo.ArgumentList.Add("--model_path");
            startInfo.ArgumentList.Add(request.ModelPath);
            startInfo.ArgumentList.Add("--ms_path");
            startInfo.ArgumentList.Add(request.MsPath);
            startInfo.ArgumentList.Add("--pan_path");
            startInfo.ArgumentList.Add(request.PanPath);
            startInfo.ArgumentList.Add("--save_path");
            startInfo.ArgumentList.Add(outputFullPath);
            startInfo.ArgumentList.Add("--float_scale");
            startInfo.ArgumentList.Add(request.FloatScale.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--overlap");
            startInfo.ArgumentList.Add(request.Overlap.ToString(CultureInfo.InvariantCulture));
            if (request.NoCuda) startInfo.ArgumentList.Add("--no_cuda");
            if (request.Fp16) startInfo.ArgumentList.Add("--fp16");

            using var process = new Process { StartInfo = startInfo };
            onProcessCreated?.Invoke(process);
            var output = new ProcessOutputBuffer();
            process.OutputDataReceived += (_, e) => AppendProcessLine(output, e.Data, progress, TryParseInferenceProgress);
            process.ErrorDataReceived += (_, e) => AppendProcessLine(output, e.Data, progress, TryParseInferenceProgress);

            if (!process.Start())
                throw new InvalidOperationException("无法启动 Python 推理进程。");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                string message = output.ToString();
                if (IsDiskFullMessage(message))
                    throw new IOException(BuildDiskFullError(outputFullPath, message));

                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(message)
                        ? $"推理失败，退出码 {process.ExitCode}。"
                        : message);
            }

            if (!File.Exists(outputFullPath))
                throw new FileNotFoundException("推理进程结束，但没有生成输出文件。", outputFullPath);
        }

        private static void AppendProcessLine(
            ProcessOutputBuffer output,
            string? line,
            IProgress<int>? progress,
            Func<string, int?> progressParser)
        {
            output.AppendLine(line);
            if (line == null || progress == null)
                return;

            int? percent = progressParser(line);
            if (percent.HasValue)
                progress.Report(percent.Value);
        }

        private static int? TryParseInferenceProgress(string line)
        {
            if (!line.Contains("Fuse tiles", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("Infer", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var match = PercentRegex.Match(line);
            if (!match.Success ||
                !double.TryParse(match.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
            {
                return null;
            }

            return Math.Clamp((int)Math.Round(percent), 0, 100);
        }

        private void OnBrowseMs(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 MS 影像",
                Filter = "Raster|*.tif;*.tiff|All files|*.*"
            };
            if (dlg.ShowDialog() == true) MsBox.Text = dlg.FileName;
        }

        private void OnBrowsePan(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 PAN 影像",
                Filter = "Raster|*.tif;*.tiff|All files|*.*"
            };
            if (dlg.ShowDialog() == true) PanBox.Text = dlg.FileName;
        }

        private void OnMsDropDownClosed(object sender, EventArgs e)
        {
            if (MsBox.SelectedItem is RasterLayerInfo info)
                MsBox.Text = info.FilePath;
        }

        private void OnPanDropDownClosed(object sender, EventArgs e)
        {
            if (PanBox.SelectedItem is RasterLayerInfo info)
                PanBox.Text = info.FilePath;
        }

        private void OnBrowseOutput(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "保存融合结果",
                Filter = "GeoTIFF|*.tif|All files|*.*"
            };
            if (dlg.ShowDialog() == true) OutputBox.Text = dlg.FileName;
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            if (!TryBuildRequest(out var request))
                return;

            Request = request;
            DialogResult = true;
        }

        private bool TryBuildRequest(out FusionRequest request)
        {
            request = default!;
            string ms = MsBox.Text.Trim();
            string pan = PanBox.Text.Trim();
            string output = OutputBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(ms))
            {
                MessageBox.Show("请选择 MS 影像。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(pan))
            {
                MessageBox.Show("请选择 PAN 影像。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!File.Exists(ms))
            {
                MessageBox.Show("MS 影像文件不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!File.Exists(pan))
            {
                MessageBox.Show("PAN 影像文件不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(output))
            {
                string dir = Path.GetDirectoryName(ms) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string name = Path.GetFileNameWithoutExtension(ms);
                output = Path.Combine(dir, $"{name}_fused.tif");
            }

            if (!ValidateFusionInputs(ms, pan))
                return false;

            request = new FusionRequest(
                GetPythonExe(),
                GetScriptPath(),
                GetModelPath(),
                ms,
                pan,
                output,
                DefaultFloatScale,
                32,
                false,
                false,
                LoadAfterFusionBox.IsChecked == true);

            return ConfirmOutputDiskSpace(request);
        }

        internal static bool ValidateFusionInputs(string msPath, string panPath, bool showMessages = true)
        {
            try
            {
                using var msDs = Gdal.Open(msPath, Access.GA_ReadOnly);
                using var panDs = Gdal.Open(panPath, Access.GA_ReadOnly);
                if (msDs == null || panDs == null)
                {
                    if (showMessages)
                        MessageBox.Show("GDAL 无法打开输入影像。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (msDs.RasterCount != OutputBandCount)
                {
                    if (showMessages)
                        MessageBox.Show($"MS 影像必须是 {OutputBandCount} 波段，当前为 {msDs.RasterCount} 波段。", "提示",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (panDs.RasterCount != 1)
                {
                    if (showMessages)
                        MessageBox.Show($"PAN 影像必须是 1 波段，当前为 {panDs.RasterCount} 波段。", "提示",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (msDs.RasterXSize != panDs.RasterXSize || msDs.RasterYSize != panDs.RasterYSize)
                {
                    if (showMessages)
                        MessageBox.Show(
                            $"MS/PAN 尺寸不一致，不能直接融合。\n\nMS: {msDs.RasterXSize} x {msDs.RasterYSize}\nPAN: {panDs.RasterXSize} x {panDs.RasterYSize}",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var msGt = new double[6];
                var panGt = new double[6];
                msDs.GetGeoTransform(msGt);
                panDs.GetGeoTransform(panGt);
                if (!GeoTransformsMatch(msGt, panGt))
                {
                    if (showMessages)
                        MessageBox.Show("MS/PAN 的 GeoTransform 不一致，请先使用标准配准结果再融合。", "提示",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (!SpatialReferencesMatch(msDs.GetProjection(), panDs.GetProjection()))
                {
                    if (showMessages)
                        MessageBox.Show("MS/PAN 的坐标系不一致，请先配准到同一坐标系后再融合。", "提示",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                if (showMessages)
                    MessageBox.Show($"融合输入检查失败：{ex.Message}", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private static bool GeoTransformsMatch(double[] a, double[] b)
        {
            double scale = Math.Max(1d, Math.Max(Math.Abs(a[1]), Math.Abs(a[5])));
            double tolerance = scale * 1e-8;
            for (int i = 0; i < 6; i++)
            {
                if (Math.Abs(a[i] - b[i]) > tolerance)
                    return false;
            }

            return true;
        }

        private static bool SpatialReferencesMatch(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
                return true;
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;

            try
            {
                using var srsA = new SpatialReference(a);
                using var srsB = new SpatialReference(b);
                return srsA.IsSame(srsB, Array.Empty<string>()) != 0;
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private bool ConfirmOutputDiskSpace(FusionRequest request)
        {
            try
            {
                long recommendedBytes = EstimateRecommendedOutputBytes(request.MsPath);
                DriveInfo outputDrive = GetOutputDrive(request.OutputPath);
                long freeBytes = outputDrive.AvailableFreeSpace;

                if (freeBytes >= recommendedBytes)
                    return true;

                string message =
                    $"输出盘 {outputDrive.Name} 的剩余空间可能不足。\n\n" +
                    $"建议至少预留：{FormatBytes(recommendedBytes)}\n" +
                    $"当前可用空间：{FormatBytes(freeBytes)}\n\n" +
                    "融合输出为 4 波段 float32 GeoTIFF，虽然已经启用压缩，但大影像仍可能需要较多空间。" +
                    "截图里的 `_tiffWriteProc: No space left on device` 就是写结果时磁盘空间不够。\n\n" +
                    "建议把输出路径改到剩余空间更大的 D/E 盘，或清理磁盘后再运行。仍要继续吗？";

                return MessageBox.Show(message, "磁盘空间可能不足", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                       == MessageBoxResult.Yes;
            }
            catch (Exception ex)
            {
                string message =
                    $"无法提前检查输出空间：{ex.Message}\n\n" +
                    "可以继续运行，但如果目标磁盘空间不足，融合会在写入结果时失败。是否继续？";

                return MessageBox.Show(message, "空间检查失败", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                       == MessageBoxResult.Yes;
            }
        }

        private static long EstimateRecommendedOutputBytes(string msPath)
        {
            using var ds = Gdal.Open(msPath, Access.GA_ReadOnly);
            if (ds == null)
                throw new InvalidDataException($"GDAL 无法打开 MS 影像：{msPath}");

            double rawBytes = (double)ds.RasterXSize * ds.RasterYSize * OutputBandCount * OutputBytesPerSample;
            double recommended = rawBytes * RecommendedSpaceMultiplier + RecommendedSpacePaddingBytes;
            if (recommended >= long.MaxValue)
                return long.MaxValue;

            return (long)Math.Ceiling(recommended);
        }

        private static DriveInfo GetOutputDrive(string outputPath)
        {
            string fullPath = Path.GetFullPath(outputPath);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException($"无法识别输出路径所在磁盘：{outputPath}");

            return new DriveInfo(root);
        }

        private static bool IsDiskFullMessage(string message)
        {
            return message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("There is not enough space", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("磁盘空间不足", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildDiskFullError(string outputPath, string processOutput)
        {
            DriveInfo drive = GetOutputDrive(outputPath);
            string detail = Tail(processOutput, 20);

            return
                $"磁盘空间不足，融合结果写入失败。\n\n" +
                $"输出路径：{outputPath}\n" +
                $"目标盘剩余空间：{FormatBytes(drive.AvailableFreeSpace)}\n\n" +
                "请把输出路径改到剩余空间更大的磁盘，或清理空间后重新运行。上一次失败可能留下未完成的 .tif 文件，建议删除后再试。\n\n" +
                detail;
        }

        private static string Tail(string text, int maxLines)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string[] lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length <= maxLines)
                return text.Trim();

            return string.Join(Environment.NewLine, lines[^maxLines..]);
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.##} {units[unit]}";
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }

    public sealed record FusionRequest(
        string PythonPath,
        string ScriptPath,
        string ModelPath,
        string MsPath,
        string PanPath,
        string OutputPath,
        double FloatScale,
        int Overlap,
        bool NoCuda,
        bool Fp16,
        bool LoadAfterFusion);

    public sealed record RasterLayerInfo(string Name, string FilePath, int BandCount = 0);
}
