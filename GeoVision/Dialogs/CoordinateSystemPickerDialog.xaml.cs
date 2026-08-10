using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GeoVision.Helpers;
using GeoVision.Services;
using Microsoft.Win32;
using OSGeo.GDAL;

namespace GeoVision.Dialogs
{
    public partial class CoordinateSystemPickerDialog : Window
    {
        private IReadOnlyList<CrsCatalogEntry> _entries = CrsCatalogService.CommonEntries;
        private ICollectionView? _catalogView;
        private bool _suppressCustomChange;
        private SpatialReferenceDetails? _candidate;

        public string? SelectedCrs { get; private set; }
        public SpatialReferenceDetails? SelectedDetails { get; private set; }

        public CoordinateSystemPickerDialog(string? initialCrs = null)
        {
            InitializeComponent();
            SetCatalog(_entries);
            SetCustomDefinition(string.IsNullOrWhiteSpace(initialCrs) ? "EPSG:4326" : initialCrs, false);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            CatalogStatusText.Text = "正在加载完整 EPSG 坐标系库……";
            IReadOnlyList<CrsCatalogEntry> entries = await CrsCatalogService.LoadAsync();
            if (!IsLoaded)
                return;

            _entries = entries;
            SetCatalog(entries);
            CatalogStatusText.Text = entries.Count > CrsCatalogService.CommonEntries.Count
                ? $"已加载 {entries.Count:N0} 个未弃用 EPSG 坐标系。"
                : "完整 EPSG 库不可用，当前显示常用坐标系；仍可手动输入或导入。";
            SelectCandidateInCatalog();
        }

        private void SetCatalog(IReadOnlyList<CrsCatalogEntry> entries)
        {
            _catalogView = CollectionViewSource.GetDefaultView(entries);
            _catalogView.Filter = FilterEntry;
            CatalogList.ItemsSource = _catalogView;
        }

        private bool FilterEntry(object item)
        {
            if (item is not CrsCatalogEntry entry)
                return false;

            string category = (CategoryBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
            if (category != "all" && entry.Category != category)
                return false;

            string search = SearchBox.Text.Trim();
            if (search.Length == 0)
                return true;
            return search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .All(term => entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
            => _catalogView?.Refresh();

        private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
            => _catalogView?.Refresh();

        private void OnCatalogSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CatalogList.SelectedItem is CrsCatalogEntry entry)
                SetCustomDefinition(entry.Identifier, false);
        }

        private void OnCatalogDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CatalogList.SelectedItem is CrsCatalogEntry)
                AcceptCurrentDefinition();
        }

        private void OnCustomCrsChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressCustomChange)
                return;
            _candidate = null;
            DetailsText.Text = "自定义内容尚未验证。点击“验证”或“确定”进行解析。";
            CatalogList.SelectedItem = null;
        }

        private void OnValidateCustom(object sender, RoutedEventArgs e)
            => SetCustomDefinition(CustomCrsBox.Text, true);

        private void OnImportRaster(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "从影像导入坐标系",
                Filter = "栅格影像|*.tif;*.tiff;*.img;*.vrt|所有文件|*.*"
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                using var dataset = Gdal.Open(dialog.FileName, Access.GA_ReadOnly)
                    ?? throw new InvalidDataException("GDAL 无法打开该影像。");
                string projection = dataset.GetProjection();
                if (string.IsNullOrWhiteSpace(projection))
                    throw new InvalidDataException("所选影像没有坐标系定义。");
                SetCustomDefinition(projection, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "导入坐标系",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnImportDefinition(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入坐标系定义",
                Filter = "坐标系定义|*.prj;*.wkt;*.txt|所有文件|*.*"
            };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                SetCustomDefinition(File.ReadAllText(dialog.FileName), true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"读取坐标系文件失败：{ex.Message}", "导入坐标系",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SetCustomDefinition(string definition, bool showError)
        {
            if (!SpatialReferenceHelper.TryParse(definition, out var details, out string error) || details == null)
            {
                _candidate = null;
                DetailsText.Text = error;
                if (showError)
                {
                    MessageBox.Show(this, error, "坐标系无效",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            _candidate = details;
            _suppressCustomChange = true;
            CustomCrsBox.Text = details.CanonicalInput;
            CustomCrsBox.CaretIndex = 0;
            _suppressCustomChange = false;
            DetailsText.Text =
                $"标识：{details.Identifier}\n" +
                $"名称：{details.DisplayName}\n" +
                $"类型：{details.Type}\n" +
                $"基准面：{details.Datum}\n" +
                $"单位：{details.Unit}";
            SelectCandidateInCatalog();
        }

        private void SelectCandidateInCatalog()
        {
            string? code = _candidate?.AuthorityCode;
            if (string.IsNullOrWhiteSpace(code))
                return;
            var match = _entries.FirstOrDefault(entry => entry.Code == code);
            if (match == null || ReferenceEquals(CatalogList.SelectedItem, match))
                return;
            CatalogList.SelectedItem = match;
            CatalogList.ScrollIntoView(match);
        }

        private void OnAccept(object sender, RoutedEventArgs e)
            => AcceptCurrentDefinition();

        private void AcceptCurrentDefinition()
        {
            if (!SpatialReferenceHelper.TryParse(CustomCrsBox.Text, out var details, out string error) ||
                details == null)
            {
                MessageBox.Show(this, error, "坐标系无效",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedCrs = details.CanonicalInput;
            SelectedDetails = details;
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }
}
