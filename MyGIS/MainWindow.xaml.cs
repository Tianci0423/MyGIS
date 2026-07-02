using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Manipulations;
using Mapsui.Providers;
using Microsoft.Win32;
using MyGIS.Helpers;
using MyGIS.Models;
using MyGIS.Services;

namespace MyGIS
{
    public partial class MainWindow : Window
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        private Point? _dragStart;
        private int _dragIndex = -1;
        private Dialogs.IdentifyDialog? _identifyDialog;
        private int _markerVersion;
        private int _identifyHighlightVersion;
        private ILayer? _identifyHighlightLayer;
        private IdentifyRequest? _lastIdentifyRequest;
        private DispatcherTimer? _coordTimer;
        private Process? _runningPythonProcess;
        private readonly ObservableCollection<LayerItem> _layerItems = new();
        private ICollectionView? _layerItemsView;

        private void KillRunningPythonProcess()
        {
            try
            {
                if (_runningPythonProcess is { HasExited: false })
                {
                    _runningPythonProcess.Kill(entireProcessTree: true);
                    _runningPythonProcess.Dispose();
                }
            }
            catch { }
            _runningPythonProcess = null;
        }

        public void ShowProgress(string label)
        {
            ProgressLabel.Text = label;
            ProgressBar.IsIndeterminate = true;
            ProgressBar.Value = 0;
            ProgressPanel.Visibility = Visibility.Visible;
            StatusText.Text = label;
        }

        public void UpdateProgress(string label, int percent)
        {
            if (percent < 0)
            {
                ProgressLabel.Text = label;
                ProgressBar.IsIndeterminate = true;
            }
            else
            {
                percent = Math.Clamp(percent, 0, 100);
                label = $"{percent}% {label}";
                ProgressLabel.Text = label;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = percent;
            }
            ProgressPanel.Visibility = Visibility.Visible;
            StatusText.Text = label;
        }

