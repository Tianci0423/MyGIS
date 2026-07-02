using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mapsui;
using MyGIS.Providers;
using OpenTK.Graphics.OpenGL;
using OpenTK.Wpf;
using GLPixelFormat = OpenTK.Graphics.OpenGL.PixelFormat;

namespace MyGIS.Controls
{
    public sealed class GpuRasterMapControl : Grid, IDisposable
    {
        private const int TileGridScreenPixels = 512;
        private const int FinalTileRenderPixels = 512;
        private const int InteractiveTileRenderPixels = 256;
        private const int TilePrefetchMargin = 1;
        private const int MaxGpuTextures = 480;
        private const int MaxPixelGridLines = 3000;
        private const int MaxDetailPrefetchTilesPerFrame = 18;
        private const int MaxPredictiveDetailPrefetchTiles = 10;
        private const int MaxQueuedTileLoads = 96;
        private const bool ShowPixelGridOverlay = false;
        private const double TileSeamOverlapScreenPixels = 0.65;
        private const double ZoomInFactor = 0.8;
        private const double ZoomOutFactor = 1.25;
        private const double ZoomAnimationSeconds = 0.14;
        private const double MinimumZoomViewportPixels = 16;
        private const double PixelInspectionMinScreenPixels = 1.0;
        private const double DetailPrefetchMinScreenPixels = 0.25;
        private const double DetailPrefetchViewExpansion = 0.35;

        private readonly GLWpfControl _gl = new();
        private readonly List<GpuRasterLayer> _layers = new();
        private readonly Dictionary<TileKey, GpuTile> _gpuTiles = new();
        private readonly ConcurrentDictionary<TileKey, long> _loadingTiles = new();
        private readonly ConcurrentQueue<TileUpload> _pendingUploads = new();
        private readonly ConcurrentQueue<int> _texturesToDelete = new();
        private readonly SemaphoreSlim _loadSemaphore = new(Math.Max(1, Environment.ProcessorCount / 2));
        private readonly DispatcherTimer _resizeSettleTimer = new();
        private readonly ScaleTransform _resizeScaleTransform = new(1, 1);
        private readonly TranslateTransform _resizeTranslateTransform = new(0, 0);
        private readonly TransformGroup _resizeTransform = new();

        private bool _started;
        private bool _ready;
        private bool _mouseDown;
        private bool _dragging;
        private bool _resizing;
        private bool _hasStableGlSize;
        private double _stableGlWidth = 1;
        private double _stableGlHeight = 1;
        private Point _dragStartMouse;
        private Point _lastMouse;
        private double _centerX;
        private double _centerY;
        private double _resolution = 1;
        private bool _zoomAnimating;
        private double _zoomAnimationElapsed;
        private double _zoomStartCenterX;
        private double _zoomStartCenterY;
        private double _zoomStartResolution;
        private double _zoomTargetCenterX;
        private double _zoomTargetCenterY;
        private double _zoomTargetResolution;
        private MRect? _pendingZoomExtent;
        private long _frame;
        private long _viewportRevision;
        private int _disposed;

        public event EventHandler? ViewportChanged;

        public GpuRasterMapControl()
        {
            ClipToBounds = true;
            Focusable = true;
            Visibility = Visibility.Collapsed;

            _gl.Ready += OnReady;
            _gl.Render += OnRender;
            _gl.MouseLeftButtonDown += OnMouseLeftButtonDown;
            _gl.MouseLeftButtonUp += OnMouseLeftButtonUp;
            _gl.MouseMove += OnMouseMove;
            _gl.MouseWheel += OnMouseWheel;
            _gl.MouseLeave += OnMouseLeave;
            _gl.HorizontalAlignment = HorizontalAlignment.Left;
            _gl.VerticalAlignment = VerticalAlignment.Top;
            _resizeTransform.Children.Add(_resizeScaleTransform);
            _resizeTransform.Children.Add(_resizeTranslateTransform);
            _gl.RenderTransformOrigin = new Point(0, 0);
            _gl.RenderTransform = _resizeTransform;

            Children.Add(_gl);

            _resizeSettleTimer.Interval = TimeSpan.FromMilliseconds(180);
            _resizeSettleTimer.Tick += OnResizeSettled;

            Loaded += (_, _) =>
            {
                CommitGlControlSize();
                ApplyPendingZoomExtent();
                StartGl();
            };
            Unloaded += (_, _) => Dispose();
            SizeChanged += OnControlSizeChanged;
        }

        public bool HasRasterLayers => _layers.Count > 0;
        public double Resolution => _resolution;

        public MRect? ViewExtent
        {
            get
            {
                double width = ViewportWidth;
                double height = ViewportHeight;
                if (!HasRasterLayers || width <= 0 || height <= 0)
                    return null;

                double halfW = width * _resolution * 0.5;
                double halfH = height * _resolution * 0.5;
                return new MRect(_centerX - halfW, _centerY - halfH, _centerX + halfW, _centerY + halfH);
            }
        }

        public MPoint ScreenToWorld(double screenX, double screenY)
            => ScreenToWorld(screenX, screenY, _centerX, _centerY, _resolution);

        public bool ContainsVisibleRasterData(MPoint worldPoint)
        {
            foreach (var layer in _layers.Where(l => l.IsVisible))
            {
                var extent = layer.Provider.GetExtent();
                if (extent != null && extent.Contains(worldPoint))
                    return true;
            }

            return false;
        }

        public void AddRasterLayer(GdalRasterProvider provider, string name)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (_layers.Any(l => ReferenceEquals(l.Provider, provider)))
                return;

            _layers.Add(new GpuRasterLayer(provider, name));
            Visibility = Visibility.Visible;

