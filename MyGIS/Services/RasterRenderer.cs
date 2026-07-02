using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OSGeo.GDAL;
using SkiaSharp;

namespace MyGIS.Services
{
    public enum RasterRendererType { Rgb, Gray, Palette }

    public enum StretchType
    {
        PercentClip,
        None,
        MinMax,
        StdDev,
        HistEq,
        Sigmoid
    }

    public class StretchParameters
    {
        public StretchType Type { get; set; } = StretchType.PercentClip;
        public ColorRampType ColorRamp { get; set; } = ColorRampType.Gray;
        public float[] Lo { get; set; } = Array.Empty<float>();
        public float[] Hi { get; set; } = Array.Empty<float>();
        public float[] DataMin { get; init; } = Array.Empty<float>();
        public float[] DataMax { get; init; } = Array.Empty<float>();
        public bool[] HasNoData { get; init; } = Array.Empty<bool>();
        public double[] NoDataValues { get; init; } = Array.Empty<double>();
        public List<float>[] SortedValues { get; init; } = Array.Empty<List<float>>();
        public float[] Mean { get; set; } = Array.Empty<float>();
        public float[] StdDev { get; set; } = Array.Empty<float>();
        public byte[][] Cdf { get; set; } = Array.Empty<byte[]>();
    }

    public static class RasterRenderer
    {
        private const int MaxStoredSamples = 500_000;
        private const int MaxAnalysisSampleDim = 2048;
        private const float PercentClipLow = 0.005f;
        private const float PercentClipHigh = 0.995f;
        private const int StretchCacheVersion = 2;
        private static readonly ConcurrentDictionary<string, StretchParameters> StretchCache = new();

        public static RasterRendererType DetermineRendererType(Dataset ds, int bandCount)
        {
            if (bandCount <= 0)
                throw new InvalidOperationException("Raster does not contain any readable bands.");

            var firstBand = GetRequiredBand(ds, 1);
            var colorTable = firstBand.GetRasterColorTable();
            if (bandCount == 1 && colorTable != null && colorTable.GetCount() > 0)
                return RasterRendererType.Palette;
            if (bandCount == 1)
                return RasterRendererType.Gray;
            return RasterRendererType.Rgb;
        }

        public static int[] SelectDisplayBands(Dataset ds, int bands)
        {
            int red = 0, green = 0, blue = 0;
            for (int i = 1; i <= bands; i++)
            {
                if (!TryGetBand(ds, i, out var band) || band == null)
                    continue;

                var ci = band.GetRasterColorInterpretation();
                if (ci == ColorInterp.GCI_RedBand) red = i;
                else if (ci == ColorInterp.GCI_GreenBand) green = i;
                else if (ci == ColorInterp.GCI_BlueBand) blue = i;
            }

            if (red > 0 && green > 0 && blue > 0)
                return new[] { red, green, blue };

            if (bands >= 4)
                return new[] { 3, 2, 1 };

            return Enumerable.Range(1, Math.Min(bands, 3)).ToArray();
        }

        public static int GetBandCount(Dataset ds) => ds.RasterCount;

        private static bool TryGetBand(Dataset ds, int bandIndex, out Band? band)
        {
            band = null;
            int bandCount = SafeRasterCount(ds);
            if (bandIndex < 1 || bandIndex > bandCount)
                return false;

            try
            {
                band = ds.GetRasterBand(bandIndex);
                return band != null;
            }
            catch
            {
                return false;
            }
        }

        private static Band GetRequiredBand(Dataset ds, int bandIndex)
        {
            if (TryGetBand(ds, bandIndex, out var band) && band != null)
                return band;

            throw new InvalidOperationException(
                $"Raster band {bandIndex} is not available. Raster has {SafeRasterCount(ds)} band(s). Source: {SafeDatasetDescription(ds)}");
        }

        private static int SafeRasterCount(Dataset ds)
        {
            try
            {
                return ds.RasterCount;
            }
            catch
            {
                return 0;
            }
        }

        private static string SafeDatasetDescription(Dataset ds)
        {
            try
            {
                var description = ds.GetDescription();
                return string.IsNullOrWhiteSpace(description) ? "<memory>" : description;
            }
            catch
            {
                return "<unknown>";
            }
        }

        // ---- Stretch computation ----

