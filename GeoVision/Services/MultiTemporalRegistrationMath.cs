namespace GeoVision.Services
{
    public enum RegistrationTransformModel
    {
        Translation,
        Similarity,
        Affine
    }

    public sealed record RegistrationTiePoint(
        double MovingX,
        double MovingY,
        double ReferenceX,
        double ReferenceY);

    public sealed record RegistrationFitResult(
        double A,
        double B,
        double C,
        double D,
        double Tx,
        double Ty,
        double[] GeoTransform,
        IReadOnlyList<(double Dx, double Dy, double Distance)> Residuals,
        double Rmse);

    public static class MultiTemporalRegistrationMath
    {
        public static int MinimumPointCount(RegistrationTransformModel model) => model switch
        {
            RegistrationTransformModel.Translation => 1,
            RegistrationTransformModel.Similarity => 2,
            RegistrationTransformModel.Affine => 3,
            _ => 3
        };

        public static RegistrationFitResult Fit(
            RegistrationTransformModel model,
            IReadOnlyList<RegistrationTiePoint> points,
            IReadOnlyList<double> movingGeoTransform)
        {
            int minimum = MinimumPointCount(model);
            if (points.Count < minimum)
                throw new ArgumentException($"{ModelName(model)}模型至少需要 {minimum} 组有效控制点。");
            if (movingGeoTransform.Count != 6)
                throw new ArgumentException("待配准影像的仿射变换必须包含 6 个参数。");

            double a, b, c, d, tx, ty;
            switch (model)
            {
                case RegistrationTransformModel.Translation:
                    a = d = 1;
                    b = c = 0;
                    tx = points.Average(p => p.ReferenceX - p.MovingX);
                    ty = points.Average(p => p.ReferenceY - p.MovingY);
                    break;

                case RegistrationTransformModel.Similarity:
                    FitSimilarity(points, out a, out b, out c, out d, out tx, out ty);
                    break;

                default:
                    FitAffine(points, out a, out b, out c, out d, out tx, out ty);
                    break;
            }

            double[] g = movingGeoTransform.ToArray();
            double[] corrected =
            [
                a * g[0] + b * g[3] + tx,
                a * g[1] + b * g[4],
                a * g[2] + b * g[5],
                c * g[0] + d * g[3] + ty,
                c * g[1] + d * g[4],
                c * g[2] + d * g[5]
            ];

            var residuals = new List<(double Dx, double Dy, double Distance)>(points.Count);
            double sumSquared = 0;
            foreach (var point in points)
            {
                double fittedX = a * point.MovingX + b * point.MovingY + tx;
                double fittedY = c * point.MovingX + d * point.MovingY + ty;
                double dx = point.ReferenceX - fittedX;
                double dy = point.ReferenceY - fittedY;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                residuals.Add((dx, dy, distance));
                sumSquared += distance * distance;
            }

            return new RegistrationFitResult(
                a, b, c, d, tx, ty, corrected, residuals,
                Math.Sqrt(sumSquared / points.Count));
        }

        private static void FitSimilarity(
            IReadOnlyList<RegistrationTiePoint> points,
            out double a, out double b, out double c, out double d,
            out double tx, out double ty)
        {
            double sx = points.Average(p => p.MovingX);
            double sy = points.Average(p => p.MovingY);
            double rx = points.Average(p => p.ReferenceX);
            double ry = points.Average(p => p.ReferenceY);
            double numeratorA = 0;
            double numeratorB = 0;
            double denominator = 0;

            foreach (var p in points)
            {
                double x = p.MovingX - sx;
                double y = p.MovingY - sy;
                double u = p.ReferenceX - rx;
                double v = p.ReferenceY - ry;
                numeratorA += x * u + y * v;
                numeratorB += x * v - y * u;
                denominator += x * x + y * y;
            }

            if (denominator <= 1e-20)
                throw new InvalidOperationException("控制点分布退化，无法计算相似变换。请选取位置不同的控制点。");

            a = numeratorA / denominator;
            double rotationTerm = numeratorB / denominator;
            b = -rotationTerm;
            c = rotationTerm;
            d = a;
            tx = rx - a * sx - b * sy;
            ty = ry - c * sx - d * sy;
        }

        private static void FitAffine(
            IReadOnlyList<RegistrationTiePoint> points,
            out double a, out double b, out double c, out double d,
            out double tx, out double ty)
        {
            double sx = points.Average(p => p.MovingX);
            double sy = points.Average(p => p.MovingY);
            double rx = points.Average(p => p.ReferenceX);
            double ry = points.Average(p => p.ReferenceY);
            double xx = 0, xy = 0, yy = 0;
            double xu = 0, yu = 0, xv = 0, yv = 0;

            foreach (var p in points)
            {
                double x = p.MovingX - sx;
                double y = p.MovingY - sy;
                double u = p.ReferenceX - rx;
                double v = p.ReferenceY - ry;
                xx += x * x;
                xy += x * y;
                yy += y * y;
                xu += x * u;
                yu += y * u;
                xv += x * v;
                yv += y * v;
            }

            double determinant = xx * yy - xy * xy;
            double scale = Math.Max(1, xx * yy);
            if (Math.Abs(determinant) <= scale * 1e-12)
                throw new InvalidOperationException("控制点接近共线，无法计算仿射变换。请在影像不同方位选取控制点。");

            a = (xu * yy - yu * xy) / determinant;
            b = (yu * xx - xu * xy) / determinant;
            c = (xv * yy - yv * xy) / determinant;
            d = (yv * xx - xv * xy) / determinant;
            tx = rx - a * sx - b * sy;
            ty = ry - c * sx - d * sy;
        }

        public static string ModelName(RegistrationTransformModel model) => model switch
        {
            RegistrationTransformModel.Translation => "平移",
            RegistrationTransformModel.Similarity => "相似",
            RegistrationTransformModel.Affine => "仿射",
            _ => "配准"
        };
    }
}
