using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using GeoVision.Helpers;
using GeoVision.Services;
using Microsoft.Win32;
using OSGeo.GDAL;

namespace GeoVision.Dialogs
{
    public partial class RasterDefineProjectionDialog : Window
    {
        private string? _autoOutputPath;
        private string? _sourceProjection;

        public RasterDefineProjectionRequest? Request { get; private set; }

        public RasterDefineProjectionDialog(IReadOnlyList<RasterLayerInfo> rasterLayers)
        {
            InitializeComponent();
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

        private void OnTargetLostFocus(object sender, RoutedEventArgs e)
            => UpdateDefaultOutputPath();

        private void OnBrowseInput(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择需要定义投影的影像",
                Filter = "GeoTIFF|*.tif;*.tiff|所有文件|*.*"
            };
            if (dialog.ShowDialog() != true)
                return;
            InputBox.Text = dialog.FileName;
            RefreshSourceInfo();
        }

        private void OnSelectCrs(object sender, RoutedEventArgs e)
        {
            var picker = new CoordinateSystemPickerDialog(TargetCrsBox.Text) { Owner = this };
            if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedCrs))
            {
                TargetCrsBox.Text = picker.SelectedCrs;
                UpdateDefaultOutputPath();
            }
        }

        private void OnBrowseOutput(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存定义投影后的影像",
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

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (OutputRow == null || LoadResultBox == null)
                return;
            bool inPlace = InPlaceModeRadio.IsChecked == true;
            OutputRow.Visibility = inPlace ? Visibility.Collapsed : Visibility.Visible;
            LoadResultBox.IsEnabled = !inPlace;
            LoadResultBox.IsChecked = !inPlace;
        }

        private void RefreshSourceInfo()
        {
            string path = InputBox.Text.Trim();
            _sourceProjection = null;
            if (!File.Exists(path))
            {
                SourceInfoText.Text = "请选择有效的 GeoTIFF 影像。";
                SourceInfoText.Foreground = System.Windows.Media.Brushes.Firebrick;
                return;
            }

            try
            {
                using var dataset = Gdal.Open(path, Access.GA_ReadOnly)
                    ?? throw new InvalidDataException("GDAL 无法打开该影像。");
                _sourceProjection = dataset.GetProjection();
                string currentDefinition;
                if (string.IsNullOrWhiteSpace(_sourceProjection))
                {
                    currentDefinition = "未定义";
                    SourceInfoText.Foreground = System.Windows.Media.Brushes.DarkOrange;
                }
                else if (SpatialReferenceHelper.TryParse(_sourceProjection, out var details, out _) && details != null)
                {
                    currentDefinition = $"{details.Identifier} — {details.DisplayName}（{details.Type}）";
                    SourceInfoText.Foreground = System.Windows.Media.Brushes.DarkGreen;
                }
                else
                {
                    currentDefinition = "已定义，但无法解析";
                    SourceInfoText.Foreground = System.Windows.Media.Brushes.DarkOrange;
                }

                SourceInfoText.Text =
                    $"当前坐标系：{currentDefinition}\n" +
                    $"尺寸：{dataset.RasterXSize}×{dataset.RasterYSize}，{dataset.RasterCount} 波段";
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
            if (!File.Exists(input) || InPlaceModeRadio.IsChecked == true)
                return;
            if (!string.IsNullOrWhiteSpace(OutputBox.Text) &&
                !string.Equals(OutputBox.Text, _autoOutputPath, StringComparison.OrdinalIgnoreCase))
                return;

            string suffix = "defined";
            if (SpatialReferenceHelper.TryParse(TargetCrsBox.Text, out var details, out _) && details != null)
                suffix = Regex.Replace(details.Identifier, @"[^A-Za-z0-9]+", "_").Trim('_');
            string directory = Path.GetDirectoryName(input) ?? Environment.CurrentDirectory;
            string name = Path.GetFileNameWithoutExtension(input);
            _autoOutputPath = Path.Combine(directory, $"{name}_defined_{suffix}.tif");
            OutputBox.Text = _autoOutputPath;
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            string input = InputBox.Text.Trim();
            if (!File.Exists(input))
            {
                MessageBox.Show(this, "输入影像不存在。", "定义投影",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string extension = Path.GetExtension(input);
            if (!extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "当前版本的定义投影仅支持 GeoTIFF（.tif/.tiff）。", "定义投影",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!SpatialReferenceHelper.TryParse(TargetCrsBox.Text, out var targetDetails, out string error) ||
                targetDetails == null)
            {
                MessageBox.Show(this, error, "定义投影",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool inPlace = InPlaceModeRadio.IsChecked == true;
            string output = input;
            if (!inPlace)
            {
                output = OutputBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(output))
                {
                    MessageBox.Show(this, "请选择输出文件。", "定义投影",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                output = Path.GetFullPath(output);
                if (string.IsNullOrWhiteSpace(Path.GetExtension(output)))
                    output += ".tif";
                string outputExtension = Path.GetExtension(output);
                if (!outputExtension.Equals(".tif", StringComparison.OrdinalIgnoreCase) &&
                    !outputExtension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "输出文件必须是 GeoTIFF（.tif/.tiff）。", "定义投影",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.Equals(Path.GetFullPath(input), output, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "创建新影像时输出路径不能与输入影像相同。", "定义投影",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (File.Exists(output) && MessageBox.Show(
                        this, "输出文件已经存在，是否覆盖？", "定义投影",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(_sourceProjection))
            {
                bool same = SpatialReferenceHelper.AreSame(_sourceProjection, targetDetails.CanonicalInput);
                string message = same
                    ? "影像当前坐标系与所选坐标系相同。仍要重新写入坐标系定义吗？"
                    : "影像已有不同的坐标系定义。\n\n定义投影不会转换坐标或移动像元，只有在现有标签错误、且像元坐标本来就属于新坐标系时才能继续。是否确认更正定义？";
                if (MessageBox.Show(this, message, "确认定义投影",
                        MessageBoxButton.YesNo, same ? MessageBoxImage.Question : MessageBoxImage.Warning)
                    != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            if (inPlace && MessageBox.Show(
                    this,
                    "将直接替换输入影像的坐标系定义。原始像元不会改变，但错误定义会导致影像显示在错误位置。是否继续？",
                    "确认直接修改",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            Request = new RasterDefineProjectionRequest(
                Path.GetFullPath(input),
                Path.GetFullPath(output),
                targetDetails.CanonicalInput,
                targetDetails.Wkt,
                inPlace,
                !inPlace && LoadResultBox.IsChecked == true);
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }
}