        public static StretchParameters ComputeStretchParameters(
            Dataset ds, int[] sourceBandIndexes, RasterRendererType type,
            StretchType stretchType = StretchType.PercentClip)
        {
            var cacheKey = BuildStretchCacheKey(ds, sourceBandIndexes, type, stretchType);
            if (cacheKey != null && StretchCache.TryGetValue(cacheKey, out var cached))
                return CloneStretchParameters(cached);

            var persistent = TryLoadPersistentStretch(ds, sourceBandIndexes, type, stretchType);
            if (persistent != null)
            {
                if (cacheKey != null)
                    StretchCache[cacheKey] = CloneStretchParameters(persistent);
                return persistent;
            }

            int displayBands = sourceBandIndexes.Length;
            var hasNoData = new bool[displayBands];
            var noDataValues = new double[displayBands];

            var sortedValues = new List<float>[displayBands];
            var means = new float[displayBands];
            var stddevs = new float[displayBands];
            var dataMin = new float[displayBands];
            var dataMax = new float[displayBands];
            var lo = new float[displayBands];
            var hi = new float[displayBands];

            for (int b = 0; b < displayBands; b++)
            {
                if (!TryGetBand(ds, sourceBandIndexes[b], out var sourceBand) || sourceBand == null)
                {
                    lo[b] = 0;
                    hi[b] = 1;
                    sortedValues[b] = new List<float>();
                    continue;
                }

                sourceBand.GetMinimum(out double bandMin, out int hasMin);
                sourceBand.GetMaximum(out double bandMax, out int hasMax);
                var minMax = new double[2];
                sourceBand.ComputeRasterMinMax(minMax, 1);
                if (IsFiniteRange(minMax[0], minMax[1]))
                {
                    bandMin = minMax[0];
                    bandMax = minMax[1];
                }
                else if (hasMin == 0 || hasMax == 0)
                {
                    bandMin = 0;
                    bandMax = 1;
                }
                dataMin[b] = (float)bandMin;
                dataMax[b] = (float)bandMax;

                sourceBand.GetNoDataValue(out double ndv, out int hasNdv);
                hasNoData[b] = hasNdv != 0;
                noDataValues[b] = ndv;

                var band = SelectStatisticsBand(sourceBand, bandMin, bandMax, out int sampleW, out int sampleH);
                if (band == null) { lo[b] = 0; hi[b] = 1; sortedValues[b] = new List<float>(); continue; }

                int totalSamples = sampleW * sampleH;
                var buf = new float[totalSamples];
                band.ReadRaster(0, 0, band.XSize, band.YSize, buf, sampleW, sampleH, 0, 0);

                var values = new List<float>(Math.Min(totalSamples, MaxStoredSamples));
                double sum = 0;
                int validCount = 0;
                int zeroCount = 0;

                foreach (float v in buf)
                {
                    if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                    if (hasNoData[b] && Math.Abs(v - ndv) < 0.0001) continue;
                    if (Math.Abs(v) < 0.0001) zeroCount++;
                }

                bool treatZeroAsBackground = !hasNoData[b] && zeroCount > totalSamples * 0.02;

                foreach (float v in buf)
                {
                    if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                    if (hasNoData[b] && Math.Abs(v - ndv) < 0.0001) continue;
                    if (treatZeroAsBackground && Math.Abs(v) < 0.0001) continue;
                    sum += v;
                    validCount++;
                    values.Add(v);
                }

                if (values.Count == 0) { lo[b] = 0; hi[b] = 1; sortedValues[b] = values; continue; }

                values.Sort();
                means[b] = validCount > 0 ? (float)(sum / validCount) : 0;

                // Compute stddev
                double sumSq = 0;
                foreach (float v in values)
                {
                    double d = v - means[b];
                    sumSq += d * d;
                }
                stddevs[b] = values.Count > 1 ? (float)Math.Sqrt(sumSq / values.Count) : 0;

                // Subsample if too many values
                if (values.Count > MaxStoredSamples)
                {
                    var sampled = new List<float>(MaxStoredSamples);
                    float step = (float)values.Count / MaxStoredSamples;
                    for (int j = 0; j < MaxStoredSamples; j++)
                        sampled.Add(values[(int)(j * step)]);
                    sortedValues[b] = sampled;
                }
                else
                {
                    sortedValues[b] = values;
                }
            }

            var result = new StretchParameters
            {
                Type = stretchType,
                HasNoData = hasNoData,
                NoDataValues = noDataValues,
                SortedValues = sortedValues,
                DataMin = dataMin,
                DataMax = dataMax,
                Mean = means,
                StdDev = stddevs,
                Lo = lo,
                Hi = hi
            };

            ApplyStretch(result);
            if (cacheKey != null)
                StretchCache[cacheKey] = CloneStretchParameters(result);
            TrySavePersistentStretch(ds, sourceBandIndexes, type, stretchType, result);

            return result;
        }

