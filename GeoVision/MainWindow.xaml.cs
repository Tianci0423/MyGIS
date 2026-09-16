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
using GeoVision.Helpers;
using GeoVision.Models;
using GeoVision.Services;

namespace GeoVision
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
        private readonly Dictionary<Providers.GdalRasterProvider, Dialogs.BandDisplayDialog> _bandDisplayDialogs = new();
        private string? _currentProjectPath;
        private bool _lastSaveSucceeded;
        private readonly List<string> _recentProjectPaths = new();
        private const int MaxRecentProjects = 10;
        private static readonly string RecentProjectsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeoVision",
            "recent-projects.json");
        private Dialogs.RasterClipRequest? _pendingRectangleClip;
        private Point? _clipDragStart;
        private bool _clipSelectionActive;
        private bool _clipAdjustmentActive;
        private Rect _clipSelectionRect;
        private Providers.GdalRasterProvider? _swipeLeftProvider;
        private Providers.GdalRasterProvider? _swipeRightProvider;
        private double _swipePosition = 0.5;
        private bool _overviewEnabled;
        private bool _cancelRequested;

        private void KillRunningPythonProcess()
        {
            try
            {
                if (_runningPythonProcess is { HasExited: false })
                {
                    _runningPythonProcess.Kill(entireProcessTree: true);
                }
            }
            catch { }
            finally
            {
                _runningPythonProcess?.Dispose();
            }
            _runningPythonProcess = null;
            CancelTaskButton.IsEnabled = false;
        }

        public void ShowProgress(string label)
        {
            _cancelRequested = false;
            ProgressLabel.Text = label;
            ProgressBar.IsIndeterminate = true;
            ProgressBar.Value = 0;
            ProgressPanel.Visibility = Visibility.Visible;
            CancelTaskButton.IsEnabled = _runningPythonProcess != null;
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
            CancelTaskButton.IsEnabled = _runningPythonProcess != null;
        }

        public void HideProgress()
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            CancelTaskButton.IsEnabled = false;
        }

        private void SetRunningPythonProcess(Process process)
        {
            _runningPythonProcess = process;
            CancelTaskButton.IsEnabled = true;
        }

        private void OnCancelTaskClick(object sender, RoutedEventArgs e)
        {
            if (_runningPythonProcess == null)
                return;
            _cancelRequested = true;
            ProgressLabel.Text = "正在取消任务...";
            KillRunningPythonProcess();
        }

        private bool ConsumeCancellation()
        {
            if (!_cancelRequested)
                return false;
            _cancelRequested = false;
            HideProgress();
            return true;
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
            LoadRecentProjects();
            RefreshRecentProjectsMenu();
            PreviewKeyDown += OnClipSelectionKeyDown;
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
            map.Navigator.FetchRequested += (_, _) =>
                Dispatcher.BeginInvoke(new Action(UpdateOverviewViewport));
            MapControl.PreviewMouseLeftButtonDown += OnMapClick;
            GpuRasterMap.PreviewMouseLeftButtonDown += OnClipSelectionMouseDown;
            GpuRasterMap.PreviewMouseMove += OnClipSelectionMouseMove;
            GpuRasterMap.PreviewMouseLeftButtonUp += OnClipSelectionMouseUp;
            GpuRasterMap.PreviewMouseLeftButtonDown += OnMapClick;
            GpuRasterMap.ViewportChanged += (_, _) => UpdateOverviewViewport();

            var overviewMap = new Map { CRS = "EPSG:3857" };
            overviewMap.Widgets.Clear();
            OverviewMapControl.UseFling = false;
            OverviewMapControl.Map = overviewMap;

            _coordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _coordTimer.Tick += OnCoordTimerTick;
            _coordTimer.Start();
        }

        private void OnOverviewToggleClick(object sender, RoutedEventArgs e)
        {
            _overviewEnabled = sender switch
            {
                ToggleButton toggle => toggle.IsChecked == true,
                MenuItem menuItem => menuItem.IsChecked,
                _ => !_overviewEnabled
            };

            SetOverviewToggleState(_overviewEnabled);
            if (_overviewEnabled)
                RefreshOverviewMap();
            else
                UpdateOverviewVisibility();
        }

        private void OnOverviewCloseClick(object sender, RoutedEventArgs e)
        {
            _overviewEnabled = false;
            SetOverviewToggleState(false);
            UpdateOverviewVisibility();
        }

        private void SetOverviewToggleState(bool enabled)
        {
            OverviewBtn.IsChecked = enabled;
            OverviewMenuItem.IsChecked = enabled;
        }

        private void UpdateOverviewVisibility()
        {
            OverviewPanel.Visibility = _overviewEnabled && _layerItems.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void RefreshOverviewMap()
        {
            var overviewMap = OverviewMapControl.Map;
            if (overviewMap == null)
                return;

            ClearOverviewLayers();
            overviewMap.CRS = _mapCrs ?? MapControl.Map?.CRS ?? "EPSG:3857";

            foreach (var item in _layerItems.Reverse())
            {
                if (item.Layer is not Mapsui.Layers.Layer sourceLayer ||
                    sourceLayer.DataSource == null)
                {
                    continue;
                }

                var overviewLayer = new Mapsui.Layers.Layer(item.Name)
                {
                    DataSource = sourceLayer.DataSource,
                    Style = sourceLayer.Style,
                    Enabled = item.IsVisible,
                    Opacity = sourceLayer.Opacity,
                    Tag = sourceLayer.Tag
                };
                overviewMap.Layers.Add(overviewLayer);
            }

            UpdateOverviewVisibility();
            if (!_overviewEnabled || _layerItems.Count == 0)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (GetOverviewExtent() is { } extent &&
                    OverviewMapHost.ActualWidth > 0 && OverviewMapHost.ActualHeight > 0)
                {
                    overviewMap.Navigator.ZoomToBox(extent, MBoxFit.Fit, 0);
                }

                OverviewMapControl.Refresh();
                UpdateOverviewViewport();
            }), DispatcherPriority.Loaded);
        }

        private void ClearOverviewLayers()
        {
            var layers = OverviewMapControl.Map?.Layers;
            if (layers == null)
                return;

            for (int i = layers.Count - 1; i >= 0; i--)
                layers.Remove(layers.Get(i));
        }

        private MRect? GetOverviewExtent()
        {
            MRect? combined = null;
            foreach (var item in _layerItems.Where(item => item.IsVisible && item.Layer != null))
            {
                MRect? extent = GetRasterProvider(item.Layer)?.GetExtent() ?? item.Layer!.Extent;
                if (extent == null)
                    continue;

                combined = combined == null ? extent : combined.Join(extent);
            }

            return combined;
        }

        private void OnOverviewMapSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_overviewEnabled && _layerItems.Count > 0)
                RefreshOverviewMap();
        }

        private void UpdateOverviewViewport()
        {
            if (OverviewPanel.Visibility != Visibility.Visible ||
                OverviewMapControl.Map == null)
            {
                return;
            }

            MRect? mainExtent = GpuRasterMap.HasRasterLayers
                ? GpuRasterMap.ViewExtent
                : MapControl.Map?.Navigator.Viewport.ToExtent();
            if (mainExtent == null ||
                OverviewMapHost.ActualWidth <= 0 || OverviewMapHost.ActualHeight <= 0 ||
                OverviewMapControl.Map.Navigator.Viewport.Width <= 0 ||
                OverviewMapControl.Map.Navigator.Viewport.Height <= 0)
            {
                OverviewViewportThumb.Visibility = Visibility.Collapsed;
                return;
            }

            MRect screen = OverviewMapControl.Map.Navigator.Viewport.WorldToScreen(mainExtent);
            double left = Math.Clamp(screen.MinX, 0, OverviewMapHost.ActualWidth);
            double top = Math.Clamp(screen.MinY, 0, OverviewMapHost.ActualHeight);
            double right = Math.Clamp(screen.MaxX, 0, OverviewMapHost.ActualWidth);
            double bottom = Math.Clamp(screen.MaxY, 0, OverviewMapHost.ActualHeight);
            double width = right - left;
            double height = bottom - top;

            if (width <= 0 || height <= 0)
            {
                OverviewViewportThumb.Visibility = Visibility.Collapsed;
                return;
            }

            OverviewViewportThumb.Visibility = Visibility.Visible;
            Canvas.SetLeft(OverviewViewportThumb, left);
            Canvas.SetTop(OverviewViewportThumb, top);
            OverviewViewportThumb.Width = Math.Max(6, width);
            OverviewViewportThumb.Height = Math.Max(6, height);
        }

        private void OnOverviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (OverviewViewportThumb.IsMouseOver || OverviewMapControl.Map == null)
                return;

            Point position = e.GetPosition(OverviewInteractionCanvas);
            MPoint world = OverviewMapControl.Map.Navigator.Viewport.ScreenToWorld(position.X, position.Y);
            NavigateMainMapTo(world);
            e.Handled = true;
        }

        private void OnOverviewViewportDragDelta(object sender, DragDeltaEventArgs e)
        {
            if (OverviewMapControl.Map == null)
                return;

            double left = Canvas.GetLeft(OverviewViewportThumb);
            double top = Canvas.GetTop(OverviewViewportThumb);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            double centerX = left + OverviewViewportThumb.Width * 0.5 + e.HorizontalChange;
            double centerY = top + OverviewViewportThumb.Height * 0.5 + e.VerticalChange;
            MPoint world = OverviewMapControl.Map.Navigator.Viewport.ScreenToWorld(centerX, centerY);
            NavigateMainMapTo(world);
        }

        private void NavigateMainMapTo(MPoint world)
        {
            if (GpuRasterMap.HasRasterLayers)
                GpuRasterMap.CenterOn(world);
            else
                MapControl.Map?.Navigator.CenterOn(world, 0);
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

        private void OnRasterSwipeClick(object sender, RoutedEventArgs e)
        {
            bool enable = sender switch
            {
                ToggleButton toggle => toggle.IsChecked == true,
                MenuItem menuItem => menuItem.IsChecked,
                _ => false
            };

            if (!enable)
            {
                StopRasterSwipe();
                return;
            }

            var options = _layerItems
                .Where(item => item.Layer != null)
                .Select(item => new
                {
                    item.Name,
                    Provider = GetRasterProvider(item.Layer)
                })
                .Where(option => option.Provider != null)
                .Select(option => new Dialogs.RasterSwipeLayerOption(
                    option.Name,
                    option.Provider!))
                .ToList();

            if (options.Count < 2)
            {
                SetRasterSwipeToggleState(false);
                MessageBox.Show(
                    "请先加载至少两幅栅格影像。",
                    "卷帘对比",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new Dialogs.RasterSwipeDialog(options) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.LeftLayer == null || dialog.RightLayer == null)
            {
                SetRasterSwipeToggleState(false);
                return;
            }

            if (_clipSelectionActive || _clipAdjustmentActive)
                CancelRectangleClip();

            _swipeLeftProvider = dialog.LeftLayer.Provider;
            _swipeRightProvider = dialog.RightLayer.Provider;
            _swipePosition = 0.5;
            GpuRasterMap.ConfigureSwipe(_swipeLeftProvider, _swipeRightProvider, _swipePosition);

            SwipeLeftLabelText.Text = $"左：{dialog.LeftLayer.Name}";
            SwipeRightLabelText.Text = $"右：{dialog.RightLayer.Name}";
            SwipeOverlayCanvas.Visibility = Visibility.Visible;
            SetRasterSwipeToggleState(true);
            UpdateSwipeOverlay();
        }

        private void StopRasterSwipe()
        {
            GpuRasterMap.ClearSwipe();
            _swipeLeftProvider = null;
            _swipeRightProvider = null;
            SwipeOverlayCanvas.Visibility = Visibility.Collapsed;
            SetRasterSwipeToggleState(false);
        }

        private void SetRasterSwipeToggleState(bool enabled)
        {
            RasterSwipeBtn.IsChecked = enabled;
            RasterSwipeMenuItem.IsChecked = enabled;
        }

        private void OnSwipeThumbDragDelta(object sender, DragDeltaEventArgs e)
        {
            double width = SwipeOverlayCanvas.ActualWidth;
            if (width <= 0)
                return;

            _swipePosition = Math.Clamp(_swipePosition + e.HorizontalChange / width, 0, 1);
            GpuRasterMap.SetSwipePosition(_swipePosition);
            UpdateSwipeOverlay();
        }

        private void OnSwipeOverlaySizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSwipeOverlay();
        }

        private void UpdateSwipeOverlay()
        {
            double width = SwipeOverlayCanvas.ActualWidth;
            double height = SwipeOverlayCanvas.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            SwipeThumb.Height = height;
            Canvas.SetLeft(SwipeThumb, width * _swipePosition - SwipeThumb.Width * 0.5);
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
            if (_clipSelectionActive) return;
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
                _currentProjectPath = null;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"操作失败:\n{ex}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearAllLayers()
        {
            if (GpuRasterMap.IsSwipeActive)
                StopRasterSwipe();

            if (_clipSelectionActive || _clipAdjustmentActive)
                CancelRectangleClip();

            var itemsToDispose = _layerItems.ToList();

            _mapCrs = null;
            if (MapControl.Map != null)
                MapControl.Map.CRS = "EPSG:3857";
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
            ClearOverviewLayers();

            foreach (var item in itemsToDispose)
                DisposeLayerItem(item);

            _layerItems.Clear();
            UpdateOverviewVisibility();

            MapControl.Map?.Refresh();
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

        private List<Dialogs.VectorLayerInfo> GetLoadedVectorLayerInfos()
        {
            var vectorLayers = new List<Dialogs.VectorLayerInfo>();
            foreach (var item in _layerItems)
            {
                if (item.Layer == null) continue;
                string? path = GetShapeProvider(item.Layer)?.FilePath ?? GetVectorFilePath(item.Layer);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                vectorLayers.Add(new Dialogs.VectorLayerInfo(
                    item.Name,
                    path,
                    GetLayerCrs(item.Layer)));
            }

            return vectorLayers;
        }

        private async void OnVectorReprojection(object sender, RoutedEventArgs e)
        {
            var dialog = new Dialogs.VectorReprojectionDialog(GetLoadedVectorLayerInfos())
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.Request == null)
                return;

            try
            {
                ShowProgress("正在重投影矢量图层...");
                var progress = new Progress<(int percent, string label)>(value =>
                    UpdateProgress(value.label, value.percent));
                await Services.VectorReprojectionService.RunAsync(dialog.Request, progress);
                UpdateProgress("矢量重投影完成，正在加载结果...", 100);
                LoadFilesAsync([dialog.Request.OutputPath]);
            }
            catch (Exception ex)
            {
                HideProgress();
                MessageBox.Show(this, $"矢量重投影失败：\n{ex.Message}", "矢量重投影",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OnDefineProjection(object sender, RoutedEventArgs e)
        {
            var dialog = new Dialogs.RasterDefineProjectionDialog(GetLoadedRasterLayerInfos())
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.Request == null)
                return;

            try
            {
                ShowProgress("正在定义投影...");
                var progress = new Progress<int>(percent =>
                    UpdateProgress("正在定义投影...", percent));
                await Services.RasterDefineProjectionService.RunAsync(dialog.Request, progress);

                if (dialog.Request.LoadResult)
                {
                    UpdateProgress("定义投影完成，正在加载结果...", 100);
                    LoadFilesAsync([dialog.Request.OutputPath]);
                }
                else
                {
                    HideProgress();
                    string additional = dialog.Request.InPlace
                        ? "\n\n若该影像当前已加载，请移除后重新加载，以刷新坐标系信息。"
                        : string.Empty;
                    MessageBox.Show(
                        this,
                        $"坐标系定义已写入：\n{dialog.Request.OutputPath}{additional}",
                        "定义投影",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                HideProgress();
                MessageBox.Show(
                    this,
                    $"定义投影失败：\n{ex.Message}",
                    "定义投影",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void OnRasterReprojection(object sender, RoutedEventArgs e)
        {
            var dialog = new Dialogs.RasterReprojectionDialog(GetLoadedRasterLayerInfos())
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.Request == null)
                return;

            try
            {
                KillRunningPythonProcess();
                ShowProgress("投影转换中...");
                var progress = new Progress<int>(percent =>
                    UpdateProgress("投影转换中...", percent));
                await Services.RasterReprojectionService.RunAsync(
                    dialog.Request,
                    process => SetRunningPythonProcess(process),
                    progress);
                _runningPythonProcess = null;

                if (dialog.Request.LoadResult)
                {
                    UpdateProgress("投影转换完成，正在加载结果...", 100);
                    LoadFilesAsync([dialog.Request.OutputPath]);
                }
                else
                {
                    HideProgress();
                    MessageBox.Show(
                        this,
                        $"投影转换完成：\n{dialog.Request.OutputPath}",
                        "投影转换",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _runningPythonProcess = null;
                if (ConsumeCancellation()) return;
                HideProgress();
                MessageBox.Show(
                    this,
                    $"投影转换失败：\n{ex.Message}",
                    "投影转换",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void OnMultiTemporalRegistration(object sender, RoutedEventArgs e)
        {
            var dialog = new Dialogs.MultiTemporalRegistrationDialog(GetLoadedRasterLayerInfos())
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.Request == null)
                return;

            try
            {
                KillRunningPythonProcess();
                ShowProgress("正在进行多时相影像配准...");
                var progress = new Progress<int>(percent =>
                    UpdateProgress("正在进行多时相影像配准...", percent));
                await Services.MultiTemporalRegistrationService.RunAsync(
                    dialog.Request,
                    process => SetRunningPythonProcess(process),
                    progress);
                _runningPythonProcess = null;

                if (dialog.Request.LoadResult)
                {
                    UpdateProgress("配准完成，正在加载结果...", 100);
                    LoadFilesAsync([dialog.Request.OutputPath]);
                }
                else
                {
                    HideProgress();
                    MessageBox.Show(
                        this,
                        $"多时相影像配准完成：\n{dialog.Request.OutputPath}",
                        "多时相影像配准",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _runningPythonProcess = null;
                if (ConsumeCancellation()) return;
                HideProgress();
                MessageBox.Show(
                    this,
                    $"多时相影像配准失败：\n{ex.Message}",
                    "多时相影像配准",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void OnRasterMosaic(object sender, RoutedEventArgs e)
        {
            var dialog = new Dialogs.RasterMosaicDialog(GetLoadedRasterLayerInfos())
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.Request == null)
                return;

            try
            {
                KillRunningPythonProcess();
                ShowProgress("正在拼接影像...");
                var progress = new Progress<int>(percent =>
                    UpdateProgress("正在匀色并融合重叠区域...", percent));
                await Services.RasterMosaicService.RunAsync(
                    dialog.Request,
                    process => SetRunningPythonProcess(process),
                    progress);
                _runningPythonProcess = null;

                if (dialog.Request.LoadResult)
                {
                    UpdateProgress("拼接完成，正在加载结果...", 100);
                    LoadFilesAsync([dialog.Request.OutputPath]);
                }
                else
                {
                    HideProgress();
                    MessageBox.Show(this, $"影像拼接完成：\n{dialog.Request.OutputPath}",
                        "影像拼接", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _runningPythonProcess = null;
                if (ConsumeCancellation()) return;
                HideProgress();
                MessageBox.Show(this, $"影像拼接失败：\n{ex.Message}", "影像拼接",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                    p => SetRunningPythonProcess(p));
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
                if (ConsumeCancellation()) return;
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
                            p => SetRunningPythonProcess(p),
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
                        if (ConsumeCancellation()) return;
                        failures.Add($"{Path.GetFileName(request.MsPath)} + {Path.GetFileName(request.PanPath)}: {ex.Message}");
                        int failedOverallPercent = (int)Math.Round((i + 1) * 100.0 / requests.Count);
                        UpdateProgress(
                            $"批量配准失败 {i + 1}/{requests.Count}: {Path.GetFileName(request.MsPath)}",
                            failedOverallPercent);
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
                if (ConsumeCancellation()) return;
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
                UpdateProgress("影像融合中...", 0);
                var progress = new Progress<int>(percent =>
                    UpdateProgress("影像融合中...", percent));
                await Dialogs.FusionDialog.RunInferenceAsync(dlg.Request,
                    p => SetRunningPythonProcess(p),
                    progress);
                _runningPythonProcess = null;
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
                _runningPythonProcess = null;
                if (ConsumeCancellation()) return;
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
                var batchProgress = new Progress<Dialogs.BatchFusionProgress>(item =>
                {
                    int overallPercent = (int)Math.Round(
                        ((item.TaskIndex + item.Percent / 100d) / item.TaskCount) * 100d);
                    string progressLabel =
                        $"批量融合 正在处理 {item.TaskIndex + 1}/{item.TaskCount}: {Path.GetFileName(item.MsPath)}";
                    UpdateProgress(progressLabel, Math.Clamp(overallPercent, 0, 100));
                });

                var result = await Dialogs.FusionDialog.RunBatchInferenceAsync(
                    requests,
                    dlg.ContinueOnError,
                    p => SetRunningPythonProcess(p),
                    batchProgress);
                _runningPythonProcess = null;

                attempted = result.AttemptedCount;
                completed = result.CompletedTaskIndices.Count;
                foreach (int index in result.CompletedTaskIndices)
                {
                    if (requests[index].LoadAfterFusion)
                        outputsToLoad.Add(requests[index].OutputPath);
                }
                foreach (var failure in result.Failures)
                {
                    var request = requests[failure.TaskIndex];
                    failures.Add(
                        $"{Path.GetFileName(request.MsPath)} + {Path.GetFileName(request.PanPath)}: {failure.Message}");
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
                if (ConsumeCancellation()) return;
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

        private async void OnRasterClip(object sender, RoutedEventArgs e)
        {
            var rasters = GetLoadedRasterLayerInfos();
            if (rasters.Count == 0)
            {
                MessageBox.Show("请先加载至少一个栅格影像。", "影像裁剪",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var vectors = _layerItems
                .Select(item => CreateVectorClipLayerInfo(item))
                .Where(info => info != null)
                .Cast<Dialogs.VectorClipLayerInfo>()
                .ToList();

            var dialog = new Dialogs.RasterClipDialog(rasters, vectors) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Request == null)
                return;

            if (dialog.Request.Mode == Dialogs.RasterClipMode.Rectangle)
            {
                BeginRectangleClip(dialog.Request);
                return;
            }

            MRect? bounds = dialog.Request.Mode == Dialogs.RasterClipMode.CurrentView
                ? GpuRasterMap.ViewExtent
                : null;
            if (dialog.Request.Mode == Dialogs.RasterClipMode.CurrentView && bounds == null)
            {
                MessageBox.Show("当前地图视图不可用。", "影像裁剪",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await ExecuteRasterClipAsync(dialog.Request, bounds);
        }

        private void BeginRectangleClip(Dialogs.RasterClipRequest request)
        {
            _pendingRectangleClip = request;
            _clipSelectionActive = true;
            _clipAdjustmentActive = false;
            _clipDragStart = null;
            ClipSelectionCanvas.Visibility = Visibility.Visible;
            ClipSelectionCanvas.IsHitTestVisible = false;
            ClipSelectionRectangle.Visibility = Visibility.Collapsed;
            SetClipAdjustmentControlsVisibility(Visibility.Collapsed);
            Mouse.OverrideCursor = Cursors.Cross;
        }

        private void CancelRectangleClip()
        {
            _pendingRectangleClip = null;
            _clipSelectionActive = false;
            _clipAdjustmentActive = false;
            _clipDragStart = null;
            ClipSelectionCanvas.IsHitTestVisible = false;
            ClipSelectionCanvas.Visibility = Visibility.Collapsed;
            ClipSelectionRectangle.Visibility = Visibility.Collapsed;
            SetClipAdjustmentControlsVisibility(Visibility.Collapsed);
            Mouse.Capture(null);
            Mouse.OverrideCursor = null;
        }

        private void OnClipSelectionKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            if (_clipSelectionActive || _clipAdjustmentActive)
            {
                CancelRectangleClip();
                e.Handled = true;
            }
            else if (GpuRasterMap.IsSwipeActive)
            {
                StopRasterSwipe();
                e.Handled = true;
            }
        }

        private void OnClipSelectionMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_clipSelectionActive || e.ChangedButton != MouseButton.Left)
                return;

            _clipDragStart = e.GetPosition(GpuRasterMap);
            Canvas.SetLeft(ClipSelectionRectangle, _clipDragStart.Value.X);
            Canvas.SetTop(ClipSelectionRectangle, _clipDragStart.Value.Y);
            ClipSelectionRectangle.Width = 0;
            ClipSelectionRectangle.Height = 0;
            ClipSelectionRectangle.Visibility = Visibility.Visible;
            Mouse.Capture(GpuRasterMap);
            e.Handled = true;
        }

        private void OnClipSelectionMouseMove(object sender, MouseEventArgs e)
        {
            if (!_clipSelectionActive || _clipDragStart == null ||
                e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point current = e.GetPosition(GpuRasterMap);
            UpdateClipSelectionRectangle(_clipDragStart.Value, current);
            e.Handled = true;
        }

        private void OnClipSelectionMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_clipSelectionActive || _clipDragStart == null ||
                e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            Point start = _clipDragStart.Value;
            Point end = e.GetPosition(GpuRasterMap);
            end = ClampClipPoint(end);
            Mouse.Capture(null);
            _clipDragStart = null;
            e.Handled = true;

            if (_pendingRectangleClip == null ||
                Math.Abs(end.X - start.X) < 12 || Math.Abs(end.Y - start.Y) < 12)
            {
                ClipSelectionRectangle.Visibility = Visibility.Collapsed;
                return;
            }

            _clipSelectionRect = new Rect(
                Math.Min(start.X, end.X),
                Math.Min(start.Y, end.Y),
                Math.Abs(end.X - start.X),
                Math.Abs(end.Y - start.Y));
            _clipSelectionActive = false;
            _clipAdjustmentActive = true;
            Mouse.OverrideCursor = null;
            ClipSelectionCanvas.IsHitTestVisible = true;
            SetClipAdjustmentControlsVisibility(Visibility.Visible);
            RenderClipSelectionControls();
        }

        private void UpdateClipSelectionRectangle(Point start, Point current)
        {
            current = ClampClipPoint(current);
            double left = Math.Min(start.X, current.X);
            double top = Math.Min(start.Y, current.Y);
            _clipSelectionRect = new Rect(
                left,
                top,
                Math.Abs(current.X - start.X),
                Math.Abs(current.Y - start.Y));
            RenderClipSelectionRectangle();
        }

        private Point ClampClipPoint(Point point)
            => new(
                Math.Clamp(point.X, 0, Math.Max(0, ClipSelectionCanvas.ActualWidth)),
                Math.Clamp(point.Y, 0, Math.Max(0, ClipSelectionCanvas.ActualHeight)));

        private void OnClipThumbDragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_clipAdjustmentActive || sender is not Thumb thumb)
                return;

            const double minSize = 12d;
            string handle = thumb.Tag?.ToString() ?? string.Empty;
            Rect rect = _clipSelectionRect;
            double canvasWidth = Math.Max(minSize, ClipSelectionCanvas.ActualWidth);
            double canvasHeight = Math.Max(minSize, ClipSelectionCanvas.ActualHeight);

            if (handle == "Move")
            {
                rect.X = Math.Clamp(rect.X + e.HorizontalChange, 0, Math.Max(0, canvasWidth - rect.Width));
                rect.Y = Math.Clamp(rect.Y + e.VerticalChange, 0, Math.Max(0, canvasHeight - rect.Height));
            }
            else
            {
                double left = rect.Left;
                double right = rect.Right;
                double top = rect.Top;
                double bottom = rect.Bottom;

                if (handle.Contains('W'))
                    left = Math.Clamp(left + e.HorizontalChange, 0, right - minSize);
                if (handle.Contains('E'))
                    right = Math.Clamp(right + e.HorizontalChange, left + minSize, canvasWidth);
                if (handle.Contains('N'))
                    top = Math.Clamp(top + e.VerticalChange, 0, bottom - minSize);
                if (handle.Contains('S'))
                    bottom = Math.Clamp(bottom + e.VerticalChange, top + minSize, canvasHeight);

                rect = new Rect(left, top, right - left, bottom - top);
            }

            _clipSelectionRect = rect;
            RenderClipSelectionControls();
        }

        private async void OnConfirmRectangleClip(object sender, RoutedEventArgs e)
        {
            if (!_clipAdjustmentActive || _pendingRectangleClip == null)
                return;

            var request = _pendingRectangleClip;
            Rect rect = _clipSelectionRect;
            MPoint a = GpuRasterMap.ScreenToWorld(rect.Left, rect.Top);
            MPoint b = GpuRasterMap.ScreenToWorld(rect.Right, rect.Bottom);
            var bounds = new MRect(
                Math.Min(a.X, b.X),
                Math.Min(a.Y, b.Y),
                Math.Max(a.X, b.X),
                Math.Max(a.Y, b.Y));

            CancelRectangleClip();
            await ExecuteRasterClipAsync(request, bounds);
        }

        private void OnCancelRectangleClip(object sender, RoutedEventArgs e)
        {
            CancelRectangleClip();
        }

        private void RenderClipSelectionControls()
        {
            RenderClipSelectionRectangle();

            Rect rect = _clipSelectionRect;
            Canvas.SetLeft(ClipMoveThumb, rect.Left);
            Canvas.SetTop(ClipMoveThumb, rect.Top);
            ClipMoveThumb.Width = rect.Width;
            ClipMoveThumb.Height = rect.Height;

            PositionClipHandle(ClipHandleNW, rect.Left, rect.Top);
            PositionClipHandle(ClipHandleN, rect.Left + rect.Width / 2, rect.Top);
            PositionClipHandle(ClipHandleNE, rect.Right, rect.Top);
            PositionClipHandle(ClipHandleE, rect.Right, rect.Top + rect.Height / 2);
            PositionClipHandle(ClipHandleSE, rect.Right, rect.Bottom);
            PositionClipHandle(ClipHandleS, rect.Left + rect.Width / 2, rect.Bottom);
            PositionClipHandle(ClipHandleSW, rect.Left, rect.Bottom);
            PositionClipHandle(ClipHandleW, rect.Left, rect.Top + rect.Height / 2);

            double actionsWidth = 122;
            double actionLeft = Math.Min(rect.Right + 8, Math.Max(0, ClipSelectionCanvas.ActualWidth - actionsWidth));
            double actionTop = rect.Bottom + 8;
            if (actionTop + 26 > ClipSelectionCanvas.ActualHeight)
                actionTop = Math.Max(0, rect.Top - 34);
            Canvas.SetLeft(ClipSelectionActions, actionLeft);
            Canvas.SetTop(ClipSelectionActions, actionTop);
        }

        private void RenderClipSelectionRectangle()
        {
            Canvas.SetLeft(ClipSelectionRectangle, _clipSelectionRect.Left);
            Canvas.SetTop(ClipSelectionRectangle, _clipSelectionRect.Top);
            ClipSelectionRectangle.Width = _clipSelectionRect.Width;
            ClipSelectionRectangle.Height = _clipSelectionRect.Height;
        }

        private static void PositionClipHandle(Thumb handle, double x, double y)
        {
            Canvas.SetLeft(handle, x - handle.Width / 2);
            Canvas.SetTop(handle, y - handle.Height / 2);
        }

        private void SetClipAdjustmentControlsVisibility(Visibility visibility)
        {
            ClipMoveThumb.Visibility = visibility;
            ClipHandleNW.Visibility = visibility;
            ClipHandleN.Visibility = visibility;
            ClipHandleNE.Visibility = visibility;
            ClipHandleE.Visibility = visibility;
            ClipHandleSE.Visibility = visibility;
            ClipHandleS.Visibility = visibility;
            ClipHandleSW.Visibility = visibility;
            ClipHandleW.Visibility = visibility;
            ClipSelectionActions.Visibility = visibility;
        }

        private async Task ExecuteRasterClipAsync(Dialogs.RasterClipRequest request, MRect? bounds)
        {
            if (string.Equals(
                Path.GetFullPath(request.RasterPath),
                Path.GetFullPath(request.OutputPath),
                StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("输出文件不能覆盖正在裁剪的输入影像。", "影像裁剪",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? cutlinePath = null;
            try
            {
                ShowProgress("正在裁剪影像...");
                if (request.Mode == Dialogs.RasterClipMode.VectorBoundary)
                {
                    cutlinePath = CreateCutlineJson(request.VectorPath!);
                    await RasterClipService.RunCutlineAsync(
                        request.RasterPath,
                        request.OutputPath,
                        cutlinePath);
                }
                else
                {
                    await RasterClipService.RunExtentAsync(
                        request.RasterPath,
                        request.OutputPath,
                        bounds ?? throw new InvalidOperationException("缺少裁剪范围。"));
                }

                if (request.LoadResult)
                {
                    UpdateProgress("裁剪完成，正在加载结果...", 100);
                    LoadFilesAsync(new[] { request.OutputPath });
                }
                else
                {
                    HideProgress();
                    MessageBox.Show($"裁剪完成：\n{request.OutputPath}", "影像裁剪",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                HideProgress();
                MessageBox.Show($"影像裁剪失败：\n{ex.Message}", "影像裁剪",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(cutlinePath))
                {
                    try { File.Delete(cutlinePath); } catch { }
                }
            }
        }

        private string CreateCutlineJson(string vectorPath)
        {
            var provider = _layerItems
                .Select(item => GetShapeProvider(item.Layer))
                .FirstOrDefault(item => item != null &&
                    string.Equals(item.FilePath, vectorPath, StringComparison.OrdinalIgnoreCase))
                ;

            if (provider == null)
                return CreateGeoJsonCutline(vectorPath);

            var geometries = new List<object>();
            for (uint i = 0; i < provider.FeatureCount; i++)
            {
                if (provider.GetFeature(i) is Mapsui.Nts.GeometryFeature { Geometry: { } geometry })
                    AppendPolygonGeometries(geometry, geometries);
            }

            if (geometries.Count == 0)
                throw new InvalidOperationException("选定图层中没有面或多面要素。");

            string tempPath = Path.Combine(Path.GetTempPath(), $"GeoVision_cutline_{Guid.NewGuid():N}.json");
            var payload = new { crs = provider.CRS, geometries };
            File.WriteAllText(tempPath, JsonSerializer.Serialize(payload));
            return tempPath;
        }

        private static Dialogs.VectorClipLayerInfo? CreateVectorClipLayerInfo(LayerItem item)
        {
            if (GetShapeProvider(item.Layer) is { } shapeProvider)
            {
                return new Dialogs.VectorClipLayerInfo(
                    item.Name,
                    shapeProvider.FilePath,
                    shapeProvider.CRS);
            }

            if (item.Layer?.Tag is string filePath &&
                Path.GetExtension(filePath).ToLowerInvariant() is ".geojson" or ".json")
            {
                string? crs = (item.Layer as Mapsui.Layers.Layer)?.DataSource?.CRS;
                return new Dialogs.VectorClipLayerInfo(item.Name, filePath, crs);
            }

            return null;
        }

        private static string CreateGeoJsonCutline(string vectorPath)
        {
            if (!File.Exists(vectorPath) ||
                Path.GetExtension(vectorPath).ToLowerInvariant() is not (".geojson" or ".json"))
            {
                throw new InvalidOperationException("找不到选定的矢量边界图层。");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(vectorPath));
            JsonElement root = document.RootElement;
            var geometries = new List<JsonElement>();

            if (root.TryGetProperty("type", out var typeElement))
            {
                string? type = typeElement.GetString();
                if (string.Equals(type, "FeatureCollection", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("features", out var features))
                {
                    foreach (var feature in features.EnumerateArray())
                    {
                        if (feature.TryGetProperty("geometry", out var geometry) &&
                            IsPolygonGeoJson(geometry))
                        {
                            geometries.Add(geometry.Clone());
                        }
                    }
                }
                else if (string.Equals(type, "Feature", StringComparison.OrdinalIgnoreCase) &&
                         root.TryGetProperty("geometry", out var geometry) &&
                         IsPolygonGeoJson(geometry))
                {
                    geometries.Add(geometry.Clone());
                }
                else if (IsPolygonGeoJson(root))
                {
                    geometries.Add(root.Clone());
                }
            }

            if (geometries.Count == 0)
                throw new InvalidOperationException("选定 GeoJSON 中没有 Polygon 或 MultiPolygon 要素。");

            string? crs = "EPSG:4326";
            if (root.TryGetProperty("crs", out var crsElement) &&
                crsElement.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty("name", out var name))
            {
                crs = name.GetString();
            }

            string tempPath = Path.Combine(Path.GetTempPath(), $"GeoVision_cutline_{Guid.NewGuid():N}.json");
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new { crs, geometries }));
            return tempPath;
        }

        private static bool IsPolygonGeoJson(JsonElement geometry)
        {
            if (!geometry.TryGetProperty("type", out var typeElement))
                return false;
            string? type = typeElement.GetString();
            return string.Equals(type, "Polygon", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "MultiPolygon", StringComparison.OrdinalIgnoreCase);
        }

        private static void AppendPolygonGeometries(
            NetTopologySuite.Geometries.Geometry geometry,
            List<object> output)
        {
            if (geometry is NetTopologySuite.Geometries.Polygon polygon)
            {
                var rings = new List<double[][]>
                {
                    ToCoordinateRing(polygon.ExteriorRing.Coordinates)
                };
                for (int i = 0; i < polygon.NumInteriorRings; i++)
                    rings.Add(ToCoordinateRing(polygon.GetInteriorRingN(i).Coordinates));

                output.Add(new Dictionary<string, object>
                {
                    ["type"] = "Polygon",
                    ["coordinates"] = rings
                });
                return;
            }

            for (int i = 0; i < geometry.NumGeometries; i++)
            {
                var child = geometry.GetGeometryN(i);
                if (!ReferenceEquals(child, geometry))
                    AppendPolygonGeometries(child, output);
            }
        }

        private static double[][] ToCoordinateRing(NetTopologySuite.Geometries.Coordinate[] coordinates)
            => coordinates.Select(coordinate => new[] { coordinate.X, coordinate.Y }).ToArray();

        private void OnMenuOpen(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => OnOpenFileClick(this, new RoutedEventArgs())));
        }

        private void OnMenuSave(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => TrySaveCurrentProject()));
        }

        private void OnMenuSaveAs(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => TrySaveProjectWithDialog()));
        }

        private void LoadRecentProjects()
        {
            try
            {
                if (!File.Exists(RecentProjectsFilePath))
                    return;

                var paths = JsonSerializer.Deserialize<List<string>>(
                    File.ReadAllText(RecentProjectsFilePath));
                if (paths == null)
                    return;

                _recentProjectPaths.AddRange(paths
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxRecentProjects));
            }
            catch
            {
                _recentProjectPaths.Clear();
            }
        }

        private void AddRecentProject(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            _recentProjectPaths.RemoveAll(path =>
                string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase));
            _recentProjectPaths.Insert(0, fullPath);
            if (_recentProjectPaths.Count > MaxRecentProjects)
                _recentProjectPaths.RemoveRange(MaxRecentProjects, _recentProjectPaths.Count - MaxRecentProjects);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecentProjectsFilePath)!);
                File.WriteAllText(
                    RecentProjectsFilePath,
                    JsonSerializer.Serialize(_recentProjectPaths, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // A recent-projects write failure must not block project saving/opening.
            }

            RefreshRecentProjectsMenu();
        }

        private void RefreshRecentProjectsMenu()
        {
            RecentProjectsMenu.Items.Clear();
            if (_recentProjectPaths.Count == 0)
            {
                RecentProjectsMenu.Items.Add(new MenuItem
                {
                    Header = "暂无最近项目",
                    IsEnabled = false
                });
                return;
            }

            foreach (string path in _recentProjectPaths)
            {
                var item = new MenuItem
                {
                    Header = Path.GetFileNameWithoutExtension(path),
                    ToolTip = path,
                    Tag = path
                };
                item.Click += OnOpenFileClick;
                RecentProjectsMenu.Items.Add(item);
            }
        }

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            if (!PromptToSaveCurrentProject("退出 GeoVision"))
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
                MessageBoxResult.Yes => TrySaveCurrentProject(),
                MessageBoxResult.No => true,
                _ => false
            };
        }

        private bool TrySaveCurrentProject()
        {
            if (!string.IsNullOrWhiteSpace(_currentProjectPath))
                return TrySaveProject(_currentProjectPath);

            return TrySaveProjectWithDialog();
        }

        private bool TrySaveProject(string filePath)
        {
            _lastSaveSucceeded = false;
            SaveProject(filePath);
            return _lastSaveSucceeded;
        }

        private bool TrySaveProjectWithDialog()
        {
            var dlg = new SaveFileDialog
            {
                Title = "保存项目",
                Filter = "GeoVision 项目|*.geovision",
                DefaultExt = ".geovision"
            };

            if (dlg.ShowDialog() != true)
                return false;

            try
            {
                return TrySaveProject(dlg.FileName);
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
            string? projectPath = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                var dlg = new OpenFileDialog
                {
                    Title = "打开项目文件",
                    Filter = "GeoVision 项目|*.geovision|旧版项目|*.mygis|所有文件|*.*",
                    DefaultExt = ".geovision"
                };
                if (dlg.ShowDialog() != true)
                    return;
                projectPath = dlg.FileName;
            }

            if (!File.Exists(projectPath))
            {
                _recentProjectPaths.RemoveAll(path =>
                    string.Equals(path, projectPath, StringComparison.OrdinalIgnoreCase));
                RefreshRecentProjectsMenu();
                MessageBox.Show("项目文件不存在，已从最近打开列表中移除。", "GeoVision",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            projectPath = Path.GetFullPath(projectPath);

            // Prompt to save current project first
            if (!PromptToSaveCurrentProject("打开项目"))
                return;

            Models.ProjectFile project;
            try
            {
                var json = File.ReadAllText(projectPath);
                project = JsonSerializer.Deserialize<Models.ProjectFile>(json)
                    ?? new Models.ProjectFile();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"项目文件读取失败:\n{ex.Message}", "GeoVision",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string projectDirectory = Path.GetDirectoryName(projectPath)!;
            foreach (var entry in project.Layers)
                entry.FilePath = ResolveProjectLayerPath(projectDirectory, entry.FilePath);

            // Validate file paths exist
            var missing = project.Layers
                .Where(e => !File.Exists(e.FilePath))
                .Select(e => e.FilePath).ToList();
            if (missing.Count > 0)
            {
                var msg = $"以下文件不存在:\n{string.Join("\n", missing)}\n\n是否继续加载其余图层?";
                if (MessageBox.Show(msg, "GeoVision", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes)
                    return;
            }

            // Clear existing layers
            ClearAllLayers();
            _currentProjectPath = null;

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
                            if (entry.Opacity.HasValue)
                                rp.ChangeOpacity(entry.Opacity.Value);
                        }
                    }

                    string crs = GetLayerCrs(layer);
                    layer = await ResolveCrsMismatchAsync(layer, entry.FilePath, entry.Name);
                    if (layer == null) continue;
                    ApplySavedRasterSettings(layer, entry);
                    AddLayerToRenderer(layer);
                    AddLayerItem(layer);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载失败: {entry.Name}\n{ex.Message}",
                        "GeoVision", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            }
            HideProgress();
            _currentProjectPath = Path.GetFullPath(projectPath);
            AddRecentProject(_currentProjectPath);
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
            await Dispatcher.Yield(DispatcherPriority.Render);
            int fileIndex = 0;
            foreach (var path in filePaths)
            {
                var loadProgress = CreateLoadProgress(path, fileIndex, filePaths.Length);
                try
                {
                    var layer = await Task.Run(async () => await DataLoader.LoadAsync(path, loadProgress));
                    if (layer != null)
                    {
                        layer = await ResolveCrsMismatchAsync(layer, path, Path.GetFileName(path));
                        if (layer == null) continue;
                        AddLayerToRenderer(layer);
                        AddLayerItem(layer);
                    }
                    else
                    {
                        MessageBox.Show($"不支持的文件格式: {Path.GetExtension(path)}",
                                        "GeoVision", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载文件失败: {Path.GetFileName(path)}\n{ex.Message}",
                                    "GeoVision", MessageBoxButton.OK, MessageBoxImage.Error);
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
            }

            HideProgress();
        }

        private string? _mapCrs;

        private async Task<ILayer?> ResolveCrsMismatchAsync(
            ILayer layer,
            string sourcePath,
            string fileName)
        {
            string layerCrs = GetLayerCrs(layer);
            if (string.IsNullOrWhiteSpace(layerCrs) || layerCrs == "未知") return layer;

            string? referenceCrs = _mapCrs ?? GetFirstKnownLayerCrs();
            if (string.IsNullOrWhiteSpace(referenceCrs))
            {
                _mapCrs = layerCrs;
                if (MapControl.Map != null)
                    MapControl.Map.CRS = layerCrs;
                return layer;
            }

            _mapCrs = referenceCrs;
            string keyMap = CoordinateConverter.GetEpsgComparisonKey(referenceCrs);
            string keyLayer = CoordinateConverter.GetEpsgComparisonKey(layerCrs);
            if (!string.IsNullOrEmpty(keyMap) &&
                string.Equals(keyMap, keyLayer, StringComparison.OrdinalIgnoreCase))
                return layer;

            string mapName = CoordinateConverter.GetCrsDisplayName(referenceCrs);
            string layerName = CoordinateConverter.GetCrsDisplayName(layerCrs);
            bool canReproject = GetRasterProvider(layer) != null &&
                                File.Exists(sourcePath) &&
                                SpatialReferenceHelper.TryParse(referenceCrs, out _, out _);
            var dialog = new Dialogs.CrsMismatchDialog(fileName, mapName, layerName, canReproject)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.Action == Dialogs.CrsMismatchAction.Cancel)
            {
                DisposeUntrackedLayer(layer);
                return null;
            }

            if (dialog.Action != Dialogs.CrsMismatchAction.Reproject || !canReproject)
                return layer;

            string tempDirectory = Path.Combine(Path.GetTempPath(), "GeoVision", "reprojected");
            Directory.CreateDirectory(tempDirectory);
            string tempPath = Path.Combine(
                tempDirectory,
                $"{Path.GetFileNameWithoutExtension(sourcePath)}_to_{SanitizeFileName(referenceCrs)}_{Guid.NewGuid():N}.tif");

            try
            {
                ShowProgress($"正在将 {fileName} 重投影到当前地图坐标系...");
                var progress = new Progress<int>(percent =>
                    UpdateProgress($"正在重投影 {fileName}...", percent));
                await Services.RasterReprojectionService.RunAsync(
                    new Services.RasterReprojectionRequest(
                        sourcePath,
                        tempPath,
                        referenceCrs,
                        "bilinear",
                        null,
                        true),
                    process => SetRunningPythonProcess(process),
                    progress);
                _runningPythonProcess = null;

                DisposeUntrackedLayer(layer);
                var loadProgress = CreateLoadProgress(tempPath, 0, 1);
                var reprojected = await Task.Run(async () =>
                    await DataLoader.LoadAsync(tempPath, loadProgress));
                if (reprojected == null)
                    throw new InvalidDataException("重投影完成，但无法重新加载结果影像。");

                reprojected.Name = layer.Name;
                reprojected.Enabled = layer.Enabled;
                return reprojected;
            }
            catch
            {
                _runningPythonProcess = null;
                TryDeleteTempRaster(tempPath);
                throw;
            }
        }

        private static void DisposeUntrackedLayer(ILayer layer)
        {
            if (GetRasterProvider(layer) is { } rasterProvider)
                rasterProvider.Dispose();
        }

        private static string SanitizeFileName(string value)
        {
            string safe = string.IsNullOrWhiteSpace(value) ? "map" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                safe = safe.Replace(invalid, '_');
            return safe.Length > 48 ? safe[..48] : safe;
        }

        private static void TryDeleteTempRaster(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".msk")) File.Delete(path + ".msk");
                if (File.Exists(path + ".ovr")) File.Delete(path + ".ovr");
            }
            catch { }
        }

        private static void ApplySavedRasterSettings(ILayer layer, Models.LayerEntry entry)
        {
            var rp = GetRasterProvider(layer);
            if (rp == null) return;
            if (entry.StretchType != null && Enum.TryParse(entry.StretchType, out Services.StretchType st))
                rp.ChangeStretch(st);
            if (entry.ColorRamp != null && Enum.TryParse(entry.ColorRamp, out Services.ColorRampType cr))
                rp.ChangeColorRamp(cr);
            if (entry.BandIndexes is { Length: 3 }) rp.ChangeBands(entry.BandIndexes);
            if (entry.Opacity.HasValue) rp.ChangeOpacity(entry.Opacity.Value);
        }

        private string? GetFirstKnownLayerCrs()
        {
            foreach (var item in _layerItems)
            {
                string candidate = GetLayerCrs(item.Layer);
                if (!string.IsNullOrWhiteSpace(candidate) && candidate != "未知")
                    return candidate;
            }

            return null;
        }

        private static string GetLayerCrs(ILayer? layer)
        {
            string? rasterCrs = GetRasterProvider(layer)?.RasterCrs;
            if (!string.IsNullOrWhiteSpace(rasterCrs))
                return rasterCrs;

            string? vectorCrs = GetShapeProvider(layer)?.CRS;
            return string.IsNullOrWhiteSpace(vectorCrs) ? "未知" : vectorCrs;
        }

        private void RecalculateMapCrs()
        {
            string? firstCrs = GetFirstKnownLayerCrs();
            _mapCrs = firstCrs;
            if (MapControl.Map != null)
                MapControl.Map.CRS = firstCrs ?? "EPSG:3857";
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
            {
                if (ReferenceEquals(rp, _swipeLeftProvider) ||
                    ReferenceEquals(rp, _swipeRightProvider))
                {
                    StopRasterSwipe();
                }

                GpuRasterMap.RemoveRasterLayer(rp);
            }

            if (item.Layer != null)
                MapControl.Map?.Layers.Remove(item.Layer);
        }

        private void DisposeLayerItem(LayerItem item)
        {
            if (GetRasterProvider(item.Layer) is { } provider &&
                _bandDisplayDialogs.Remove(provider, out var displayDialog))
            {
                displayDialog.Close();
            }

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
            RefreshOverviewMap();
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

            RefreshOverviewMap();
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
            RefreshOverviewMap();
            return crs;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "保存项目",
                Filter = "GeoVision 项目|*.geovision",
                DefaultExt = ".geovision"
            };
            if (dlg.ShowDialog() == true)
                SaveProject(dlg.FileName);
        }

        private void SaveProject(string filePath)
        {
            var project = new Models.ProjectFile();
            string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
            project.Version = 2;

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
                    entry.FilePath = ToProjectLayerPath(projectDirectory, rp.FilePath);
                    entry.Type = "raster";
                    entry.StretchType = rp.CurrentStretchType.ToString();
                    entry.ColorRamp = rp.Stretch.ColorRamp.ToString();
                    entry.BandIndexes = (int[])rp.SourceBandIndexes.Clone();
                    entry.RendererType = rp.RendererType.ToString();
                    entry.Opacity = rp.Opacity;
                }
                else if (sfp != null)
                {
                    entry.FilePath = ToProjectLayerPath(projectDirectory, sfp.FilePath);
                    entry.Type = "vector";
                }
                else if (layer.Tag is string vectorPath &&
                         Path.GetExtension(vectorPath).ToLowerInvariant() is ".geojson" or ".json")
                {
                    entry.FilePath = ToProjectLayerPath(projectDirectory, vectorPath);
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
                _currentProjectPath = Path.GetFullPath(filePath);
                _lastSaveSucceeded = true;
                AddRecentProject(_currentProjectPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败:\n{ex.Message}", "GeoVision",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string ResolveProjectLayerPath(string projectDirectory, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return string.Empty;
            return Path.GetFullPath(Path.IsPathRooted(filePath)
                ? filePath
                : Path.Combine(projectDirectory, filePath));
        }

        private static string ToProjectLayerPath(string projectDirectory, string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            string relative = Path.GetRelativePath(projectDirectory, fullPath);
            return relative == "." ? Path.GetFileName(fullPath) : relative;
        }

        private void OnZoomInClick(object sender, RoutedEventArgs e)
        {
            if (GpuRasterMap.HasRasterLayers)
                GpuRasterMap.ZoomIn();
            else
                MapControl.Map?.Navigator.ZoomIn(200);
        }

        private void OnZoomOutClick(object sender, RoutedEventArgs e)
        {
            if (GpuRasterMap.HasRasterLayers)
                GpuRasterMap.ZoomOut();
            else
                MapControl.Map?.Navigator.ZoomOut(200);
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
                RefreshOverviewMap();
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
                RefreshOverviewMap();
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
                hasAttrTable = l.DataSource is Services.DataLoader.ShapeFileProvider ||
                               IsGeoJsonLayer(_rightClickedItem.Layer);
            if (!hasAttrTable)
                hasAttrTable = _rightClickedItem.Layer?.Tag is Services.DataLoader.ShapeFileProvider ||
                               IsGeoJsonLayer(_rightClickedItem.Layer);

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
                }
                return;
            }

            if (item.Layer.Extent is MRect extent)
            {
                MapControl.Map?.Navigator.ZoomToBox(extent, MBoxFit.Fit, 200);
            }
        }

        private void OnLayerDisplaySettings(object sender, RoutedEventArgs e)
        {
            var item = GetLayerItemFromSender(sender);
            if (item?.Layer?.Tag is not Providers.GdalRasterProvider provider) return;

            if (_bandDisplayDialogs.TryGetValue(provider, out var existingDialog))
            {
                existingDialog.Activate();
                return;
            }

            bool isMulti = provider.RendererType == Services.RasterRendererType.Rgb;
            var dlg = new Dialogs.BandDisplayDialog(
                provider.TotalBands,
                provider.SourceBandIndexes,
                provider.CurrentStretchType,
                provider.Stretch.ColorRamp,
                provider.Opacity,
                isMulti);

            dlg.Owner = this;
            _bandDisplayDialogs[provider] = dlg;
            dlg.Closed += (_, _) => _bandDisplayDialogs.Remove(provider);
            dlg.SettingsApplied += async (_, args) =>
            {
                try
                {
                    bool changed = false;
                    if (args.StretchChanged)
                    {
                        await provider.ChangeStretchAsync(args.SelectedStretch);
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
                    if (args.OpacityChanged)
                    {
                        provider.ChangeOpacity(args.SelectedOpacity);
                        changed = true;
                    }

                    if (changed)
                    {
                        RefreshLegend(item, provider);
                        RefreshLayerRenderer(item);
                    }
                }
                catch (ObjectDisposedException)
                {
                    dlg.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"应用显示设置失败：\n{ex.Message}", "显示设置",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            dlg.Show();
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
                if (IsGeoJsonLayer(item.Layer) && TryReadGeoJsonAttributeTable(
                        GetVectorFilePath(item.Layer)!, out var geoJsonTable, out int geoJsonCount))
                {
                    var geoJsonDialog = new Dialogs.AttributeTableDialog(
                        item.Name, geoJsonTable, geoJsonCount) { Owner = this };
                    geoJsonDialog.Show();
                    return;
                }

                MessageBox.Show("当前图层没有可读取的属性表数据。", "提示",
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

        private static bool IsGeoJsonLayer(ILayer? layer)
        {
            string? path = GetVectorFilePath(layer);
            return path != null &&
                   Path.GetExtension(path).ToLowerInvariant() is ".geojson" or ".json";
        }

        private static string? GetVectorFilePath(ILayer? layer)
        {
            if (layer?.Tag is string path && !string.IsNullOrWhiteSpace(path))
                return path;
            return null;
        }

        private static bool TryReadGeoJsonAttributeTable(
            string path,
            out System.Data.DataTable table,
            out int featureCount)
        {
            table = new System.Data.DataTable();
            table.Columns.Add("FID", typeof(int));
            featureCount = 0;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var features = new List<JsonElement>();
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("type", out var typeElement) &&
                    string.Equals(typeElement.GetString(), "FeatureCollection", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("features", out var featureArray) &&
                    featureArray.ValueKind == JsonValueKind.Array)
                {
                    features.AddRange(featureArray.EnumerateArray());
                }
                else if (root.ValueKind == JsonValueKind.Object &&
                         root.TryGetProperty("type", out var singleTypeElement) &&
                         string.Equals(singleTypeElement.GetString(), "Feature", StringComparison.OrdinalIgnoreCase))
                {
                    features.Add(root);
                }
                else
                {
                    return false;
                }

                featureCount = features.Count;
                var properties = new List<Dictionary<string, string>>(Math.Min(features.Count, 10_000));
                var fieldNames = new List<string>();
                var fieldSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var feature in features.Take(10_000))
                {
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (feature.ValueKind == JsonValueKind.Object &&
                        feature.TryGetProperty("properties", out var propertyObject) &&
                        propertyObject.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in propertyObject.EnumerateObject())
                        {
                            string field = string.IsNullOrWhiteSpace(property.Name) ? "字段" : property.Name;
                            if (!fieldSet.Contains(field))
                            {
                                fieldSet.Add(field);
                                fieldNames.Add(field);
                            }
                            values[field] = property.Value.ValueKind switch
                            {
                                JsonValueKind.Null => "",
                                JsonValueKind.String => property.Value.GetString() ?? "",
                                _ => property.Value.GetRawText()
                            };
                        }
                    }
                    properties.Add(values);
                }

                foreach (string field in fieldNames)
                    table.Columns.Add(field, typeof(string));
                for (int index = 0; index < properties.Count; index++)
                {
                    var row = table.NewRow();
                    row["FID"] = index;
                    foreach (string field in fieldNames)
                        row[field] = properties[index].TryGetValue(field, out string? value) ? value : "";
                    table.Rows.Add(row);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GeoJSON attribute table failed: {ex}");
                table = new System.Data.DataTable();
                featureCount = 0;
                return false;
            }
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
            double? pixelSizeX = null, pixelSizeY = null;
            string? pixelSizeUnit = null;

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
                pixelSizeX = rp.PixelSizeX;
                pixelSizeY = rp.PixelSizeY;
                pixelSizeUnit = rp.PixelSizeUnit;
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
                    var corners = new[]
                    {
                        CoordinateConverter.ToLonLat(extent.MinX, extent.MinY, rawCrs),
                        CoordinateConverter.ToLonLat(extent.MinX, extent.MaxY, rawCrs),
                        CoordinateConverter.ToLonLat(extent.MaxX, extent.MinY, rawCrs),
                        CoordinateConverter.ToLonLat(extent.MaxX, extent.MaxY, rawCrs)
                    };
                    minX = corners.Min(corner => corner.Lon);
                    maxX = corners.Max(corner => corner.Lon);
                    minY = corners.Min(corner => corner.Lat);
                    maxY = corners.Max(corner => corner.Lat);
                    isLonLat = true;
                }
            }

            var dlg = new Dialogs.LayerPropertiesDialog(
                item.Name, layerType, filePath, item.Crs,
                minX, minY, maxX, maxY, isLonLat,
                rasterW, rasterH, bandCount, renderer, overviewCount, stretchType,
                featureCount, encoding, pixelSizeX, pixelSizeY, pixelSizeUnit)
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

            RecalculateMapCrs();
            MapControl.Map?.Refresh();
            RefreshOverviewMap();
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
            RecalculateMapCrs();
            MapControl.Map?.Refresh();
            RefreshOverviewMap();
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
