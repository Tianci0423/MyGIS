using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GeoVision.Services;
using Microsoft.Win32;
using OSGeo.GDAL;

namespace GeoVision.Dialogs
{
    public partial class MultiTemporalRegistrationDialog : Window
    {
        private RasterPreview? _moving;
        private RasterPreview? _reference;
        private bool _capturing;
        private PendingPoint? _pendingMovingPoint;
        private RegistrationFitResult? _fit;
        private bool _suppressPointChanges;

        public ObservableCollection<RegistrationControlPoint> ControlPoints { get; } = [];
        public MultiTemporalRegistrationRequest? Request { get; private set; }

        public MultiTemporalRegistrationDialog(IReadOnlyList<RasterLayerInfo> rasterLayers)
        {
            InitializeComponent();
            DataContext = this;
            foreach (RasterLayerInfo layer in rasterLayers)
            {
                MovingPathBox.Items.Add(layer);
                ReferencePathBox.Items.Add(layer);
            }

            if (rasterLayers.Count > 0)
            {
                MovingPathBox.SelectedIndex = 0;
                MovingPathBox.Text = rasterLayers[0].FilePath;
            }
            if (rasterLayers.Count > 1)
            {
                ReferencePathBox.SelectedIndex = 1;
                ReferencePathBox.Text = rasterLayers[1].FilePath;
            }
        }

        private void OnMovingPathSelected(object? sender, EventArgs e)
        {
            if (MovingPathBox.SelectedItem is RasterLayerInfo info)
                MovingPathBox.Text = info.FilePath;
        }

        private void OnReferencePathSelected(object? sender, EventArgs e)
        {
            if (ReferencePathBox.SelectedItem is RasterLayerInfo info)
                ReferencePathBox.Text = info.FilePath;
        }

        private void OnBrowseMoving(object sender, RoutedEventArgs e)
        {
            string? path = BrowseRaster("选择待配准的时相影像");
            if (path != null)
                MovingPathBox.Text = path;
        }

        private void OnBrowseReference(object sender, RoutedEventArgs e)
        {
            string? path = BrowseRaster("选择基准时相影像");
            if (path != null)
                ReferencePathBox.Text = path;
        }