        public static StretchParameters ComputeDisplayStretchParameters(
            Dataset ds, int[] sourceBandIndexes, RasterRendererType type,
            StretchType stretchType = StretchType.PercentClip)
        {
            if (type != RasterRendererType.Rgb || sourceBandIndexes.Length <= 1)
                return ComputeStretchParameters(ds, sourceBandIndexes, type, stretchType);

            var cacheKey = BuildStretchCacheKey(ds, sourceBandIndexes, type, stretchType);
            if (cacheKey != null && StretchCache.TryGetValue(cacheKey, out var cached))
                return CloneStretchParameters(cached);

            var persistent = TryLoadPersistentStretch(ds, sourceBandIndexes, type, stretchType);
            if (persistent != null)
            {
                if (cacheKey != null)
                    StretchCache[cacheKey] = CloneStretchParameters(persistent);
                return persistent;
            }

            var perBand = new StretchParameters[sourceBandIndexes.Length];
            for (int i = 0; i < sourceBandIndexes.Length; i++)
                perBand[i] = ComputeStretchParameters(
                    ds, new[] { sourceBandIndexes[i] }, RasterRendererType.Gray, stretchType);

            var result = ComposeBandIndependentStretch(perBand, stretchType);
            if (cacheKey != null)
                StretchCache[cacheKey] = CloneStretchParameters(result);
            TrySavePersistentStretch(ds, sourceBandIndexes, type, stretchType, result);
            return result;
        }

        private static StretchParameters ComposeBandIndependentStretch(
            StretchParameters[] perBand,
            StretchType stretchType)
        {
            int bands = perBand.Length;
            var lo = new float[bands];
            var hi = new float[bands];
            var dataMin = new float[bands];
            var dataMax = new float[bands];
            var hasNoData = new bool[bands];
            var noDataValues = new double[bands];
            var sortedValues = new List<float>[bands];
            var mean = new float[bands];
            var stdDev = new float[bands];
            var cdf = new byte[bands][];

            for (int i = 0; i < bands; i++)
            {
                var s = perBand[i];
                lo[i] = FirstOrDefault(s.Lo, 0);
                hi[i] = FirstOrDefault(s.Hi, 1);
                dataMin[i] = FirstOrDefault(s.DataMin, 0);
                dataMax[i] = FirstOrDefault(s.DataMax, 1);
                hasNoData[i] = FirstOrDefault(s.HasNoData, false);
                noDataValues[i] = FirstOrDefault(s.NoDataValues, 0);
                sortedValues[i] = s.SortedValues.Length > 0
                    ? new List<float>(s.SortedValues[0])
                    : new List<float>();
                mean[i] = FirstOrDefault(s.Mean, 0);
                stdDev[i] = FirstOrDefault(s.StdDev, 0);
                cdf[i] = s.Cdf.Length > 0 ? (byte[])s.Cdf[0].Clone() : Array.Empty<byte>();
            }

            return new StretchParameters
            {
                Type = stretchType,
                ColorRamp = ColorRampType.Gray,
                Lo = lo,
                Hi = hi,
                DataMin = dataMin,
                DataMax = dataMax,
                HasNoData = hasNoData,
                NoDataValues = noDataValues,
                SortedValues = sortedValues,
                Mean = mean,
                StdDev = stdDev,
                Cdf = cdf
            };
        }

        private static T FirstOrDefault<T>(T[] values, T fallback)
            => values.Length > 0 ? values[0] : fallback;

        private static Band? SelectStatisticsBand(Band sourceBand, double fullMin, double fullMax, out int sampleW, out int sampleH)
        {
            if (sourceBand.GetOverviewCount() > 0)
            {
                var overview = sourceBand.GetOverview(0);
                if (overview != null && IsBandRangeCompatible(overview, fullMin, fullMax))
                {
                    sampleW = overview.XSize;
                    sampleH = overview.YSize;
                    return overview;
                }
            }

            double scale = Math.Min(1d, MaxAnalysisSampleDim / (double)Math.Max(sourceBand.XSize, sourceBand.YSize));
            sampleW = Math.Max(1, (int)Math.Round(sourceBand.XSize * scale));
            sampleH = Math.Max(1, (int)Math.Round(sourceBand.YSize * scale));
            return sourceBand;
        }

        private static bool IsBandRangeCompatible(Band band, double fullMin, double fullMax)
        {
            if (!IsFiniteRange(fullMin, fullMax))
                return true;

            var minMax = new double[2];
            band.ComputeRasterMinMax(minMax, 1);
            double sampleMin = minMax[0];
            double sampleMax = minMax[1];
            if (!IsFiniteRange(sampleMin, sampleMax))
                return false;

            double fullRange = fullMax - fullMin;
            double sampleRange = sampleMax - sampleMin;
            double fullCenter = (fullMin + fullMax) * 0.5;
            double sampleCenter = (sampleMin + sampleMax) * 0.5;

            double rangeRatio = sampleRange / fullRange;
            double centerDelta = Math.Abs(sampleCenter - fullCenter) / Math.Max(fullRange, 1d);
            return rangeRatio is > 0.2 and < 5.0 && centerDelta < 1.0;
        }

