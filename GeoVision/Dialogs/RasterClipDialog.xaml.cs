using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GeoVision.Dialogs
{
    public enum RasterClipMode
    {
        CurrentView,
        Rectangle,
        VectorBoundary
    }

    public sealed record VectorClipLayerInfo(string Name, string FilePath, string? Crs);

    public sealed record RasterClipRequest(
        string RasterPath,
        string OutputPath,
        RasterClipMode Mode,
        string? VectorPath,
        bool LoadResult);

    public partial class RasterClipDialog : Window
    {
        public RasterClipRequest? Request { get; private set; }

        public RasterClipDialog(
            IReadOnlyList<RasterLayerInfo> rasterLayers,
            IReadOnlyList<VectorClipLayerInfo> vectorLayers)
        {
            InitializeComponent();
            RasterCombo.ItemsSource = rasterLayers;
            VectorCombo.ItemsSource = vectorLayers;
            if (rasterLayers.Count > 0)
                RasterCombo.SelectedIndex = 0;
            if (vectorLayers.Count > 0)
                VectorCombo.SelectedIndex = 0;
        }

        private RasterClipMode SelectedMode
        {
            get
            {
                if (ModeCombo.SelectedItem is ComboBoxItem item &&
                    Enum.TryParse<RasterClipMode>(item.Tag?.ToString(), out var mode))
                {
                    return mode;
                }

                return RasterClipMode.CurrentView;
            }
        }

        private void OnModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VectorRow == null)
                return;
            VectorRow.Visibility = SelectedMode == RasterClipMode.VectorBoundary
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnRasterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RasterCombo.SelectedItem is not RasterLayerInfo raster ||
                !string.IsNullOrWhiteSpace(OutputBox.Text))
            {
                return;
            }

            string directory = Path.GetDirectoryName(raster.FilePath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(raster.FilePath);
            OutputBox.Text = Path.Combine(directory, $"{name}_clip.tif");
        }

        private void OnBrowseOutput(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存裁剪结果",
                Filter = "GeoTIFF|*.tif;*.tiff",
                DefaultExt = ".tif",
                FileName = Path.GetFileName(OutputBox.Text)
            };
            if (dialog.ShowDialog() == true)
                OutputBox.Text = dialog.FileName;
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            if (RasterCombo.SelectedItem is not RasterLayerInfo raster)
            {
                MessageBox.Show(this, "请选择输入影像。", "影像裁剪",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string output = OutputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                MessageBox.Show(this, "请选择输出文件。", "影像裁剪",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? vectorPath = null;
            if (SelectedMode == RasterClipMode.VectorBoundary)
            {
                if (VectorCombo.SelectedItem is not VectorClipLayerInfo vector)
                {
                    MessageBox.Show(this, "请先加载并选择一个矢量边界图层。", "影像裁剪",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                vectorPath = vector.FilePath;
            }

            Request = new RasterClipRequest(
                raster.FilePath,
                Path.GetFullPath(output),
                SelectedMode,
                vectorPath,
                LoadResultBox.IsChecked == true);
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