        private static string? BrowseRaster(string title)
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = "栅格影像|*.tif;*.tiff;*.img;*.jp2;*.vrt|所有文件|*.*"
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private async void OnLoadImages(object sender, RoutedEventArgs e)
        {
            string movingPath = MovingPathBox.Text.Trim();
            string referencePath = ReferencePathBox.Text.Trim();
            if (!File.Exists(movingPath) || !File.Exists(referencePath))
            {
                MessageBox.Show(this, "请选择有效的待配准影像和基准影像。", "多时相影像配准",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.Equals(Path.GetFullPath(movingPath), Path.GetFullPath(referencePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "待配准影像与基准影像不能是同一个文件。", "多时相影像配准",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                LoadButton.IsEnabled = false;
                LoadButton.Content = "正在加载…";
                CaptureStatusText.Text = "正在生成高分辨率预览，请稍候…";
                Mouse.OverrideCursor = Cursors.Wait;
                var movingTask = Task.Run(() => LoadPreview(movingPath));
                var referenceTask = Task.Run(() => LoadPreview(referencePath));
                RasterPreview[] previews = await Task.WhenAll(movingTask, referenceTask);
                _moving = previews[0];
                _reference = previews[1];
                ApplyPreview(_moving, MovingImage, MovingSurface, MovingOverlay);
                ApplyPreview(_reference, ReferenceImage, ReferenceSurface, ReferenceOverlay);

                ClearPoints();
                AddPointButton.IsEnabled = true;
                CaptureStatusText.Text = "单击“采集控制点”，然后依次点击左侧与右侧的同一地物位置。";
                UpdateCrsWarning();
                UpdateDefaultOutputPath();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"加载影像失败：\n{ex.Message}", "多时相影像配准",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                CaptureStatusText.Text = "影像加载失败，请检查文件格式与访问权限。";
            }
            finally
            {
                Mouse.OverrideCursor = null;
                LoadButton.Content = "加载双视图";
                LoadButton.IsEnabled = true;
            }
        }

        private static RasterPreview LoadPreview(string path)
        {
            using Dataset dataset = Gdal.Open(path, Access.GA_ReadOnly)
                ?? throw new InvalidDataException($"GDAL 无法打开影像：{path}");
            if (dataset.RasterXSize <= 0 || dataset.RasterYSize <= 0 || dataset.RasterCount <= 0)
                throw new InvalidDataException($"影像没有有效的栅格数据：{path}");

            var geoTransform = new double[6];
            dataset.GetGeoTransform(geoTransform);
            if (geoTransform.All(value => Math.Abs(value) < double.Epsilon))
                throw new InvalidDataException($"影像没有有效的地理仿射变换：{path}");

            const int maxPreviewDimension = 1800;
            double previewRatio = Math.Min(1d, Math.Min(
                (double)maxPreviewDimension / dataset.RasterXSize,
                (double)maxPreviewDimension / dataset.RasterYSize));
            int previewWidth = Math.Max(1, (int)Math.Round(dataset.RasterXSize * previewRatio));
            int previewHeight = Math.Max(1, (int)Math.Round(dataset.RasterYSize * previewRatio));

            RasterRendererType rendererType = RasterRenderer.DetermineRendererType(dataset, dataset.RasterCount);
            int[] bands = RasterRenderer.SelectDisplayBands(dataset, dataset.RasterCount);
            StretchParameters stretch = RasterRenderer.ComputeDisplayStretchParameters(
                dataset, bands, rendererType, StretchType.PercentClip);
            byte[] rgba = RasterRenderer.RenderToRgba(
                dataset, bands, stretch, rendererType,
                0, 0, dataset.RasterXSize, dataset.RasterYSize,
                previewWidth, previewHeight);
            for (int i = 0; i < rgba.Length; i += 4)
                (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);

            BitmapSource bitmap = BitmapSource.Create(
                previewWidth, previewHeight, 96, 96, PixelFormats.Bgra32, null,
                rgba, previewWidth * 4);
            bitmap.Freeze();
            return new RasterPreview(
                path, dataset.RasterXSize, dataset.RasterYSize,
                previewWidth, previewHeight, geoTransform,
                dataset.GetProjection() ?? string.Empty, bitmap);
        }

        private static void ApplyPreview(
            RasterPreview preview, Image image, FrameworkElement surface, Canvas overlay)
        {
            image.Source = preview.Bitmap;
            image.Width = preview.PreviewWidth;
            image.Height = preview.PreviewHeight;
            surface.Width = preview.PreviewWidth;
            surface.Height = preview.PreviewHeight;
            overlay.Width = preview.PreviewWidth;
            overlay.Height = preview.PreviewHeight;
        }

        private void UpdateCrsWarning()
        {
            if (_moving == null || _reference == null)
                return;
            string movingCrs = NormalizeWkt(_moving.Projection);
            string referenceCrs = NormalizeWkt(_reference.Projection);
            if (string.IsNullOrWhiteSpace(movingCrs) || string.IsNullOrWhiteSpace(referenceCrs))
                CrsWarningText.Text = "注意：至少一幅影像未定义坐标系";
            else if (!string.Equals(movingCrs, referenceCrs, StringComparison.OrdinalIgnoreCase))
                CrsWarningText.Text = "注意：两幅影像坐标系不同，建议使用仿射模型并检查残差";
            else
                CrsWarningText.Text = string.Empty;
        }

        private static string NormalizeWkt(string value)
            => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

        private void OnTogglePointCapture(object sender, RoutedEventArgs e)
        {
            _capturing = !_capturing;
            AddPointButton.Content = _capturing ? "停止采集" : "采集控制点";
            CaptureStatusText.Text = _capturing
                ? "请先在左侧待配准影像点击清晰地物点。"
                : "控制点采集已暂停。";
            if (!_capturing)
                CancelPendingPoint();
        }

        private void OnMovingImageClick(object sender, MouseButtonEventArgs e)
        {
            if (!_capturing || _moving == null)
                return;
            Point position = e.GetPosition(MovingOverlay);
            if (!IsInside(position, _moving))
                return;
            _pendingMovingPoint = CreatePendingPoint(position, _moving);
            RedrawMarkers();
            CaptureStatusText.Text = "左侧点已记录；请在右侧基准影像点击同一地物位置。";
            e.Handled = true;
        }

        private void OnReferenceImageClick(object sender, MouseButtonEventArgs e)
        {
            if (!_capturing || _reference == null)
                return;
            if (_pendingMovingPoint == null)
            {
                CaptureStatusText.Text = "请先在左侧待配准影像点击对应地物点。";
                return;
            }
            Point position = e.GetPosition(ReferenceOverlay);
            if (!IsInside(position, _reference))
                return;
            PendingPoint referencePoint = CreatePendingPoint(position, _reference);
            var point = new RegistrationControlPoint(
                ControlPoints.Count + 1,
                _pendingMovingPoint.PixelX, _pendingMovingPoint.PixelY,
                _pendingMovingPoint.MapX, _pendingMovingPoint.MapY,
                referencePoint.PixelX, referencePoint.PixelY,
                referencePoint.MapX, referencePoint.MapY);
            point.PropertyChanged += OnControlPointChanged;
            ControlPoints.Add(point);
            _pendingMovingPoint = null;
            RenumberPoints();
            Recalculate(showErrors: false);
            RedrawMarkers();
            CaptureStatusText.Text = $"已添加第 {ControlPoints.Count} 组控制点；请继续在左侧选取下一点。";
            e.Handled = true;
        }

        private static bool IsInside(Point p, RasterPreview preview)
            => p.X >= 0 && p.Y >= 0 && p.X < preview.PreviewWidth && p.Y < preview.PreviewHeight;

        private static PendingPoint CreatePendingPoint(Point p, RasterPreview preview)
        {
            double pixelX = p.X * preview.RasterWidth / preview.PreviewWidth;
            double pixelY = p.Y * preview.RasterHeight / preview.PreviewHeight;
            double mapX = preview.GeoTransform[0]
                          + pixelX * preview.GeoTransform[1]
                          + pixelY * preview.GeoTransform[2];
            double mapY = preview.GeoTransform[3]
                          + pixelX * preview.GeoTransform[4]
                          + pixelY * preview.GeoTransform[5];
            return new PendingPoint(pixelX, pixelY, mapX, mapY);
        }

        private void OnCancelPendingPoint(object sender, RoutedEventArgs e) => CancelPendingPoint();

        private void CancelPendingPoint()
        {
            _pendingMovingPoint = null;
            RedrawMarkers();
            if (_capturing)
                CaptureStatusText.Text = "待选点已清除；请在左侧待配准影像重新点击。";
        }

        private void OnDeleteSelected(object sender, RoutedEventArgs e)
        {
            var selected = ControlPointGrid.SelectedItems.Cast<RegistrationControlPoint>().ToArray();
            foreach (RegistrationControlPoint point in selected)
            {
                point.PropertyChanged -= OnControlPointChanged;
                ControlPoints.Remove(point);
            }
            RenumberPoints();
            Recalculate(showErrors: false);
            RedrawMarkers();
        }

        private void OnClearAll(object sender, RoutedEventArgs e) => ClearPoints();

        private void ClearPoints()
        {
            foreach (RegistrationControlPoint point in ControlPoints)
                point.PropertyChanged -= OnControlPointChanged;
            ControlPoints.Clear();
            _pendingMovingPoint = null;
            _fit = null;
            FitInfoText.Text = "尚未计算";
            RedrawMarkers();
        }

        private void RenumberPoints()
        {
            for (int i = 0; i < ControlPoints.Count; i++)
                ControlPoints[i].Index = i + 1;
        }

        private void OnControlPointChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!_suppressPointChanges && e.PropertyName == nameof(RegistrationControlPoint.IsEnabled))
                Dispatcher.BeginInvoke(() =>
                {
                    Recalculate(showErrors: false);
                    RedrawMarkers();
                });
        }

        private void OnModelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FitInfoText != null)
                Recalculate(showErrors: false);
        }