        private static bool IsFiniteRange(double min, double max)
        {
            return !double.IsNaN(min) && !double.IsNaN(max) &&
                   !double.IsInfinity(min) && !double.IsInfinity(max) &&
                   max > min;
        }

        private static string? BuildStretchCacheKey(
            Dataset ds, int[] sourceBandIndexes, RasterRendererType type, StretchType stretchType)
        {
            var path = ds.GetDescription();
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string identity = path;
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    var fi = new FileInfo(fullPath);
                    identity = $"{fullPath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                }
            }
            catch
            {
                identity = path;
            }

            return $"{identity}|{type}|{stretchType}|{string.Join(",", sourceBandIndexes)}";
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

        private static StretchParameters? TryLoadPersistentStretch(
            Dataset ds, int[] sourceBandIndexes, RasterRendererType type, StretchType stretchType)
        {
            if (!TryGetSourceInfo(ds, out var sourcePath, out var sourceLength, out var sourceModifiedUtcTicks))
                return null;

            string entryKey = BuildPersistentEntryKey(sourceBandIndexes, type, stretchType);
            foreach (var cachePath in GetPersistentCacheReadPaths(sourcePath))
            {
                try
                {
                    if (!File.Exists(cachePath)) continue;

                    var json = File.ReadAllText(cachePath);
                    var cache = JsonSerializer.Deserialize<PersistentStretchCache>(json);
                    if (cache == null ||
                        cache.Version != StretchCacheVersion ||
                        cache.SourceLength != sourceLength ||
                        cache.SourceModifiedUtcTicks != sourceModifiedUtcTicks)
                        continue;

                    var entry = cache.Entries.FirstOrDefault(e => e.Key == entryKey);
                    if (entry == null) continue;

                    int bandCount = sourceBandIndexes.Length;
                    return new StretchParameters
                    {
                        Type = stretchType,
                        ColorRamp = ColorRampType.Gray,
                        Lo = Resize(entry.Lo, bandCount, 0),
                        Hi = Resize(entry.Hi, bandCount, 1),
                        DataMin = Resize(entry.DataMin, bandCount, 0),
                        DataMax = Resize(entry.DataMax, bandCount, 1),
                        HasNoData = Resize(entry.HasNoData, bandCount, false),
                        NoDataValues = Resize(entry.NoDataValues, bandCount, 0),
                        SortedValues = Enumerable.Range(0, bandCount).Select(_ => new List<float>()).ToArray(),
                        Mean = Resize(entry.Mean, bandCount, 0),
                        StdDev = Resize(entry.StdDev, bandCount, 0),
                        Cdf = Enumerable.Range(0, bandCount).Select(_ => Array.Empty<byte>()).ToArray()
                    };
                }
                catch
                {
                    // Ignore invalid or inaccessible cache files.
                }
            }

            return null;
        }

        private static void TrySavePersistentStretch(
            Dataset ds, int[] sourceBandIndexes, RasterRendererType type, StretchType stretchType,
            StretchParameters stretch)
        {
            if (!TryGetSourceInfo(ds, out var sourcePath, out var sourceLength, out var sourceModifiedUtcTicks))
                return;

            string entryKey = BuildPersistentEntryKey(sourceBandIndexes, type, stretchType);
            var entry = new PersistentStretchEntry
            {
                Key = entryKey,
                Bands = sourceBandIndexes,
                RendererType = type.ToString(),
                StretchType = stretchType.ToString(),
                Lo = stretch.Lo,
                Hi = stretch.Hi,
                DataMin = stretch.DataMin,
                DataMax = stretch.DataMax,
                HasNoData = stretch.HasNoData,
                NoDataValues = stretch.NoDataValues,
                Mean = stretch.Mean,
                StdDev = stretch.StdDev
            };

            foreach (var cachePath in GetPersistentCacheWritePaths(sourcePath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

                    PersistentStretchCache cache;
                    if (File.Exists(cachePath))
                    {
                        var existing = JsonSerializer.Deserialize<PersistentStretchCache>(File.ReadAllText(cachePath));
                        cache = existing ?? new PersistentStretchCache();
                    }
                    else
                    {
                        cache = new PersistentStretchCache();
                    }

                    if (cache.SourceLength != sourceLength ||
                        cache.SourceModifiedUtcTicks != sourceModifiedUtcTicks)
                    {
                        cache.Entries.Clear();
                    }

                    cache.Version = StretchCacheVersion;
                    cache.SourcePath = sourcePath;
                    cache.SourceLength = sourceLength;
                    cache.SourceModifiedUtcTicks = sourceModifiedUtcTicks;
                    cache.Entries.RemoveAll(e => e.Key == entryKey);
                    cache.Entries.Add(entry);

                    var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(cachePath, json);
                    return;
                }
                catch
                {
                    // Try the next cache path if the preferred cache path is not writable.
                }
            }
        }