        public void HideProgress()
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "就绪";
        }

        private IProgress<(int percent, string label)> CreateLoadProgress(
            string filePath,
            int fileIndex,
            int totalFiles)
        {
            totalFiles = Math.Max(1, totalFiles);
            int start = (int)Math.Round(fileIndex * 100.0 / totalFiles);
            int end = (int)Math.Round((fileIndex + 1) * 100.0 / totalFiles);

            return new Progress<(int percent, string label)>(p =>
            {
                string label = p.label;
                if (totalFiles > 1)
                    label = $"{label} ({fileIndex + 1}/{totalFiles})";

                if (p.percent < 0)
                {
                    ShowProgress(label);
                    return;
                }

                int localPercent = Math.Clamp(p.percent, 0, 100);
                int overallPercent = start + (int)Math.Round((end - start) * localPercent / 100.0);
                UpdateProgress(label, overallPercent);
            });
        }

        private IProgress<int> CreateBatchTaskProgress(
            string label,
            int taskIndex,
            int totalTasks)
        {
            totalTasks = Math.Max(1, totalTasks);
            int start = (int)Math.Round(taskIndex * 100.0 / totalTasks);
            int end = (int)Math.Round((taskIndex + 1) * 100.0 / totalTasks);
            int lastOverallPercent = -1;

            return new Progress<int>(localPercent =>
            {
                localPercent = Math.Clamp(localPercent, 0, 100);
                int overallPercent = start + (int)Math.Round((end - start) * localPercent / 100.0);
                if (overallPercent < lastOverallPercent)
                    overallPercent = lastOverallPercent;

                lastOverallPercent = overallPercent;
                UpdateProgress(label, overallPercent);
            });
        }

        public MainWindow()
        {
            InitializeComponent();
            InitializeLayerList();
            InitializeMap();
            Closing += OnWindowClosing;
            Closed += (_, _) =>
            {
                _coordTimer?.Stop();
                KillRunningPythonProcess();
            };
        }

        private void InitializeLayerList()
        {
            _layerItemsView = CollectionViewSource.GetDefaultView(_layerItems);
            _layerItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LayerItem.LayerGroup)));
            _layerItemsView.Filter = FilterLayerItem;
            LayerListBox.ItemsSource = _layerItemsView;
        }

        private bool FilterLayerItem(object obj)
        {
            if (obj is not LayerItem item) return false;
            var query = LayerSearchBox?.Text?.Trim();
            if (string.IsNullOrEmpty(query)) return true;

            return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   item.LayerTypeLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   item.Crs.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private void InitializeMap()
        {
            var map = new Map { CRS = "EPSG:3857" };
            map.Widgets.Clear();
            MapControl.UseFling = true;
            MapControl.Map = map;
            MapControl.PreviewMouseLeftButtonDown += OnMapClick;
            GpuRasterMap.PreviewMouseLeftButtonDown += OnMapClick;
            GpuRasterMap.ViewportChanged += (_, _) => RefreshStatusBar();
            RefreshStatusBar();

            _coordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _coordTimer.Tick += OnCoordTimerTick;
            _coordTimer.Start();
        }

        private void OnCoordTimerTick(object? sender, EventArgs e)
        {
            var map = MapControl.Map;
            if (map == null) return;

            if (!GetCursorPos(out var cursorPt)) return;

            var source = PresentationSource.FromVisual(this);
            if (source == null) return;
            double dpi = source.CompositionTarget.TransformToDevice.M11;

            var mapScreenPos = MapControl.PointToScreen(new Point(0, 0));
            double mapRight = mapScreenPos.X + MapControl.ActualWidth * dpi;
            double mapBottom = mapScreenPos.Y + MapControl.ActualHeight * dpi;

            // Stop updating when mouse is outside the map area
            if (cursorPt.X < mapScreenPos.X || cursorPt.Y < mapScreenPos.Y ||
                cursorPt.X > mapRight || cursorPt.Y > mapBottom)
            {
                CoordText.Text = "—";
                return;
            }

            int relX = cursorPt.X - (int)mapScreenPos.X;
            int relY = cursorPt.Y - (int)mapScreenPos.Y;

            double worldX;
            double worldY;
            if (GpuRasterMap.HasRasterLayers)
            {
                var world = GpuRasterMap.ScreenToWorld(relX / dpi, relY / dpi);
                if (!GpuRasterMap.ContainsVisibleRasterData(world))
                {
                    CoordText.Text = "—";
                    return;
                }

                worldX = world.X;
                worldY = world.Y;
            }
            else
            {
                var vp = map.Navigator.Viewport;
                if (vp.Width <= 0) return;

                worldX = vp.CenterX + (relX - vp.Width / 2.0) * vp.Resolution;
                worldY = vp.CenterY - (relY - vp.Height / 2.0) * vp.Resolution;
            }

            // Show lon/lat if CRS is known, otherwise raw X/Y
            if (!string.IsNullOrEmpty(_mapCrs) && _mapCrs != "未知")
            {
                var (lon, lat) = CoordinateConverter.ToLonLat(worldX, worldY, _mapCrs);
                CoordText.Text = $"经度: {lon:F6}  纬度: {lat:F6}";
            }
            else
            {
                CoordText.Text = $"X: {worldX:F4}  Y: {worldY:F4}";
            }

            if (!GpuRasterMap.HasRasterLayers)
                RefreshStatusBar();
        }

        private void RefreshStatusBar()
        {
            var map = MapControl.Map;
            if (map == null) return;

            string crs = _mapCrs ?? map.CRS ?? "EPSG:3857";
            CrsStatusText.Text = CoordinateConverter.GetCrsDisplayName(crs);
        }

        // ===== 工具栏 =====

        private void OnIdentifyClick(object sender, RoutedEventArgs e)
        {
            if (IdentifyBtn.IsChecked != true)
            {
                PanBtn.IsChecked = true;
                IdentifyDot.Visibility = Visibility.Collapsed;
                _markerVersion++;
                ClearIdentifyHighlight();
                _identifyDialog?.Close();
                _identifyDialog = null;
                return;
            }

            PanBtn.IsChecked = false;
        }

        private void OnPanClick(object sender, RoutedEventArgs e)
        {
            PanBtn.IsChecked = true;
            if (IdentifyBtn.IsChecked == true)
            {
                IdentifyDot.Visibility = Visibility.Collapsed;
                _markerVersion++;
                ClearIdentifyHighlight();
                _identifyDialog?.Close();
                _identifyDialog = null;
                _lastIdentifyRequest = null;
                IdentifyBtn.IsChecked = false;
            }
        }

        private void OnLayerSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            _layerItemsView?.Refresh();
        }

        private async void ShowMarkerDot(double screenX, double screenY)
        {
            int ver = ++_markerVersion;
            Canvas.SetLeft(IdentifyDot, screenX - 5);
            Canvas.SetTop(IdentifyDot, screenY - 5);
            IdentifyDot.Visibility = Visibility.Visible;
            await Task.Delay(500);
            if (ver == _markerVersion) // only hide if no newer click arrived
                IdentifyDot.Visibility = Visibility.Collapsed;
        }

        private void OnMapClick(object sender, MouseButtonEventArgs e)
        {
            if (IdentifyBtn.IsChecked != true) return;

            var pos = e.GetPosition(MapControl);
            ShowMarkerDot(pos.X, pos.Y);

            var map = MapControl.Map;
            if (map == null) return;

            MPoint world = GpuRasterMap.HasRasterLayers
                ? GpuRasterMap.ScreenToWorld(e.GetPosition(GpuRasterMap).X, e.GetPosition(GpuRasterMap).Y)
                : map.Navigator.Viewport.ScreenToWorld(pos.X, pos.Y);
            double geoX = world.X;
            double geoY = world.Y;
            var (lon, lat) = CoordinateConverter.ToLonLat(geoX, geoY, map.CRS ?? "EPSG:3857");
            _lastIdentifyRequest = new IdentifyRequest(geoX, geoY, lon, lat);

            if (_identifyDialog == null || !_identifyDialog.IsLoaded)
            {
                _identifyDialog = new Dialogs.IdentifyDialog { Owner = this };
                _identifyDialog.ModeChanged += RedoLastIdentify;
                _identifyDialog.ClosedByUser += ResetIdentifyTool;
                _identifyDialog.Closed += (_, _) => ResetIdentifyTool();
                _identifyDialog.Show();
            }

            DoIdentify(_lastIdentifyRequest.Value);

            e.Handled = true;
        }

        private void RedoLastIdentify()
        {
            if (_lastIdentifyRequest is { } request)
                DoIdentify(request);
        }

        private void ResetIdentifyTool()
        {
            IdentifyDot.Visibility = Visibility.Collapsed;
            _markerVersion++;
            ClearIdentifyHighlight();
            _identifyDialog = null;
            _lastIdentifyRequest = null;
            IdentifyBtn.IsChecked = false;
            PanBtn.IsChecked = true;
        }

        private void DoIdentify(IdentifyRequest request)
        {
            if (_identifyDialog == null) return;

            ClearIdentifyHighlight();

            string mode = _identifyDialog.LayerMode;
            var results = new List<(string name, List<string> bandValues)>();
            IFeature? highlightFeature = null;
            var layers = MapControl.Map?.Layers;
            double geoX = request.GeoX;
            double geoY = request.GeoY;

            foreach (var item in _layerItems)
            {
                if (item.Layer == null || GetRasterProvider(item.Layer) is not { } rp)
                    continue;
                if ((mode == "Visible" || mode == "Top") && !item.Layer.Enabled)
                    continue;

                var extent = rp.GetExtent();
                if (extent == null || !IsPointInExtent(geoX, geoY, extent))
                    continue;

                var vals = rp.ReadPixelValue(extent, geoX, geoY);
                if (vals == null) continue;

                results.Add((item.Layer.Name, vals));
                if (mode == "Top") break;
            }

            if (!(mode == "Top" && results.Count > 0) && layers != null)
            {
                double tolerance = Math.Max(
                    GpuRasterMap.HasRasterLayers ? GpuRasterMap.Resolution : (MapControl.Map?.Navigator.Viewport.Resolution ?? 1),
                    1) * 8;
                var identifyBox = new MRect(
                    geoX - tolerance, geoY - tolerance,
                    geoX + tolerance, geoY + tolerance);

                for (int i = layers.Count - 1; i >= 0; i--)
                {
                    var layer = layers.Get(i);
                    if (IsIdentifyHighlightLayer(layer)) continue;
                    if ((mode == "Visible" || mode == "Top") && !layer.Enabled) continue;

                    if (layer is Mapsui.Layers.Layer l && l.DataSource is Providers.GdalRasterProvider rp)
                    {
                        if (layer.Extent == null || !IsPointInExtent(geoX, geoY, layer.Extent))
                            continue;

                        var vals = rp.ReadPixelValue(layer.Extent, geoX, geoY);
                        if (vals != null)
                        {
                            results.Add((layer.Name, vals));
                            if (mode == "Top") break;
                        }
                    }
                    else if (layer is Mapsui.Layers.Layer vectorLayer &&
                             vectorLayer.DataSource is Services.DataLoader.ShapeFileProvider sfp)
                    {
                        var feature = FindBestVectorFeature(sfp, identifyBox, geoX, geoY, tolerance);
                        if (feature != null)
                        {
                            results.Add((layer.Name, GetVectorAttributeValues(sfp, feature)));
                            highlightFeature ??= feature;
                        }

                        if (mode == "Top" && highlightFeature != null) break;
                    }
                }
            }

            ShowFeatureHighlight(highlightFeature);
            _identifyDialog.UpdateResults(request.Lon, request.Lat, mode, results);
        }

        private readonly record struct IdentifyRequest(double GeoX, double GeoY, double Lon, double Lat);

        private static bool IsPointInExtent(double x, double y, MRect extent)
            => x >= extent.MinX && x <= extent.MaxX && y >= extent.MinY && y <= extent.MaxY;

        private static IFeature? FindBestVectorFeature(
            Services.DataLoader.ShapeFileProvider provider,
            MRect identifyBox,
            double geoX,
            double geoY,
            double tolerance)
        {
            IFeature? bestFeature = null;
            double bestScore = double.MaxValue;
            var point = new NetTopologySuite.Geometries.Point(geoX, geoY);

            foreach (var feature in provider.GetFeaturesInView(identifyBox, 200))
            {
                double? score = GetVectorHitScore(feature, point, tolerance);
                if (score == null || score.Value >= bestScore) continue;

                bestScore = score.Value;
                bestFeature = feature;
            }

            return bestFeature;
        }

        private static double? GetVectorHitScore(
            IFeature feature,
            NetTopologySuite.Geometries.Point point,
            double tolerance)
        {
            if (feature is Mapsui.Nts.GeometryFeature geometryFeature &&
                geometryFeature.Geometry != null)
            {
                var geometry = geometryFeature.Geometry;
                if (geometry.Contains(point) || geometry.Covers(point))
                    return 0;

                double distance = geometry.Distance(point);
                return distance <= tolerance ? distance : null;
            }

            var extent = feature.Extent;
            if (extent == null) return null;
            if (IsPointInExtent(point.X, point.Y, extent)) return 0;

            double dx = point.X < extent.MinX ? extent.MinX - point.X :
                point.X > extent.MaxX ? point.X - extent.MaxX : 0;
            double dy = point.Y < extent.MinY ? extent.MinY - point.Y :
                point.Y > extent.MaxY ? point.Y - extent.MaxY : 0;
            double distanceToExtent = Math.Sqrt(dx * dx + dy * dy);
            return distanceToExtent <= tolerance ? distanceToExtent : null;
        }

        private static List<string> GetVectorAttributeValues(
            Services.DataLoader.ShapeFileProvider provider,
            IFeature feature)
        {
            var values = new List<string>();
            var displayNames = Services.DataLoader.ReadDbfFieldNames(provider.FilePath, provider.Encoding);
            var lookupKeys = feature.Fields
                .Where(key => key != "geometry" && key != "Geometry")
                .ToList();

            for (int i = 0; i < lookupKeys.Count; i++)
            {
                var displayName = i < displayNames.Length ? displayNames[i] : lookupKeys[i];
                var value = feature[lookupKeys[i]]?.ToString() ?? "";
                values.Add($"{displayName}: {value}");
            }

            if (values.Count == 0)
                values.Add("No attributes");

            return values;
        }

        private async void ShowFeatureHighlight(IFeature? feature)
        {
            int version = ++_identifyHighlightVersion;
            ClearIdentifyHighlight(incrementVersion: false);

            if (feature == null || MapControl.Map == null) return;

            _identifyHighlightLayer = new Mapsui.Layers.Layer("__identify_highlight")
            {
                DataSource = new SingleFeatureProvider(feature),
                Style = new Mapsui.Styles.VectorStyle
                {
                    Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(255, 230, 0, 80)),
                    Line = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(255, 140, 0, 255), 4),
                    Outline = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(255, 140, 0, 255), 4)
                }
            };

            MapControl.Map.Layers.Add(_identifyHighlightLayer);
            MapControl.Refresh();

            await Task.Delay(500);
            if (version == _identifyHighlightVersion)
                ClearIdentifyHighlight();
        }

        private void ClearIdentifyHighlight(bool incrementVersion = true)
        {
            if (incrementVersion) _identifyHighlightVersion++;

            if (_identifyHighlightLayer != null && MapControl.Map?.Layers.Contains(_identifyHighlightLayer) == true)
            {
                MapControl.Map.Layers.Remove(_identifyHighlightLayer);
                MapControl.Refresh();
            }

            _identifyHighlightLayer = null;
        }

        private bool IsIdentifyHighlightLayer(ILayer layer)
        {
            return ReferenceEquals(layer, _identifyHighlightLayer) ||
                   layer.Name == "__identify_highlight" ||
                   layer is Mapsui.Layers.Layer { DataSource: SingleFeatureProvider };
        }

        private sealed class SingleFeatureProvider : IProvider
        {
            private readonly IFeature _feature;

            public SingleFeatureProvider(IFeature feature) => _feature = feature;
            public string? CRS { get; set; }
            public MRect? GetExtent() => _feature.Extent;

            public Task<IEnumerable<IFeature>> GetFeaturesAsync(FetchInfo fetchInfo)
            {
                return Task.FromResult<IEnumerable<IFeature>>(new[] { _feature });
            }
        }

        private void OnFileNew(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!PromptToSaveCurrentProject("新建项目"))
                    return;

                ClearAllLayers();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"操作失败:\n{ex}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearAllLayers()
        {
            var itemsToDispose = _layerItems.ToList();

            _mapCrs = null;
            _identifyDialog?.Close();
            _identifyDialog = null;
            ClearIdentifyHighlight();
            _identifyHighlightLayer = null;
            _lastIdentifyRequest = null;

            var layers = MapControl.Map?.Layers;
            if (layers != null)
            {
                for (int i = layers.Count - 1; i >= 0; i--)
                    layers.Remove(layers.Get(i));
            }
            GpuRasterMap.ClearRasterLayers();

            foreach (var item in itemsToDispose)
                DisposeLayerItem(item);

            _layerItems.Clear();

            MapControl.Map?.Refresh();
            RefreshStatusBar();
        }

        private List<Dialogs.RasterLayerInfo> GetLoadedRasterLayerInfos()
        {
            var rasterLayers = new List<Dialogs.RasterLayerInfo>();
            foreach (var (layer, rp) in RasterLayerItems())
            {
                if (!string.IsNullOrWhiteSpace(rp.FilePath))
                    rasterLayers.Add(new Dialogs.RasterLayerInfo(layer.Name, rp.FilePath, rp.TotalBands));
            }

            return rasterLayers;
        }

        private async void OnRegistration(object sender, RoutedEventArgs e)
        {
            var rasterLayers = GetLoadedRasterLayerInfos();
            var dlg = new Dialogs.RegistrationDialog(rasterLayers) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Request == null)
                return;

            try
            {
                KillRunningPythonProcess();
                ShowProgress("影像配准中...");
                await Dialogs.RegistrationDialog.RunRegistrationAsync(dlg.Request,
                    p => _runningPythonProcess = p);
                if (dlg.Request.LoadAfterRegistration)
                {
                    UpdateProgress("配准完成，正在加载结果...", 100);
                    LoadFilesAsync(new[] { dlg.Request.OutMsPath, dlg.Request.OutPanPath });
                }
                else
                {
                    HideProgress();
                    MessageBox.Show(
                        $"配准完成，结果已保存：\n{dlg.Request.OutMsPath}\n{dlg.Request.OutPanPath}",
                        "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                HideProgress();
                MessageBox.Show($"影像配准失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OnBatchRegistration(object sender, RoutedEventArgs e)
        {
            var dlg = new Dialogs.BatchRegistrationDialog(GetLoadedRasterLayerInfos()) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Requests.Count == 0)
                return;

            var requests = dlg.Requests.ToList();
            var outputsToLoad = new List<string>();
            var failures = new List<string>();
            int completed = 0;
            int attempted = 0;

            try
            {
                KillRunningPythonProcess();
                ShowProgress("批量影像配准中...");

                for (int i = 0; i < requests.Count; i++)
                {
                    var request = requests[i];
                    attempted++;
                    string progressLabel = $"批量配准 正在处理 {i + 1}/{requests.Count}: {Path.GetFileName(request.MsPath)}";
                    var taskProgress = CreateBatchTaskProgress(progressLabel, i, requests.Count);
                    taskProgress.Report(0);

                    try
                    {
                        await Dialogs.RegistrationDialog.RunRegistrationAsync(request,
                            p => _runningPythonProcess = p,
                            taskProgress);
                        _runningPythonProcess = null;
                        completed++;
                        taskProgress.Report(100);

                        if (request.LoadAfterRegistration)
                        {
                            outputsToLoad.Add(request.OutMsPath);
                            outputsToLoad.Add(request.OutPanPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _runningPythonProcess = null;
                        failures.Add($"{Path.GetFileName(request.MsPath)} + {Path.GetFileName(request.PanPath)}: {ex.Message}");
                        taskProgress.Report(100);
                        if (!dlg.ContinueOnError)
                            break;
                    }
                }

                string summary = BuildBatchRegistrationSummary(requests.Count, attempted, completed, failures);
                if (failures.Count > 0 || outputsToLoad.Count == 0)
                {
                    HideProgress();
                    MessageBox.Show(summary, "批量配准完成", MessageBoxButton.OK,
                        failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                }

                if (outputsToLoad.Count > 0)
                {
                    UpdateProgress("批量配准完成，正在加载结果...", 100);
                    LoadFilesAsync(outputsToLoad.ToArray());
                }
            }
            catch (Exception ex)
            {
                _runningPythonProcess = null;
                HideProgress();
                MessageBox.Show($"批量影像配准失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string BuildBatchRegistrationSummary(
            int total,
            int attempted,
            int completed,
            IReadOnlyList<string> failures)
        {
            int skipped = Math.Max(0, total - attempted);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"批量配准完成：成功 {completed}/{total}");
            if (skipped > 0)
                sb.AppendLine($"未执行：{skipped}");

            if (failures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("失败任务：");
                foreach (var failure in failures.Take(5))
                    sb.AppendLine($"- {failure}");
                if (failures.Count > 5)
                    sb.AppendLine($"- 还有 {failures.Count - 5} 个失败任务未显示");
            }

            return sb.ToString();
        }

        private async void OnImageFusion(object sender, RoutedEventArgs e)
        {
            var rasterLayers = GetLoadedRasterLayerInfos();
            var dlg = new Dialogs.FusionDialog(rasterLayers) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Request == null)
                return;

            try
            {
                KillRunningPythonProcess();
                ShowProgress("影像融合中...");
                await Dialogs.FusionDialog.RunInferenceAsync(dlg.Request,
                    p => _runningPythonProcess = p);
                if (dlg.Request.LoadAfterFusion)
                {
                    UpdateProgress("融合完成，正在加载结果...", 100);
                    LoadFilesAsync(new[] { dlg.Request.OutputPath });
                }
                else
                {
                    HideProgress();
                    MessageBox.Show(
                        $"融合完成，结果已保存：\n{dlg.Request.OutputPath}",
                        "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                HideProgress();
                MessageBox.Show($"影像融合失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OnBatchImageFusion(object sender, RoutedEventArgs e)
        {
            var dlg = new Dialogs.BatchFusionDialog(GetLoadedRasterLayerInfos()) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Requests.Count == 0)
                return;

            var requests = dlg.Requests.ToList();
            var outputsToLoad = new List<string>();
            var failures = new List<string>();
            int completed = 0;
            int attempted = 0;

            try
            {
                KillRunningPythonProcess();
                ShowProgress("批量影像融合中...");

                for (int i = 0; i < requests.Count; i++)
                {
                    var request = requests[i];
                    attempted++;
                    string progressLabel = $"批量融合 正在处理 {i + 1}/{requests.Count}: {Path.GetFileName(request.MsPath)}";
                    var taskProgress = CreateBatchTaskProgress(progressLabel, i, requests.Count);
                    taskProgress.Report(0);

                    try
                    {
                        await Dialogs.FusionDialog.RunInferenceAsync(request,
                            p => _runningPythonProcess = p,
                            taskProgress);
                        _runningPythonProcess = null;
                        completed++;
                        taskProgress.Report(100);

                        if (request.LoadAfterFusion)
                            outputsToLoad.Add(request.OutputPath);
                    }
                    catch (Exception ex)
                    {
                        _runningPythonProcess = null;
                        failures.Add($"{Path.GetFileName(request.MsPath)} + {Path.GetFileName(request.PanPath)}: {ex.Message}");
                        taskProgress.Report(100);
                        if (!dlg.ContinueOnError)
                            break;
                    }
                }

                string summary = BuildBatchFusionSummary(requests.Count, attempted, completed, failures);
                if (failures.Count > 0 || outputsToLoad.Count == 0)
                {
                    HideProgress();
                    MessageBox.Show(summary, "批量融合完成", MessageBoxButton.OK,
                        failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                }

                if (outputsToLoad.Count > 0)
                {
                    UpdateProgress("批量融合完成，正在加载结果...", 100);
                    LoadFilesAsync(outputsToLoad.ToArray());
                }
            }
            catch (Exception ex)
            {
                _runningPythonProcess = null;
                HideProgress();
                MessageBox.Show($"批量影像融合失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string BuildBatchFusionSummary(
            int total,
            int attempted,
            int completed,
            IReadOnlyList<string> failures)
        {
            int skipped = Math.Max(0, total - attempted);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"批量融合完成：成功 {completed}/{total}");
            if (skipped > 0)
                sb.AppendLine($"未执行：{skipped}");

            if (failures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("失败任务：");
                foreach (var failure in failures.Take(5))
                    sb.AppendLine($"- {failure}");
                if (failures.Count > 5)
                    sb.AppendLine($"- 还有 {failures.Count - 5} 个失败任务未显示");
            }

            return sb.ToString();
        }

        private void OnFusionPreprocess(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "影像融合预处理功能入口已添加。\n后续可在这里打开 PAN/MS 预处理对话框。",
                "影像融合预处理",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OnBandCalculator(object sender, RoutedEventArgs e)
        {
            var rasters = new List<(string Name, string FilePath, int Bands)>();
            foreach (var (layer, rp) in RasterLayerItems())
            {
                if (rp.RendererType == Services.RasterRendererType.Rgb)
                    rasters.Add((layer.Name, rp.FilePath, rp.TotalBands));
            }

            if (rasters.Count == 0)
            {
                MessageBox.Show("没有可用的多波段影像。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Dialogs.BandCalcDialog(rasters) { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.SourcePath))
                return;

            // Computation on background thread, dialog already closed
            var progress = new Progress<(int percent, string label)>(p =>
            {
                if (p.percent < 0)
                    ShowProgress(p.label);
                else
                    UpdateProgress(p.label, p.percent);
            });
            string srcPath = dlg.SourcePath, formula = dlg.Formula, outName = dlg.ResultLayerName!;
            int bands = dlg.BandCount;

            ShowProgress("波段计算中...");
            _ = Task.Run(() =>
            {
                try
                {
                    string outPath = Dialogs.BandCalcDialog.ComputeBandMath(
                        srcPath, formula, bands, outName, progress);
                    Dispatcher.Invoke(() =>
                    {
                        UpdateProgress("计算完成，正在加载结果...", 100);
                        LoadFilesAsync(new[] { outPath });
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        HideProgress();
                        MessageBox.Show($"计算失败: {ex.Message}", "错误",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            });
        }

        private void OnMenuOpen(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => OnOpenFileClick(this, new RoutedEventArgs())));
        }

        private void OnMenuSave(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => OnSaveClick(this, new RoutedEventArgs())));
        }

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            if (!PromptToSaveCurrentProject("退出 MyGIS"))
                e.Cancel = true;
        }

        private bool HasProjectContent()
            => (MapControl.Map?.Layers?.Count ?? 0) > 0 || GpuRasterMap.HasRasterLayers;

        private bool PromptToSaveCurrentProject(string title)
        {
            if (!HasProjectContent())
                return true;

            var result = MessageBox.Show(
                "是否保存当前项目?",
                title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            return result switch
            {
                MessageBoxResult.Yes => TrySaveProjectWithDialog(),
                MessageBoxResult.No => true,
                _ => false
            };
        }

        private bool TrySaveProjectWithDialog()
        {
            var dlg = new SaveFileDialog
            {
                Title = "保存项目",
                Filter = "MyGIS 项目|*.mygis",
                DefaultExt = ".mygis"
            };

            if (dlg.ShowDialog() != true)
                return false;

            try
            {
                SaveProject(dlg.FileName);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async void OnOpenFileClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "打开项目文件",
                Filter = "MyGIS 项目|*.mygis|所有文件|*.*",
                DefaultExt = ".mygis"
            };
            if (dlg.ShowDialog() != true) return;

            // Prompt to save current project first
            if (!PromptToSaveCurrentProject("打开项目"))
                return;

            Models.ProjectFile project;
            try
            {
                var json = File.ReadAllText(dlg.FileName);
                project = JsonSerializer.Deserialize<Models.ProjectFile>(json)
                    ?? new Models.ProjectFile();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"项目文件读取失败:\n{ex.Message}", "MyGIS",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Validate file paths exist
            var missing = project.Layers
                .Where(e => !File.Exists(e.FilePath))
                .Select(e => e.FilePath).ToList();
            if (missing.Count > 0)
            {
                var msg = $"以下文件不存在:\n{string.Join("\n", missing)}\n\n是否继续加载其余图层?";
                if (MessageBox.Show(msg, "MyGIS", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes)
                    return;
            }

            // Clear existing layers
            ClearAllLayers();

            // Load layers in saved order
            var existing = project.Layers.Where(e => File.Exists(e.FilePath)).ToList();
            ShowProgress("正在加载项目...");
            int projectLoadIndex = 0;
            foreach (var entry in existing)
            {
                var loadProgress = CreateLoadProgress(entry.FilePath, projectLoadIndex, existing.Count);
                try
                {
                    var layer = await Task.Run(async () => await DataLoader.LoadAsync(entry.FilePath, loadProgress));
                    if (layer == null) continue;

                    layer.Name = entry.Name;
                    layer.Enabled = entry.IsVisible;

                    // Apply raster settings
                    if (entry.Type == "raster")
                    {
                        Providers.GdalRasterProvider? rp = null;
                        if (layer is Mapsui.Layers.Layer l)
                            rp = l.DataSource as Providers.GdalRasterProvider;
                        rp ??= layer.Tag as Providers.GdalRasterProvider;

                        if (rp != null)
                        {
                            if (entry.StretchType != null &&
                                Enum.TryParse<Services.StretchType>(entry.StretchType, out var st))
                                rp.ChangeStretch(st);
                            if (entry.ColorRamp != null &&
                                Enum.TryParse<Services.ColorRampType>(entry.ColorRamp, out var cr))
                                rp.ChangeColorRamp(cr);
                            if (entry.BandIndexes is { Length: 3 })
                                rp.ChangeBands(entry.BandIndexes);
                        }
                    }

                    AddLayerToRenderer(layer);
                    var crs = AddLayerItem(layer);
                    CheckCrsMismatch(crs, entry.Name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载失败: {entry.Name}\n{ex.Message}",
                        "MyGIS", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    projectLoadIndex++;
                }
            }

            if ((MapControl.Map?.Layers.Count ?? 0) > 0 || GpuRasterMap.HasRasterLayers)
            {
                var extent = GetLayersExtent();
                if (extent != null)
                {
                    if (GpuRasterMap.HasRasterLayers)
                        GpuRasterMap.ZoomToExtent(extent);
                    else
                        MapControl.Map?.Navigator.ZoomToBox(extent, MBoxFit.Fit, 200);
                }
                MapControl.Refresh();
                RefreshStatusBar();
            }
            HideProgress();
        }

        private void OnAddDataClick(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "添加数据",
                Filter = DataLoader.GetFilter(),
                Multiselect = true
            };
            if (dlg.ShowDialog() == true)
            {
                LoadFilesAsync(dlg.FileNames);
            }
        }

        private async void LoadFilesAsync(string[] filePaths)
        {
            ShowProgress("正在加载...");
            int fileIndex = 0;
            foreach (var path in filePaths)
            {
                var loadProgress = CreateLoadProgress(path, fileIndex, filePaths.Length);
                try
                {
                    var layer = await Task.Run(async () => await DataLoader.LoadAsync(path, loadProgress));
                    if (layer != null)
                    {
                        AddLayerToRenderer(layer);
                        var crs = AddLayerItem(layer);
                        CheckCrsMismatch(crs, Path.GetFileName(path));
                    }
                    else
                    {
                        MessageBox.Show($"不支持的文件格式: {Path.GetExtension(path)}",
                                        "MyGIS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载文件失败: {Path.GetFileName(path)}\n{ex.Message}",
                                    "MyGIS", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    fileIndex++;
                }
            }

            if ((MapControl.Map?.Layers.Count ?? 0) > 0 || GpuRasterMap.HasRasterLayers)
            {
                var extent = GetLayersExtent();
                if (extent != null)
                {
                    if (GpuRasterMap.HasRasterLayers)
                        GpuRasterMap.ZoomToExtent(extent);
                    else
                        MapControl.Map?.Navigator.ZoomToBox(extent, MBoxFit.Fit, 200);
                }
                MapControl.Refresh();
                RefreshStatusBar();
            }

            HideProgress();
        }

        private string? _mapCrs;

        private void CheckCrsMismatch(string layerCrs, string fileName)
        {
            if (string.IsNullOrWhiteSpace(layerCrs) || layerCrs == "未知") return;

            if (_mapCrs == null)
            {
                _mapCrs = layerCrs;
                if (MapControl.Map != null)
                    MapControl.Map.CRS = layerCrs;
                return;
            }

            string keyMap = CoordinateConverter.GetEpsgComparisonKey(_mapCrs);
            string keyLayer = CoordinateConverter.GetEpsgComparisonKey(layerCrs);
            if (!string.IsNullOrEmpty(keyMap) && keyMap == keyLayer)
                return;

            string mapName = CoordinateConverter.GetCrsDisplayName(_mapCrs);
            string layerName = CoordinateConverter.GetCrsDisplayName(layerCrs);
            MessageBox.Show(
                $"坐标系不一致:\n\n" +
                $"  当前地图: {mapName}\n" +
                $"  {fileName}: {layerName}\n\n" +
                $"不同坐标系的数据叠加可能存在位置偏差。\n" +
                $"后续版本将支持投影转换。",
                "坐标系提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static Providers.GdalRasterProvider? GetRasterProvider(ILayer? layer)
        {
            Providers.GdalRasterProvider? rp = null;
            if (layer is Mapsui.Layers.Layer l)
                rp = l.DataSource as Providers.GdalRasterProvider;
            rp ??= layer?.Tag as Providers.GdalRasterProvider;
            return rp;
        }

        private static Services.DataLoader.ShapeFileProvider? GetShapeProvider(ILayer? layer)
        {
            Services.DataLoader.ShapeFileProvider? sfp = null;
            if (layer is Mapsui.Layers.Layer l)
                sfp = l.DataSource as Services.DataLoader.ShapeFileProvider;
            sfp ??= layer?.Tag as Services.DataLoader.ShapeFileProvider;
            return sfp;
        }

        private void AddLayerToRenderer(ILayer layer)
        {
            if (GetRasterProvider(layer) is { } rp)
            {
                GpuRasterMap.AddRasterLayer(rp, layer.Name);
                GpuRasterMap.SetLayerVisibility(rp, layer.Enabled);
                return;
            }

            MapControl.Map?.Layers.Add(layer);
        }

        private void RemoveLayerFromRenderer(LayerItem item)
        {
            if (GetRasterProvider(item.Layer) is { } rp)
                GpuRasterMap.RemoveRasterLayer(rp);

            if (item.Layer != null)
                MapControl.Map?.Layers.Remove(item.Layer);
        }

        private void DisposeLayerItem(LayerItem item)
        {
            item.PropertyChanged -= OnLayerItemPropertyChanged;
            item.RasterProviderHandle?.Dispose();
            item.RasterProviderHandle = null;
        }

        private void RefreshLayerRenderer(LayerItem item)
        {
            if (GetRasterProvider(item.Layer) is { } rp)
                GpuRasterMap.RefreshRasterLayer(rp);
            else
                MapControl.Map?.Refresh();
        }

        private void SyncLayerRenderOrder()
        {
            var rasterProvidersTopToBottom = _layerItems
                .Select(item => GetRasterProvider(item.Layer))
                .Where(provider => provider != null)
                .Cast<Providers.GdalRasterProvider>()
                .ToList();
            GpuRasterMap.SetRasterLayerOrder(rasterProvidersTopToBottom);

            var layers = MapControl.Map?.Layers;
            if (layers == null) return;

            var vectorLayers = _layerItems
                .Select(item => item.Layer)
                .Where(layer => layer != null && GetRasterProvider(layer) == null)
                .Cast<ILayer>()
                .Where(layer => layers.Contains(layer))
                .ToList();

            foreach (var layer in vectorLayers)
                layers.Remove(layer);

            int insertIndex = 0;
            foreach (var item in _layerItems.Reverse())
            {
                var layer = item.Layer;
                if (layer == null || GetRasterProvider(layer) != null || !vectorLayers.Contains(layer))
                    continue;

                layers.Insert(insertIndex++, layer);
            }
        }

        private IEnumerable<(ILayer Layer, Providers.GdalRasterProvider Provider)> RasterLayerItems()
        {
            foreach (var item in _layerItems)
            {
                if (item.Layer != null && GetRasterProvider(item.Layer) is { } rp)
                    yield return (item.Layer, rp);
            }
        }

        private string AddLayerItem(ILayer layer)
        {
            var rp = GetRasterProvider(layer);
            var sfp = GetShapeProvider(layer);

            string crs = "未知";
            IDisposable? rasterHandle = null;
            Models.LegendInfo? legend = null;

            if (rp != null)
            {
                crs = rp.RasterCrs;
                rasterHandle = rp;
                var st = rp.Stretch;
                if (rp.RendererType == Services.RasterRendererType.Rgb)
                {
                    var bands = rp.SourceBandIndexes;
                    legend = new Models.LegendInfo
                    {
                        IsRgb = true,
                        RedBand = $"红色: Band_{bands[0]}",
                        GreenBand = $"绿色: Band_{bands[1]}",
                        BlueBand = $"蓝色: Band_{bands[2]}"
                    };
                }
                else if (rp.RendererType == Services.RasterRendererType.Gray)
                {
                    legend = new Models.LegendInfo
                    {
                        IsGray = true,
                        MaxValue = Math.Round(st.DataMax.Length > 0 ? st.DataMax[0] : st.Hi[0], 2),
                        MinValue = Math.Round(st.DataMin.Length > 0 ? st.DataMin[0] : st.Lo[0], 2),
                        ColorRamp = st.ColorRamp
                    };
                }
            }
            else if (sfp != null)
            {
                crs = string.IsNullOrWhiteSpace(sfp.CRS) ? "未知" : sfp.CRS;
            }

            var item = new LayerItem
            {
                Name = layer.Name,
                IsVisible = layer.Enabled,
                Layer = layer,
                LayerGroup = rp != null ? "栅格图层" : sfp != null ? "矢量图层" : "其他图层",
                LayerTypeLabel = rp != null
                    ? $"{rp.TotalBands} 波段 · {rp.RasterWidth} x {rp.RasterHeight}"
                    : sfp != null
                        ? $"矢量 · {sfp.FeatureCount} 个要素"
                        : "图层",
                Crs = CoordinateConverter.GetCrsDisplayName(crs),
                RasterProviderHandle = rasterHandle,
                Legend = legend
            };
            item.PropertyChanged += OnLayerItemPropertyChanged;
            _layerItems.Insert(0, item);
            _layerItemsView?.Refresh();
            return crs;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "保存项目",
                Filter = "MyGIS 项目|*.mygis",
                DefaultExt = ".mygis"
            };
            if (dlg.ShowDialog() == true)
                SaveProject(dlg.FileName);
        }

        private void SaveProject(string filePath)
        {
            var project = new Models.ProjectFile();

            // Layer list is shown top-to-bottom; save bottom-to-top so reload preserves draw order.
            foreach (var item in _layerItems.Reverse())
            {
                var layer = item.Layer;
                if (layer == null) continue;

                var entry = new Models.LayerEntry
                {
                    Name = layer.Name,
                    IsVisible = layer.Enabled
                };

                var rp = GetRasterProvider(layer);
                var sfp = GetShapeProvider(layer);

                if (rp != null)
                {
                    entry.FilePath = rp.FilePath;
                    entry.Type = "raster";
                    entry.StretchType = rp.CurrentStretchType.ToString();
                    entry.ColorRamp = rp.Stretch.ColorRamp.ToString();
                    entry.BandIndexes = (int[])rp.SourceBandIndexes.Clone();
                    entry.RendererType = rp.RendererType.ToString();
                }
                else if (sfp != null)
                {
                    entry.FilePath = sfp.FilePath;
                    entry.Type = "vector";
                }
                else
                {
                    continue;
                }

                project.Layers.Add(entry);
            }

            try
            {
                var json = JsonSerializer.Serialize(project,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败:\n{ex.Message}", "MyGIS",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnZoomInClick(object sender, RoutedEventArgs e)
        {
            if (GpuRasterMap.HasRasterLayers)
                GpuRasterMap.ZoomIn();
            else
                MapControl.Map?.Navigator.ZoomIn(200);
            RefreshStatusBar();
        }

        private void OnZoomOutClick(object sender, RoutedEventArgs e)
        {
            if (GpuRasterMap.HasRasterLayers)
                GpuRasterMap.ZoomOut();
            else
                MapControl.Map?.Navigator.ZoomOut(200);
            RefreshStatusBar();
        }

        private void OnFullExtentClick(object sender, RoutedEventArgs e)
        {
            var extent = GetLayersExtent();
            if (extent != null)
            {
                if (GpuRasterMap.HasRasterLayers)
                    GpuRasterMap.ZoomToExtent(extent);
                else
                    MapControl.Map?.Navigator.ZoomToBox(extent, MBoxFit.Fit, 200);
                RefreshStatusBar();
            }
        }

        private static void DeleteTempFile(string filePath)
        {
            try
            {
                File.Delete(filePath);
                var ovrPath = filePath + ".ovr";
                if (File.Exists(ovrPath)) File.Delete(ovrPath);
            }
            catch { }
        }

        private MRect? GetLayersExtent()
        {
            MRect? combined = GpuRasterMap.GetFullExtent();
            if (MapControl.Map == null) return combined;

            foreach (var layer in MapControl.Map.Layers)
            {
                var layerExtent = layer.Extent;
                if (layerExtent == null) continue;
                combined = combined == null
                    ? layerExtent
                    : combined.Join(layerExtent);
            }
            return combined;
        }

        // ===== 图层操作 =====

        private void OnLayerVisibilityChanged(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is LayerItem item && item.Layer != null)
            {
                item.Layer.Enabled = item.IsVisible;
                if (GetRasterProvider(item.Layer) is { } rp)
                    GpuRasterMap.SetLayerVisibility(rp, item.IsVisible);
                else
                    MapControl.Map?.Refresh();
            }
        }

        private void OnLayerCheckBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is CheckBox { DataContext: LayerItem item })
                SelectLayerForPlainClick(item);
        }

        private void SelectLayerForPlainClick(LayerItem item)
        {
            var modifiers = Keyboard.Modifiers;
            bool isMultiSelectGesture =
                modifiers.HasFlag(ModifierKeys.Control) ||
                modifiers.HasFlag(ModifierKeys.Shift);

            if (isMultiSelectGesture) return;

            if (LayerListBox.SelectedItems.Count == 1 && ReferenceEquals(LayerListBox.SelectedItem, item))
                return;

            LayerListBox.SelectedItems.Clear();
            LayerListBox.SelectedItem = item;
        }

        private void OnLayerItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is LayerItem item && e.PropertyName == nameof(LayerItem.IsVisible) && item.Layer != null)
            {
                item.Layer.Enabled = item.IsVisible;
                if (GetRasterProvider(item.Layer) is { } rp)
                    GpuRasterMap.SetLayerVisibility(rp, item.IsVisible);
                else
                    MapControl.Map?.Refresh();
            }
        }

        private void RefreshLegend(LayerItem item, Providers.GdalRasterProvider rp)
        {
            var st = rp.Stretch;
            if (rp.RendererType == Services.RasterRendererType.Rgb)
            {
                var bands = rp.SourceBandIndexes;
                item.Legend = new Models.LegendInfo
                {
                    IsRgb = true,
                    RedBand = $"红色: Band_{bands[0]}",
                    GreenBand = $"绿色: Band_{bands[1]}",
                    BlueBand = $"蓝色: Band_{bands[2]}"
                };
            }
            else if (rp.RendererType == Services.RasterRendererType.Gray)
            {
                item.Legend = new Models.LegendInfo
                {
                    IsGray = true,
                    MaxValue = Math.Round(st.DataMax.Length > 0 ? st.DataMax[0] : st.Hi[0], 2),
                    MinValue = Math.Round(st.DataMin.Length > 0 ? st.DataMin[0] : st.Lo[0], 2),
                    ColorRamp = st.ColorRamp
                };
            }
        }

        // ===== 图层右键菜单 =====

        private Popup? _layerPopup;

        private void OnLayerListRightClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? current = e.OriginalSource as DependencyObject;
            ListBoxItem? clickedContainer = null;
            while (current != null)
            {
                if (current is ListBoxItem lbi)
                {
                    clickedContainer = lbi;
                    break;
                }
                current = VisualTreeHelper.GetParent(current);
            }

            if (clickedContainer == null) return;

            // If clicked item is not in current selection, select only it
            if (!clickedContainer.IsSelected)
            {
                LayerListBox.SelectedItems.Clear();
                clickedContainer.IsSelected = true;
            }
            // Otherwise preserve multi-selection

            _rightClickedItem = clickedContainer.DataContext as LayerItem;
            if (_rightClickedItem == null) return;

            int selCount = LayerListBox.SelectedItems.Count;

            bool hasAttrTable = false;
            if (_rightClickedItem.Layer is Mapsui.Layers.Layer l)
                hasAttrTable = l.DataSource is Services.DataLoader.ShapeFileProvider;
            if (!hasAttrTable)
                hasAttrTable = _rightClickedItem.Layer?.Tag is Services.DataLoader.ShapeFileProvider;

            _layerPopup = new Popup
            {
                PlacementTarget = sender as UIElement,
                Placement = PlacementMode.MousePoint,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var panel = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xFE, 0xFE)) };
            panel.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2, 2, 2, 2),
                Child = BuildPopupMenu(hasAttrTable, selCount)
            });

            _layerPopup.Child = panel;
            _layerPopup.IsOpen = true;
            e.Handled = true;
        }

        private UIElement BuildPopupMenu(bool hasAttrTable, int selCount)
        {
            var stack = new StackPanel { Margin = new Thickness(4, 4, 4, 4) };

            var zoomBtn = CreatePopupButton("缩放到图层");
            zoomBtn.Click += (_, _) => { _layerPopup!.IsOpen = false; OnLayerZoomTo(zoomBtn, new RoutedEventArgs()); };
            stack.Children.Add(zoomBtn);

            var dispBtn = CreatePopupButton("显示设置");
            dispBtn.Click += (_, _) => { _layerPopup!.IsOpen = false; OnLayerDisplaySettings(dispBtn, new RoutedEventArgs()); };
            stack.Children.Add(dispBtn);

            var attrBtn = CreatePopupButton("打开属性表");
            if (hasAttrTable)
            {
                attrBtn.Click += (_, _) => { _layerPopup!.IsOpen = false; OnOpenAttributeTable(attrBtn, new RoutedEventArgs()); };
            }
            else
            {
                attrBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));
                attrBtn.Cursor = System.Windows.Input.Cursors.Arrow;
            }
            stack.Children.Add(attrBtn);

            stack.Children.Add(new Separator { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), Margin = new Thickness(0, 2, 0, 2) });

            var propBtn = CreatePopupButton("属性");
            propBtn.Click += (_, _) => { _layerPopup!.IsOpen = false; OnLayerProperties(propBtn, new RoutedEventArgs()); };
            stack.Children.Add(propBtn);

            stack.Children.Add(new Separator { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), Margin = new Thickness(0, 2, 0, 2) });

            string removeLabel = selCount > 1 ? $"移除所选 ({selCount})" : "移除图层";
            var removeBtn = CreatePopupButton(removeLabel);
            removeBtn.Click += (_, _) => { _layerPopup!.IsOpen = false; OnRemoveSelected(); };
            stack.Children.Add(removeBtn);

            return stack;
        }

        private static Button CreatePopupButton(string text) => new()
        {
            Content = text,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Cursor = System.Windows.Input.Cursors.Hand,
            MinWidth = 150
        };

        private LayerItem? _rightClickedItem;

        private void OnLayerZoomTo(object sender, RoutedEventArgs e)
        {
            var item = GetLayerItemFromSender(sender);
            if (item?.Layer == null)
                return;

            if (GetRasterProvider(item.Layer) is { } rp)
            {
                if (rp.GetExtent() is { } rasterExtent)
                {
                    GpuRasterMap.ZoomToExtent(rasterExtent);
                    RefreshStatusBar();
                }
                return;
            }

            if (item.Layer.Extent is MRect extent)
            {
                MapControl.Map?.Navigator.ZoomToBox(extent, MBoxFit.Fit, 200);
                RefreshStatusBar();
            }
        }

        private void OnLayerDisplaySettings(object sender, RoutedEventArgs e)
        {
            var item = GetLayerItemFromSender(sender);
            if (item?.Layer?.Tag is not Providers.GdalRasterProvider provider) return;

            bool isMulti = provider.RendererType == Services.RasterRendererType.Rgb;
            var dlg = new Dialogs.BandDisplayDialog(
                provider.TotalBands,
                provider.SourceBandIndexes,
                provider.CurrentStretchType,
                provider.Stretch.ColorRamp,
                isMulti);

            dlg.Owner = this;
            dlg.SettingsApplied += (_, args) =>
            {
                bool changed = false;
                if (args.StretchChanged)
                {
                    provider.ChangeStretch(args.SelectedStretch);
                    changed = true;
                }
                if (args.ColorRampChanged)
                {
                    provider.ChangeColorRamp(args.SelectedColorRamp);
                    changed = true;
                }
                if (args.BandsChanged)
                {
                    provider.ChangeBands(args.SelectedBands);
                    changed = true;
                }

                if (changed)
                {
                    RefreshLegend(item, provider);
                    RefreshLayerRenderer(item);
                }
            };

            dlg.ShowDialog();
        }

        private void OnOpenAttributeTable(object sender, RoutedEventArgs e)
        {
            if (GetLayerItemFromSender(sender) is not { } item)
                return;

            Services.DataLoader.ShapeFileProvider? sfp = null;
            if (item.Layer is Mapsui.Layers.Layer l && l.DataSource is Services.DataLoader.ShapeFileProvider p)
                sfp = p;
            else if (item.Layer?.Tag is Services.DataLoader.ShapeFileProvider p2)
                sfp = p2;

            if (sfp == null)
            {
                MessageBox.Show("仅 Shapefile 图层支持属性表。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int count = sfp.FeatureCount;
            var features = new List<Mapsui.IFeature>();

            // Display names from DBF (correct encoding), lookup keys from Mapsui feature
            var displayNames = Services.DataLoader.ReadDbfFieldNames(sfp.FilePath, sfp.Encoding);
            var lookupKeys = new List<string>();
            var first = sfp.GetFeature(0u);
            if (first?.Fields != null)
            {
                foreach (var key in first.Fields)
                {
                    if (key != "geometry" && key != "Geometry")
                        lookupKeys.Add(key);
                }
            }

            // Read all features (limit to 10000 for performance)
            int maxRead = Math.Min(count, 10000);
            for (uint i = 0u; i < maxRead; i++)
            {
                var f = sfp.GetFeature(i);
                if (f != null) features.Add(f);
            }

            var dlg = new Dialogs.AttributeTableDialog(
                item.Name, count, features, displayNames, lookupKeys.ToArray())
            { Owner = this };
            dlg.Show();
        }

        private void OnLayerProperties(object sender, RoutedEventArgs e)
        {
            var item = GetLayerItemFromSender(sender);
            if (item?.Layer == null) return;

            var extent = item.Layer.Extent;

            // Find providers
            Providers.GdalRasterProvider? rp = null;
            Services.DataLoader.ShapeFileProvider? sfp = null;
            if (item.Layer is Mapsui.Layers.Layer l)
            {
                rp = l.DataSource as Providers.GdalRasterProvider;
                sfp = l.DataSource as Services.DataLoader.ShapeFileProvider;
            }
            rp ??= item.Layer.Tag as Providers.GdalRasterProvider;
            sfp ??= item.Layer.Tag as Services.DataLoader.ShapeFileProvider;

            string layerType, filePath;
            int? rasterW = null, rasterH = null, bandCount = null, overviewCount = null, featureCount = null;
            string? renderer = null, stretchType = null, encoding = null;

            if (rp != null)
            {
                layerType = "栅格";
                filePath = rp.FilePath;
                rasterW = rp.RasterWidth;
                rasterH = rp.RasterHeight;
                bandCount = rp.TotalBands;
                renderer = rp.RendererType.ToString();
                overviewCount = rp.OverviewCount;
                stretchType = rp.CurrentStretchType.ToString();
            }
            else if (sfp != null)
            {
                layerType = "矢量";
                filePath = sfp.FilePath;
                featureCount = sfp.FeatureCount;
                encoding = sfp.Encoding.WebName;
            }
            else
            {
                layerType = "未知";
                filePath = "—";
            }

            // Convert extent to lon/lat if the data has a known CRS
            double? minX = extent?.MinX, minY = extent?.MinY, maxX = extent?.MaxX, maxY = extent?.MaxY;
            bool isLonLat = false;
            if (extent != null)
            {
                string rawCrs = (rp?.RasterCrs ?? sfp?.CRS) ?? "";
                if (!string.IsNullOrEmpty(rawCrs) && rawCrs != "未知")
                {
                    var (ll1Lon, ll1Lat) = CoordinateConverter.ToLonLat(extent.MinX, extent.MinY, rawCrs);
                    var (ll2Lon, ll2Lat) = CoordinateConverter.ToLonLat(extent.MaxX, extent.MaxY, rawCrs);
                    minX = Math.Min(ll1Lon, ll2Lon);
                    maxX = Math.Max(ll1Lon, ll2Lon);
                    minY = Math.Min(ll1Lat, ll2Lat);
                    maxY = Math.Max(ll1Lat, ll2Lat);
                    isLonLat = true;
                }
            }

            var dlg = new Dialogs.LayerPropertiesDialog(
                item.Name, layerType, filePath, item.Crs,
                minX, minY, maxX, maxY, isLonLat,
                rasterW, rasterH, bandCount, renderer, overviewCount, stretchType,
                featureCount, encoding)
            { Owner = this };
            dlg.ShowDialog();
        }

        private void OnRemoveSelected()
        {
            var selected = LayerListBox.SelectedItems.Cast<LayerItem>().ToList();
            if (selected.Count == 0) return;

            if (selected.Count > 1)
            {
                if (MessageBox.Show($"确定移除 {selected.Count} 个图层?",
                    "移除图层", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                    != MessageBoxResult.OK)
                    return;
            }

            foreach (var item in selected)
            {
                if (item.Layer == null) continue;

                bool isTemp = item.Layer.Tag is Providers.GdalRasterProvider rp && rp.IsTempFile;
                string? filePath = isTemp ? (item.Layer.Tag as Providers.GdalRasterProvider)?.FilePath : null;
                string? fileToDelete = null;

                if (selected.Count == 1 || isTemp)
                {
                    var dlg = new Dialogs.RemoveLayerDialog(item.Name, isTemp, filePath) { Owner = this };
                    if (dlg.ShowDialog() != true || dlg.Action == Dialogs.RemoveLayerDialog.RemoveAction.Cancel)
                        continue;

                    if ((dlg.Action == Dialogs.RemoveLayerDialog.RemoveAction.Delete ||
                         dlg.Action == Dialogs.RemoveLayerDialog.RemoveAction.SaveAs) &&
                        filePath != null)
                    {
                        fileToDelete = filePath;
                    }
                }

                RemoveLayerFromRenderer(item);
                DisposeLayerItem(item);
                _layerItems.Remove(item);

                if (fileToDelete != null)
                    DeleteTempFile(fileToDelete);
            }

            MapControl.Map?.Refresh();
        }

        private void OnLayerRemove(object sender, RoutedEventArgs e)
        {
            var item = GetLayerItemFromSender(sender);
            if (item?.Layer == null) return;

            bool isTemp = item.Layer.Tag is Providers.GdalRasterProvider rp && rp.IsTempFile;
            string? filePath = isTemp ? (item.Layer.Tag as Providers.GdalRasterProvider)?.FilePath : null;

            var dlg = new Dialogs.RemoveLayerDialog(item.Name, isTemp, filePath) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Action == Dialogs.RemoveLayerDialog.RemoveAction.Cancel)
                return;

            string? fileToDelete = null;
            if ((dlg.Action == Dialogs.RemoveLayerDialog.RemoveAction.Delete ||
                 dlg.Action == Dialogs.RemoveLayerDialog.RemoveAction.SaveAs) &&
                filePath != null)
                fileToDelete = filePath;

            RemoveLayerFromRenderer(item);
            DisposeLayerItem(item);
            _layerItems.Remove(item);
            if (fileToDelete != null)
                DeleteTempFile(fileToDelete);
            MapControl.Map?.Refresh();
        }

        private LayerItem? GetLayerItemFromSender(object sender)
        {
            if (sender is Button && _rightClickedItem != null)
                return _rightClickedItem;

            try
            {
                // First try: use SelectedItem (works when ContextMenu is on ListBox)
                if (LayerListBox.SelectedItem is LayerItem selected)
                    return selected;
            }
            catch { }

            try
            {
                if (sender is not MenuItem menuItem) return null;
                if (menuItem.Parent is not ContextMenu ctxMenu) return null;
                if (ctxMenu.PlacementTarget is not FrameworkElement fe) return null;

                DependencyObject? current = fe;
                while (current != null)
                {
                    if (current is FrameworkElement el && el.DataContext is LayerItem item)
                        return item;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            catch { }
            return null;
        }

        // ===== 图层拖拽排序 =====

        private void OnLayerListPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragStart = null;
                _dragIndex = -1;
                return;
            }

            var currentPos = e.GetPosition(null);

            // First move with button down: record start and find item
            if (_dragStart == null)
            {
                _dragStart = currentPos;
                _dragIndex = -1;

                DependencyObject? current = e.OriginalSource as DependencyObject;
                while (current != null)
                {
                    if (current is ListBoxItem lbi)
                    {
                        var lb = sender as ListBox;
                        if (lb != null)
                        {
                            _dragIndex = lb.ItemContainerGenerator.IndexFromContainer(lbi);
                            if (lbi.DataContext is LayerItem item)
                                SelectLayerForPlainClick(item);
                        }
                        break;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
                return;
            }

            // Check if dragged far enough to start
            var diff = _dragStart.Value - currentPos;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (LayerListBox.SelectedItem is LayerItem item)
                {
                    _dragStart = null;
                    DragDrop.DoDragDrop(LayerListBox, item, DragDropEffects.Move);
                    _dragIndex = -1;
                }
            }
        }

        private void OnLayerListDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(LayerItem)) || _dragIndex < 0) return;

            var draggedItem = e.Data.GetData(typeof(LayerItem)) as LayerItem;
            if (draggedItem?.Layer == null) return;

            var lb = sender as ListBox;
            if (lb == null) return;
            var pos = e.GetPosition(lb);

            int targetIndex = lb.Items.Count;
            for (int i = 0; i < lb.Items.Count; i++)
            {
                var container = lb.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
                if (container != null)
                {
                    var itemPos = container.TransformToAncestor(lb).Transform(new System.Windows.Point(0, 0));
                    if (pos.Y < itemPos.Y + container.ActualHeight / 2)
                    {
                        targetIndex = i;
                        break;
                    }
                }
            }

            if (targetIndex < lb.Items.Count && lb.Items[targetIndex] is LayerItem targetItem)
            {
                targetIndex = _layerItems.IndexOf(targetItem);
            }
            else if (lb.Items.Count > 0 && lb.Items[lb.Items.Count - 1] is LayerItem lastVisibleItem)
            {
                targetIndex = _layerItems.IndexOf(lastVisibleItem) + 1;
            }
            else
            {
                targetIndex = _layerItems.Count;
            }

            if (targetIndex < 0)
                targetIndex = _layerItems.Count;

            int sourceIndex = _layerItems.IndexOf(draggedItem);
            if (sourceIndex < 0) return;

            if (targetIndex > sourceIndex)
                targetIndex--;

            _layerItems.RemoveAt(sourceIndex);

            // Clamp targetIndex to valid range after removal
            if (targetIndex < 0) targetIndex = 0;
            if (targetIndex > _layerItems.Count) targetIndex = _layerItems.Count;

            _layerItems.Insert(targetIndex, draggedItem);
            LayerListBox.SelectedItem = draggedItem;
            _layerItemsView?.Refresh();

            SyncLayerRenderOrder();
            MapControl.Map?.Refresh();

            _dragStart = null;
            _dragIndex = -1;
            e.Handled = true;
        }
    }
}