        private void OnRecalculate(object sender, RoutedEventArgs e) => Recalculate(showErrors: true);

        private bool Recalculate(bool showErrors)
        {
            if (_moving == null)
                return false;
            RegistrationTransformModel model = SelectedModel();
            RegistrationControlPoint[] enabled = ControlPoints.Where(p => p.IsEnabled).ToArray();
            int minimum = MultiTemporalRegistrationMath.MinimumPointCount(model);
            if (enabled.Length < minimum)
            {
                _fit = null;
                FitInfoText.Text = $"{MultiTemporalRegistrationMath.ModelName(model)}模型需要至少 {minimum} 组有效控制点；当前 {enabled.Length} 组。";
                SetResiduals(null, enabled);
                return false;
            }

            try
            {
                RegistrationTiePoint[] tiePoints = enabled.Select(p => new RegistrationTiePoint(
                    p.MovingX, p.MovingY, p.ReferenceX, p.ReferenceY)).ToArray();
                _fit = MultiTemporalRegistrationMath.Fit(model, tiePoints, _moving.GeoTransform);
                SetResiduals(_fit.Residuals, enabled);
                FitInfoText.Text =
                    $"有效点：{enabled.Length}　RMSE：{_fit.Rmse:F3} {CoordinateUnitLabel()}\n" +
                    $"X′={_fit.A:F8}X + {_fit.B:F8}Y + {_fit.Tx:F3}\n" +
                    $"Y′={_fit.C:F8}X + {_fit.D:F8}Y + {_fit.Ty:F3}";
                return true;
            }
            catch (Exception ex)
            {
                _fit = null;
                FitInfoText.Text = ex.Message;
                SetResiduals(null, enabled);
                if (showErrors)
                    MessageBox.Show(this, ex.Message, "控制点计算", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private void SetResiduals(
            IReadOnlyList<(double Dx, double Dy, double Distance)>? residuals,
            IReadOnlyList<RegistrationControlPoint> enabled)
        {
            _suppressPointChanges = true;
            try
            {
                foreach (RegistrationControlPoint point in ControlPoints)
                    point.Residual = double.NaN;
                if (residuals != null)
                {
                    for (int i = 0; i < enabled.Count; i++)
                    {
                        enabled[i].ResidualX = residuals[i].Dx;
                        enabled[i].ResidualY = residuals[i].Dy;
                        enabled[i].Residual = residuals[i].Distance;
                    }
                }
            }
            finally
            {
                _suppressPointChanges = false;
            }
        }

        private string CoordinateUnitLabel()
            => _reference?.Projection.Contains("UNIT[\"metre\"", StringComparison.OrdinalIgnoreCase) == true
                || _reference?.Projection.Contains("UNIT[\"meter\"", StringComparison.OrdinalIgnoreCase) == true
                ? "m"
                : "坐标单位";

        private RegistrationTransformModel SelectedModel()
        {
            string tag = (ModelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Translation";
            return Enum.TryParse(tag, out RegistrationTransformModel model)
                ? model
                : RegistrationTransformModel.Translation;
        }

        private void RedrawMarkers()
        {
            if (MovingOverlay == null || ReferenceOverlay == null)
                return;
            MovingOverlay.Children.Clear();
            ReferenceOverlay.Children.Clear();
            if (_moving != null && _reference != null)
            {
                foreach (RegistrationControlPoint point in ControlPoints)
                {
                    Brush color = point.IsEnabled ? Brushes.Red : Brushes.Gray;
                    AddMarker(MovingOverlay,
                        point.MovingPixelX * _moving.PreviewWidth / _moving.RasterWidth,
                        point.MovingPixelY * _moving.PreviewHeight / _moving.RasterHeight,
                        point.Index.ToString(), color);
                    AddMarker(ReferenceOverlay,
                        point.ReferencePixelX * _reference.PreviewWidth / _reference.RasterWidth,
                        point.ReferencePixelY * _reference.PreviewHeight / _reference.RasterHeight,
                        point.Index.ToString(), color);
                }
                if (_pendingMovingPoint != null)
                {
                    AddMarker(MovingOverlay,
                        _pendingMovingPoint.PixelX * _moving.PreviewWidth / _moving.RasterWidth,
                        _pendingMovingPoint.PixelY * _moving.PreviewHeight / _moving.RasterHeight,
                        "?", Brushes.Orange);
                }
            }
        }

        private static void AddMarker(Canvas canvas, double x, double y, string label, Brush brush)
        {
            const double radius = 4.5;
            const double crossHalfLength = 7.5;
            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = brush,
                StrokeThickness = 1.4,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(ellipse, x - radius);
            Canvas.SetTop(ellipse, y - radius);
            canvas.Children.Add(ellipse);
            var horizontal = new System.Windows.Shapes.Line
            {
                X1 = x - crossHalfLength, X2 = x + crossHalfLength, Y1 = y, Y2 = y,
                Stroke = brush, StrokeThickness = 1.2, IsHitTestVisible = false
            };
            var vertical = new System.Windows.Shapes.Line
            {
                X1 = x, X2 = x, Y1 = y - crossHalfLength, Y2 = y + crossHalfLength,
                Stroke = brush, StrokeThickness = 1.2, IsHitTestVisible = false
            };
            canvas.Children.Add(horizontal);
            canvas.Children.Add(vertical);
            var text = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                Background = brush,
                FontWeight = FontWeights.Bold,
                FontSize = 9,
                Padding = new Thickness(1.5, 0, 1.5, 0),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(text, x + 5.5);
            Canvas.SetTop(text, y - 13);
            canvas.Children.Add(text);
        }

        private void OnMovingZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MovingScale != null)
                MovingScale.ScaleX = MovingScale.ScaleY = e.NewValue;
        }

        private void OnReferenceZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ReferenceScale != null)
                ReferenceScale.ScaleX = ReferenceScale.ScaleY = e.NewValue;
        }

