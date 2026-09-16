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
    public partial class RegistrationDialog : Window
    {
        private static readonly Regex RegistrationProgressRegex = new(
            @"\b(?:GPU\s+progress|Progress):\s*(?<percent>\d+(?:\.\d+)?)%",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private const double StandardGridSpaceMultiplier = 1.15;
        private const double GeneralRpcSpaceMultiplier = 1.70;
        private const long OutputSpacePaddingBytes = 512L * 1024 * 1024;

        public RegistrationRequest? Request { get; private set; }

        public RegistrationDialog(IReadOnlyList<RasterLayerInfo>? availableLayers = null)
        {
            InitializeComponent();

            if (availableLayers is { Count: > 0 })
            {
                foreach (var info in availableLayers)
                {
                    if (info.BandCount == 4)
                        MsBox.Items.Add(info);
                    else if (info.BandCount == 1)
                        PanBox.Items.Add(info);
                }
            }
        }

        private static string ReadCrs(Dataset ds)
        {
            try
            {
                string wkt = ds.GetProjection();
                if (string.IsNullOrWhiteSpace(wkt)) return "";
                using var sr = new SpatialReference(wkt);
                string? auth = sr.GetAuthorityName(null);
                string? code = sr.GetAuthorityCode(null);
                if (auth == "EPSG" && !string.IsNullOrWhiteSpace(code))
                    return $"EPSG:{code}";
                string? name = sr.GetName();
                return !string.IsNullOrWhiteSpace(name) ? name : "";
            }
            catch { return ""; }
        }

        private static (double Left, double Bottom, double Right, double Top) GetExtent(Dataset ds)
        {
            double[] gt = new double[6];
            ds.GetGeoTransform(gt);

            int w = ds.RasterXSize, h = ds.RasterYSize;
            double x0 = gt[0], y0 = gt[3];
            double x1 = gt[0] + gt[1] * w;
            double y1 = gt[3] + gt[5] * h;
            double x2 = gt[0] + gt[2] * h;
            double y2 = gt[3] + gt[4] * w;
            double x3 = gt[0] + gt[1] * w + gt[2] * h;
            double y3 = gt[3] + gt[4] * w + gt[5] * h;

            return (
                Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)),
                Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)),
                Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)),
                Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)));
        }

        private static bool Intersects(
            (double Left, double Bottom, double Right, double Top) a,
            (double Left, double Bottom, double Right, double Top) b)
        {
            return a.Left < b.Right && a.Right > b.Left &&
                   a.Bottom < b.Top && a.Top > b.Bottom;
        }

        private static bool HasRpcMetadata(Dataset ds)
        {
            try
            {
                string[] metadata = ds.GetMetadata("RPC") ?? Array.Empty<string>();
                return metadata.Any(item => item.StartsWith("LINE_OFF=", StringComparison.OrdinalIgnoreCase)) &&
                       metadata.Any(item => item.StartsWith("SAMP_OFF=", StringComparison.OrdinalIgnoreCase)) &&
                       metadata.Any(item => item.StartsWith("LINE_NUM_COEFF=", StringComparison.OrdinalIgnoreCase)) &&
                       metadata.Any(item => item.StartsWith("SAMP_NUM_COEFF=", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetRpcPixelGridRatio(Dataset msDs, Dataset panDs, out int ratio)
        {
            ratio = 0;
            if (!string.IsNullOrWhiteSpace(ReadCrs(msDs)) ||
                !string.IsNullOrWhiteSpace(ReadCrs(panDs)) ||
                !HasRpcMetadata(msDs) ||
                !HasRpcMetadata(panDs) ||
                msDs.RasterXSize <= 0 || msDs.RasterYSize <= 0 ||
                panDs.RasterXSize % msDs.RasterXSize != 0 ||
                panDs.RasterYSize % msDs.RasterYSize != 0)
            {
                return false;
            }

            int ratioX = panDs.RasterXSize / msDs.RasterXSize;
            int ratioY = panDs.RasterYSize / msDs.RasterYSize;
            if (ratioX < 1 || ratioX != ratioY)
                return false;

            ratio = ratioX;
            return true;
        }

        internal static bool ValidateRegistrationInputs(string ms, string pan, bool showMessages)
        {
            try
            {
                using var msDs = Gdal.Open(ms, Access.GA_ReadOnly);
                using var panDs = Gdal.Open(pan, Access.GA_ReadOnly);
                if (msDs == null || panDs == null)
                {
                    if (showMessages) MessageBox.Show("GDAL 无法打开输入影像。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (msDs.RasterCount != 4)
                {
                    if (showMessages) MessageBox.Show($"MS 影像必须是 4 波段，当前为 {msDs.RasterCount} 波段。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                if (panDs.RasterCount != 1)
                {
                    if (showMessages) MessageBox.Show($"PAN 影像必须是 1 波段，当前为 {panDs.RasterCount} 波段。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                string crsMs = ReadCrs(msDs);
                string crsPan = ReadCrs(panDs);
                if (string.IsNullOrWhiteSpace(crsMs) || string.IsNullOrWhiteSpace(crsPan))
                {
                    if (string.IsNullOrWhiteSpace(crsMs) &&
                        string.IsNullOrWhiteSpace(crsPan) &&
                        HasRpcMetadata(msDs) &&
                        HasRpcMetadata(panDs))
                    {
                        return true;
                    }

                    if (showMessages)
                        MessageBox.Show(
                            "MS/PAN 缺少坐标系。双方必须都包含有效 RPC/RPB，才能进行像素网格配准或通用 RPC 正射配准。",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (crsMs == crsPan && !Intersects(GetExtent(msDs), GetExtent(panDs)))
                {
                    if (showMessages) MessageBox.Show("MS 与 PAN 地理范围无重叠，无法生成融合输入。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                if (showMessages) MessageBox.Show($"输入检查失败: {ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private void RunValidation()
        {
            string ms = MsBox.Text.Trim();
            string pan = PanBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(ms) || string.IsNullOrWhiteSpace(pan))
            {
                ValidationPanel.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                if (!File.Exists(ms) || !File.Exists(pan))
                {
                    ShowValidationError("文件不存在。");
                    return;
                }

                using var msDs = Gdal.Open(ms, Access.GA_ReadOnly);
                using var panDs = Gdal.Open(pan, Access.GA_ReadOnly);

                if (msDs == null || panDs == null)
                {
                    ShowValidationError("GDAL 无法打开文件。");
                    return;
                }

                double[] gtMs = new double[6], gtPan = new double[6];
                msDs.GetGeoTransform(gtMs);
                panDs.GetGeoTransform(gtPan);

                string crsMs = ReadCrs(msDs);
                string crsPan = ReadCrs(panDs);

                int wMs = msDs.RasterXSize, hMs = msDs.RasterYSize, bandsMs = msDs.RasterCount;
                int wPan = panDs.RasterXSize, hPan = panDs.RasterYSize, bandsPan = panDs.RasterCount;

                bool crsMatch = !string.IsNullOrWhiteSpace(crsMs) && !string.IsNullOrWhiteSpace(crsPan) &&
                                crsMs == crsPan;
                bool hasOverlap = crsMatch && Intersects(GetExtent(msDs), GetExtent(panDs));
                bool bandReady = bandsMs == 4 && bandsPan == 1;

                // Build results
                string crsLabelMs = string.IsNullOrWhiteSpace(crsMs) ? "无" : crsMs;
                string crsLabelPan = string.IsNullOrWhiteSpace(crsPan) ? "无" : crsPan;

                ValMsInfo.Text = $"MS:  {bandsMs} 波段, {wMs}×{hMs}, {crsLabelMs}";
                ValPanInfo.Text = $"PAN: {bandsPan} 波段, {wPan}×{hPan}, {crsLabelPan}";

                ValBandCheck.Text = bandReady
                    ? "✓ 波段数符合融合要求 (MS 4 波段 / PAN 1 波段)"
                    : $"✗ 波段数不符合融合要求 (MS: {bandsMs}, PAN: {bandsPan})";
                ValBandCheck.Foreground = bandReady
                    ? System.Windows.Media.Brushes.Green
                    : System.Windows.Media.Brushes.Red;

                // CRS check
                bool msHasCrs = !string.IsNullOrWhiteSpace(crsMs);
                bool panHasCrs = !string.IsNullOrWhiteSpace(crsPan);
                int rpcPixelRatio = 0;
                bool rpcPixelGridReady = !msHasCrs && !panHasCrs &&
                                         TryGetRpcPixelGridRatio(msDs, panDs, out rpcPixelRatio);
                bool generalRpcReady = !msHasCrs && !panHasCrs && !rpcPixelGridReady &&
                                       HasRpcMetadata(msDs) && HasRpcMetadata(panDs);
                if (rpcPixelGridReady)
                {
                    ValCrsCheck.Text = $"✓ 缺少坐标系，但检测到双方 RPC/RPB 和严格 {rpcPixelRatio}× 像素网格";
                    ValCrsCheck.Foreground = System.Windows.Media.Brushes.Green;
                }
                else if (generalRpcReady)
                {
                    ValCrsCheck.Text = "✓ 缺少坐标系，但双方均包含 RPC/RPB，将使用通用 RPC 正射配准";
                    ValCrsCheck.Foreground = System.Windows.Media.Brushes.Green;
                }
                else
                {
                    ValCrsCheck.Text = msHasCrs && panHasCrs
                        ? (crsMatch ? "✓ 坐标系一致" : $"⚠ 坐标系不一致，将重投影到 PAN 坐标系 (MS: {crsLabelMs}, PAN: {crsLabelPan})")
                        : $"✗ 缺少地理标签 (MS: {crsLabelMs}, PAN: {crsLabelPan})";
                    ValCrsCheck.Foreground = msHasCrs && panHasCrs && crsMatch
                        ? System.Windows.Media.Brushes.Green
                        : msHasCrs && panHasCrs
                            ? System.Windows.Media.Brushes.DarkOrange
                            : System.Windows.Media.Brushes.Red;
                }

                // Overlap check
                if (rpcPixelGridReady)
                {
                    ValOverlapCheck.Text = $"✓ 将按 RPC 一致的 {rpcPixelRatio}× 像素关系把 MS 重采样到 PAN 网格；运行时会继续校验 RPC 系数";
                    ValOverlapCheck.Foreground = System.Windows.Media.Brushes.Green;
                }
                else if (generalRpcReady)
                {
                    ValOverlapCheck.Text = "✓ 将根据双方各自 RPC 投影到 PAN 分辨率的共同地图网格，并裁剪共同有效区";
                    ValOverlapCheck.Foreground = System.Windows.Media.Brushes.Green;
                }
                else if (crsMatch)
                {
                    ValOverlapCheck.Text = hasOverlap
                        ? "✓ 地理范围有重叠，将裁剪为共同覆盖区"
                        : "✗ 地理范围无重叠 — 无法生成融合输入";
                    ValOverlapCheck.Foreground = hasOverlap
                        ? System.Windows.Media.Brushes.Green
                        : System.Windows.Media.Brushes.Red;
                }
                else
                {
                    ValOverlapCheck.Text = "⚠ 坐标系不同，精确重叠范围将由配准脚本校验";
                    ValOverlapCheck.Foreground = System.Windows.Media.Brushes.DarkOrange;
                }

                ValidationPanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ShowValidationError($"验证失败: {ex.Message}");
            }
        }

        private void ShowValidationError(string msg)
        {
            ValMsInfo.Text = msg;
            ValPanInfo.Text = "";
            ValBandCheck.Text = "";
            ValCrsCheck.Text = "";
            ValOverlapCheck.Text = "";
            ValMsInfo.Foreground = System.Windows.Media.Brushes.Red;
            ValidationPanel.Visibility = Visibility.Visible;
        }

        internal static string GetPythonExe()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string exe = Path.Combine(baseDir, "python_env", "runtime", "python", "python.exe");
            return File.Exists(exe) ? exe : "python";
        }

        internal static string GetScriptPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, "python_env", "runtime", "register.py");
        }

        internal static long EstimateRecommendedOutputBytes(RegistrationRequest request)
        {
            using var msDs = Gdal.Open(request.MsPath, Access.GA_ReadOnly);
            using var panDs = Gdal.Open(request.PanPath, Access.GA_ReadOnly);
            if (msDs == null || panDs == null)
                throw new InvalidDataException("GDAL 无法打开 MS/PAN 输入影像，不能估算配准输出空间。");

            int msBytesPerSample = GetDataTypeBytes(msDs.GetRasterBand(1).DataType);
            int panBytesPerSample = GetDataTypeBytes(panDs.GetRasterBand(1).DataType);
            double bytesPerTargetPixel =
                (double)msDs.RasterCount * msBytesPerSample +
                (double)panDs.RasterCount * panBytesPerSample;

            bool generalRpcOrtho =
                string.IsNullOrWhiteSpace(ReadCrs(msDs)) &&
                string.IsNullOrWhiteSpace(ReadCrs(panDs)) &&
                HasRpcMetadata(msDs) &&
                HasRpcMetadata(panDs) &&
                !TryGetRpcPixelGridRatio(msDs, panDs, out _);

            // 通用 RPC 正射会先生成北向上的外包矩形，面积通常大于原始 PAN。
            // 其余路径的共同覆盖区不会明显超过 PAN 网格，仅保留少量 TIFF/块边界余量。
            double areaMultiplier = generalRpcOrtho
                ? GeneralRpcSpaceMultiplier
                : StandardGridSpaceMultiplier;

            double estimated =
                (double)panDs.RasterXSize *
                panDs.RasterYSize *
                bytesPerTargetPixel *
                areaMultiplier +
                OutputSpacePaddingBytes;

            return estimated >= long.MaxValue
                ? long.MaxValue
                : (long)Math.Ceiling(estimated);
        }

        internal static DriveInfo GetOutputDrive(string outputPath)
        {
            string fullPath = Path.GetFullPath(outputPath);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException($"无法识别输出路径所在磁盘：{outputPath}");

            return new DriveInfo(root);
        }

        internal static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            double value = Math.Max(0, bytes);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:F1} {units[unit]}";
        }

        private static int GetDataTypeBytes(DataType dataType)
        {
            return dataType switch
            {
                DataType.GDT_Byte or DataType.GDT_Int8 => 1,
                DataType.GDT_UInt16 or DataType.GDT_Int16 or DataType.GDT_Float16 => 2,
                DataType.GDT_UInt32 or DataType.GDT_Int32 or DataType.GDT_Float32 or
                    DataType.GDT_CInt16 => 4,
                DataType.GDT_UInt64 or DataType.GDT_Int64 or DataType.GDT_Float64 or
                    DataType.GDT_CInt32 or DataType.GDT_CFloat32 => 8,
                DataType.GDT_CFloat64 => 16,
                _ => 8
            };
        }

        private static void EnsureOutputDiskSpace(RegistrationRequest request)
        {
            long recommendedBytes = EstimateRecommendedOutputBytes(request);
            DriveInfo drive = GetOutputDrive(request.OutMsPath);
            long freeBytes = drive.AvailableFreeSpace;
            if (freeBytes >= recommendedBytes)
                return;

            throw new IOException(
                $"输出盘 {drive.Name} 空间不足，已在配准开始前停止。\n\n" +
                $"本任务建议预留：{FormatBytes(recommendedBytes)}\n" +
                $"当前可用空间：{FormatBytes(freeBytes)}\n" +
                $"输出目录：{Path.GetDirectoryName(request.OutMsPath)}\n\n" +
                "请清理磁盘空间，或把输出目录改到空间更大的磁盘后重试。");
        }

        public static async Task RunRegistrationAsync(
            RegistrationRequest request,
            Action<Process>? onProcessCreated = null,
            IProgress<int>? progress = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutMsPath)!);
            EnsureOutputDiskSpace(request);

            string scriptDir = Path.GetDirectoryName(request.ScriptPath) ?? Environment.CurrentDirectory;
            var startInfo = new ProcessStartInfo
            {
                FileName = request.PythonPath,
                WorkingDirectory = scriptDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.ArgumentList.Add(request.ScriptPath);
            startInfo.ArgumentList.Add("--ms_path");
            startInfo.ArgumentList.Add(request.MsPath);
            startInfo.ArgumentList.Add("--pan_path");
            startInfo.ArgumentList.Add(request.PanPath);
            startInfo.ArgumentList.Add("--out_ms");
            startInfo.ArgumentList.Add(request.OutMsPath);
            startInfo.ArgumentList.Add("--out_pan");
            startInfo.ArgumentList.Add(request.OutPanPath);
            startInfo.ArgumentList.Add("--resample");
            startInfo.ArgumentList.Add(request.ResampleMethod);
            if (request.KeepBlackBorder)
                startInfo.ArgumentList.Add("--keep_black_border");

            using var process = new Process { StartInfo = startInfo };
            onProcessCreated?.Invoke(process);
            var output = new ProcessOutputBuffer();
            process.OutputDataReceived += (_, e) => AppendProcessLine(output, e.Data, progress);
            process.ErrorDataReceived += (_, e) => AppendProcessLine(output, e.Data, progress);

            if (!process.Start())
                throw new InvalidOperationException("无法启动 Python 配准进程。");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                string processOutput = output.ToString();
                if (IsDiskFullMessage(processOutput))
                {
                    DeleteIncompleteOutput(request.OutMsPath);
                    DeleteIncompleteOutput(request.OutPanPath);
                    DriveInfo drive = GetOutputDrive(request.OutMsPath);
                    throw new IOException(
                        $"输出盘 {drive.Name} 空间不足，配准结果写入失败；未完成的输出已清理。\n\n" +
                        $"当前可用空间：{FormatBytes(drive.AvailableFreeSpace)}\n" +
                        $"输出目录：{Path.GetDirectoryName(request.OutMsPath)}\n\n" +
                        "请清理磁盘空间，或更换输出目录后重试。");
                }

                throw new InvalidOperationException(output.Length == 0
                    ? $"配准失败，退出码 {process.ExitCode}。"
                    : processOutput);
            }

            if (!File.Exists(request.OutMsPath) || !File.Exists(request.OutPanPath))
                throw new FileNotFoundException("配准进程结束，但没有生成所有输出文件。");
        }

        private static bool IsDiskFullMessage(string message)
        {
            return message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("There is not enough space", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("not enough space on the disk", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("磁盘空间不足", StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteIncompleteOutput(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 清理失败不覆盖真正的配准错误。
            }
        }

        private static void AppendProcessLine(
            ProcessOutputBuffer output,
            string? line,
            IProgress<int>? progress)
        {
            output.AppendLine(line);
            if (line == null || progress == null)
                return;

            int? percent = TryParseRegistrationProgress(line);
            if (percent.HasValue)
                progress.Report(percent.Value);
        }

        private static int? TryParseRegistrationProgress(string line)
        {
            var match = RegistrationProgressRegex.Match(line);
            if (match.Success &&
                double.TryParse(match.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
            {
                return Math.Clamp((int)Math.Round(percent), 0, 100);
            }

            if (line.Contains("Scanning MS/PAN common valid data boundary", StringComparison.OrdinalIgnoreCase))
                return 5;
            if (line.Contains("Building warp VRT", StringComparison.OrdinalIgnoreCase))
                return 12;
            if (line.Contains("Warp VRT ready", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("GPU warp engine", StringComparison.OrdinalIgnoreCase))
                return 20;
            if (line.Contains("MS output saved", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("PAN output saved", StringComparison.OrdinalIgnoreCase))
                return 95;
            if (line.Contains("Registration complete", StringComparison.OrdinalIgnoreCase))
                return 100;

            return null;
        }

        private void OnMsDropDownClosed(object sender, EventArgs e)
        {
            if (MsBox.SelectedItem is RasterLayerInfo info)
                MsBox.Text = info.FilePath;
            UpdateOutputPreview();
        }

        private void OnPanDropDownClosed(object sender, EventArgs e)
        {
            if (PanBox.SelectedItem is RasterLayerInfo info)
                PanBox.Text = info.FilePath;
            UpdateOutputPreview();
        }

        private void OnMsTextChanged(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateOutputPreview),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnPanTextChanged(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateOutputPreview),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnBrowseMs(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 MS 影像",
                Filter = "Raster|*.tif;*.tiff|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                MsBox.Text = dlg.FileName;
                UpdateOutputPreview();
            }
        }

        private void OnBrowsePan(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 PAN 影像",
                Filter = "Raster|*.tif;*.tiff|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                PanBox.Text = dlg.FileName;
                UpdateOutputPreview();
            }
        }

        private void OnBrowseOutputDir(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择输出目录"
            };
            if (dlg.ShowDialog() == true)
            {
                OutputDirBox.Text = dlg.FolderName;
            }
        }

        private void OnOutputDirChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateOutputPreview();
        }

        private void UpdateOutputPreview()
        {
            string ms = MsBox.Text.Trim();
            string pan = PanBox.Text.Trim();
            string dir = OutputDirBox.Text.Trim();
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            if (!string.IsNullOrWhiteSpace(ms) && !string.IsNullOrWhiteSpace(dir))
            {
                string name = Path.GetFileNameWithoutExtension(ms);
                MsOutPreview.Text = $"MS:  {Path.Combine(dir, $"{name}_aligned_ms_{ts}.tif")}";
            }
            else
            {
                MsOutPreview.Text = "MS: —";
            }

            if (!string.IsNullOrWhiteSpace(pan) && !string.IsNullOrWhiteSpace(dir))
            {
                string name = Path.GetFileNameWithoutExtension(pan);
                PanOutPreview.Text = $"PAN: {Path.Combine(dir, $"{name}_aligned_pan_{ts}.tif")}";
            }
            else
            {
                PanOutPreview.Text = "PAN: —";
            }

            RunValidation();
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            if (!TryBuildRequest(out var request))
                return;

            Request = request;
            DialogResult = true;
        }

        private bool TryBuildRequest(out RegistrationRequest request)
        {
            request = default!;
            string ms = MsBox.Text.Trim();
            string pan = PanBox.Text.Trim();
            string dir = OutputDirBox.Text.Trim();

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
            if (string.IsNullOrWhiteSpace(dir))
            {
                MessageBox.Show("请选择输出目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex)
                {
                    MessageBox.Show($"无法创建输出目录: {ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            if (!ValidateRegistrationInputs(ms, pan, showMessages: true))
                return false;

            string resample = "bilinear";
            if (ResampleBox.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag != null)
                resample = item.Tag.ToString()!;

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string msOut = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(ms)}_aligned_ms_{ts}.tif");
            string panOut = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(pan)}_aligned_pan_{ts}.tif");

            request = new RegistrationRequest(
                GetPythonExe(),
                GetScriptPath(),
                ms,
                pan,
                msOut,
                panOut,
                resample,
                LoadAfterRegistrationBox.IsChecked == true,
                KeepBlackBorderBox.IsChecked == true);
            return true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }

    public sealed record RegistrationRequest(
        string PythonPath,
        string ScriptPath,
        string MsPath,
        string PanPath,
        string OutMsPath,
        string OutPanPath,
        string ResampleMethod,
        bool LoadAfterRegistration,
        bool KeepBlackBorder);
}
