using System;
using System.Collections.Generic;
using Newtonsoft.Json;

// ─── Exact JSON structure as specified ───────────────────────────────────────

[Serializable]
public class ConceptMapFile
{
    public CanvasData canvas;
    public MapInfo    map;
}

[Serializable]
public class MapInfo
{
    public string cmid;
    public string direction;
}

[Serializable]
public class CanvasData
{
    public List<ConceptData>     concepts         = new();
    public List<LinkData>        links            = new();
    public List<LinkTargetData>  linktargets      = new();
    public List<object>          concepts_ext     = new();
    public List<object>          links_ext        = new();
    public List<object>          linktargets_ext  = new();
}

[Serializable]
public class ConceptData
{
    public string cid;
    public string cmid;
    public string label;
    public float  x;
    public float  y;
    public string data;

    // Parsed from data field
    [JsonIgnore] public NodeVisualData Visual => ParseVisual();
    private NodeVisualData ParseVisual()
    {
        try { return JsonConvert.DeserializeObject<NodeVisualData>(data ?? "{}"); }
        catch { return new NodeVisualData(); }
    }
}

[Serializable]
public class LinkData
{
    public string lid;
    public string cmid;
    public string label;
    public float  x;
    public float  y;
    public string data;
    public string source_cid;
    public string source_cmid;
    public string source_data;

    [JsonIgnore] public NodeVisualData Visual => ParseVisual();
    private NodeVisualData ParseVisual()
    {
        try { return JsonConvert.DeserializeObject<NodeVisualData>(data ?? "{}"); }
        catch { return new NodeVisualData(); }
    }
}

[Serializable]
public class LinkTargetData
{
    public string lid;
    public string cmid;
    public string target_cid;
    public string target_cmid;
    public string target_data;
}

[Serializable]
public class NodeVisualData
{
    public string color            = "#000000";
    [JsonProperty("background-color")]
    public string backgroundColor  = "#FFBF40";
    public float  width            = 70;
    public float  height           = 22;
    public string type             = "concept";
    public int?   limit;            // null = tanpa batas target
}
