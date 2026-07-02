using System.Text.Json.Serialization;

namespace MyGIS.Models
{
    public class ProjectFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("layers")]
        public List<LayerEntry> Layers { get; set; } = new();
    }

    public class LayerEntry
    {
        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("isVisible")]
        public bool IsVisible { get; set; } = true;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "unknown"; // "raster" or "vector"

        // Raster-specific
        [JsonPropertyName("stretchType")]
        public string? StretchType { get; set; }

        [JsonPropertyName("colorRamp")]
        public string? ColorRamp { get; set; }

        [JsonPropertyName("bandIndexes")]
        public int[]? BandIndexes { get; set; }

        [JsonPropertyName("rendererType")]
        public string? RendererType { get; set; }
    }
}
