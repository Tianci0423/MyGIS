using System.Diagnostics;
using System.Globalization;
using System.IO;
using OSGeo.GDAL;

namespace GeoVision.Services
{
    internal static class OverviewBuildWorker
    {
        internal const string Command = "--geovision-build-overview";
        private const string Resampling = "AVERAGE";

        internal static bool IsWorkerCommand(string[] args)
            => args.Length == 5 && string.Equals(args[0], Command, StringComparison.Ordinal);

        internal static int Run(string[] args)
        {
            string sourcePath = Path.GetFullPath(args[1]);
            string tempVrtPath = Path.GetFullPath(args[2]);
            string finalOverviewPath = Path.GetFullPath(args[3]);
            string progressPath = Path.GetFullPath(args[4]);
            string lockPath = finalOverviewPath + ".geovision.lock";
            string tempOverviewPath = tempVrtPath + ".ovr";

            FileStream? buildLock = null;
            try
            {
                buildLock = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                Gdal.SetConfigOption("GDAL_NUM_THREADS", "2");
                Gdal.SetConfigOption("SPARSE_OK_OVERVIEW", "OFF");
                WriteProgress(progressPath, 1);

                using var source = OpenRequired(sourcePath);
                var levels = CreateOverviewLevels(source.RasterXSize, source.RasterYSize);
                CreateTemporaryVrt(source, tempVrtPath);

                using (var vrt = OpenRequired(tempVrtPath))
                {
                    Gdal.GDALProgressFuncDelegate callback = (complete, message, data) =>
                    {
                        int percent = 2 + (int)Math.Round(Math.Clamp(complete, 0d, 1d) * 94d);
                        WriteProgress(progressPath, percent);
                        return 1;
                    };

                    int result = vrt.BuildOverviews(Resampling, levels.ToArray(), callback, "GeoVisionOverviewWorker");
                    if (result != (int)CPLErr.CE_None)
                        throw new IOException($"GDAL BuildOverviews failed with code {result}.");
                }

                WriteProgress(progressPath, 97);
                ValidateTemporaryOverview(
                    tempOverviewPath,
                    source.RasterXSize,
                    source.RasterYSize,
                    source.RasterCount,
                    levels.Count);

                if (File.Exists(finalOverviewPath))
                    throw new IOException($"Overview destination already exists: {finalOverviewPath}");

                File.Move(tempOverviewPath, finalOverviewPath);
                WriteProgress(progressPath, 100);
                return 0;
            }
            catch (IOException ex) when (buildLock == null)
            {
                Debug.WriteLine($"Overview worker could not acquire lock: {ex.Message}");
                return 3;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Overview worker failed: {ex}");
                TryWriteError(progressPath, ex.Message);
                return 2;
            }
            finally
            {
                buildLock?.Dispose();
                TryDelete(tempVrtPath);
                TryDelete(tempVrtPath + ".aux.xml");
                TryDelete(tempOverviewPath);
                TryDelete(tempOverviewPath + ".aux.xml");
                TryDelete(lockPath);
            }
        }

        private static void CreateTemporaryVrt(Dataset source, string tempVrtPath)
        {
            var driver = Gdal.GetDriverByName("VRT")
                ?? throw new InvalidOperationException("GDAL VRT driver is unavailable.");
            using var vrt = driver.CreateCopy(
                tempVrtPath,
                source,
                0,
                Array.Empty<string>(),
                null,
                null);
            if (vrt == null)
                throw new IOException("Could not create temporary VRT dataset.");
        }

        private static Dataset OpenRequired(string path)
            => Gdal.Open(path, Access.GA_ReadOnly)
               ?? throw new InvalidDataException($"GDAL cannot open file: {path}");

        private static List<int> CreateOverviewLevels(int width, int height)
        {
            int maxDimension = Math.Max(width, height);
            var levels = new List<int>();
            for (int level = 2; maxDimension / level >= 128; level *= 2)
                levels.Add(level);

            if (levels.Count == 0)
                levels.Add(2);
            return levels;
        }

        private static void ValidateTemporaryOverview(
            string overviewPath,
            int sourceWidth,
            int sourceHeight,
            int sourceBandCount,
            int expectedLevelCount)
        {
            var info = new FileInfo(overviewPath);
            if (!info.Exists || info.Length < 4096)
                throw new InvalidDataException("Temporary overview file is missing or incomplete.");

            using var overview = OpenRequired(overviewPath);
            int expectedWidth = Math.Max(1, (int)Math.Ceiling(sourceWidth / 2d));
            int expectedHeight = Math.Max(1, (int)Math.Ceiling(sourceHeight / 2d));
            if (overview.RasterCount != sourceBandCount ||
                Math.Abs(overview.RasterXSize - expectedWidth) > 1 ||
                Math.Abs(overview.RasterYSize - expectedHeight) > 1)
                throw new InvalidDataException("Temporary overview dimensions or band count are invalid.");

            using var firstBand = overview.GetRasterBand(1);
            int totalLevels = 1 + (firstBand?.GetOverviewCount() ?? 0);
            if (firstBand == null || totalLevels < expectedLevelCount)
                throw new InvalidDataException("Temporary overview does not contain every requested level.");

            var probe = new float[1];
            firstBand.ReadRaster(
                Math.Max(0, firstBand.XSize / 2),
                Math.Max(0, firstBand.YSize / 2),
                1,
                1,
                probe,
                1,
                1,
                0,
                0);
        }

        private static void WriteProgress(string path, int percent)
        {
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, percent.ToString(CultureInfo.InvariantCulture));
            File.Move(tempPath, path, true);
        }

        private static void TryWriteError(string path, string message)
        {
            try
            {
                File.WriteAllText(path + ".error", message);
            }
            catch
            {
            }
        }

        internal static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
