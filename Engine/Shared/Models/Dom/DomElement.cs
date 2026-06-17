using Newtonsoft.Json;

namespace SnapStak.Wasm.Client.Models.Dom;

/// <summary>
/// A single element from content.js DOM serialisation.
/// JsonProperty names MUST match exactly what content.js sends in the entry object.
/// </summary>
public sealed class DomElement
{
    [JsonProperty("internalId")]    public string  InternalId   { get; set; } = string.Empty;
    [JsonProperty("parentId")]      public string? ParentId     { get; set; }
    [JsonProperty("tag")]           public string  Tag          { get; set; } = "div";
    [JsonProperty("tagName")]       public string? TagName      { get; set; }
    [JsonProperty("textContent")]   public string? TextContent  { get; set; }
    [JsonProperty("className")]     public string? ClassName    { get; set; }
    [JsonProperty("ariaLabel")]     public string? AriaLabel    { get; set; }
    [JsonProperty("role")]          public string? Role         { get; set; }
    [JsonProperty("segmentId")]     public string? SegmentId    { get; set; }
    [JsonProperty("rect")]          public DomRect Rect         { get; set; } = new();
    [JsonProperty("cssProps")]      public Dictionary<string, string>? CssProps { get; set; }

    // Image — content.js emits "src" for main snapshots, "imgSrc" for hidden components.
    // Both deserialise into the same backing field via two JsonProperty setters.
    [JsonProperty("src")]
    public string? ImgSrc
    {
        get => _imgSrc;
        set { if (!string.IsNullOrWhiteSpace(value)) _imgSrc = value; }
    }
    [JsonProperty("imgSrc")]
    public string? ImgSrcAlt
    {
        get => null;
        set { if (!string.IsNullOrWhiteSpace(value)) _imgSrc = value; }
    }
    private string? _imgSrc;

    public string? ResolvedImgSrc => _imgSrc;

    [JsonProperty("svgDataUri")]    public string? SvgDataUri      { get; set; }
    [JsonProperty("componentType")] public string? ComponentType   { get; set; }
    [JsonProperty("label")]         public string? Label           { get; set; }
    [JsonProperty("href")]          public string? Href            { get; set; }
    [JsonProperty("borderRadiusPx")]public double  BorderRadiusPx  { get; set; }
    [JsonProperty("hidden")]        public bool    Hidden          { get; set; }
    [JsonProperty("responsive")]    public bool    Responsive      { get; set; }

    // Browser-measured text wrap lines (Range API).
    [JsonProperty("textWrapLines")]        public List<string>? TextWrapLines       { get; set; }
    [JsonProperty("textWrapLineWidths")]   public List<double>? TextWrapLineWidths  { get; set; }
    [JsonProperty("textWrapContainerW")]   public double        TextWrapContainerW  { get; set; }
    [JsonProperty("textWrapContainerH")]   public double        TextWrapContainerH  { get; set; }

    public List<DomElement> Children { get; set; } = new();
}

public sealed class PictureSource
{
    [JsonProperty("srcset")] public string? Srcset { get; set; }
    [JsonProperty("sizes")]  public string? Sizes  { get; set; }
    [JsonProperty("media")]  public string? Media  { get; set; }
    [JsonProperty("type")]   public string? Type   { get; set; }
}

public sealed class DomRect
{
    [JsonProperty("x")]      public double X      { get; set; }
    [JsonProperty("y")]      public double Y      { get; set; }
    [JsonProperty("width")]  public double Width  { get; set; }
    [JsonProperty("height")] public double Height { get; set; }
    [JsonProperty("top")]    public double Top    { get; set; }
    [JsonProperty("left")]   public double Left   { get; set; }
}
