using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Nts.Providers;
using Mapsui.Providers;
using Mapsui.Styles;
using GeoVision.Helpers;
using GeoVision.Providers;
using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;
using OSGeo.GDAL;

namespace GeoVision.Services
{
    public static class DataLoader
    {
        public static string? LastDiagnostic { get; private set; }

        public static Task<ILayer?> LoadAsync(
            string filePath,
            IProgress<(int percent, string label)>? progress = null)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".geojson" or ".json" => Task.FromResult<ILayer?>(LoadWithSimpleProgress(filePath, progress, LoadGeoJson)),
                ".shp" => Task.FromResult<ILayer?>(LoadWithSimpleProgress(filePath, progress, LoadShapefile)),
                ".tif" or ".tiff" => Task.FromResult(LoadGeoTiffGdal(filePath, progress)),
                _ => Task.FromResult<ILayer?>(null)
            };
        }

        private static ILayer LoadWithSimpleProgress(
            string filePath,
            IProgress<(int percent, string label)>? progress,
            Func<string, ILayer> loader)
        {
            string fileName = Path.GetFileName(filePath);
            progress?.Report((5, $"正在读取 {fileName}"));
            var layer = loader(filePath);
            progress?.Report((100, $"加载完成 {fileName}"));
            return layer;
        }

        public static bool IsSupported(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext is ".geojson" or ".json" or ".shp" or ".tif" or ".tiff";
        }

        public static string GetFilter()
            => "GIS Data|*.shp;*.geojson;*.json;*.tif;*.tiff|Shapefile|*.shp|GeoJSON|*.geojson;*.json|GeoTIFF|*.tif;*.tiff|All Files|*.*";

        // ---- GeoJSON / Shapefile ----

        private static ILayer LoadGeoJson(string filePath)
        {
            var json = File.ReadAllText(filePath);
            var provider = new GeoJsonProvider(json);
            return new Layer(Path.GetFileName(filePath))
            {
                DataSource = provider,
                Style = CreateVectorStyle(),
                Tag = filePath
            };
        }

        private static ILayer LoadShapefile(string filePath)
        {
            // Determine encoding
            var encoding = GetDefaultShapefileEncoding();
            var cpgPath = Path.ChangeExtension(filePath, ".cpg");
            if (File.Exists(cpgPath))
            {
                try
                {
                    var cpg = File.ReadAllText(cpgPath).Trim();
                    if (int.TryParse(cpg, out int cp))
                        encoding = Encoding.GetEncoding(cp);
                    else if (cpg.StartsWith("OEM ", StringComparison.OrdinalIgnoreCase) && int.TryParse(cpg[4..], out cp))
                        encoding = Encoding.GetEncoding(cp);
                    else
                        encoding = Encoding.GetEncoding(cpg);
                }
                catch { }
            }

            var shpFile = new Mapsui.Nts.Providers.Shapefile.ShapeFile(filePath);
            shpFile.Encoding = encoding;
            var provider = new ShapeFileProvider(shpFile);

            return new Layer(Path.GetFileName(filePath))
            {
                DataSource = provider,
                Style = CreateVectorStyle()
            };
        }

        private static Encoding GetDefaultShapefileEncoding()
        {
            try
            {
                return Encoding.GetEncoding(936);
            }
            catch
            {
                return Encoding.Default;
            }
        }

        // ---- GeoTIFF via GDAL (pyramid/tile-based) ----

        private static ILayer? LoadGeoTiffGdal(
            string filePath,
            IProgress<(int percent, string label)>? progress)
        {
            string fileName = Path.GetFileName(filePath);
            progress?.Report((0, $"正在加载 {fileName}"));

            using var inspectHandle = GdalDatasetManager.Open(filePath, progress);
            var inspectDs = inspectHandle.DS;
            if (inspectDs == null) throw new InvalidDataException("GDAL 无法打开文件");

            int w = inspectDs.RasterXSize, h = inspectDs.RasterYSize, bands = inspectDs.RasterCount;
            var extent = GetExtent(inspectDs, w, h);

            progress?.Report((92, $"正在分析波段 {fileName}"));
            var rendererType = RasterRenderer.DetermineRendererType(inspectDs, bands);
            int[] sourceBandIndexes = rendererType switch
            {
                RasterRendererType.Palette => new[] { 1 },
                RasterRendererType.Gray => new[] { 1 },
                RasterRendererType.Rgb => RasterRenderer.SelectDisplayBands(inspectDs, bands),
                _ => throw new InvalidOperationException("Unknown renderer type")
            };

            progress?.Report((95, $"正在计算显示参数 {fileName}"));
            var stretch = RasterRenderer.ComputeDisplayStretchParameters(inspectDs, sourceBandIndexes, rendererType);

            LastDiagnostic = $"{w}x{h} bands={bands} renderer={rendererType} " +
                             $"ext=[{extent.MinX:F0},{extent.MinY:F0}]-[{extent.MaxX:F0},{extent.MaxY:F0}]";

            progress?.Report((98, $"正在创建图层 {fileName}"));
            var provider = new GdalRasterProvider(filePath, stretch, rendererType, sourceBandIndexes);

            LastDiagnostic += $" overviews={provider.OverviewCount}";

            var layer = new Layer(Path.GetFileName(filePath))
            {
                DataSource = provider,
                Style = new RasterStyle(),
                Tag = provider
            };

            progress?.Report((100, $"加载完成 {fileName}"));
            return layer;
        }

        private static MRect GetExtent(Dataset ds, int w, int h)
        {
            double[] gt = new double[6];
            ds.GetGeoTransform(gt);

            var corners = new[]
            {
                TransformPixelToWorld(gt, 0, 0),
                TransformPixelToWorld(gt, w, 0),
                TransformPixelToWorld(gt, 0, h),
                TransformPixelToWorld(gt, w, h)
            };

            return new MRect(
                corners.Min(p => p.X),
                corners.Min(p => p.Y),
                corners.Max(p => p.X),
                corners.Max(p => p.Y));
        }

        private static MPoint TransformPixelToWorld(double[] gt, double col, double row)
        {
            return new MPoint(
                gt[0] + gt[1] * col + gt[2] * row,
                gt[3] + gt[4] * col + gt[5] * row);
        }

        private static IStyle CreateVectorStyle() => new VectorStyle
        {
            Fill = new Brush(new Color(120, 120, 180, 80)),
            Line = new Pen(new Color(60, 60, 140, 200), 2),
            Outline = new Pen(new Color(60, 60, 140, 200), 2)
        };

        // ---- ShapeFile Provider (unchanged) ----

        internal class ShapeFileProvider : IProvider
        {
            private const int MaxCachedFeatures = 20_000;

            private readonly Mapsui.Nts.Providers.Shapefile.ShapeFile _sf;
            private readonly ConcurrentDictionary<uint, IFeature> _cache = new();
            private readonly object _sfLock = new();

            public ShapeFileProvider(Mapsui.Nts.Providers.Shapefile.ShapeFile sf) => _sf = sf;
            public string? CRS { get => _sf.CRS; set { } }
            public MRect? GetExtent() => _sf.GetExtent();
            public int FeatureCount => _sf.GetFeatureCount();
            public string FilePath => _sf.Filename ?? string.Empty;
            public Encoding Encoding => _sf.Encoding ?? GetDefaultShapefileEncoding();
            public IFeature? GetFeature(uint id) => GetFeatureCached(id);

            private IFeature? GetFeatureCached(uint id)
            {
                if (_cache.TryGetValue(id, out var cached))
                    return cached;

                lock (_sfLock)
                {
                    if (_cache.TryGetValue(id, out cached))
                        return cached;

                    var feature = _sf.GetFeature(id);
                    if (feature != null)
                    {
                        if (_cache.Count > MaxCachedFeatures)
                            _cache.Clear();

                        _cache[id] = feature;
                    }
                    return feature;
                }
            }

            public IEnumerable<IFeature> GetFeaturesInView(MRect extent, int maxCount = 20)
            {
                var ids = _sf.GetObjectIDsInView(extent);
                int added = 0;
                foreach (var id in ids)
                {
                    var feature = GetFeatureCached(id);

                    if (feature == null) continue;
                    yield return feature;

                    added++;
                    if (added >= maxCount) yield break;
                }
            }

            public Task<IEnumerable<IFeature>> GetFeaturesAsync(FetchInfo fi)
            {
                var ids = _sf.GetObjectIDsInView(fi.Extent);
                int count = ids.Count;
                var result = new List<IFeature>(Math.Min(count, 5000));
                double simplifyTolerance = fi.Resolution * 0.3; // simplify based on zoom

                foreach (var id in ids)
                {
                    var f = GetFeatureCached(id);

                    if (f == null) continue;

                    // Simplify geometry at lower zoom levels
                    if (simplifyTolerance > 0.1 && f is GeometryFeature gf && gf.Geometry != null)
                    {
                        try
                        {
                            var simplified = DouglasPeuckerSimplifier.Simplify(
                                gf.Geometry, simplifyTolerance);
                            if (simplified != null && !simplified.IsEmpty)
                            {
                                var sf = new GeometryFeature { Geometry = simplified };
                                foreach (var key in gf.Fields)
                                    sf[key] = gf[key];
                                result.Add(sf);
                                continue;
                            }
                        }
                        catch { }
                    }

                    result.Add(f);
                    if (result.Count >= 5000) break;
                }

                return Task.FromResult<IEnumerable<IFeature>>(result);
            }
        }
        public static string[] ReadDbfFieldNames(string shpPath, System.Text.Encoding encoding)
        {
            var dbfPath = Path.ChangeExtension(shpPath, ".dbf");
            if (!File.Exists(dbfPath)) return Array.Empty<string>();

            using var fs = new FileStream(dbfPath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);

            // DBF header: 32 bytes
            br.ReadBytes(8); // version + date + records
            short headerLen = br.ReadInt16();
            short recordLen = br.ReadInt16();
            br.ReadBytes(20); // reserved

            var names = new List<string>();
            int fieldCount = (headerLen - 33) / 32;
            for (int i = 0; i < fieldCount; i++)
            {
                byte[] nameBytes = br.ReadBytes(11);
                int nullIdx = Array.IndexOf<byte>(nameBytes, 0);
                if (nullIdx >= 0)
                    names.Add(encoding.GetString(nameBytes, 0, nullIdx));
                else
                    names.Add(encoding.GetString(nameBytes));
                br.ReadBytes(21); // type + displacement + length + decimal + flags + reserved
            }
            return names.ToArray();
        }
    }
}
