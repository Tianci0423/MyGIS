using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using GeoVision.Helpers;
using GeoVision.Services;
using Microsoft.Win32;
using OSGeo.GDAL;

namespace GeoVision.Dialogs
{
    public partial class RasterReprojectionDialog : Window
    {
        private static readonly string[] CommonTargetCrs =
        [
            "EPSG:4326",
            "EPSG:3857",
            "EPSG:4490",
            "EPSG:32648"
        ];

        private string? _autoOutputPath;

        public RasterReprojectionRequest? Request { get; private set; }

        public RasterReprojectionDialog(IReadOnlyList<RasterLayerInfo> rasterLayers)
        {
            InitializeComponent();

            foreach (var target in CommonTargetCrs)
                TargetCrsBox.Items.Add(target);
            TargetCrsBox.Text = "EPSG:4326";

            foreach (var layer in rasterLayers)
                InputBox.Items.Add(layer);

            if (rasterLayers.Count > 0)
            {
                InputBox.SelectedIndex = 0;
                InputBox.Text = rasterLayers[0].FilePath;
                RefreshSourceInfo();
            }
        }

        private void OnInputDropDownClosed(object sender, EventArgs e)
        {
            if (InputBox.SelectedItem is RasterLayerInfo info)
            {
                InputBox.Text = info.FilePath;
                RefreshSourceInfo();
            }
        }

        private void OnInputLostFocus(object sender, RoutedEventArgs e)
            => RefreshSourceInfo();

        private void OnTargetCrsLostFocus(object sender, RoutedEventArgs e)
            => UpdateDefaultOutputPath();