            if (_layers.Count == 1 && provider.GetExtent() is { } extent)
                ZoomToExtent(extent);

            RequestFrame();
        }

        public void SetRasterLayerOrder(IEnumerable<GdalRasterProvider> providersTopToBottom)
        {
            var ordered = new List<GpuRasterLayer>();

            foreach (var provider in providersTopToBottom.Reverse())
            {
                var layer = _layers.FirstOrDefault(l => ReferenceEquals(l.Provider, provider));
                if (layer != null && !ordered.Contains(layer))
                    ordered.Add(layer);
            }

            foreach (var layer in _layers)
            {
                if (!ordered.Contains(layer))
                    ordered.Add(layer);
            }

            _layers.Clear();
            _layers.AddRange(ordered);
            Visibility = _layers.Any(l => l.IsVisible) ? Visibility.Visible : Visibility.Collapsed;
            RequestFrame();
        }

        public void RemoveRasterLayer(GdalRasterProvider provider)
        {
            _layers.RemoveAll(l => ReferenceEquals(l.Provider, provider));
            DropTilesFor(provider);

            if (_layers.Count == 0)
            {
                _pendingZoomExtent = null;
                Visibility = Visibility.Collapsed;
            }

            RequestFrame();
        }

        public void ClearRasterLayers()
        {
            _layers.Clear();
            _pendingZoomExtent = null;
            DropAllTiles();
            Visibility = Visibility.Collapsed;
            RequestFrame();
        }

        public void SetLayerVisibility(GdalRasterProvider provider, bool isVisible)
        {
            var layer = _layers.FirstOrDefault(l => ReferenceEquals(l.Provider, provider));
            if (layer == null) return;

            layer.IsVisible = isVisible;
            Visibility = _layers.Any(l => l.IsVisible) ? Visibility.Visible : Visibility.Collapsed;
            RequestFrame();
        }

        public void RefreshRasterLayer(GdalRasterProvider provider)
        {
            DropTilesFor(provider);
            RequestFrame();
        }

        public MRect? GetFullExtent()
        {
            MRect? combined = null;
            foreach (var layer in _layers.Where(l => l.IsVisible))
            {
                var extent = layer.Provider.GetExtent();
                if (extent == null || !IsUsableExtent(extent)) continue;
                combined = combined == null ? extent : combined.Join(extent);
            }

            return combined;
        }

        public void ZoomIn() => ZoomBy(ZoomInFactor, new Point(ViewportWidth * 0.5, ViewportHeight * 0.5));

        public void ZoomOut() => ZoomBy(ZoomOutFactor, new Point(ViewportWidth * 0.5, ViewportHeight * 0.5));

        public void ZoomToFullExtent()
        {
            if (GetFullExtent() is { } extent)
                ZoomToExtent(extent);
        }

        public void ZoomToExtent(MRect extent)
        {
            if (!IsUsableExtent(extent))
                return;

            _zoomAnimating = false;

            if (!TryGetZoomViewportSize(out double width, out double height))
            {
                _pendingZoomExtent = extent;
                RequestFrame();
                return;
            }

            _pendingZoomExtent = null;
            ApplyZoomToExtent(extent, width, height);
        }

        private void ApplyZoomToExtent(MRect extent, double width, double height)
        {
            double resX = (extent.MaxX - extent.MinX) / Math.Max(1, width * 0.92);
            double resY = (extent.MaxY - extent.MinY) / Math.Max(1, height * 0.92);
            _resolution = Math.Max(Math.Max(resX, resY), 1e-12);
            _centerX = (extent.MinX + extent.MaxX) * 0.5;
            _centerY = (extent.MinY + extent.MaxY) * 0.5;
            OnViewportChanged();
        }

        private bool ApplyPendingZoomExtent()
        {
            if (_pendingZoomExtent is not { } extent ||
                !TryGetZoomViewportSize(out double width, out double height))
                return false;

            _pendingZoomExtent = null;
            _zoomAnimating = false;
            ApplyZoomToExtent(extent, width, height);
            return true;
        }

        private bool TryGetZoomViewportSize(out double width, out double height)
        {
            width = ActualWidth;
            height = ActualHeight;
            if (IsUsableViewportSize(width, height))
                return true;

            width = _stableGlWidth;
            height = _stableGlHeight;
            return _hasStableGlSize && IsUsableViewportSize(width, height);
        }

        private static bool IsUsableViewportSize(double width, double height)
            => width >= MinimumZoomViewportPixels &&
               height >= MinimumZoomViewportPixels &&
               double.IsFinite(width) &&
               double.IsFinite(height);

        private static bool IsUsableExtent(MRect extent)
        {
            double width = extent.MaxX - extent.MinX;
            double height = extent.MaxY - extent.MinY;
            return width > 0 &&
                   height > 0 &&
                   double.IsFinite(extent.MinX) &&
                   double.IsFinite(extent.MinY) &&
                   double.IsFinite(extent.MaxX) &&
                   double.IsFinite(extent.MaxY) &&
                   double.IsFinite(width) &&
                   double.IsFinite(height);
        }

        private void StartGl()
        {
            if (_started) return;
            _started = true;

            var settings = new GLWpfControlSettings
            {
                MajorVersion = 2,
                MinorVersion = 1,
                RenderContinuously = true,
                UseDeviceDpi = true
            };
            _gl.Start(settings);
        }

