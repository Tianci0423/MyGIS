using System.Windows;
using System.Windows.Controls;
using GeoVision.Helpers;
using Microsoft.Win32;

namespace GeoVision.Dialogs
{
    public sealed record VectorLayerInfo(string Name, string FilePath, string Crs);

    public partial class VectorReprojectionDialog : Window
    {
        public Services.VectorReprojectionRequest? Request { get; private set; }

        public VectorReprojectionDialog(IReadOnlyList<VectorLayerInfo> layers)
        {
            InitializeComponent();
            foreach (var layer in layers) InputBox.Items.Add(layer);
            if (layers.Count > 0)
            {
                InputBox.SelectedIndex = 0;
                InputBox.Text = layers[0].FilePath;
                TargetCrsBox.Text = "EPSG:4326";
                RefreshInfo();
            }
        }

        private void OnInputChanged(object sender, SelectionChangedEventArgs e)
        {
            if (InputBox.SelectedItem is VectorLayerInfo info)
            {
                InputBox.Text = info.FilePath;
                if (string.IsNullOrWhiteSpace(TargetCrsBox.Text) || TargetCrsBox.Text == info.Crs)
                    TargetCrsBox.Text = "EPSG:4326";
                UpdateOutputPath();
            }
            RefreshInfo();
        }

        private void OnBrowseInput(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择矢量文件",
                Filter = "矢量文件|*.shp;*.geojson;*.json|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) == true)
            {
                InputBox.Text = dialog.FileName;
                UpdateOutputPath();
                RefreshInfo();
            }
        }

        private void OnSelectCrs(object sender, RoutedEventArgs e)
        {
            var picker = new CoordinateSystemPickerDialog(TargetCrsBox.Text) { Owner = this };
            if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedCrs))
            {
                TargetCrsBox.Text = picker.SelectedCrs;
                UpdateOutputPath();
            }
        }

        private void OnBrowseOutput(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存重投影矢量",
                Filter = "GeoJSON|*.geojson|所有文件|*.*",
                DefaultExt = ".geojson",
                AddExtension = true,
                FileName = System.IO.Path.GetFileName(OutputBox.Text)
            };
            if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FileName;
        }

        private void RefreshInfo()
        {
            if (InputBox.SelectedItem is VectorLayerInfo info)
                InfoText.Text = $"源坐标系：{info.Crs}\n输出格式：GeoJSON（包含属性字段和几何）";
            else
                InfoText.Text = "请选择矢量文件。";
        }

        private void UpdateOutputPath()
        {
            string input = InputBox.Text.Trim();
            if (!System.IO.File.Exists(input)) return;
            string directory = System.IO.Path.GetDirectoryName(input) ?? Environment.CurrentDirectory;
            string name = System.IO.Path.GetFileNameWithoutExtension(input);
            OutputBox.Text = System.IO.Path.Combine(directory, $"{name}_reprojected.geojson");
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            string input = InputBox.Text.Trim();
            string target = SpatialReferenceHelper.NormalizeInput(TargetCrsBox.Text);
            string output = OutputBox.Text.Trim();
            if (!System.IO.File.Exists(input))
            {
                MessageBox.Show(this, "输入矢量文件不存在。", "矢量重投影", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!SpatialReferenceHelper.TryParse(target, out _, out string error))
            {
                MessageBox.Show(this, error, "目标坐标系无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(output))
            {
                MessageBox.Show(this, "请选择输出 GeoJSON 文件。", "矢量重投影", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            output = System.IO.Path.GetFullPath(output);
            if (string.IsNullOrWhiteSpace(System.IO.Path.GetExtension(output))) output += ".geojson";
            if (string.Equals(System.IO.Path.GetFullPath(input), output, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "输出文件不能覆盖输入矢量。", "矢量重投影", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (System.IO.File.Exists(output) && MessageBox.Show(this, "输出文件已经存在，是否覆盖？",
                "矢量重投影", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            Request = new Services.VectorReprojectionRequest(input, output, target);
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