        private void OnSelectTargetCrs(object sender, RoutedEventArgs e)
        {
            var picker = new CoordinateSystemPickerDialog(TargetCrsBox.Text) { Owner = this };
            if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedCrs))
            {
                TargetCrsBox.Text = picker.SelectedCrs;
                UpdateDefaultOutputPath();
            }
        }

        private void OnBrowseInput(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择需要重投影的影像",
                Filter = "栅格影像|*.tif;*.tiff|所有文件|*.*"
            };
            if (dialog.ShowDialog() != true)
                return;

            InputBox.Text = dialog.FileName;
            RefreshSourceInfo();
        }

        private void OnBrowseOutput(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存重投影结果",
                Filter = "GeoTIFF|*.tif;*.tiff",
                DefaultExt = ".tif",
                AddExtension = true,
                FileName = Path.GetFileName(OutputBox.Text)
            };
            if (dialog.ShowDialog() == true)
            {
                OutputBox.Text = dialog.FileName;
                _autoOutputPath = null;
            }
        }

        private void RefreshSourceInfo()
        {
            string path = InputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SourceInfoText.Text = "请选择有效的栅格影像。";
                SourceInfoText.Foreground = System.Windows.Media.Brushes.Firebrick;
                return;
            }

            try
            {
                using var dataset = Gdal.Open(path, Access.GA_ReadOnly)
                    ?? throw new InvalidDataException("GDAL 无法打开该影像。");
                string projection = dataset.GetProjection();
                if (string.IsNullOrWhiteSpace(projection))
                {
                    SourceInfoText.Text =
                        $"尺寸：{dataset.RasterXSize}×{dataset.RasterYSize}，{dataset.RasterCount} 波段；缺少坐标系，请先使用“定义投影”。";
                    SourceInfoText.Foreground = System.Windows.Media.Brushes.Firebrick;
                    UpdateDefaultOutputPath();
                    return;
                }

                string crs = DescribeSpatialReference(projection);
                var transform = new double[6];
                dataset.GetGeoTransform(transform);
                // GDAL geotransform vectors: column/pixel-X = (gt[1], gt[4]),
                // row/pixel-Y = (gt[2], gt[5]).
                double pixelX = Math.Sqrt(transform[1] * transform[1] + transform[4] * transform[4]);
                double pixelY = Math.Sqrt(transform[2] * transform[2] + transform[5] * transform[5]);
                SourceInfoText.Text =
                    $"源坐标系：{crs}\n" +
                    $"尺寸：{dataset.RasterXSize}×{dataset.RasterYSize}，{dataset.RasterCount} 波段；" +
                    $"像元大小：{pixelX:G8} × {pixelY:G8}";
                SourceInfoText.Foreground = System.Windows.Media.Brushes.DarkGreen;
                UpdateDefaultOutputPath();
            }
            catch (Exception ex)
            {
                SourceInfoText.Text = $"读取影像信息失败：{ex.Message}";
                SourceInfoText.Foreground = System.Windows.Media.Brushes.Firebrick;
            }
        }

        private void UpdateDefaultOutputPath()
        {
            string input = InputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
                return;
            if (!string.IsNullOrWhiteSpace(OutputBox.Text) &&
                !string.Equals(OutputBox.Text, _autoOutputPath, StringComparison.OrdinalIgnoreCase))
                return;

            string target = SpatialReferenceHelper.NormalizeInput(TargetCrsBox.Text);
            string suffix = Regex.Replace(target, @"[^A-Za-z0-9]+", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(suffix))
                suffix = "reprojected";
            string directory = Path.GetDirectoryName(input) ?? Environment.CurrentDirectory;
            string name = Path.GetFileNameWithoutExtension(input);
            _autoOutputPath = Path.Combine(directory, $"{name}_{suffix}.tif");
            OutputBox.Text = _autoOutputPath;
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            string input = InputBox.Text.Trim();
            if (!File.Exists(input))
            {
                MessageBox.Show(this, "输入影像不存在。", "投影转换",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryReadProjection(input, out string sourceProjection))
            {
                MessageBox.Show(this, "输入影像没有可用的坐标系。请先使用“定义投影”写入正确的源坐标系。", "投影转换",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!SpatialReferenceHelper.TryParse(TargetCrsBox.Text, out var targetDetails, out string error) ||
                targetDetails == null)
            {
                MessageBox.Show(this, error, "投影转换",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string targetCrs = targetDetails.CanonicalInput;

            double? resolution = null;
            string resolutionText = ResolutionBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(resolutionText))
            {
                bool parsed = double.TryParse(resolutionText, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
                              double.TryParse(resolutionText, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                if (!parsed || !double.IsFinite(value) || value <= 0)
                {
                    MessageBox.Show(this, "目标像元大小必须是大于 0 的有效数字。", "投影转换",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                resolution = value;
            }

            string output = OutputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                MessageBox.Show(this, "请选择输出文件。", "投影转换",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            output = Path.GetFullPath(output);
            if (string.IsNullOrWhiteSpace(Path.GetExtension(output)))
                output += ".tif";
            if (string.Equals(Path.GetFullPath(input), output, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "输出文件不能覆盖输入影像。", "投影转换",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (File.Exists(output) && MessageBox.Show(
                    this,
                    "输出文件已经存在，是否覆盖？",
                    "投影转换",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            if (SpatialReferenceHelper.AreSame(sourceProjection, targetCrs) && resolution == null)
            {
                if (MessageBox.Show(
                        this,
                        "源坐标系和目标坐标系相同，继续执行只会重新生成像元网格。是否继续？",
                        "投影转换",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            string resampling = (ResamplingBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "bilinear";
            Request = new RasterReprojectionRequest(
                Path.GetFullPath(input),
                output,
                targetCrs,
                resampling,
                resolution,
                LoadResultBox.IsChecked == true);
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
            => DialogResult = false;

        private static bool TryReadProjection(string path, out string projection)
        {
            projection = string.Empty;
            try
            {
                using var dataset = Gdal.Open(path, Access.GA_ReadOnly);
                if (dataset == null)
                    return false;
                projection = dataset.GetProjection();
                return !string.IsNullOrWhiteSpace(projection);
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeSpatialReference(string wkt)
        {
            return SpatialReferenceHelper.TryParse(wkt, out var details, out _) && details != null
                ? $"{details.Identifier} — {details.DisplayName}（{details.Type}）"
                : "已定义（无法解析名称）";
        }
    }
}