        private void OnReady()
        {
            _ready = true;
            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Texture2D);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.ClearColor(1f, 1f, 1f, 1f);
        }

        private void OnRender(TimeSpan delta)
        {
            if (!_ready) return;

            if (Volatile.Read(ref _disposed) != 0)
            {
                DrainPendingUploads();
                DeleteQueuedTextures();
                return;
            }

            UpdateZoomAnimation(delta);

            int fbW = Math.Max(1, _gl.FrameBufferWidth);
            int fbH = Math.Max(1, _gl.FrameBufferHeight);

            GL.Viewport(0, 0, fbW, fbH);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0, fbW, fbH, 0, -1, 1);
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            UploadPendingTiles();
            DeleteQueuedTextures();

            var view = ViewExtent;
            if (view != null)
                DrawAndScheduleVisibleTiles(view, fbW, fbH);

            EvictOldTextures();
            _frame++;
        }

        private void DrawAndScheduleVisibleTiles(MRect view, int fbW, int fbH)
        {
            double width = ViewportWidth;
            double height = ViewportHeight;
            double scaleX = width > 0 ? fbW / width : 1;
            double scaleY = height > 0 ? fbH / height : 1;
            long viewportRevision = Volatile.Read(ref _viewportRevision);

            foreach (var layer in _layers.Where(l => l.IsVisible))
            {
                bool pixelInspection = IsPixelInspectionActive(layer.Provider);
                var renderSettings = GetRenderSettings(pixelInspection);
                var requests = layer.Provider.CreateGpuTileRequests(
                    view, _resolution, TileGridScreenPixels,
                    renderSettings.RenderPixels, renderSettings.TileMargin);

                var visibleTiles = new List<VisibleTileRequest>(requests.Count);
                var loadedTiles = new List<VisibleLoadedTile>(requests.Count);

                foreach (var request in requests)
                {
                    var key = CreateTileKey(layer.Provider, request);
                    visibleTiles.Add(new VisibleTileRequest(key, request));

                    if (TryGetBestTexture(key, out var tile))
                        loadedTiles.Add(new VisibleLoadedTile(key, tile));
                }

                if (requests.Count > 0)
                    DrawCachedBackdrop(
                        layer.Provider,
                        requests[0].Generation,
                        requests[0].Level,
                        view,
                        scaleX,
                        scaleY,
                        pixelInspection);

                foreach (var loaded in loadedTiles)
                    DrawTile(loaded.Tile, scaleX, scaleY, pixelInspection && loaded.Key.Level == 0);

                foreach (var visible in visibleTiles)
                {
                    if (!HasCompatibleTexture(visible.Key))
                        ScheduleTileLoad(layer.Provider, visible.Request, visible.Key, viewportRevision);
                }

                if (IsDetailPrefetchActive(layer.Provider))
                    ScheduleDetailPrefetch(layer.Provider, view, viewportRevision, MaxDetailPrefetchTilesPerFrame);

                if (pixelInspection && ShowPixelGridOverlay)
                    DrawPixelGrid(layer.Provider, view, scaleX, scaleY);
            }
        }

        private (int RenderPixels, int TileMargin) GetRenderSettings(bool pixelInspection = false)
        {
            bool isInteractive = _zoomAnimating || _dragging || _resizing;
            if (pixelInspection)
                return (FinalTileRenderPixels, isInteractive ? 0 : TilePrefetchMargin);

            if (_dragging || _resizing)
                return (FinalTileRenderPixels, 0);

            return _zoomAnimating
                ? (InteractiveTileRenderPixels, 0)
                : (FinalTileRenderPixels, TilePrefetchMargin);
        }

        private void DrawTile(GpuTile tile, double scaleX, double scaleY, bool nearest)
        {
            var e = tile.Extent;
            double x1 = WorldToFrameX(e.MinX, scaleX);
            double y1 = WorldToFrameY(e.MaxY, scaleY);
            double x2 = WorldToFrameX(e.MaxX, scaleX);
            double y2 = WorldToFrameY(e.MinY, scaleY);
            if (!nearest)
                ExpandScreenQuad(ref x1, ref y1, ref x2, ref y2);

            GL.BindTexture(TextureTarget.Texture2D, tile.TextureId);
            ApplyTextureFilter(tile, nearest);
            GL.Color4(1f, 1f, 1f, 1f);
            GL.Begin(PrimitiveType.Quads);
            GL.TexCoord2(tile.TexMinX, tile.TexMinY); GL.Vertex2(x1, y1);
            GL.TexCoord2(tile.TexMaxX, tile.TexMinY); GL.Vertex2(x2, y1);
            GL.TexCoord2(tile.TexMaxX, tile.TexMaxY); GL.Vertex2(x2, y2);
            GL.TexCoord2(tile.TexMinX, tile.TexMaxY); GL.Vertex2(x1, y2);
            GL.End();
        }

        private void DrawTileClipped(GpuTile tile, MRect clipExtent, double scaleX, double scaleY, bool nearest)
        {
            var e = tile.Extent;
            var drawExtent = IntersectExtents(e, clipExtent);
            if (drawExtent == null || drawExtent.Width <= 0 || drawExtent.Height <= 0 ||
                e.Width <= 0 || e.Height <= 0)
                return;

            double x1 = WorldToFrameX(drawExtent.MinX, scaleX);
            double y1 = WorldToFrameY(drawExtent.MaxY, scaleY);
            double x2 = WorldToFrameX(drawExtent.MaxX, scaleX);
            double y2 = WorldToFrameY(drawExtent.MinY, scaleY);
            if (!nearest)
                ExpandScreenQuad(ref x1, ref y1, ref x2, ref y2);

            double tx1 = Lerp(tile.TexMinX, tile.TexMaxX, (drawExtent.MinX - e.MinX) / e.Width);
            double tx2 = Lerp(tile.TexMinX, tile.TexMaxX, (drawExtent.MaxX - e.MinX) / e.Width);
            double ty1 = Lerp(tile.TexMinY, tile.TexMaxY, (e.MaxY - drawExtent.MaxY) / e.Height);
            double ty2 = Lerp(tile.TexMinY, tile.TexMaxY, (e.MaxY - drawExtent.MinY) / e.Height);

            GL.BindTexture(TextureTarget.Texture2D, tile.TextureId);
            ApplyTextureFilter(tile, nearest);
            GL.Color4(1f, 1f, 1f, 1f);
            GL.Begin(PrimitiveType.Quads);
            GL.TexCoord2(tx1, ty1); GL.Vertex2(x1, y1);
            GL.TexCoord2(tx2, ty1); GL.Vertex2(x2, y1);
            GL.TexCoord2(tx2, ty2); GL.Vertex2(x2, y2);
            GL.TexCoord2(tx1, ty2); GL.Vertex2(x1, y2);
            GL.End();
        }

        private void DrawCachedBackdrop(
            GdalRasterProvider provider,
            int generation,
            int requestedLevel,
            MRect view,
            double scaleX,
            double scaleY,
            bool pixelInspection)
        {
            int? bestLevel = null;
            double bestScore = double.NegativeInfinity;
            double viewArea = Math.Max(view.Width * view.Height, 1e-12);

            foreach (var group in _gpuTiles
                         .Where(p => ReferenceEquals(p.Key.Provider, provider) &&
                                     p.Key.Generation == generation &&
                                     p.Value.Extent.Intersects(view))
                         .GroupBy(p => p.Key.Level))
            {
                double coveredArea = 0;
                foreach (var pair in group)
                {
                    var overlap = IntersectExtents(pair.Value.Extent, view);
                    if (overlap != null)
                        coveredArea += overlap.Width * overlap.Height;
                }

                double coverage = Math.Min(1d, coveredArea / viewArea);
                int distance = Math.Abs(group.Key - requestedLevel);
                double coarserBias = group.Key >= requestedLevel ? 0.001 : 0;
                double score = coverage * 10_000 - distance + coarserBias;
                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestLevel = group.Key;
            }

            if (bestLevel == null)
                return;

            foreach (var pair in _gpuTiles.Where(p =>
                         ReferenceEquals(p.Key.Provider, provider) &&
                         p.Key.Generation == generation &&
                         p.Key.Level == bestLevel.Value &&
                         p.Value.Extent.Intersects(view)))
            {
                pair.Value.LastUsedFrame = _frame;
                DrawTileClipped(pair.Value, view, scaleX, scaleY, pixelInspection && pair.Key.Level == 0);
            }
        }

        private void ApplyTextureFilter(GpuTile tile, bool nearest)
        {
            if (tile.UsesNearestFiltering == nearest)
                return;

            var filter = nearest ? (int)TextureMinFilter.Nearest : (int)TextureMinFilter.Linear;
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
            tile.UsesNearestFiltering = nearest;
        }

        private static void ExpandScreenQuad(ref double x1, ref double y1, ref double x2, ref double y2)
        {
            x1 -= TileSeamOverlapScreenPixels;
            y1 -= TileSeamOverlapScreenPixels;
            x2 += TileSeamOverlapScreenPixels;
            y2 += TileSeamOverlapScreenPixels;
        }

        private bool IsPixelInspectionActive(GdalRasterProvider provider)
        {
            double nativeResolution = provider.NativePixelResolution;
            if (nativeResolution <= 0 || _resolution <= 0)
                return false;

            return nativeResolution / _resolution >= PixelInspectionMinScreenPixels;
        }

        private bool IsDetailPrefetchActive(GdalRasterProvider provider)
        {
            double nativeResolution = provider.NativePixelResolution;
            if (nativeResolution <= 0 || _resolution <= 0)
                return false;

            double screenPixelsPerSourcePixel = nativeResolution / _resolution;
            return screenPixelsPerSourcePixel >= DetailPrefetchMinScreenPixels;
        }

        private void ScheduleDetailPrefetch(
            GdalRasterProvider provider,
            MRect view,
            long viewportRevision,
            int maxTiles)
        {
            if (_dragging || _resizing || _loadingTiles.Count >= MaxQueuedTileLoads)
                return;

            double nativeResolution = provider.NativePixelResolution;
            if (nativeResolution <= 0)
                return;

            var prefetchView = ExpandExtent(view, DetailPrefetchViewExpansion);
            var requests = provider.CreateGpuTileRequests(
                prefetchView,
                nativeResolution,
                TileGridScreenPixels,
                FinalTileRenderPixels,
                tileMargin: 0);

            if (requests.Count == 0)
                return;

            double centerX = (view.MinX + view.MaxX) * 0.5;
            double centerY = (view.MinY + view.MaxY) * 0.5;

            foreach (var request in requests
                         .Where(r => r.Level == 0)
                         .OrderBy(r =>
                         {
                             double tileCenterX = (r.Extent.MinX + r.Extent.MaxX) * 0.5;
                             double tileCenterY = (r.Extent.MinY + r.Extent.MaxY) * 0.5;
                             double dx = tileCenterX - centerX;
                             double dy = tileCenterY - centerY;
                             return dx * dx + dy * dy;
                         })
                         .Take(Math.Max(0, maxTiles)))
            {
                var key = CreateTileKey(provider, request);
                if (!HasCompatibleTexture(key))
                    ScheduleTileLoad(provider, request, key, viewportRevision, isPrefetch: true);
            }
        }

        private void SchedulePredictiveDetailPrefetch(
            double targetCenterX,
            double targetCenterY,
            double targetResolution)
        {
            if (_dragging || _resizing || _layers.Count == 0 || targetResolution <= 0)
                return;

            double width = ViewportWidth;
            double height = ViewportHeight;
            if (width <= 0 || height <= 0)
                return;

            var targetView = new MRect(
                targetCenterX - width * targetResolution * 0.5,
                targetCenterY - height * targetResolution * 0.5,
                targetCenterX + width * targetResolution * 0.5,
                targetCenterY + height * targetResolution * 0.5);

            long viewportRevision = Volatile.Read(ref _viewportRevision);
            foreach (var layer in _layers.Where(l => l.IsVisible))
            {
                double nativeResolution = layer.Provider.NativePixelResolution;
                if (nativeResolution <= 0)
                    continue;

                double targetScreenPixelsPerSourcePixel = nativeResolution / targetResolution;
                if (targetScreenPixelsPerSourcePixel < DetailPrefetchMinScreenPixels)
                    continue;

                ScheduleDetailPrefetch(
                    layer.Provider,
                    targetView,
                    viewportRevision,
                    MaxPredictiveDetailPrefetchTiles);
            }
        }

        private void DrawPixelGrid(
            GdalRasterProvider provider,
            MRect view,
            double scaleX,
            double scaleY)
        {
            if (!provider.TryGetPixelWindowForExtent(view, out int colStart, out int rowStart, out int colEnd, out int rowEnd))
                return;

            int verticalLines = colEnd - colStart + 1;
            int horizontalLines = rowEnd - rowStart + 1;
            if (verticalLines <= 0 || horizontalLines <= 0 || verticalLines + horizontalLines > MaxPixelGridLines)
                return;

            GL.Disable(EnableCap.Texture2D);
            GL.Color4(0f, 0f, 0f, 0.34f);
            GL.LineWidth(1f);
            GL.Begin(PrimitiveType.Lines);

            for (int col = colStart; col <= colEnd; col++)
            {
                var p1 = provider.PixelToWorldPoint(col, rowStart);
                var p2 = provider.PixelToWorldPoint(col, rowEnd);
                AddGridVertex(p1, scaleX, scaleY);
                AddGridVertex(p2, scaleX, scaleY);
            }

            for (int row = rowStart; row <= rowEnd; row++)
            {
                var p1 = provider.PixelToWorldPoint(colStart, row);
                var p2 = provider.PixelToWorldPoint(colEnd, row);
                AddGridVertex(p1, scaleX, scaleY);
                AddGridVertex(p2, scaleX, scaleY);
            }

            GL.End();
            GL.Color4(1f, 1f, 1f, 1f);
            GL.Enable(EnableCap.Texture2D);
        }

        private void AddGridVertex(MPoint worldPoint, double scaleX, double scaleY)
            => GL.Vertex2(WorldToFrameX(worldPoint.X, scaleX), WorldToFrameY(worldPoint.Y, scaleY));

        private double WorldToFrameX(double worldX, double scaleX)
            => ((worldX - _centerX) / _resolution + ViewportWidth * 0.5) * scaleX;

        private double WorldToFrameY(double worldY, double scaleY)
            => (ViewportHeight * 0.5 - (worldY - _centerY) / _resolution) * scaleY;

        private void ScheduleTileLoad(
            GdalRasterProvider provider,
            GdalRasterProvider.RasterTileRequest request,
            TileKey key,
            long viewportRevision,
            bool isPrefetch = false)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (isPrefetch && _loadingTiles.Count >= MaxQueuedTileLoads)
                return;

            if (_loadingTiles.TryGetValue(key, out long queuedRevision))
            {
                if (queuedRevision < viewportRevision)
                    _loadingTiles[key] = viewportRevision;
                return;
            }

            if (HasCompatibleTexture(key))
                return;

            if (!_loadingTiles.TryAdd(key, viewportRevision))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _loadSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (Volatile.Read(ref _disposed) != 0)
                            return;

                        long latestKnownRevision = _loadingTiles.TryGetValue(key, out long latestBeforeRender)
                            ? Math.Max(latestBeforeRender, viewportRevision)
                            : viewportRevision;
                        if (isPrefetch && latestKnownRevision + 1 < Volatile.Read(ref _viewportRevision))
                            return;

                        var rgba = provider.RenderTileToRgba(request);
                        if (Volatile.Read(ref _disposed) != 0)
                            return;

                        long uploadRevision = _loadingTiles.TryGetValue(key, out long latestRevision)
                            ? Math.Max(latestRevision, viewportRevision)
                            : viewportRevision;

                        _pendingUploads.Enqueue(new TileUpload(
                            key,
                            request.Extent,
                            request.BufW,
                            request.BufH,
                            request.TexMinX,
                            request.TexMinY,
                            request.TexMaxX,
                            request.TexMaxY,
                            uploadRevision,
                            rgba));
                    }
                    finally
                    {
                        _loadSemaphore.Release();
                    }
                }
                catch
                {
                    // Tile load failures are skipped; later frames can retry if still visible.
                }
                finally
                {
                    _loadingTiles.TryRemove(key, out _);
                }
            });
        }

        private bool TryGetCoveringTexture(
            GdalRasterProvider provider,
            int generation,
            int requestedLevel,
            MRect requestedExtent,
            out TileKey key,
            out GpuTile tile)
        {
            key = default;
            tile = null!;

            TileKey bestKey = default;
            GpuTile? bestTile = null;
            double bestScore = double.PositiveInfinity;

            foreach (var pair in _gpuTiles)
            {
                var candidateKey = pair.Key;
                var candidateTile = pair.Value;
                if (!ReferenceEquals(candidateKey.Provider, provider) ||
                    candidateKey.Generation != generation ||
                    candidateKey.Level < requestedLevel ||
                    !ContainsExtent(candidateTile.Extent, requestedExtent))
                    continue;

                double area = candidateTile.Extent.Width * candidateTile.Extent.Height;
                double score = (candidateKey.Level - requestedLevel) * 1_000_000_000d + area;
                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestKey = candidateKey;
                bestTile = candidateTile;
            }

            if (bestTile == null)
                return false;

            bestTile.LastUsedFrame = _frame;
            key = bestKey;
            tile = bestTile;
            return true;
        }

        private bool HasCompatibleTexture(TileKey key)
        {
            if (_gpuTiles.ContainsKey(key))
                return true;

            foreach (var existing in _gpuTiles.Keys)
            {
                if (!ReferenceEquals(existing.Provider, key.Provider) ||
                    existing.Generation != key.Generation ||
                    existing.Level != key.Level ||
                    existing.ColStart != key.ColStart ||
                    existing.RowStart != key.RowStart ||
                    existing.ColEnd != key.ColEnd ||
                    existing.RowEnd != key.RowEnd ||
                    existing.BufW < key.BufW ||
                    existing.BufH < key.BufH)
                    continue;

                return true;
            }

            return false;
        }

        private bool TryGetBestTexture(TileKey key, out GpuTile tile)
        {
            if (_gpuTiles.TryGetValue(key, out tile!))
            {
                tile.LastUsedFrame = _frame;
                return true;
            }

            GpuTile? bestTile = null;
            int bestScore = int.MinValue;

            foreach (var pair in _gpuTiles)
            {
                var existing = pair.Key;
                if (!ReferenceEquals(existing.Provider, key.Provider) ||
                    existing.Generation != key.Generation ||
                    existing.Level != key.Level ||
                    existing.ColStart != key.ColStart ||
                    existing.RowStart != key.RowStart ||
                    existing.ColEnd != key.ColEnd ||
                    existing.RowEnd != key.RowEnd)
                    continue;

                int score = TextureQualityScore(existing, key);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestTile = pair.Value;
            }

            if (bestTile == null)
            {
                tile = null!;
                return false;
            }

            bestTile.LastUsedFrame = _frame;
            tile = bestTile;
            return true;
        }

        private static int TextureQualityScore(TileKey candidate, TileKey requested)
        {
            int area = candidate.BufW * candidate.BufH;
            bool enoughQuality = candidate.BufW >= requested.BufW && candidate.BufH >= requested.BufH;
            return (enoughQuality ? 1_000_000_000 : 0) + area;
        }

        private static MRect? IntersectExtents(MRect a, MRect b)
        {
            double minX = Math.Max(a.MinX, b.MinX);
            double minY = Math.Max(a.MinY, b.MinY);
            double maxX = Math.Min(a.MaxX, b.MaxX);
            double maxY = Math.Min(a.MaxY, b.MaxY);
            return maxX > minX && maxY > minY ? new MRect(minX, minY, maxX, maxY) : null;
        }

        private static MRect ExpandExtent(MRect extent, double factor)
        {
            if (factor <= 0)
                return extent;

            double expandX = extent.Width * factor;
            double expandY = extent.Height * factor;
            return new MRect(
                extent.MinX - expandX,
                extent.MinY - expandY,
                extent.MaxX + expandX,
                extent.MaxY + expandY);
        }

        private static bool ContainsExtent(MRect outer, MRect inner)
        {
            double tolerance = Math.Max(outer.Width, outer.Height) * 1e-9;
            return outer.MinX <= inner.MinX + tolerance &&
                   outer.MinY <= inner.MinY + tolerance &&
                   outer.MaxX >= inner.MaxX - tolerance &&
                   outer.MaxY >= inner.MaxY - tolerance;
        }

        private static TileKey CreateTileKey(
            GdalRasterProvider provider,
            GdalRasterProvider.RasterTileRequest request)
            => new(provider, request.Generation, request.Level,
                request.ColStart, request.RowStart, request.ColEnd, request.RowEnd,
                request.BufW, request.BufH);

        private static bool IsCompatibleTileKey(TileKey candidate, TileKey requested)
        {
            return ReferenceEquals(candidate.Provider, requested.Provider) &&
                   candidate.Generation == requested.Generation &&
                   candidate.Level == requested.Level &&
                   candidate.ColStart == requested.ColStart &&
                   candidate.RowStart == requested.RowStart &&
                   candidate.ColEnd == requested.ColEnd &&
                   candidate.RowEnd == requested.RowEnd &&
                   candidate.BufW >= requested.BufW &&
                   candidate.BufH >= requested.BufH;
        }

        private bool IsUploadStillRequested(TileUpload upload, MRect currentView)
        {
            var renderSettings = GetRenderSettings(IsPixelInspectionActive(upload.Key.Provider));
            var requests = upload.Key.Provider.CreateGpuTileRequests(
                currentView, _resolution, TileGridScreenPixels,
                renderSettings.RenderPixels, renderSettings.TileMargin);

            foreach (var request in requests)
            {
                if (IsCompatibleTileKey(upload.Key, CreateTileKey(upload.Key.Provider, request)))
                    return true;
            }

            return false;
        }

        private void UploadPendingTiles()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                DrainPendingUploads();
                return;
            }

            while (_pendingUploads.TryDequeue(out var upload))
            {
                if (!_layers.Any(l => l.IsVisible && ReferenceEquals(l.Provider, upload.Key.Provider)))
                    continue;

                if (!upload.Key.Provider.IsRenderGenerationCurrent(upload.Key.Generation))
                    continue;

                if (_gpuTiles.ContainsKey(upload.Key))
                    continue;

                if (upload.ViewportRevision < Volatile.Read(ref _viewportRevision) &&
                    (ViewExtent is not { } currentView || !IsUploadStillRequested(upload, currentView)))
                    continue;

                int texture = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                var handle = GCHandle.Alloc(upload.Rgba, GCHandleType.Pinned);
                try
                {
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                        upload.Width, upload.Height, 0, GLPixelFormat.Rgba,
                        PixelType.UnsignedByte, handle.AddrOfPinnedObject());
                }
                finally
                {
                    handle.Free();
                }

                _gpuTiles[upload.Key] = new GpuTile(
                    texture,
                    upload.Extent,
                    upload.TexMinX,
                    upload.TexMinY,
                    upload.TexMaxX,
                    upload.TexMaxY,
                    _frame);
            }
        }

        private void DeleteQueuedTextures()
        {
            while (_texturesToDelete.TryDequeue(out int texture))
            {
                if (texture > 0)
                    GL.DeleteTexture(texture);
            }
        }

        private void EvictOldTextures()
        {
            if (_gpuTiles.Count <= MaxGpuTextures)
                return;

            foreach (var pair in _gpuTiles.OrderBy(p => p.Value.LastUsedFrame)
                         .Take(_gpuTiles.Count - MaxGpuTextures)
                         .ToList())
            {
                _texturesToDelete.Enqueue(pair.Value.TextureId);
                _gpuTiles.Remove(pair.Key);
            }
        }

        private void DropTilesFor(GdalRasterProvider provider)
        {
            foreach (var pair in _gpuTiles.Where(p => ReferenceEquals(p.Key.Provider, provider)).ToList())
            {
                _texturesToDelete.Enqueue(pair.Value.TextureId);
                _gpuTiles.Remove(pair.Key);
            }

            foreach (var key in _loadingTiles.Keys.Where(k => ReferenceEquals(k.Provider, provider)).ToList())
                _loadingTiles.TryRemove(key, out _);
        }

        private void DropAllTiles()
        {
            DrainPendingUploads();
            foreach (var tile in _gpuTiles.Values)
                _texturesToDelete.Enqueue(tile.TextureId);
            _gpuTiles.Clear();
            _loadingTiles.Clear();
        }

        private void DrainPendingUploads()
        {
            while (_pendingUploads.TryDequeue(out _))
            {
            }
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!HasRasterLayers) return;
            Focus();
            _zoomAnimating = false;
            _mouseDown = true;
            _dragging = false;
            _dragStartMouse = e.GetPosition(this);
            _lastMouse = _dragStartMouse;
            _gl.CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_mouseDown && !_dragging) return;
            bool wasDragging = _dragging;
            _mouseDown = false;
            _dragging = false;
            _gl.ReleaseMouseCapture();
            if (wasDragging)
                OnViewportChanged();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown || e.LeftButton != MouseButtonState.Pressed)
                return;

            var pos = e.GetPosition(this);
            var dx = pos.X - _lastMouse.X;
            var dy = pos.Y - _lastMouse.Y;

            if (!_dragging)
            {
                double totalDx = pos.X - _dragStartMouse.X;
                double totalDy = pos.Y - _dragStartMouse.Y;
                if (totalDx * totalDx + totalDy * totalDy < 4)
                    return;

                _dragging = true;
            }

            _lastMouse = pos;

            _centerX -= dx * _resolution;
            _centerY += dy * _resolution;
            OnViewportChanged();
            e.Handled = true;
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_mouseDown && !_dragging) return;
            bool wasDragging = _dragging;
            _mouseDown = false;
            _dragging = false;
            _gl.ReleaseMouseCapture();
            if (wasDragging)
                OnViewportChanged();
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!HasRasterLayers) return;
            ZoomBy(e.Delta > 0 ? ZoomInFactor : ZoomOutFactor, e.GetPosition(this));
            e.Handled = true;
        }

        private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ActualWidth <= 0 || ActualHeight <= 0)
                return;

            if (!_hasStableGlSize)
            {
                CommitGlControlSize();
                if (ApplyPendingZoomExtent())
                    return;

                OnViewportChanged(invalidateTileRequests: false);
                return;
            }

            _resizing = true;
            ApplyResizeTransform();
            _resizeSettleTimer.Stop();
            _resizeSettleTimer.Start();

            // Resizing changes the visible rectangle but should not invalidate in-flight tile renders.
            // Keeping their viewport revision stable prevents texture uploads from being discarded mid-resize.
            OnViewportChanged(invalidateTileRequests: false);
        }

        private void OnResizeSettled(object? sender, EventArgs e)
        {
            _resizeSettleTimer.Stop();
            if (!_resizing)
                return;

            _resizing = false;
            CommitGlControlSize();
            if (ApplyPendingZoomExtent())
                return;

            OnViewportChanged();
        }

        private double ViewportWidth
            => _resizing && _hasStableGlSize ? _stableGlWidth : ActualWidth;

        private double ViewportHeight
            => _resizing && _hasStableGlSize ? _stableGlHeight : ActualHeight;

        private void CommitGlControlSize()
        {
            double width = Math.Max(1, ActualWidth);
            double height = Math.Max(1, ActualHeight);

            _stableGlWidth = width;
            _stableGlHeight = height;
            _hasStableGlSize = true;

            _gl.Width = width;
            _gl.Height = height;
            _resizeScaleTransform.ScaleX = 1;
            _resizeScaleTransform.ScaleY = 1;
            _resizeTranslateTransform.X = 0;
            _resizeTranslateTransform.Y = 0;
        }

        private void ApplyResizeTransform()
        {
            if (!_hasStableGlSize || _stableGlWidth <= 0 || _stableGlHeight <= 0)
            {
                CommitGlControlSize();
                return;
            }

            double scaleX = Math.Max(0.01, ActualWidth / _stableGlWidth);
            double scaleY = Math.Max(0.01, ActualHeight / _stableGlHeight);
            double scale = Math.Max(scaleX, scaleY);
            double scaledWidth = _stableGlWidth * scale;
            double scaledHeight = _stableGlHeight * scale;

            _resizeScaleTransform.ScaleX = scale;
            _resizeScaleTransform.ScaleY = scale;
            _resizeTranslateTransform.X = (ActualWidth - scaledWidth) * 0.5;
            _resizeTranslateTransform.Y = (ActualHeight - scaledHeight) * 0.5;
        }

        private void ZoomBy(double factor, Point anchor)
        {
            double width = ViewportWidth;
            double height = ViewportHeight;
            if (width <= 0 || height <= 0)
                return;

            double baseResolution = _zoomAnimating ? _zoomTargetResolution : _resolution;
            var world = ScreenToWorld(anchor.X, anchor.Y, _centerX, _centerY, _resolution);

            double targetResolution = Math.Max(1e-12, baseResolution * factor);
            double targetCenterX = world.X - (anchor.X - width * 0.5) * targetResolution;
            double targetCenterY = world.Y + (anchor.Y - height * 0.5) * targetResolution;
            StartZoomAnimation(targetCenterX, targetCenterY, targetResolution);
        }

        private void StartZoomAnimation(double targetCenterX, double targetCenterY, double targetResolution)
        {
            _zoomAnimating = true;
            _zoomAnimationElapsed = 0;
            _zoomStartCenterX = _centerX;
            _zoomStartCenterY = _centerY;
            _zoomStartResolution = _resolution;
            _zoomTargetCenterX = targetCenterX;
            _zoomTargetCenterY = targetCenterY;
            _zoomTargetResolution = targetResolution;
            OnViewportChanged();
            SchedulePredictiveDetailPrefetch(targetCenterX, targetCenterY, targetResolution);
        }

        private void UpdateZoomAnimation(TimeSpan delta)
        {
            if (!_zoomAnimating)
                return;

            _zoomAnimationElapsed += Math.Clamp(delta.TotalSeconds, 0, 0.05);
            double t = Math.Clamp(_zoomAnimationElapsed / ZoomAnimationSeconds, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);

            _centerX = Lerp(_zoomStartCenterX, _zoomTargetCenterX, eased);
            _centerY = Lerp(_zoomStartCenterY, _zoomTargetCenterY, eased);
            _resolution = Lerp(_zoomStartResolution, _zoomTargetResolution, eased);

            if (t >= 1)
            {
                _centerX = _zoomTargetCenterX;
                _centerY = _zoomTargetCenterY;
                _resolution = _zoomTargetResolution;
                _zoomAnimating = false;
                OnViewportChanged();
                return;
            }

            OnViewportChanged(invalidateTileRequests: false);
        }

        private MPoint ScreenToWorld(
            double screenX,
            double screenY,
            double centerX,
            double centerY,
            double resolution)
            => new(
                centerX + (screenX - ViewportWidth * 0.5) * resolution,
                centerY - (screenY - ViewportHeight * 0.5) * resolution);

        private static double Lerp(double start, double end, double t)
            => start + (end - start) * t;

        private void OnViewportChanged(bool invalidateTileRequests = true)
        {
            if (invalidateTileRequests)
                Interlocked.Increment(ref _viewportRevision);
            RequestFrame();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RequestFrame()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (Visibility != Visibility.Visible && HasRasterLayers)
                Visibility = Visibility.Visible;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _resizeSettleTimer.Stop();
            DropAllTiles();
        }

        private sealed class GpuRasterLayer
        {
            public GpuRasterLayer(GdalRasterProvider provider, string name)
            {
                Provider = provider;
                Name = name;
            }

            public GdalRasterProvider Provider { get; }
            public string Name { get; }
            public bool IsVisible { get; set; } = true;
        }

        private readonly record struct TileKey(
            GdalRasterProvider Provider,
            int Generation,
            int Level,
            int ColStart,
            int RowStart,
            int ColEnd,
            int RowEnd,
            int BufW,
            int BufH);

        private readonly record struct TileUpload(
            TileKey Key,
            MRect Extent,
            int Width,
            int Height,
            double TexMinX,
            double TexMinY,
            double TexMaxX,
            double TexMaxY,
            long ViewportRevision,
            byte[] Rgba);

        private readonly record struct VisibleTileRequest(
            TileKey Key,
            GdalRasterProvider.RasterTileRequest Request);

        private readonly record struct VisibleLoadedTile(
            TileKey Key,
            GpuTile Tile);

        private readonly record struct VisibleFallbackTile(
            TileKey Key,
            GpuTile Tile,
            MRect Extent);

        private sealed class GpuTile
        {
            public GpuTile(
                int textureId,
                MRect extent,
                double texMinX,
                double texMinY,
                double texMaxX,
                double texMaxY,
                long lastUsedFrame)
            {
                TextureId = textureId;
                Extent = extent;
                TexMinX = texMinX;
                TexMinY = texMinY;
                TexMaxX = texMaxX;
                TexMaxY = texMaxY;
                LastUsedFrame = lastUsedFrame;
            }

            public int TextureId { get; }
            public MRect Extent { get; }
            public double TexMinX { get; }
            public double TexMinY { get; }
            public double TexMaxX { get; }
            public double TexMaxY { get; }
            public long LastUsedFrame { get; set; }
            public bool UsesNearestFiltering { get; set; }
        }
    }
}
