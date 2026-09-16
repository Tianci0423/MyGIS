using System.IO;
using System.Windows;
using System.Windows.Controls;
using GeoVision.Services;
using Microsoft.Win32;
using OSGeo.GDAL;

namespace GeoVision.Dialogs
{
    public partial class RasterMosaicDialog : Window
    {
        private string? _autoOutputPath;

        public RasterMosaicRequest? Request { get; private set; }

        public RasterMosaicDialog(IReadOnlyList<RasterLayerInfo> rasterLayers)
        {
            InitializeComponent();
            foreach (var layer in rasterLayers)
            {
                BaseBox.Items.Add(layer);
                OverlayBox.Items.Add(layer);
            }

            if (rasterLayers.Count > 0)
            {
                BaseBox.SelectedIndex = 0;
                BaseBox.Text = rasterLayers[0].FilePath;
            }
            if (rasterLayers.Count > 1)
            {
                OverlayBox.SelectedIndex = 1;
                OverlayBox.Text = rasterLayers[1].FilePath;
            }
            RefreshInputInfo();
        }

        private void OnInputChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox box && box.SelectedItem is RasterLayerInfo info)
                box.Text = info.FilePath;
            RefreshInputInfo();
        }

        private void OnInputLostFocus(object sender, RoutedEventArgs e)
            => RefreshInputInfo();

        private void OnBrowseBase(object sender, RoutedEventArgs e)
            => BrowseInput(BaseBox, "选择基准影像");

        private void OnBrowseOverlay(object sender, RoutedEventArgs e)
            => BrowseInput(OverlayBox, "选择待拼接影像");

        private void BrowseInput(ComboBox target, string title)
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = "栅格影像|*.tif;*.tiff|所有文件|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                target.Text = dialog.FileName;
                RefreshInputInfo();
            }
        }

        private void OnBrowseOutput(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存影像拼接结果",
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

        private void RefreshInputInfo()
        {
            if (InputInfoText == null)
                return;

            string basePath = BaseBox.Text.Trim();
            string overlayPath = OverlayBox.Text.Trim();
            UpdateDefaultOutputPath(basePath);
            if (!File.Exists(basePath) || !File.Exists(overlayPath))
            {
                InputInfoText.Text = "请选择两幅有效的 GeoTIFF 影像。";
                InputInfoText.Foreground = System.Windows.Media.Brushes.Firebrick;
                return;
            }

            try
            {
                using var baseDataset = Gdal.Open(basePath, Access.GA_ReadOnly)
                    ?? throw new InvalidDataException("GDAL 无法打开基准影像。");
                using var overlayDataset = Gdal.Open(overlayPath, Access.GA_ReadOnly)
                    ?? throw new InvalidDataException("GDAL 无法打开待拼接影像。");
                bool baseCrs = !string.IsNullOrWhiteSpace(baseDataset.GetProjection());
                bool overlayCrs = !string.IsNullOrWhiteSpace(overlayDataset.GetProjection());
                InputInfoText.Text =
                    $"基准：{baseDataset.RasterXSize}×{baseDataset.RasterYSize}，{baseDataset.RasterCount} 波段；" +
                    $"待拼接：{overlayDataset.RasterXSize}×{overlayDataset.RasterYSize}，{overlayDataset.RasterCount} 波段。\n" +
                    $"地理参考：基准影像{(baseCrs ? "有效" : "缺失")}，待拼接影像{(overlayCrs ? "有效" : "缺失")}。";
                InputInfoText.Foreground = baseCrs && overlayCrs &&
                                                baseDataset.RasterCount == overlayDataset.RasterCount
                    ? System.Windows.Media.Brushes.DarkGreen
                    : System.Windows.Media.Brushes.Firebrick;
            }
            catch (Exception ex)
            {
                InputInfoText.Text = $"读取影像信息失败：{ex.Message}";
                InputInfoText.Foreground = System.Windows.Media.Brushes.Firebrick;
            }
        }

        private void UpdateDefaultOutputPath(string basePath)
        {
            if (!File.Exists(basePath))
                return;
            if (!string.IsNullOrWhiteSpace(OutputBox.Text) &&
                !string.Equals(OutputBox.Text, _autoOutputPath, StringComparison.OrdinalIgnoreCase))
                return;

            string directory = Path.GetDirectoryName(basePath) ?? Environment.CurrentDirectory;
            string name = Path.GetFileNameWithoutExtension(basePath);
            _autoOutputPath = Path.Combine(directory, $"{name}_mosaic.tif");
            OutputBox.Text = _autoOutputPath;
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            string basePath = BaseBox.Text.Trim();
            string overlayPath = OverlayBox.Text.Trim();
            if (!File.Exists(basePath) || !File.Exists(overlayPath))
            {
                MessageBox.Show(this, "请选择两幅存在的输入影像。", "影像拼接",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.Equals(Path.GetFullPath(basePath), Path.GetFullPath(overlayPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "基准影像和待拼接影像不能是同一个文件。", "影像拼接",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(FeatherBox.Text.Trim(), out int feather) || feather < 0 || feather > 1024)
            {
                MessageBox.Show(this, "羽化距离必须是 0～1024 之间的整数。", "影像拼接",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string output = OutputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                MessageBox.Show(this, "请选择输出文件。", "影像拼接",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            output = Path.GetFullPath(output);
            if (string.IsNullOrWhiteSpace(Path.GetExtension(output)))
                output += ".tif";
            if (new[] { basePath, overlayPath }.Any(path => string.Equals(
                    Path.GetFullPath(path), output, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "输出文件不能覆盖输入影像。", "影像拼接",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (File.Exists(output) && MessageBox.Show(this, "输出文件已经存在，是否覆盖？",
                    "影像拼接", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            string balanceText = (BalanceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Histogram";
            Enum.TryParse(balanceText, out MosaicColorBalance balance);
            string resampling = (ResamplingBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cubic";
            Request = new RasterMosaicRequest(
                Path.GetFullPath(basePath), Path.GetFullPath(overlayPath), output,
                balance, feather, resampling, LoadResultBox.IsChecked == true);
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }
}
