using System.IO;
using System.Globalization;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Providers;
using Mapsui.Styles;
using MyGIS.Services;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace MyGIS.Providers
{
    public class GdalRasterProvider : IProvider, IDisposable
    {
        private readonly GdalDatasetManager.DatasetHandle _handle;
        private readonly int _rasterWidth;
        private readonly int _rasterHeight;
        private readonly double[] _geotransform;
        private readonly bool _flipPositiveYForDisplay;
        private readonly MRect _fullExtent;
        private readonly RasterRendererType _rendererType;
        private readonly string _rasterCrs;
        private readonly int _sourceBlockWidth;
        private readonly int _sourceBlockHeight;
        private StretchParameters _stretch;
        private int[] _sourceBandIndexes;
        private readonly int _totalBands;
        private readonly object _dsLock = new();
        private readonly object _renderCacheLock = new();
        private readonly SemaphoreSlim _renderSemaphore = new(1, 1);
        private readonly Dictionary<RenderCacheKey, CachedRender> _renderCache = new();
        private readonly LinkedList<RenderCacheKey> _renderCacheLru = new();
        private readonly Dictionary<StretchType, StretchParameters> _stretchByType = new();
        private const int TileGridScreenPixels = 512;
        private const int FinalTileRenderPixels = 512;
        private const int InteractiveTileRenderPixels = 256;
        private const int MaxCachedRenders = 160;
        private const int TileMargin = 0;
        private int _renderGeneration;
        private int _latestRenderRequestId;
        private int _disposed;

        public string? CRS { get => RasterCrs; set { } }

        public GdalRasterProvider(
            string filePath,
            StretchParameters stretch,
            RasterRendererType rendererType,
            int[] sourceBandIndexes)
        {
            _handle = GdalDatasetManager.Open(filePath);
            _rasterWidth = _handle.DS.RasterXSize;
            _rasterHeight = _handle.DS.RasterYSize;
            _geotransform = new double[6];
            _handle.DS.GetGeoTransform(_geotransform);
            _flipPositiveYForDisplay = ShouldFlipPositiveYForDisplay();
            _fullExtent = ComputeExtent();
            _rendererType = rendererType;
            _stretch = stretch;
            _stretchByType[stretch.Type] = CloneStretchParameters(stretch);
            _sourceBandIndexes = sourceBandIndexes;
            _totalBands = _handle.DS.RasterCount;
            _rasterCrs = ReadCrs();
            (_sourceBlockWidth, _sourceBlockHeight) = ReadSourceBlockSize();
        }

        public string FilePath => _handle.FilePath;
        public int RasterWidth => _rasterWidth;
        public int RasterHeight => _rasterHeight;
        public bool IsTempFile => FilePath.StartsWith(
            Path.Combine(Path.GetTempPath(), "MyGIS"),
            StringComparison.OrdinalIgnoreCase);
        public int OverviewCount => _handle.OverviewCount;
        public string RasterCrs => _rasterCrs;
        public double NativePixelResolution => GetNativePixelResolution();
        public int SourceBlockWidth => _sourceBlockWidth;
        public int SourceBlockHeight => _sourceBlockHeight;
        public bool UsesInternalTiles =>
            _sourceBlockWidth > 0 &&
            _sourceBlockHeight > 0 &&
            _sourceBlockWidth < _rasterWidth &&
            _sourceBlockHeight < _rasterHeight;

        private (int Width, int Height) ReadSourceBlockSize()
        {
            try
            {
                var band = _handle.DS.GetRasterBand(1);
                if (band == null)
                    return (0, 0);

                band.GetBlockSize(out int blockWidth, out int blockHeight);
                return (blockWidth, blockHeight);
            }
            catch
            {
                return (0, 0);
            }
        }

        private string ReadCrs()
        {
            try
            {
                var wkt = _handle.DS.GetProjection();
                if (string.IsNullOrEmpty(wkt)) return "未知";

                using var sr = new OSGeo.OSR.SpatialReference(wkt);
                if (sr == null) return "未知";

                string? authority = sr.GetAuthorityName(null);
                string? code = sr.GetAuthorityCode(null);
                if (!string.IsNullOrEmpty(authority) && !string.IsNullOrEmpty(code))
                    return $"{authority}:{code}";

                return sr.GetName() ?? "未知";
            }
            catch
            {
                return "未知";
            }
        }

        public RasterRendererType RendererType => _rendererType;
        public StretchParameters Stretch => _stretch;
        public int TotalBands => _totalBands;
        public int[] SourceBandIndexes => _sourceBandIndexes;
        public StretchType CurrentStretchType => _stretch.Type;

        public void ChangeStretch(StretchType newType)
        {
            lock (_dsLock)
            {
                ThrowIfDisposed();
                if (_stretch.Type == newType)
                    return;

                var colorRamp = _stretch.ColorRamp;
                if (_stretchByType.TryGetValue(newType, out var cached))
                {
                    _stretch = CloneStretchParameters(cached);
                }
                else if (_stretch.SortedValues.Any(values => values.Count > 0))
                {
                    _stretch = CloneStretchParameters(_stretch);
                    _stretch.Type = newType;
                    RasterRenderer.ApplyStretch(_stretch);
                    _stretchByType[newType] = CloneStretchParameters(_stretch);
                }
                else
                {
                    _stretch = RasterRenderer.ComputeDisplayStretchParameters(
                        _handle.DS,
                        _sourceBandIndexes,
                        _rendererType,
                        newType);
                    _stretchByType[newType] = CloneStretchParameters(_stretch);
                }
                _stretch.ColorRamp = colorRamp;
                ClearRenderCache();
            }
        }

        public Task ChangeStretchAsync(StretchType newType)
            => Task.Run(() => ChangeStretch(newType));

        public void ChangeColorRamp(ColorRampType newRamp)
        {
            lock (_dsLock)
            {
                ThrowIfDisposed();
                _stretch.ColorRamp = newRamp;
                ClearRenderCache();
            }
        }

        public void ChangeBands(int[] newBandIndexes)
        {
            if (newBandIndexes.Length != _sourceBandIndexes.Length) return;
            lock (_dsLock)
            {
                ThrowIfDisposed();
                _sourceBandIndexes = (int[])newBandIndexes.Clone();
                // Recompute stretch from new bands
                _stretch = RasterRenderer.ComputeDisplayStretchParameters(
                    _handle.DS, _sourceBandIndexes,
                    _sourceBandIndexes.Length == 1 ? RasterRendererType.Gray : RasterRendererType.Rgb,
                    _stretch.Type);
                _stretchByType.Clear();
                _stretchByType[_stretch.Type] = CloneStretchParameters(_stretch);
                ClearRenderCache();
            }
        }

        public MRect? GetExtent() => _fullExtent;

        public bool TryGetPixelWindowForExtent(
            MRect extent,
            out int colStart,
            out int rowStart,
            out int colEnd,
            out int rowEnd)
            => TryCreatePixelWindowForExtent(extent, out colStart, out rowStart, out colEnd, out rowEnd);

        public bool IsRenderGenerationCurrent(int generation)
            => generation == Volatile.Read(ref _renderGeneration);

        public MPoint PixelToWorldPoint(double col, double row)
            => PixelToWorld(col, row);

        public IReadOnlyList<RasterTileRequest> CreateGpuTileRequests(
            MRect extent, double resolution, int tileGridScreenPixels, int maxTileRenderPixels, int tileMargin = TileMargin)
        {
            return CreatePyramidTileRequests(extent, resolution, tileGridScreenPixels, maxTileRenderPixels, tileMargin);
        }

        public byte[] RenderTileToRgba(RasterTileRequest request)
        {
            lock (_dsLock)
            {
                ThrowIfDisposed();
                var bandIndexes = (int[])_sourceBandIndexes.Clone();
                var stretch = CloneStretchParameters(_stretch);
                var rendererType = RendererType;

                return RasterRenderer.RenderToRgba(
                    _handle.DS, bandIndexes, stretch, rendererType,
                    request.ColStart, request.RowStart,
                    request.ColEnd - request.ColStart,
                    request.RowEnd - request.RowStart,
                    request.BufW, request.BufH);
            }
        }

        public async Task<IEnumerable<IFeature>> GetFeaturesAsync(FetchInfo fetchInfo)
        {
            if (IsDisposed)
                return Enumerable.Empty<IFeature>();

            var extent = fetchInfo.Extent;
            var resolution = fetchInfo.Resolution;
            if (resolution <= 0 || double.IsNaN(resolution) || double.IsInfinity(resolution))
                return Enumerable.Empty<IFeature>();

            bool isInteractive = fetchInfo.ChangeType == ChangeType.Continuous;
            int maxTileRenderPixels = isInteractive ? InteractiveTileRenderPixels : FinalTileRenderPixels;
            int requestId = Interlocked.Increment(ref _latestRenderRequestId);

            var tileRequests = CreateTileRequests(extent, resolution, TileGridScreenPixels, maxTileRenderPixels);
            if (tileRequests.Count == 0)
                return Enumerable.Empty<IFeature>();

            var features = new List<IFeature>(tileRequests.Count);
            foreach (var tile in tileRequests)
            {
                byte[]? pngBytes = await GetOrRenderAsync(tile.Key, requestId).ConfigureAwait(false);
                if (pngBytes == null || pngBytes.Length == 0)
                    continue;

                features.Add(new RasterFeature(new MRaster(pngBytes, tile.Extent)));
            }

            return features;
        }

        private List<TileRenderRequest> CreateTileRequests(
            MRect extent, double resolution, int tileGridScreenPixels, int maxTileRenderPixels, int tileMargin = TileMargin)
        {
            double tileWorldSize = resolution * tileGridScreenPixels;
            if (tileWorldSize <= 0 || double.IsNaN(tileWorldSize) || double.IsInfinity(tileWorldSize))
                return new List<TileRenderRequest>();

            int startTileX = (int)Math.Floor((extent.MinX - _fullExtent.MinX) / tileWorldSize) - tileMargin;
            int endTileX = (int)Math.Floor((extent.MaxX - _fullExtent.MinX) / tileWorldSize) + tileMargin;
            int startTileY = (int)Math.Floor((extent.MinY - _fullExtent.MinY) / tileWorldSize) - tileMargin;
            int endTileY = (int)Math.Floor((extent.MaxY - _fullExtent.MinY) / tileWorldSize) + tileMargin;

            int generation = Volatile.Read(ref _renderGeneration);
            var requests = new List<TileRenderRequest>();

            for (int ty = startTileY; ty <= endTileY; ty++)
            {
                for (int tx = startTileX; tx <= endTileX; tx++)
                {
                    double minX = _fullExtent.MinX + tx * tileWorldSize;
                    double minY = _fullExtent.MinY + ty * tileWorldSize;
                    double maxX = minX + tileWorldSize;
                    double maxY = minY + tileWorldSize;

                    var tileExtent = new MRect(minX, minY, maxX, maxY);
                    if (!tileExtent.Intersects(_fullExtent))
                        continue;

                    if (!TryCreateTileKey(tileExtent, resolution, maxTileRenderPixels, generation, out var key, out var actualExtent))
                        continue;

                    requests.Add(new TileRenderRequest(key, actualExtent));
                }
            }

            return requests;
        }

        private IReadOnlyList<RasterTileRequest> CreatePyramidTileRequests(
            MRect extent,
            double resolution,
            int tileGridPixels,
            int tileRenderPixels,
            int tileMargin,
            int? forcedLevel = null)
        {
            tileGridPixels = Math.Clamp(tileGridPixels, 64, 2048);
            tileRenderPixels = Math.Clamp(tileRenderPixels, 64, 1024);
            if (!TryCreatePixelWindowForExtent(extent, out var minCol, out var minRow, out var maxCol, out var maxRow))
                return Array.Empty<RasterTileRequest>();

            int level = forcedLevel ?? ChoosePyramidLevel(resolution, tileGridPixels);
            long levelScale = 1L << level;
            long sourceTilePixelsLong = Math.Min(int.MaxValue, tileGridPixels * levelScale);
            int sourceTilePixels = (int)Math.Max(1, sourceTilePixelsLong);
            int paddingSourcePixels = (int)Math.Min(int.MaxValue, levelScale);

            int maxTileX = Math.Max(0, (_rasterWidth - 1) / sourceTilePixels);
            int maxTileY = Math.Max(0, (_rasterHeight - 1) / sourceTilePixels);
            int startTileX = Math.Clamp(minCol / sourceTilePixels - tileMargin, 0, maxTileX);
            int endTileX = Math.Clamp((Math.Max(minCol, maxCol - 1)) / sourceTilePixels + tileMargin, 0, maxTileX);
            int startTileY = Math.Clamp(minRow / sourceTilePixels - tileMargin, 0, maxTileY);
            int endTileY = Math.Clamp((Math.Max(minRow, maxRow - 1)) / sourceTilePixels + tileMargin, 0, maxTileY);

            int generation = Volatile.Read(ref _renderGeneration);
            var requests = new List<RasterTileRequest>();

            for (int ty = startTileY; ty <= endTileY; ty++)
            {
                for (int tx = startTileX; tx <= endTileX; tx++)
                {
                    int colStart = tx * sourceTilePixels;
                    int rowStart = ty * sourceTilePixels;
                    int colEnd = Math.Min(_rasterWidth, colStart + sourceTilePixels);
                    int rowEnd = Math.Min(_rasterHeight, rowStart + sourceTilePixels);
                    if (colEnd <= colStart || rowEnd <= rowStart)
                        continue;

                    int readColStart = Math.Max(0, colStart - paddingSourcePixels);
                    int readRowStart = Math.Max(0, rowStart - paddingSourcePixels);
                    int readColEnd = Math.Min(_rasterWidth, colEnd + paddingSourcePixels);
                    int readRowEnd = Math.Min(_rasterHeight, rowEnd + paddingSourcePixels);
                    int bufW = Math.Max(1, (int)Math.Ceiling((readColEnd - readColStart) / (double)levelScale));
                    int bufH = Math.Max(1, (int)Math.Ceiling((readRowEnd - readRowStart) / (double)levelScale));
                    int paddingBufferPixels = (int)Math.Ceiling(paddingSourcePixels / (double)levelScale);
                    int maxBufferPixels = tileRenderPixels + paddingBufferPixels * 2;
                    if (bufW > maxBufferPixels || bufH > maxBufferPixels)
                    {
                        double scale = maxBufferPixels / (double)Math.Max(bufW, bufH);
                        bufW = Math.Max(1, (int)Math.Round(bufW * scale));
                        bufH = Math.Max(1, (int)Math.Round(bufH * scale));
                    }

                    var actualExtent = PixelWindowToExtent(colStart, rowStart, colEnd, rowEnd);
                    double texMinX = (colStart - readColStart) / (double)(readColEnd - readColStart);
                    double texMinY = (rowStart - readRowStart) / (double)(readRowEnd - readRowStart);
                    double texMaxX = (colEnd - readColStart) / (double)(readColEnd - readColStart);
                    double texMaxY = (rowEnd - readRowStart) / (double)(readRowEnd - readRowStart);

                    requests.Add(new RasterTileRequest(
                        generation,
                        level,
                        readColStart,
                        readRowStart,
                        readColEnd,
                        readRowEnd,
                        bufW,
                        bufH,
                        actualExtent,
                        texMinX,
                        texMinY,
                        texMaxX,
                        texMaxY));
                }
            }

            return requests;
        }

        private int ChoosePyramidLevel(double resolution, int tileRenderPixels)
        {
            double nativeResolution = GetNativePixelResolution();
            if (resolution <= nativeResolution || nativeResolution <= 0)
                return 0;

            int maxLevel = GetMaxPyramidLevel(tileRenderPixels);
            double idealLevel = Math.Log(resolution / nativeResolution, 2.0);
            if (double.IsNaN(idealLevel) || double.IsInfinity(idealLevel))
                return 0;

            return Math.Clamp((int)Math.Round(idealLevel), 0, maxLevel);
        }

        private int GetMaxPyramidLevel(int tileRenderPixels)
        {
            int maxDim = Math.Max(_rasterWidth, _rasterHeight);
            int level = 0;
            long sourceTilePixels = tileRenderPixels;
            while (sourceTilePixels < maxDim && level < 21)
            {
                sourceTilePixels *= 2;
                level++;
            }

            return level;
        }

        private double GetNativePixelResolution()
        {
            double colVector = Math.Sqrt(_geotransform[1] * _geotransform[1] + _geotransform[4] * _geotransform[4]);
            double rowVector = Math.Sqrt(_geotransform[2] * _geotransform[2] + _geotransform[5] * _geotransform[5]);
            double resolution = Math.Max(colVector, rowVector);
            if (resolution > 0 && !double.IsNaN(resolution) && !double.IsInfinity(resolution))
                return resolution;

            return Math.Max(_fullExtent.Width / Math.Max(1, _rasterWidth), _fullExtent.Height / Math.Max(1, _rasterHeight));
        }

        private bool TryCreatePixelWindowForExtent(
            MRect extent,
            out int colStart,
            out int rowStart,
            out int colEnd,
            out int rowEnd)
        {
            colStart = rowStart = colEnd = rowEnd = 0;
            Span<MPoint> corners =
            [
                new MPoint(extent.MinX, extent.MinY),
                new MPoint(extent.MinX, extent.MaxY),
                new MPoint(extent.MaxX, extent.MinY),
                new MPoint(extent.MaxX, extent.MaxY)
            ];

            double minCol = double.PositiveInfinity;
            double minRow = double.PositiveInfinity;
            double maxCol = double.NegativeInfinity;
            double maxRow = double.NegativeInfinity;

            foreach (var corner in corners)
            {
                if (!TryWorldToPixel(corner.X, corner.Y, out double col, out double row))
                    return false;

                minCol = Math.Min(minCol, col);
                minRow = Math.Min(minRow, row);
                maxCol = Math.Max(maxCol, col);
                maxRow = Math.Max(maxRow, row);
            }

            colStart = Math.Clamp((int)Math.Floor(minCol), 0, _rasterWidth);
            rowStart = Math.Clamp((int)Math.Floor(minRow), 0, _rasterHeight);
            colEnd = Math.Clamp((int)Math.Ceiling(maxCol), 0, _rasterWidth);
            rowEnd = Math.Clamp((int)Math.Ceiling(maxRow), 0, _rasterHeight);
            return colEnd > colStart && rowEnd > rowStart;
        }

        private MRect PixelWindowToExtent(int colStart, int rowStart, int colEnd, int rowEnd)
        {
            Span<MPoint> corners =
            [
                PixelToWorld(colStart, rowStart),
                PixelToWorld(colEnd, rowStart),
                PixelToWorld(colEnd, rowEnd),
                PixelToWorld(colStart, rowEnd)
            ];

            double minX = corners[0].X;
            double minY = corners[0].Y;
            double maxX = corners[0].X;
            double maxY = corners[0].Y;

            foreach (var corner in corners[1..])
            {
                minX = Math.Min(minX, corner.X);
                minY = Math.Min(minY, corner.Y);
                maxX = Math.Max(maxX, corner.X);
                maxY = Math.Max(maxY, corner.Y);
            }

            return new MRect(minX, minY, maxX, maxY);
        }

        private bool TryWorldToPixel(double geoX, double geoY, out double col, out double row)
        {
            double gt0 = _geotransform[0], gt1 = _geotransform[1], gt2 = _geotransform[2];
            double gt3 = _geotransform[3], gt4 = _geotransform[4], gt5 = _geotransform[5];

            if (_flipPositiveYForDisplay)
            {
                if (Math.Abs(gt1) < 1e-12 || Math.Abs(gt5) < 1e-12)
                {
                    col = row = 0;
                    return false;
                }

                col = (geoX - gt0) / gt1;
                row = _rasterHeight - ((geoY - gt3) / gt5);
                return double.IsFinite(col) && double.IsFinite(row);
            }

            double det = gt1 * gt5 - gt2 * gt4;
            if (Math.Abs(det) < 1e-12)
            {
                col = row = 0;
                return false;
            }

            col = (gt5 * (geoX - gt0) - gt2 * (geoY - gt3)) / det;
            row = (-gt4 * (geoX - gt0) + gt1 * (geoY - gt3)) / det;
            return !double.IsNaN(col) && !double.IsNaN(row) &&
                   !double.IsInfinity(col) && !double.IsInfinity(row);
        }

        private MPoint PixelToWorld(double col, double row)
        {
            double gt0 = _geotransform[0], gt1 = _geotransform[1], gt2 = _geotransform[2];
            double gt3 = _geotransform[3], gt4 = _geotransform[4], gt5 = _geotransform[5];
            if (_flipPositiveYForDisplay)
                row = _rasterHeight - row;

            return new MPoint(
                gt0 + gt1 * col + gt2 * row,
                gt3 + gt4 * col + gt5 * row);
        }

        private bool ShouldFlipPositiveYForDisplay()
        {
            const double epsilon = 1e-12;
            return Math.Abs(_geotransform[2]) < epsilon &&
                   Math.Abs(_geotransform[4]) < epsilon &&
                   _geotransform[5] > epsilon;
        }

        private bool TryCreateTileKey(
            MRect tileExtent, double resolution, int maxTileDim, int generation,
            out RenderCacheKey key, out MRect actualExtent)
        {
            key = default;
            actualExtent = default!;

            if (!TryCreatePixelWindowForExtent(tileExtent, out int colStart, out int rowStart, out int colEnd, out int rowEnd))
                return false;

            actualExtent = PixelWindowToExtent(colStart, rowStart, colEnd, rowEnd);

            int bufW = Math.Max(1, (int)Math.Ceiling(actualExtent.Width / resolution));
            int bufH = Math.Max(1, (int)Math.Ceiling(actualExtent.Height / resolution));
            if (bufW > maxTileDim || bufH > maxTileDim)
            {
                double scale = maxTileDim / (double)Math.Max(bufW, bufH);
                bufW = Math.Max(1, (int)(bufW * scale));
                bufH = Math.Max(1, (int)(bufH * scale));
            }

            key = new RenderCacheKey(generation, colStart, rowStart, colEnd, rowEnd, bufW, bufH);
            return true;
        }

        private async Task<byte[]?> GetOrRenderAsync(RenderCacheKey key, int requestId)
        {
            if (IsDisposed)
                return null;

            if (TryGetCachedRender(key, out var cachedBytes))
                return cachedBytes;

            await _renderSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsDisposed)
                    return null;

                if (TryGetCachedRender(key, out cachedBytes))
                    return cachedBytes;

                if (requestId < Volatile.Read(ref _latestRenderRequestId))
                    return null;

                var bandIndexes = (int[])_sourceBandIndexes.Clone();
                var stretch = CloneStretchParameters(_stretch);
                var rendererType = RendererType;

                byte[] pngBytes = await Task.Run(() =>
                {
                    lock (_dsLock)
                    {
                        if (IsDisposed)
                            return Array.Empty<byte>();

                        return RasterRenderer.RenderToPng(
                            _handle.DS, bandIndexes, stretch, rendererType,
                            key.ColStart, key.RowStart, key.ColEnd - key.ColStart, key.RowEnd - key.RowStart,
                            key.BufW, key.BufH);
                    }
                }).ConfigureAwait(false);

                if (pngBytes.Length == 0 || IsDisposed)
                    return null;

                if (key.Generation == Volatile.Read(ref _renderGeneration))
                    AddCachedRender(key, pngBytes);

                return pngBytes;
            }
            finally
            {
                _renderSemaphore.Release();
            }
        }

        private bool TryGetCachedRender(RenderCacheKey key, out byte[] pngBytes)
        {
            lock (_renderCacheLock)
            {
                if (_renderCache.TryGetValue(key, out var cached))
                {
                    _renderCacheLru.Remove(cached.Node);
                    _renderCacheLru.AddFirst(cached.Node);
                    pngBytes = cached.PngBytes;
                    return true;
                }

                foreach (var candidate in _renderCache)
                {
                    var candidateKey = candidate.Key;
                    if (candidateKey.Generation != key.Generation ||
                        candidateKey.ColStart != key.ColStart ||
                        candidateKey.RowStart != key.RowStart ||
                        candidateKey.ColEnd != key.ColEnd ||
                        candidateKey.RowEnd != key.RowEnd ||
                        candidateKey.BufW < key.BufW ||
                        candidateKey.BufH < key.BufH)
                        continue;

                    _renderCacheLru.Remove(candidate.Value.Node);
                    _renderCacheLru.AddFirst(candidate.Value.Node);
                    pngBytes = candidate.Value.PngBytes;
                    return true;
                }
            }

            pngBytes = Array.Empty<byte>();
            return false;
        }

        private void AddCachedRender(RenderCacheKey key, byte[] pngBytes)
        {
            lock (_renderCacheLock)
            {
                if (_renderCache.TryGetValue(key, out var cached))
                {
                    _renderCacheLru.Remove(cached.Node);
                    _renderCacheLru.AddFirst(cached.Node);
                    return;
                }

                var node = new LinkedListNode<RenderCacheKey>(key);
                _renderCacheLru.AddFirst(node);
                _renderCache[key] = new CachedRender(pngBytes, node);

                while (_renderCache.Count > MaxCachedRenders && _renderCacheLru.Last != null)
                {
                    var oldKey = _renderCacheLru.Last.Value;
                    _renderCacheLru.RemoveLast();
                    _renderCache.Remove(oldKey);
                }
            }
        }

        private void ClearRenderCache()
        {
            Interlocked.Increment(ref _renderGeneration);
            lock (_renderCacheLock)
            {
                _renderCache.Clear();
                _renderCacheLru.Clear();
            }
        }

        private static StretchParameters CloneStretchParameters(StretchParameters source)
        {
            return new StretchParameters
            {
                Type = source.Type,
                ColorRamp = source.ColorRamp,
                Lo = (float[])source.Lo.Clone(),
                Hi = (float[])source.Hi.Clone(),
                DataMin = (float[])source.DataMin.Clone(),
                DataMax = (float[])source.DataMax.Clone(),
                HasNoData = (bool[])source.HasNoData.Clone(),
                NoDataValues = (double[])source.NoDataValues.Clone(),
                SortedValues = (List<float>[])source.SortedValues.Clone(),
                Mean = (float[])source.Mean.Clone(),
                StdDev = (float[])source.StdDev.Clone(),
                Cdf = source.Cdf.Select(c => c != null ? (byte[])c.Clone() : Array.Empty<byte>()).ToArray()
            };
        }

        private MRect ComputeExtent()
        {
            int w = _rasterWidth, h = _rasterHeight;
            var corners = new[]
            {
                PixelToWorld(0, 0),
                PixelToWorld(w, 0),
                PixelToWorld(0, h),
                PixelToWorld(w, h)
            };

            return new MRect(
                corners.Min(p => p.X),
                corners.Min(p => p.Y),
                corners.Max(p => p.X),
                corners.Max(p => p.Y));
        }

        public List<string>? ReadPixelValue(MRect worldExtent, double geoX, double geoY)
        {
            if (IsDisposed)
                return null;

            if (!worldExtent.Contains(new MPoint(geoX, geoY))) return null;

            if (!TryWorldToPixel(geoX, geoY, out double colDouble, out double rowDouble))
                return null;

            int col = (int)Math.Floor(colDouble);
            int row = (int)Math.Floor(rowDouble);

            if (col < 0 || col >= _rasterWidth || row < 0 || row >= _rasterHeight)
                return null;

            lock (_dsLock)
            {
                if (IsDisposed)
                    return null;

                if (RendererType == RasterRendererType.Rgb)
                {
                    var parts = new List<string>();
                    string[] labels = { "R", "G", "B" };
                    var vals = new float[1];
                    for (int b = 0; b < _sourceBandIndexes.Length && b < 3; b++)
                    {
                        int bandIndex = _sourceBandIndexes[b];
                        if (bandIndex < 1 || bandIndex > _totalBands)
                            return null;

                        var band = _handle.DS.GetRasterBand(bandIndex);
                        if (band == null)
                            return null;

                        band.ReadRaster(col, row, 1, 1, vals, 1, 1, 0, 0);
                        bool hasNd = b < _stretch.HasNoData.Length && _stretch.HasNoData[b];
                        double ndv = b < _stretch.NoDataValues.Length ? _stretch.NoDataValues[b] : 0;
                        if (float.IsNaN(vals[0]) || float.IsInfinity(vals[0]) ||
                            (hasNd && Math.Abs(vals[0] - ndv) < 0.0001))
                            return null;
                        parts.Add($"{labels[b]}: {vals[0]:F4}");
                    }
                    return parts;
                }
                else
                {
                    var vals = new float[1];
                    int bandIndex = _sourceBandIndexes.Length > 0 ? _sourceBandIndexes[0] : 1;
                    if (bandIndex < 1 || bandIndex > _totalBands)
                        return null;

                    var band = _handle.DS.GetRasterBand(bandIndex);
                    if (band == null)
                        return null;

                    band.ReadRaster(col, row, 1, 1, vals, 1, 1, 0, 0);
                    bool hasNd = _stretch.HasNoData.Length > 0 && _stretch.HasNoData[0];
                    double ndv = _stretch.NoDataValues.Length > 0 ? _stretch.NoDataValues[0] : 0;
                    float v = vals[0];
                    if (float.IsNaN(v) || float.IsInfinity(v) ||
                        (hasNd && Math.Abs(v - ndv) < 0.0001))
                        return null;
                    return new List<string> { $"值: {v:F4}" };
                }
            }
        }

        private readonly record struct RenderCacheKey(
            int Generation,
            int ColStart,
            int RowStart,
            int ColEnd,
            int RowEnd,
            int BufW,
            int BufH);

        private readonly record struct TileRenderRequest(
            RenderCacheKey Key,
            MRect Extent);

        public readonly record struct RasterTileRequest(
            int Generation,
            int Level,
            int ColStart,
            int RowStart,
            int ColEnd,
            int RowEnd,
            int BufW,
            int BufH,
            MRect Extent,
            double TexMinX,
            double TexMinY,
            double TexMaxX,
            double TexMaxY);

        private sealed record CachedRender(
            byte[] PngBytes,
            LinkedListNode<RenderCacheKey> Node);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (_dsLock)
            {
                _handle.Dispose();
            }
        }

        private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(GdalRasterProvider));
        }
    }
}
