using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using OSGeo.GDAL;

namespace GeoVision.Services
{
    public static class GdalDatasetManager
    {
        private const int GdalOpenRasterFlag = 0x02;
        private const string OverviewBuildMarkerSuffix = ".ovr.geovision-building";
        private static readonly ConcurrentDictionary<string, DatasetHandle> _datasets = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, object> _datasetOpenLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<int, Process> _overviewWorkers = new();
        private static int _shuttingDown;

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

                var openLock = _datasetOpenLocks.GetOrAdd(normalizedPath, _ => new object());
                lock (openLock)
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

            bool ignoreExternalOverviews = RecoverInterruptedOverviewBuild(normalizedPath, progress, fileName);
            var ds = OpenDataset(normalizedPath, ignoreExternalOverviews);
            int overviewCount = GetOverviewCount(ds);
            if (overviewCount > 0 && !HasUsableExistingOverviews(ds))
            {
                progress?.Report((3, $"检测到无效金字塔，正在修复 {fileName}"));
                ds.Dispose();
                ignoreExternalOverviews = !TryDeleteOverviewArtifacts(normalizedPath);
                ds = OpenDataset(normalizedPath, ignoreExternalOverviews);
                overviewCount = GetOverviewCount(ds);
            }

            if (overviewCount == 0)
            {
                ds.Dispose();
                bool built = !ignoreExternalOverviews &&
                             BuildOverviewsAtomically(normalizedPath, progress, fileName);
                ds = OpenDataset(normalizedPath, !built && ignoreExternalOverviews);
                overviewCount = GetOverviewCount(ds);

                if (built && (overviewCount == 0 || !HasUsableExistingOverviews(ds)))
                {
                    ds.Dispose();
                    TryDeleteOverviewArtifacts(normalizedPath);
                    ds = OpenDataset(normalizedPath, true);
                    overviewCount = 0;
                }

                progress?.Report(overviewCount > 0
                    ? (90, $"金字塔构建完成 {fileName}")
                    : (90, $"金字塔未完成，使用原始影像 {fileName}"));
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

        private static Dataset OpenDataset(string normalizedPath, bool ignoreOverviews = false)
        {
            var ds = ignoreOverviews
                ? Gdal.OpenEx(
                    normalizedPath,
                    GdalOpenRasterFlag,
                    null,
                    ["OVERVIEW_LEVEL=NONE"],
                    null)
                : Gdal.Open(normalizedPath, Access.GA_ReadOnly);
            if (ds == null)
                throw new InvalidDataException($"GDAL cannot open file: {normalizedPath}");

            return ds;
        }

        private static bool BuildOverviewsAtomically(
            string sourcePath,
            IProgress<(int percent, string label)>? progress,
            string fileName)
        {
            if (Volatile.Read(ref _shuttingDown) != 0)
                return false;

            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot locate the GeoVision executable.");
            string token = Guid.NewGuid().ToString("N");
            string tempVrtPath = sourcePath + $".geovision-overview-{token}.vrt";
            string finalOverviewPath = sourcePath + ".ovr";
            string progressPath = tempVrtPath + ".progress";
            string errorPath = progressPath + ".error";
            string tempOverviewPath = tempVrtPath + ".ovr";

            CleanupStaleOverviewTemps(sourcePath, finalOverviewPath + ".geovision.lock");

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add(OverviewBuildWorker.Command);
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add(tempVrtPath);
            startInfo.ArgumentList.Add(finalOverviewPath);
            startInfo.ArgumentList.Add(progressPath);

            Process? worker = null;
            try
            {
                progress?.Report((5, $"正在构建金字塔 {fileName}"));
                worker = Process.Start(startInfo);
                if (worker == null)
                    return false;

                _overviewWorkers[worker.Id] = worker;
                int workerPercent = 0;
                int lastReportedPercent = -1;
                long lastTempLength = -1;
                TimeSpan lastCpu = TimeSpan.Zero;
                DateTime lastActivityUtc = DateTime.UtcNow;
                long sourceLength = TryGetFileLength(sourcePath);
                ulong initialReadBytes = TryGetProcessReadBytes(worker, out ulong readBytesAtStart)
                    ? readBytesAtStart
                    : 0;

                while (!worker.WaitForExit(300))
                {
                    if (Volatile.Read(ref _shuttingDown) != 0)
                    {
                        TryKill(worker);
                        return false;
                    }

                    if (TryReadWorkerProgress(progressPath, out int reportedWorkerPercent))
                    {
                        workerPercent = Math.Max(workerPercent, reportedWorkerPercent);
                    }

                    long tempLength = TryGetFileLength(tempOverviewPath);
                    worker.Refresh();
                    TimeSpan cpu = worker.TotalProcessorTime;
                    int ioPercent = EstimateReadProgress(worker, initialReadBytes, sourceLength);
                    int effectivePercent = Math.Max(workerPercent, ioPercent);
                    if (effectivePercent > lastReportedPercent)
                    {
                        lastReportedPercent = effectivePercent;
                        lastActivityUtc = DateTime.UtcNow;
                        int displayPercent = 5 + (int)Math.Round(Math.Clamp(effectivePercent, 0, 100) * 0.83);
                        progress?.Report((displayPercent, $"正在构建金字塔 {fileName}"));
                    }

                    if (tempLength != lastTempLength || cpu > lastCpu)
                    {
                        lastTempLength = tempLength;
                        lastCpu = cpu;
                        lastActivityUtc = DateTime.UtcNow;
                    }

                    if (DateTime.UtcNow - lastActivityUtc > TimeSpan.FromMinutes(2))
                    {
                        TryKill(worker);
                        progress?.Report((88, $"金字塔构建无响应，使用原始影像 {fileName}"));
                        return false;
                    }
                }

                if (worker.ExitCode == 0 && File.Exists(finalOverviewPath))
                    return true;

                if (File.Exists(errorPath))
                    Debug.WriteLine($"Overview worker error: {File.ReadAllText(errorPath)}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not build overview atomically: {ex}");
                if (worker != null)
                    TryKill(worker);
                return false;
            }
            finally
            {
                if (worker != null)
                {
                    _overviewWorkers.TryRemove(worker.Id, out _);
                    worker.Dispose();
                }

                OverviewBuildWorker.TryDelete(progressPath);
                OverviewBuildWorker.TryDelete(progressPath + ".tmp");
                OverviewBuildWorker.TryDelete(errorPath);
                OverviewBuildWorker.TryDelete(tempVrtPath);
                OverviewBuildWorker.TryDelete(tempVrtPath + ".aux.xml");
                OverviewBuildWorker.TryDelete(tempOverviewPath);
                OverviewBuildWorker.TryDelete(tempOverviewPath + ".aux.xml");
            }
        }

        private static bool TryReadWorkerProgress(string path, out int percent)
        {
            percent = 0;
            try
            {
                return File.Exists(path) && int.TryParse(File.ReadAllText(path), out percent);
            }
            catch
            {
                return false;
            }
        }

        private static int EstimateReadProgress(Process process, ulong initialReadBytes, long sourceLength)
        {
            if (sourceLength <= 0 || !TryGetProcessReadBytes(process, out ulong currentReadBytes) ||
                currentReadBytes <= initialReadBytes)
                return 1;

            double ratio = (currentReadBytes - initialReadBytes) / (double)sourceLength;
            return 1 + (int)Math.Round(Math.Clamp(ratio, 0d, 1d) * 64d);
        }

        private static bool TryGetProcessReadBytes(Process process, out ulong readBytes)
        {
            readBytes = 0;
            try
            {
                if (!GetProcessIoCounters(process.Handle, out var counters))
                    return false;

                readBytes = counters.ReadTransferCount;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CleanupStaleOverviewTemps(string sourcePath, string lockPath)
        {
            FileStream? cleanupLock = null;
            try
            {
                cleanupLock = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                string? directory = Path.GetDirectoryName(sourcePath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                    return;

                string prefix = Path.GetFileName(sourcePath) + ".geovision-overview-";
                foreach (string path in Directory.EnumerateFiles(directory, prefix + "*"))
                    OverviewBuildWorker.TryDelete(path);
            }
            catch (IOException)
            {
                // Another GeoVision instance is actively building this overview.
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                cleanupLock?.Dispose();
            }
        }

        private static long TryGetFileLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessIoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(
            IntPtr processHandle,
            out ProcessIoCounters counters);

        private static bool HasUsableExistingOverviews(Dataset ds)
        {
            try
            {
                if (ds.RasterCount <= 0)
                    return false;

                using var firstBand = ds.GetRasterBand(1);
                int overviewCount = firstBand?.GetOverviewCount() ?? 0;
                if (firstBand == null || overviewCount <= 0)
                    return false;

                for (int bandIndex = 1; bandIndex <= ds.RasterCount; bandIndex++)
                {
                    using var band = ds.GetRasterBand(bandIndex);
                    if (band == null || band.GetOverviewCount() != overviewCount)
                        return false;

                    int previousWidth = band.XSize;
                    int previousHeight = band.YSize;
                    for (int overviewIndex = 0; overviewIndex < overviewCount; overviewIndex++)
                    {
                        using var overview = band.GetOverview(overviewIndex);
                        if (overview == null || overview.XSize <= 0 || overview.YSize <= 0 ||
                            overview.XSize > previousWidth || overview.YSize > previousHeight ||
                            (overview.XSize == previousWidth && overview.YSize == previousHeight))
                            return false;

                        previousWidth = overview.XSize;
                        previousHeight = overview.YSize;
                    }
                }

                if (!OverviewHasPlausibleSamples(firstBand, 0))
                    return false;

                return overviewCount == 1 || OverviewHasPlausibleSamples(firstBand, overviewCount - 1);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Overview validation failed: {ex.Message}");
                return false;
            }
        }

        private static bool OverviewHasPlausibleSamples(Band sourceBand, int overviewIndex)
        {
            using var overview = sourceBand.GetOverview(overviewIndex);
            if (overview == null)
                return false;

            sourceBand.GetNoDataValue(out double noData, out int hasNoData);
            int sourceSignalSamples = 0;
            int blackOverviewSamples = 0;
            float[] sourceValue = new float[1];
            float[] overviewValue = new float[1];
            double[] fractions = [0.2, 0.5, 0.8];

            foreach (double yFraction in fractions)
            {
                foreach (double xFraction in fractions)
                {
                    int overviewX = Math.Clamp((int)Math.Round((overview.XSize - 1) * xFraction), 0, overview.XSize - 1);
                    int overviewY = Math.Clamp((int)Math.Round((overview.YSize - 1) * yFraction), 0, overview.YSize - 1);
                    int sourceX = Math.Clamp((int)Math.Round((overviewX + 0.5) * sourceBand.XSize / overview.XSize - 0.5), 0, sourceBand.XSize - 1);
                    int sourceY = Math.Clamp((int)Math.Round((overviewY + 0.5) * sourceBand.YSize / overview.YSize - 0.5), 0, sourceBand.YSize - 1);

                    sourceBand.ReadRaster(sourceX, sourceY, 1, 1, sourceValue, 1, 1, 0, 0);
                    overview.ReadRaster(overviewX, overviewY, 1, 1, overviewValue, 1, 1, 0, 0);

                    float sourceSample = sourceValue[0];
                    float overviewSample = overviewValue[0];
                    if (!float.IsFinite(sourceSample) || !float.IsFinite(overviewSample) ||
                        (hasNoData != 0 && Math.Abs(sourceSample - noData) < 1e-6))
                        continue;

                    if (Math.Abs(sourceSample) <= 1e-6)
                        continue;

                    sourceSignalSamples++;
                    if (Math.Abs(overviewSample) <= 1e-12)
                        blackOverviewSamples++;
                }
            }

            return sourceSignalSamples < 3 || blackOverviewSamples < Math.Ceiling(sourceSignalSamples * 0.8);
        }

        private static bool RecoverInterruptedOverviewBuild(
            string sourcePath,
            IProgress<(int percent, string label)>? progress,
            string fileName)
        {
            string markerPath = GetOverviewBuildMarkerPath(sourcePath);
            if (!File.Exists(markerPath))
                return false;

            progress?.Report((1, $"正在清理未完成的金字塔 {fileName}"));
            bool removed = TryDeleteOverviewArtifacts(sourcePath);
            if (removed)
                TryDeleteFile(markerPath);

            return !removed;
        }

        private static void DeleteOverviewArtifacts(string sourcePath)
        {
            string[] paths =
            [
                sourcePath + ".ovr",
                sourcePath + ".ovr.aux.xml",
                sourcePath + ".msk.ovr",
                sourcePath + ".msk.ovr.aux.xml"
            ];

            foreach (string path in paths)
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static bool TryDeleteOverviewArtifacts(string sourcePath)
        {
            try
            {
                DeleteOverviewArtifacts(sourcePath);
                return true;
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"Overview is locked and will be ignored: {ex.Message}");
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"Overview cannot be removed and will be ignored: {ex.Message}");
                return false;
            }
        }

        private static string GetOverviewBuildMarkerPath(string sourcePath)
            => sourcePath + OverviewBuildMarkerSuffix;

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not delete file '{path}': {ex.Message}");
            }
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
            Interlocked.Exchange(ref _shuttingDown, 1);
            foreach (var worker in _overviewWorkers.Values)
                TryKill(worker);
            _overviewWorkers.Clear();

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