        private static bool TryGetSourceInfo(
            Dataset ds, out string sourcePath, out long sourceLength, out long sourceModifiedUtcTicks)
        {
            sourcePath = ds.GetDescription();
            sourceLength = 0;
            sourceModifiedUtcTicks = 0;

            if (string.IsNullOrWhiteSpace(sourcePath))
                return false;

            try
            {
                sourcePath = Path.GetFullPath(sourcePath);
                if (!File.Exists(sourcePath))
                    return false;

                var fi = new FileInfo(sourcePath);
                sourceLength = fi.Length;
                sourceModifiedUtcTicks = fi.LastWriteTimeUtc.Ticks;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> GetPersistentCacheReadPaths(string sourcePath)
        {
            yield return GetLocalPersistentCachePath(sourcePath);

            // Backward compatibility for cache files created next to rasters by older builds.
            yield return sourcePath + ".mygis-cache.json";
        }

        private static IEnumerable<string> GetPersistentCacheWritePaths(string sourcePath)
        {
            yield return GetLocalPersistentCachePath(sourcePath);
        }

        private static string GetLocalPersistentCachePath(string sourcePath)
        {
            string localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyGIS", "RasterCache");

            string safeName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sourcePath)));
            return Path.Combine(localRoot, safeName + ".json");
        }

        private static string BuildPersistentEntryKey(
            int[] sourceBandIndexes, RasterRendererType type, StretchType stretchType)
        {
            return $"{type}|{stretchType}|{string.Join(",", sourceBandIndexes)}";
        }

        private static T[] Resize<T>(T[]? values, int length, T fallback)
        {
            var result = new T[length];
            for (int i = 0; i < length; i++)
                result[i] = values != null && i < values.Length ? values[i] : fallback;
            return result;
        }

        private sealed class PersistentStretchCache
        {
            [JsonPropertyName("version")]
            public int Version { get; set; } = StretchCacheVersion;
            [JsonPropertyName("sourcePath")]
            public string SourcePath { get; set; } = string.Empty;
            [JsonPropertyName("sourceLength")]
            public long SourceLength { get; set; }
            [JsonPropertyName("sourceModifiedUtcTicks")]
            public long SourceModifiedUtcTicks { get; set; }
            [JsonPropertyName("entries")]
            public List<PersistentStretchEntry> Entries { get; set; } = new();
        }

        private sealed class PersistentStretchEntry
        {
            [JsonPropertyName("key")]
            public string Key { get; set; } = string.Empty;
            [JsonPropertyName("bands")]
            public int[] Bands { get; set; } = Array.Empty<int>();
            [JsonPropertyName("rendererType")]
            public string RendererType { get; set; } = string.Empty;
            [JsonPropertyName("stretchType")]
            public string StretchType { get; set; } = string.Empty;
            [JsonPropertyName("lo")]
            public float[] Lo { get; set; } = Array.Empty<float>();
            [JsonPropertyName("hi")]
            public float[] Hi { get; set; } = Array.Empty<float>();
            [JsonPropertyName("dataMin")]
            public float[] DataMin { get; set; } = Array.Empty<float>();
            [JsonPropertyName("dataMax")]
            public float[] DataMax { get; set; } = Array.Empty<float>();
            [JsonPropertyName("hasNoData")]
            public bool[] HasNoData { get; set; } = Array.Empty<bool>();
            [JsonPropertyName("noDataValues")]
            public double[] NoDataValues { get; set; } = Array.Empty<double>();
            [JsonPropertyName("mean")]
            public float[] Mean { get; set; } = Array.Empty<float>();
            [JsonPropertyName("stdDev")]
            public float[] StdDev { get; set; } = Array.Empty<float>();
        }

