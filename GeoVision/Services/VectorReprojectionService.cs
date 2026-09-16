using System.IO;
using OSGeo.OGR;
using OSGeo.OSR;

namespace GeoVision.Services
{
    public sealed record VectorReprojectionRequest(
        string InputPath,
        string OutputPath,
        string TargetCrs);

    public static class VectorReprojectionService
    {
        public static Task RunAsync(
            VectorReprojectionRequest request,
            IProgress<(int percent, string label)>? progress = null)
        {
            return Task.Run(() => Run(request, progress));
        }

        private static void Run(
            VectorReprojectionRequest request,
            IProgress<(int percent, string label)>? progress)
        {
            string input = Path.GetFullPath(request.InputPath);
            string output = Path.GetFullPath(request.OutputPath);
            if (!File.Exists(input))
                throw new FileNotFoundException("输入矢量文件不存在。", input);
            if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("输出文件不能覆盖输入矢量文件。");

            string? directory = Path.GetDirectoryName(output);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("输出路径没有有效的文件夹。");
            Directory.CreateDirectory(directory);

            Ogr.RegisterAll();
            using var source = Ogr.Open(input, 0)
                ?? throw new InvalidDataException("OGR 无法打开输入矢量文件。");
            if (source.GetLayerCount() <= 0)
                throw new InvalidDataException("输入矢量文件没有图层。");

            using var sourceLayer = source.GetLayerByIndex(0)
                ?? throw new InvalidDataException("无法读取输入矢量图层。");
            using var sourceSrs = sourceLayer.GetSpatialRef();
            if (sourceSrs == null)
                throw new InvalidDataException("输入矢量图层没有坐标系定义。");

            using var targetSrs = new SpatialReference("");
            if (targetSrs.SetFromUserInput(request.TargetCrs) != 0)
                throw new InvalidDataException($"无法识别目标坐标系：{request.TargetCrs}");
            using var transform = new CoordinateTransformation(sourceSrs, targetSrs);

            string? outputDriverName = Path.GetExtension(output).Equals(".geojson", StringComparison.OrdinalIgnoreCase)
                ? "GeoJSON"
                : "GeoJSON";
            using var driver = Ogr.GetDriverByName(outputDriverName)
                ?? throw new InvalidOperationException("当前 GDAL 环境没有 GeoJSON 输出驱动。");
            if (File.Exists(output))
                driver.DeleteDataSource(output);

            using var target = driver.CreateDataSource(output, null)
                ?? throw new InvalidOperationException("无法创建输出 GeoJSON 文件。");
            using var targetLayer = target.CreateLayer(
                sourceLayer.GetName() ?? "layer", targetSrs, sourceLayer.GetGeomType(), null)
                ?? throw new InvalidOperationException("无法创建输出矢量图层。");

            FeatureDefn sourceDefinition = sourceLayer.GetLayerDefn();
            for (int index = 0; index < sourceDefinition.GetFieldCount(); index++)
            {
                using var field = sourceDefinition.GetFieldDefn(index);
                if (field != null)
                    targetLayer.CreateField(field, 1);
            }

            long total = Math.Max(1, sourceLayer.GetFeatureCount(1));
            long processed = 0;
            sourceLayer.ResetReading();
            Feature? sourceFeature;
            while ((sourceFeature = sourceLayer.GetNextFeature()) != null)
            {
                using (sourceFeature)
                {
                    using var geometry = sourceFeature.GetGeometryRef()?.Clone();
                    if (geometry == null)
                        continue;
                    if (geometry.Transform(transform) != 0)
                        throw new InvalidOperationException("矢量几何坐标转换失败。");

                    using var targetFeature = new Feature(targetLayer.GetLayerDefn());
                    for (int index = 0; index < sourceDefinition.GetFieldCount(); index++)
                        targetFeature.SetField(index, sourceFeature.GetFieldAsString(index));
                    targetFeature.SetGeometry(geometry);
                    if (targetLayer.CreateFeature(targetFeature) != 0)
                        throw new IOException("写入输出矢量要素失败。");
                }

                processed++;
                progress?.Report(((int)Math.Clamp(processed * 100 / total, 0, 100),
                    $"正在转换矢量要素 {processed}/{total}"));
            }

            target.FlushCache();
            progress?.Report((100, "矢量重投影完成"));
        }
    }
}
