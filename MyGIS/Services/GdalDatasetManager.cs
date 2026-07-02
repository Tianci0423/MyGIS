using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using OSGeo.GDAL;

namespace MyGIS.Services
{
    public static class GdalDatasetManager
    {
        private static readonly ConcurrentDictionary<string, DatasetHandle> _datasets = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, object> _overviewBuildLocks = new(StringComparer.OrdinalIgnoreCase);

        public static DatasetHandle Open(string filePath, IProgress<(int percent, string label)>? progress = null)
        {
            var normalizedPath = Path.GetFullPath(filePath);

            while (true)
            {
                if (_datasets.TryGetValue(normalizedPath, out var existingHandle))
                {
                    lock (existingHandle.SyncRoot)
                    {
                        if (existingHandle.IsDisposed ||
                            !_datasets.TryGetValue(normalizedPath, out var current) ||
                            !ReferenceEquals(current, existingHandle))
                        {
                            continue;
                        }

                        existingHandle.RefCount++;
                        return existingHandle;
                    }
                }

                var buildLock = _overviewBuildLocks.GetOrAdd(normalizedPath, _ => new object());
                lock (buildLock)
                {
                    if (_datasets.TryGetValue(normalizedPath, out existingHandle))
                        continue;

                    var newHandle = CreateHandle(normalizedPath, progress);
                    newHandle.RefCount = 1;
                    if (_datasets.TryAdd(normalizedPath, newHandle))
                        return newHandle;

                    newHandle.DS.Dispose();
                }
            }
        }

        private static DatasetHandle CreateHandle(
            string normalizedPath,
            IProgress<(int percent, string label)>? progress)
        {
            string fileName = Path.GetFileName(normalizedPath);
            progress?.Report((2, $"正在打开 {fileName}"));

            var ds = OpenDataset(normalizedPath);
            int overviewCount = GetOverviewCount(ds);
            if (overviewCount == 0)
            {
                BuildOverviews(ds, progress, fileName, normalizedPath);

                // Reopen so GDAL reliably attaches a freshly-created external .ovr file.
                progress?.Report((90, $"正在重新打开 {fileName}"));
                ds.Dispose();
                ds = OpenDataset(normalizedPath);
                overviewCount = GetOverviewCount(ds);
            }
            else
            {
                progress?.Report((90, $"{fileName} 已有金字塔"));
            }

            return new DatasetHandle
            {
                DS = ds,
                FilePath = normalizedPath,
                OverviewCount = overviewCount
            };
        }

        public static DatasetHandle Register(string filePath, Dataset ds)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            var newHandle = new DatasetHandle
            {
                DS = ds,
                FilePath = normalizedPath,
                OverviewCount = GetOverviewCount(ds)
            };

            var existing = _datasets.GetOrAdd(normalizedPath, newHandle);
            if (existing != newHandle)
                ds.Dispose();

            return existing;
        }

        private static Dataset OpenDataset(string normalizedPath)
        {
            var ds = Gdal.Open(normalizedPath, Access.GA_ReadOnly);
            if (ds == null)
                throw new InvalidDataException($"GDAL cannot open file: {normalizedPath}");

            return ds;
        }

