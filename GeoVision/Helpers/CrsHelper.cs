using DotSpatial.Projections;
using OSGeo.OSR;

namespace GeoVision.Helpers
{
    public static class CoordinateConverter
    {
        /// <summary>
        /// Convert SphericalMercator (EPSG:3857) coordinates to WGS84 lon/lat (EPSG:4326).
        /// </summary>
        public static (double Lon, double Lat) ToLonLat(double x, double y, string sourceCrsStr)
        {
            int srcCode = ParseEpsgCode(sourceCrsStr);
            if (srcCode == 4326)
                return (x, y);

            // Prefer GDAL OSR (already loaded and working)
            try
            {
                using var srcSr = new SpatialReference("");
                srcSr.ImportFromEPSG(srcCode);
                using var dstSr = new SpatialReference("");
                dstSr.ImportFromEPSG(4326);

                using var transform = new OSGeo.OSR.CoordinateTransformation(srcSr, dstSr);
                double[] point = { x, y, 0 };
                transform.TransformPoint(point);
                return (point[0], point[1]);
            }
            catch
            {
                // Fallback: DotSpatial
                try
                {
                    var srcProj = ProjectionInfo.FromEpsgCode(srcCode);
                    var dstProj = ProjectionInfo.FromEpsgCode(4326);
                    var xy = new double[] { x, y };
                    Reproject.ReprojectPoints(xy, null, srcProj, dstProj, 0, 1);
                    return (xy[0], xy[1]);
                }
                catch
                {
                    return MercatorToLonLat(x, y);
                }
            }
        }

        /// <summary>
        /// Simple Spherical Mercator to WGS84 conversion (no library needed).
        /// </summary>
        public static (double Lon, double Lat) MercatorToLonLat(double x, double y)
        {
            double lon = x / 20037508.34 * 180;
            double lat = y / 20037508.34 * 180;
            lat = 180 / Math.PI * (2 * Math.Atan(Math.Exp(lat * Math.PI / 180)) - Math.PI / 2);
            return (lon, lat);
        }

        /// <summary>
        /// WGS84 lon/lat to SphericalMercator.
        /// </summary>
        public static (double X, double Y) LonLatToMercator(double lon, double lat)
        {
            double x = lon * 20037508.34 / 180;
            double y = Math.Log(Math.Tan((90 + lat) * Math.PI / 360)) / (Math.PI / 180);
            y = y * 20037508.34 / 180;
            return (x, y);
        }

        /// <summary>
        /// Parse EPSG code from CRS string (e.g. "EPSG:3857" → 3857).
        /// </summary>
        public static int ParseEpsgCode(string crs)
        {
            if (string.IsNullOrEmpty(crs)) return 4326;

            if (crs.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(crs.AsSpan(5), out int code))
                return code;

            return 4326;
        }

        /// <summary>
        /// Extract EPSG code string for comparison (e.g. "EPSG:4326").
        /// Returns empty string if no EPSG code found.
        /// </summary>
        public static string GetEpsgComparisonKey(string crs)
        {
            if (string.IsNullOrWhiteSpace(crs)) return "";

            string trimmed = crs.Trim();
            if (trimmed.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(trimmed.AsSpan(5), out int directCode))
            {
                return $"EPSG:{directCode}";
            }

            try
            {
                using var sr = new SpatialReference(trimmed);
                try { sr.AutoIdentifyEPSG(); } catch { }

                string?[] nodes = sr.IsProjected() != 0
                    ? [null, "PROJCS", "PROJCRS"]
                    : [null, "GEOGCS", "GEOGCRS"];
                foreach (string? node in nodes)
                {
                    string? authority = sr.GetAuthorityName(node);
                    string? code = sr.GetAuthorityCode(node);
                    if (!string.IsNullOrWhiteSpace(authority) &&
                        !string.IsNullOrWhiteSpace(code))
                    {
                        return $"{authority}:{code}".ToUpperInvariant();
                    }
                }
            }
            catch
            {
                // Non-WKT names are compared as normalized text below.
            }

            return trimmed.ToUpperInvariant();
        }

        /// <summary>
        /// Convert CRS identifier to human-readable coordinate system name.
        /// </summary>
        public static string GetCrsDisplayName(string crs)
        {
            if (string.IsNullOrEmpty(crs) || crs == "未知")
                return "未知";

            int code = ParseEpsgCode(crs);
            if (code != 4326 || crs.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
            {
                // Hardcoded fallback for common codes
                var known = GetKnownCrsName(code);
                if (known != null) return known;

                // Try DotSpatial
                try
                {
                    var info = ProjectionInfo.FromEpsgCode(code);
                    string name = info.Name ?? "";
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
                catch { }

                // Try GDAL OSR
                try
                {
                    using var sr = new SpatialReference("");
                    sr.ImportFromEPSG(code);
                    string? name = sr.GetName();
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
                catch { }
            }

            if (crs.Length > 60)
                return "自定义坐标系";
            return crs;
        }

        private static string? GetKnownCrsName(int code)
        {
            // UTM North (EPSG:32601-32660)
            if (code >= 32601 && code <= 32660)
                return $"WGS 84 / UTM zone {code - 32600}N";
            // UTM South (EPSG:32701-32760)
            if (code >= 32701 && code <= 32760)
                return $"WGS 84 / UTM zone {code - 32700}S";

            return code switch
            {
                4326 => "WGS 84 (经纬度)",
                3857 => "Web Mercator (米制)",
                4490 => "CGCS2000 地理坐标系",
                4479 => "CGCS2000 / 3° 高斯-克吕格",
                4214 => "Beijing 1954 地理坐标系",
                4610 => "Xian 1980 地理坐标系",
                4322 => "WGS 72",
                4269 => "NAD 83",
                4277 => "OSGB 1936",
                >= 4523 and <= 4549 => $"CGCS2000 / 3° 高斯-克吕格 {106 + (code - 4523) * 3}°E",
                >= 4491 and <= 4499 => $"CGCS2000 / 3° 高斯-克吕格",
                >= 4511 and <= 4533 => $"CGCS2000 / 6° 高斯-克吕格",
                >= 2349 and <= 2379 => $"Xian 1980 / 3° 高斯-克吕格",
                >= 4327 and <= 4339 => $"WGS 84 / 3° 高斯-克吕格",
                _ => null
            };
        }
    }
}
