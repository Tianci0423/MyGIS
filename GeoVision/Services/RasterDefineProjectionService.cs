using System.Buffers;
using System.IO;
using OSGeo.GDAL;

namespace GeoVision.Services
{
    public sealed record RasterDefineProjectionRequest(
        string InputPath,
        string OutputPath,
        string TargetCrs,
        string TargetWkt,
        bool InPlace,
        bool LoadResult);

    public static class RasterDefineProjectionService
    {
        private const int BufferSize = 8 * 1024 * 1024;

        public static async Task RunAsync(
            RasterDefineProjectionRequest request,
            IProgress<int>? progress = null)
        {
            string targetPath = request.InPlace ? request.InputPath : request.OutputPath;
            string? directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("输出路径没有有效的文件夹。");
            Directory.CreateDirectory(directory);

            string tempPath = Path.Combine(
                directory,
                $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.define.tmp.tif");
            string backupPath = tempPath + ".backup";
            try
            {
                await CopyWithProgressAsync(request.InputPath, tempPath, progress);

                await Task.Run(() =>
                {
                    using var dataset = Gdal.Open(tempPath, Access.GA_Update)
                        ?? throw new InvalidDataException("GDAL 无法以更新模式打开临时影像。");
                    if (dataset.SetProjection(request.TargetWkt) != CPLErr.CE_None)
                        throw new InvalidOperationException("GDAL 写入坐标系定义失败。");
                    dataset.FlushCache();
                });
                progress?.Report(95);

                if (File.Exists(targetPath))
                {
                    File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
                    TryDelete(backupPath);
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }

                CopyOrRemoveMask(request.InputPath, targetPath);
                progress?.Report(100);
            }
            catch
            {
                TryDelete(tempPath);
                TryDelete(backupPath);
                throw;
            }
        }

        private static async Task CopyWithProgressAsync(
            string sourcePath,
            string targetPath,
            IProgress<int>? progress)
        {
            long totalBytes = new FileInfo(sourcePath).Length;
            long copiedBytes = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                await using var source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var target = new FileStream(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length));
                    if (read == 0)
                        break;
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    copiedBytes += read;
                    int percent = totalBytes <= 0
                        ? 90
                        : Math.Clamp((int)(copiedBytes * 90L / totalBytes), 0, 90);
                    progress?.Report(percent);
                }
                await target.FlushAsync();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static void CopyOrRemoveMask(string sourcePath, string targetPath)
        {
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                return;

            string sourceMask = sourcePath + ".msk";
            string targetMask = targetPath + ".msk";
            if (File.Exists(sourceMask))
                File.Copy(sourceMask, targetMask, overwrite: true);
            else
                TryDelete(targetMask);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