        private static void BuildOverviews(
            Dataset ds,
            IProgress<(int percent, string label)>? progress,
            string fileName,
            string sourcePath)
        {
            try
            {
                int maxDim = Math.Max(ds.RasterXSize, ds.RasterYSize);
                var levels = new List<int>();
                int level = 2;
                while (maxDim / level >= 128)
                {
                    levels.Add(level);
                    level *= 2;
                }

                if (levels.Count == 0)
                    levels.Add(2);

                progress?.Report((5, $"正在构建金字塔 {fileName}"));
                int callbackPercent = 5;
                using var monitorCancel = new CancellationTokenSource();
                var monitorTask = Task.Run(() => MonitorOverviewProgress(
                    sourcePath,
                    ds.RasterXSize,
                    ds.RasterYSize,
                    ds.RasterCount,
                    GetDatasetSampleType(ds),
                    levels,
                    progress,
                    fileName,
                    () => Volatile.Read(ref callbackPercent),
                    monitorCancel.Token));

                Gdal.GDALProgressFuncDelegate? callback = null;
                callback = (complete, message, data) =>
                {
                    int percent = 5 + (int)Math.Round(Math.Clamp(complete, 0.0, 1.0) * 83.0);
                    Interlocked.Exchange(ref callbackPercent, percent);
                    progress?.Report((percent, $"正在构建金字塔 {fileName}"));
                    return 1;
                };

                try
                {
                    ds.BuildOverviews("AVERAGE", levels.ToArray(), callback, "BuildOverviews");
                }
                finally
                {
                    monitorCancel.Cancel();
                    try { monitorTask.Wait(1000); } catch { }
                }

                progress?.Report((88, $"金字塔构建完成 {fileName}"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Overview build failed: {ex.Message}");
            }
        }

        private static void MonitorOverviewProgress(
            string sourcePath,
            int rasterWidth,
            int rasterHeight,
            int bandCount,
            DataType dataType,
            IReadOnlyList<int> levels,
            IProgress<(int percent, string label)>? progress,
            string fileName,
            Func<int> callbackPercentProvider,
            CancellationToken token)
        {
            if (progress == null)
                return;

            string overviewPath = sourcePath + ".ovr";
            long estimatedOverviewBytes = EstimateOverviewBytes(rasterWidth, rasterHeight, bandCount, dataType, levels);
            var sw = Stopwatch.StartNew();
            int lastReported = 5;

            while (!token.WaitHandle.WaitOne(350))
            {
                int callbackPercent = callbackPercentProvider();
                int filePercent = EstimateFileProgress(overviewPath, estimatedOverviewBytes);
                int timePercent = EstimateTimeProgress(sw.Elapsed);
                int percent = Math.Max(callbackPercent, Math.Max(filePercent, timePercent));
                percent = Math.Clamp(percent, 5, 86);

                if (percent <= lastReported)
                    continue;

                lastReported = percent;
                progress.Report((percent, $"正在构建金字塔 {fileName}"));
            }
        }

        private static int EstimateFileProgress(string overviewPath, long estimatedOverviewBytes)
        {
            if (estimatedOverviewBytes <= 0 || !File.Exists(overviewPath))
                return 5;

            long currentBytes;
            try
            {
                currentBytes = new FileInfo(overviewPath).Length;
            }
            catch
            {
                return 5;
            }

            double estimatedCompressedBytes = Math.Max(1, estimatedOverviewBytes * 0.65);
            double ratio = Math.Clamp(currentBytes / estimatedCompressedBytes, 0.0, 1.0);
            return 5 + (int)Math.Round(ratio * 81.0);
        }

        private static int EstimateTimeProgress(TimeSpan elapsed)
        {
            // Fallback for GDAL paths that buffer writes or emit only coarse progress callbacks.
            double ratio = 1.0 - Math.Exp(-elapsed.TotalSeconds / 35.0);
            return 5 + (int)Math.Round(Math.Clamp(ratio, 0.0, 1.0) * 75.0);
        }

        private static long EstimateOverviewBytes(
            int rasterWidth,
            int rasterHeight,
            int bandCount,
            DataType dataType,
            IReadOnlyList<int> levels)
        {
            double bytes = 0;
            int bytesPerSample = BytesPerSample(dataType);
            foreach (int level in levels)
            {
                double w = Math.Ceiling(rasterWidth / (double)level);
                double h = Math.Ceiling(rasterHeight / (double)level);
                bytes += w * h * bandCount * bytesPerSample;
            }

            return bytes >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(bytes);
        }

        private static DataType GetDatasetSampleType(Dataset ds)
        {
            try
            {
                return ds.GetRasterBand(1)?.DataType ?? DataType.GDT_Byte;
            }
            catch
            {
                return DataType.GDT_Byte;
            }
        }

        private static int BytesPerSample(DataType dataType)
        {
            return dataType switch
            {
                DataType.GDT_Byte => 1,
                DataType.GDT_Int16 or DataType.GDT_UInt16 or DataType.GDT_CInt16 => 2,
                DataType.GDT_Int32 or DataType.GDT_UInt32 or DataType.GDT_Float32 or DataType.GDT_CInt32 => 4,
                DataType.GDT_Float64 or DataType.GDT_CFloat32 => 8,
                DataType.GDT_CFloat64 => 16,
                _ => 4
            };
        }

        private static int GetOverviewCount(Dataset ds)
        {
            try
            {
                return ds.GetRasterBand(1)?.GetOverviewCount() ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        public static void Release(DatasetHandle handle)
        {
            if (handle == null) return;

            lock (handle.SyncRoot)
            {
                if (handle.IsDisposed)
                    return;

                handle.RefCount--;
                if (handle.RefCount > 0)
                    return;

                if (_datasets.TryGetValue(handle.FilePath, out var current) &&
                    ReferenceEquals(current, handle))
                {
                    _datasets.TryRemove(handle.FilePath, out _);
                }

                handle.IsDisposed = true;
                handle.DS?.Dispose();
            }
        }

        public static void DisposeAll()
        {
            foreach (var kvp in _datasets)
                kvp.Value.DS?.Dispose();
            _datasets.Clear();
        }

        public static int CachedCount => _datasets.Count;

        public class DatasetHandle : IDisposable
        {
            internal object SyncRoot { get; } = new();
            internal Dataset DS { get; init; } = null!;
            public string FilePath { get; init; } = string.Empty;
            public int RefCount;
            public int OverviewCount;
            internal bool IsDisposed;

            public void Dispose() => GdalDatasetManager.Release(this);
        }
    }
}