        private void OnOutputModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResamplingBox != null)
                ResamplingBox.IsEnabled = SelectedOutputMode() == RegistrationOutputMode.ResampleToReferenceGrid;
        }

        private RegistrationOutputMode SelectedOutputMode()
        {
            string tag = (OutputModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "GeoreferenceOnly";
            return Enum.TryParse(tag, out RegistrationOutputMode mode)
                ? mode
                : RegistrationOutputMode.GeoreferenceOnly;
        }

        private void OnBrowseOutput(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存多时相配准结果",
                Filter = "GeoTIFF|*.tif;*.tiff",
                DefaultExt = ".tif",
                AddExtension = true,
                FileName = Path.GetFileName(OutputPathBox.Text)
            };
            if (dialog.ShowDialog() == true)
                OutputPathBox.Text = dialog.FileName;
        }

        private void UpdateDefaultOutputPath()
        {
            if (_moving == null)
                return;
            string directory = Path.GetDirectoryName(_moving.Path) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(_moving.Path);
            OutputPathBox.Text = Path.Combine(directory, $"{name}_multitemporal_registered.tif");
        }

        private void OnRun(object sender, RoutedEventArgs e)
        {
            if (_moving == null || _reference == null)
            {
                MessageBox.Show(this, "请先加载待配准影像和基准影像。", "多时相影像配准",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!Recalculate(showErrors: true) || _fit == null)
                return;

            string output = OutputPathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                MessageBox.Show(this, "请选择输出文件。", "多时相影像配准",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string movingFullPath = Path.GetFullPath(_moving.Path);
            string referenceFullPath = Path.GetFullPath(_reference.Path);
            string outputFullPath = Path.GetFullPath(output);
            if (string.Equals(movingFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(referenceFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "输出文件不能覆盖输入影像，请使用新的文件名。", "多时相影像配准",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (File.Exists(outputFullPath) && MessageBox.Show(this,
                    "输出文件已存在，是否覆盖？", "多时相影像配准",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            string resampling = (ResamplingBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "bilinear";
            Request = new MultiTemporalRegistrationRequest(
                _moving.Path, _reference.Path, outputFullPath,
                (double[])_fit.GeoTransform.Clone(), SelectedOutputMode(),
                resampling, LoadResultBox.IsChecked == true);
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

        private sealed record RasterPreview(
            string Path,
            int RasterWidth,
            int RasterHeight,
            int PreviewWidth,
            int PreviewHeight,
            double[] GeoTransform,
            string Projection,
            BitmapSource Bitmap);

        private sealed record PendingPoint(
            double PixelX,
            double PixelY,
            double MapX,
            double MapY);
    }

    public sealed class RegistrationControlPoint : INotifyPropertyChanged
    {
        private int _index;
        private bool _isEnabled = true;
        private double _residual = double.NaN;
        private double _residualX;
        private double _residualY;

        public RegistrationControlPoint(
            int index,
            double movingPixelX, double movingPixelY,
            double movingX, double movingY,
            double referencePixelX, double referencePixelY,
            double referenceX, double referenceY)
        {
            _index = index;
            MovingPixelX = movingPixelX;
            MovingPixelY = movingPixelY;
            MovingX = movingX;
            MovingY = movingY;
            ReferencePixelX = referencePixelX;
            ReferencePixelY = referencePixelY;
            ReferenceX = referenceX;
            ReferenceY = referenceY;
        }

        public int Index { get => _index; set => Set(ref _index, value); }
        public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
        public double MovingPixelX { get; }
        public double MovingPixelY { get; }
        public double MovingX { get; }
        public double MovingY { get; }
        public double ReferencePixelX { get; }
        public double ReferencePixelY { get; }
        public double ReferenceX { get; }
        public double ReferenceY { get; }
        public double ResidualX { get => _residualX; set => Set(ref _residualX, value); }
        public double ResidualY { get => _residualY; set => Set(ref _residualY, value); }
        public double Residual { get => _residual; set => Set(ref _residual, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
