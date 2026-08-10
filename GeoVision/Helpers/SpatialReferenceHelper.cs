using System.Globalization;
using OSGeo.OSR;

namespace GeoVision.Helpers
{
    public sealed record SpatialReferenceDetails(
        string CanonicalInput,
        string Wkt,
        string DisplayName,
        string Type,
        string Datum,
        string Unit,
        string? Authority,
        string? AuthorityCode)
    {
        public string Identifier =>
            !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(AuthorityCode)
                ? $"{Authority}:{AuthorityCode}"
                : "自定义坐标系";
    }

    public static class SpatialReferenceHelper
    {
        public static bool TryParse(
            string text,
            out SpatialReferenceDetails? details,
            out string error)
        {
            details = null;
            error = string.Empty;
            string normalized = NormalizeInput(text);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = "坐标系不能为空。";
                return false;
            }

            try
            {
                using var spatialReference = new SpatialReference("");
                if (spatialReference.SetFromUserInput(normalized) != 0)
                {
                    error = $"无法识别坐标系：{normalized}";
                    return false;
                }

                try { spatialReference.AutoIdentifyEPSG(); } catch { }

                string? authority = null;
                string? code = null;
                string?[] authorityNodes = spatialReference.IsProjected() != 0
                    ? [null, "PROJCS", "PROJCRS"]
                    : [null, "GEOGCS", "GEOGCRS"];
                foreach (string? node in authorityNodes)
                {
                    authority = spatialReference.GetAuthorityName(node);
                    code = spatialReference.GetAuthorityCode(node);
                    if (!string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(code))
                        break;
                }

                string canonical =
                    !string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(code)
                        ? $"{authority}:{code}".ToUpperInvariant()
                        : normalized;
                string type = spatialReference.IsProjected() != 0
                    ? "投影坐标系"
                    : spatialReference.IsGeographic() != 0
                        ? "地理坐标系"
                        : "其他坐标系";
                string name = spatialReference.GetName() ?? canonical;
                string datum = spatialReference.GetAttrValue("DATUM", 0)
                    ?? spatialReference.GetAttrValue("ENSEMBLE", 0)
                    ?? "未知";
                string unit = spatialReference.GetAttrValue("UNIT", 0) ?? "未知";
                spatialReference.ExportToWkt(out string wkt, Array.Empty<string>());

                details = new SpatialReferenceDetails(
                    canonical,
                    wkt,
                    name,
                    type,
                    datum,
                    unit,
                    authority,
                    code);
                return true;
            }
            catch (Exception ex)
            {
                error = $"无法解析坐标系：{ex.Message}";
                return false;
            }
        }

        public static string NormalizeInput(string text)
        {
            string normalized = text.Trim();
            return int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out int epsg)
                ? $"EPSG:{epsg}"
                : normalized;
        }

        public static bool AreSame(string first, string second)
        {
            try
            {
                using var firstReference = new SpatialReference("");
                using var secondReference = new SpatialReference("");
                if (firstReference.SetFromUserInput(first) != 0 ||
                    secondReference.SetFromUserInput(second) != 0)
                {
                    return false;
                }
                return firstReference.IsSame(secondReference, Array.Empty<string>()) != 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