        public static void ApplyStretch(StretchParameters p)
        {
            int bands = p.SortedValues.Length;
            var lo = new float[bands];
            var hi = new float[bands];
            var cdf = new byte[bands][];

            for (int b = 0; b < bands; b++)
            {
                var values = p.SortedValues[b];
                if (values.Count == 0)
                {
                    switch (p.Type)
                    {
                        case StretchType.MinMax:
                            lo[b] = p.DataMin.Length > b ? p.DataMin[b] : 0;
                            hi[b] = p.DataMax.Length > b ? p.DataMax[b] : 1;
                            break;
                        case StretchType.StdDev:
                        case StretchType.Sigmoid:
                            float m = p.Mean.Length > b ? p.Mean[b] : 0;
                            float s = p.StdDev.Length > b ? p.StdDev[b] : 0;
                            lo[b] = m - 2 * s;
                            hi[b] = m + 2 * s;
                            break;
                        case StretchType.PercentClip:
                        case StretchType.HistEq:
                            lo[b] = p.Lo.Length > b ? p.Lo[b] : 0;
                            hi[b] = p.Hi.Length > b ? p.Hi[b] : 1;
                            break;
                        default:
                            lo[b] = 0;
                            hi[b] = 1;
                            break;
                    }
                    if (hi[b] <= lo[b]) { lo[b] = 0; hi[b] = 1; }
                    cdf[b] = Array.Empty<byte>();
                    continue;
                }

                switch (p.Type)
                {
                    case StretchType.None:
                        lo[b] = 0;
                        hi[b] = 1;
                        break;
                    case StretchType.MinMax:
                        lo[b] = values[0];
                        hi[b] = values[^1];
                        break;
                    case StretchType.StdDev:
                        float m = p.Mean[b];
                        float s = p.StdDev[b];
                        lo[b] = m - 2 * s;
                        hi[b] = m + 2 * s;
                        break;
                    case StretchType.Sigmoid:
                        lo[b] = p.Mean[b] - 2 * p.StdDev[b];
                        hi[b] = p.Mean[b] + 2 * p.StdDev[b];
                        break;
                    case StretchType.HistEq:
                        lo[b] = values[0];
                        hi[b] = values[^1];
                        cdf[b] = BuildCdf(values);
                        break;
                    default: // PercentClip: trim tails to improve contrast.
                        int pLow = (int)(values.Count * PercentClipLow);
                        int pHigh = Math.Min(values.Count - 1, (int)(values.Count * PercentClipHigh));
                        lo[b] = values[pLow];
                        hi[b] = values[pHigh];
                        break;
                }

                if (hi[b] <= lo[b]) { lo[b] = values[0]; hi[b] = values[^1]; }
                if (hi[b] <= lo[b]) { lo[b] = 0; hi[b] = 1; }
                cdf[b] ??= Array.Empty<byte>();
            }

            p.Lo = lo;
            p.Hi = hi;
            p.Cdf = cdf;
        }

        private static byte[] BuildCdf(List<float> sortedValues)
        {
            var cdf = new byte[256];
            int n = sortedValues.Count;
            if (n == 0) return cdf;

            float vMin = sortedValues[0], vMax = sortedValues[^1];
            float range = vMax - vMin;
            if (range <= 0) { for (int i = 0; i < 256; i++) cdf[i] = (byte)i; return cdf; }

            // Build histogram with 256 bins
            var hist = new int[256];
            foreach (float v in sortedValues)
            {
                int bin = (int)((v - vMin) / range * 255);
                if (bin < 0) bin = 0;
                if (bin > 255) bin = 255;
                hist[bin]++;
            }

            // Cumulative histogram → 0-255 mapping
            int cumulative = 0;
            for (int i = 0; i < 256; i++)
            {
                cumulative += hist[i];
                cdf[i] = (byte)(cumulative * 255 / n);
            }

            return cdf;
        }

        // ---- Render ----

        public static byte[] RenderToPng(Dataset ds, int[] sourceBandIndexes,
            StretchParameters stretch, RasterRendererType type,
            int xOff, int yOff, int xSize, int ySize, int bufW, int bufH)
        {
            var rgba = RenderToRgba(ds, sourceBandIndexes, stretch, type,
                xOff, yOff, xSize, ySize, bufW, bufH);
            return RgbaToPng(rgba, bufW, bufH);
        }

        public static byte[] RenderToRgba(Dataset ds, int[] sourceBandIndexes,
            StretchParameters stretch, RasterRendererType type,
            int xOff, int yOff, int xSize, int ySize, int bufW, int bufH)
        {
            return type switch
            {
                RasterRendererType.Rgb => RenderRgbToRgba(ds, sourceBandIndexes, stretch,
                    xOff, yOff, xSize, ySize, bufW, bufH),
                RasterRendererType.Gray => RenderGrayToRgba(ds, sourceBandIndexes, stretch,
                    xOff, yOff, xSize, ySize, bufW, bufH),
                RasterRendererType.Palette => RenderPaletteToRgba(ds, sourceBandIndexes,
                    stretch.HasNoData.Length > 0 ? stretch.NoDataValues[0] : null,
                    stretch.HasNoData.Length > 0 && stretch.HasNoData[0],
                    xOff, yOff, xSize, ySize, bufW, bufH),
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
        }

        private static void SetGdalResampling(int bufW, int bufH, int xSize, int ySize, bool forceNearest = false)
        {
            bool oneToOne = bufW == xSize && bufH == ySize;
            Gdal.SetConfigOption("GDAL_RASTERIO_RESAMPLING", forceNearest || oneToOne ? "NEAREST" : "BILINEAR");
        }

        private static byte[] RenderRgbToRgba(Dataset ds, int[] sourceBandIndexes,
            StretchParameters stretch, int xOff, int yOff, int xSize, int ySize,
            int bufW, int bufH)
        {
            int displayBands = sourceBandIndexes.Length;
            if (displayBands <= 0)
                throw new InvalidOperationException("No display bands are selected for RGB rendering.");

            int total = bufW * bufH;

            SetGdalResampling(bufW, bufH, xSize, ySize);

            var allData = new float[total * displayBands];
            var isNoDataPixel = new bool[total];

            for (int b = 0; b < displayBands; b++)
            {
                var band = GetRequiredBand(ds, sourceBandIndexes[b]);
                var buf = new float[total];
                band.ReadRaster(xOff, yOff, xSize, ySize, buf, bufW, bufH, 0, 0);

                bool hasNd = b < stretch.HasNoData.Length && stretch.HasNoData[b];
                double ndv = b < stretch.NoDataValues.Length ? stretch.NoDataValues[b] : 0;

                for (int i = 0; i < total; i++)
                {
                    allData[i * displayBands + b] = buf[i];
                    if (b == 0 && IsNoDataValue(buf[i], hasNd, ndv))
                        isNoDataPixel[i] = true;
                }
            }

            var rgba = new byte[total * 4];
            bool isHistEq = stretch.Type == StretchType.HistEq;

            for (int ch = 0; ch < displayBands && ch < 4; ch++)
            {
                float lo = stretch.Lo.Length > ch ? stretch.Lo[ch] : 0;
                float hi = stretch.Hi.Length > ch ? stretch.Hi[ch] : 1;
                if (hi <= lo) { lo = 0; hi = 1; }
                float range = hi - lo;
                byte[] cdf = isHistEq && ch < stretch.Cdf.Length ? stretch.Cdf[ch] : Array.Empty<byte>();
                bool hasNd = ch < stretch.HasNoData.Length && stretch.HasNoData[ch];
                double ndv = ch < stretch.NoDataValues.Length ? stretch.NoDataValues[ch] : 0;
                float mean = ch < stretch.Mean.Length ? stretch.Mean[ch] : 0;
                float stddev = ch < stretch.StdDev.Length ? stretch.StdDev[ch] : 0;

                for (int i = 0; i < total; i++)
                {
                    float v = allData[i * displayBands + ch];
                    if (IsNoDataValue(v, hasNd, ndv))
                        continue;

                    rgba[i * 4 + ch] = isHistEq && cdf.Length > 0
                        ? ApplyHistEq(v, cdf, lo, range)
                        : ApplyStretchByte(v, lo, range, stretch.Type, mean, stddev);
                }
            }

            for (int i = 0; i < total; i++)
            {
                if (isNoDataPixel[i]) { rgba[i * 4 + 3] = 0; continue; }
                for (int ch = displayBands; ch < 3; ch++)
                    rgba[i * 4 + ch] = rgba[i * 4 + 0];
                if (displayBands < 4) rgba[i * 4 + 3] = 255;
            }

            return rgba;
        }

        private static byte[] RenderGrayToRgba(Dataset ds, int[] sourceBandIndexes, StretchParameters stretch,
            int xOff, int yOff, int xSize, int ySize, int bufW, int bufH)
        {
            SetGdalResampling(bufW, bufH, xSize, ySize);

            float lo = stretch.Lo.Length > 0 ? stretch.Lo[0] : 0;
            float hi = stretch.Hi.Length > 0 ? stretch.Hi[0] : 1;
            if (hi <= lo) { lo = 0; hi = 1; }
            float range = hi - lo;

            int total = bufW * bufH;
            var data = new float[total];
            int bandIndex = sourceBandIndexes.Length > 0 ? sourceBandIndexes[0] : 1;
            var band = GetRequiredBand(ds, bandIndex);
            band.ReadRaster(xOff, yOff, xSize, ySize, data, bufW, bufH, 0, 0);

            bool hasNd = stretch.HasNoData.Length > 0 && stretch.HasNoData[0];
            double ndv = stretch.NoDataValues.Length > 0 ? stretch.NoDataValues[0] : 0;

            bool isHistEq = stretch.Type == StretchType.HistEq;
            byte[] cdf = isHistEq && stretch.Cdf.Length > 0 ? stretch.Cdf[0] : Array.Empty<byte>();
            float mean = stretch.Mean.Length > 0 ? stretch.Mean[0] : 0;
            float stddev = stretch.StdDev.Length > 0 ? stretch.StdDev[0] : 0;

            bool useColorRamp = stretch.ColorRamp != ColorRampType.Gray;
            var rgba = new byte[total * 4];
            for (int i = 0; i < total; i++)
            {
                float v = data[i];
                if (IsNoDataValue(v, hasNd, ndv)) { rgba[i * 4 + 3] = 0; continue; }

                byte gray = isHistEq && cdf.Length > 0
                    ? ApplyHistEq(v, cdf, lo, range)
                    : ApplyStretchByte(v, lo, range, stretch.Type, mean, stddev);

                if (useColorRamp)
                {
                    var (cr, cg, cb) = ColorRamp.Sample(stretch.ColorRamp, gray / 255f);
                    rgba[i * 4 + 0] = cr;
                    rgba[i * 4 + 1] = cg;
                    rgba[i * 4 + 2] = cb;
                }
                else
                {
                    rgba[i * 4 + 0] = gray;
                    rgba[i * 4 + 1] = gray;
                    rgba[i * 4 + 2] = gray;
                }
                rgba[i * 4 + 3] = 255;
            }

            return rgba;
        }

        private static byte[] RenderPaletteToRgba(Dataset ds, int[] sourceBandIndexes,
            double? noDataValue, bool hasNoData,
            int xOff, int yOff, int xSize, int ySize, int bufW, int bufH)
        {
            SetGdalResampling(bufW, bufH, xSize, ySize, forceNearest: true);
            int total = bufW * bufH;
            var indices = new int[total];
            int bandIndex = sourceBandIndexes.Length > 0 ? sourceBandIndexes[0] : 1;
            var band = GetRequiredBand(ds, bandIndex);
            band.ReadRaster(xOff, yOff, xSize, ySize, indices, bufW, bufH, 0, 0);

            var colorTable = band.GetRasterColorTable();
            if (colorTable == null)
                throw new InvalidOperationException($"Raster band {bandIndex} has no color table.");

            var rgba = new byte[total * 4];
            int colorCount = colorTable.GetCount();

            for (int i = 0; i < total; i++)
            {
                int index = indices[i];
                if (hasNoData && noDataValue.HasValue && Math.Abs(index - noDataValue.Value) < 0.0001)
                { rgba[i * 4 + 3] = 0; continue; }
                if (index < 0 || index >= colorCount)
                { rgba[i * 4 + 3] = 0; continue; }

                var color = colorTable.GetColorEntry(index);
                rgba[i * 4 + 0] = (byte)Math.Clamp((int)color.c1, 0, 255);
                rgba[i * 4 + 1] = (byte)Math.Clamp((int)color.c2, 0, 255);
                rgba[i * 4 + 2] = (byte)Math.Clamp((int)color.c3, 0, 255);
                rgba[i * 4 + 3] = (byte)Math.Clamp((int)color.c4, 0, 255);
            }

            return rgba;
        }

        // ---- Pixel transforms ----

        private static byte ApplyStretchByte(float v, float lo, float range, StretchType type, float mean, float stddev)
        {
            if (type == StretchType.Sigmoid && stddev > 0.0001f)
                return SigmoidToByte(v, mean, stddev);
            if (range > 0.0001f)
            {
                float normalized = (v - lo) / range;
                return (byte)Math.Clamp(normalized * 255f, 0, 255);
            }
            return (type == StretchType.None) ? (byte)Math.Clamp(v * 255f, 0, 255) : (byte)128;
        }

        private static byte SigmoidToByte(float v, float mean, float stddev)
        {
            float gain = 6f;
            float x = gain * (v - mean) / stddev;
            float sig = 1f / (1f + MathF.Exp(-x));
            return (byte)Math.Clamp(sig * 255f, 0, 255);
        }

        private static byte ApplyHistEq(float v, byte[] cdf, float vMin, float range)
        {
            if (range <= 0) return (byte)Math.Clamp(v * 255f, 0, 255);
            int bin = (int)((v - vMin) / range * 255);
            if (bin < 0) bin = 0;
            if (bin > 255) bin = 255;
            return cdf[bin];
        }

        // ---- Helpers ----

        private static bool IsNoDataValue(float value, bool hasNoData, double noDataValue)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return true;
            return hasNoData && Math.Abs(value - noDataValue) < 0.0001;
        }

        private static byte[] RgbaToPng(byte[] rgba, int w, int h)
        {
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                var bmp = new SKBitmap();
                bmp.InstallPixels(info, handle.AddrOfPinnedObject(), w * 4);
                using var ms = new MemoryStream();
                bmp.Encode(ms, SKEncodedImageFormat.Png, 85);
                return ms.ToArray();
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
