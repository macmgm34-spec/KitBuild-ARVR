using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;
using Newtonsoft.Json;

/// <summary>
/// Master manager for the AR Concept Map.
/// Handles: JSON load/save, node spawning, AR placement, drag-to-connect.
/// </summary>
public class ConceptMapManager : MonoBehaviour
{
    // ─── AR Foundation ────────────────────────────
    [Header("AR")]
    public ARRaycastManager  arRaycast;
    public ARPlaneManager    arPlanes;
    public Camera            arCamera;

    // ─── Prefabs ──────────────────────────────────
    [Header("Prefabs")]
    public GameObject conceptNodePrefab;
    public GameObject linkNodePrefab;
    public GameObject connectionLinePrefab;
    public GameObject arrowHeadPrefab;
    public GameObject previewLinePrefab;   // simple LineRenderer for drag preview

    // ─── UI ───────────────────────────────────────
    [Header("UI Buttons")]
    public UnityEngine.UI.Button btnAddConcept;
    public UnityEngine.UI.Button btnAddLink;
    public UnityEngine.UI.Button btnReset;
    public UnityEngine.UI.Button btnSave;
    public TextMeshProUGUI       statusText;
    public TextMeshProUGUI       trackingText;

    // ─── Runtime state ────────────────────────────
    private ConceptMapFile mapFile;
    private Dictionary<string, ConceptNode> conceptObjects = new();
    private Dictionary<string, LinkNode>    linkObjects    = new();
    private List<ConnectionLine>            connections    = new();

    // Placement mode
    public enum Mode { None, PlaceConcept, PlaceLink }
    private Mode currentMode = Mode.None;
    private string pendingId;   // cid or lid being placed

    // Connection drag state
    private bool     isDragging;
    private MapNode  dragFromNode;
    private MapNode  dragCandidate;   // konsep tempat garis preview "snap"
    private int      dragPointerId;   // pointer (tangan) pemilik drag koneksi

    // Tap di ruang kosong → lepas seleksi
    private int           emptyTapId = int.MinValue;
    private PointerSample emptyTapBegin;
    private LineRenderer previewLR;
    private Material     previewSolidMat, previewDashedMat;
    private GameObject   previewArrow, previewDot;   // ujung garis drag (▲ target / ● source)
    private bool         dragSourceHandle;
    private static readonly List<ARRaycastHit> arHits = new();

    // Counter for new node IDs
    private AppToolbar toolbar;

    private int cCounter = 0;
    private int lCounter = 0;

    // Pinch-to-scale
    private float  nodeScale     = 1.0f;
    private float  lastPinchDist = -1f;
    private const float MIN_SCALE = 0.3f;
    private const float MAX_SCALE = 3.0f;

    private const float XR_MAP_DIST     = 1.4f;
    private const float XR_MAP_WIDTH    = 1.4f;
    private const float XR_MAP_HEIGHT   = 0.8f;
    private const float XR_PLACE_DIST   = 1.2f;
    private const float XR_PICK_DEGREES = 6f;

    // ─── Undo / Redo ──────────────────────────────
    private class UndoRecord
    {
        public string description;
        public Action undo;
        public Action redo;
    }
    private readonly Stack<UndoRecord> undoStack = new();
    private readonly Stack<UndoRecord> redoStack = new();

    void PushUndo(string description, Action undo, Action redo)
    {
        undoStack.Push(new UndoRecord { description = description, undo = undo, redo = redo });
        redoStack.Clear(); // aksi baru menghapus jalur redo
    }

    public bool CanUndo => undoStack.Count > 0;
    public bool CanRedo => redoStack.Count > 0;

    public void Undo()
    {
        if (undoStack.Count == 0) { ShowStatus("[!] Tidak ada yang bisa di-undo"); return; }
        var rec = undoStack.Pop();
        rec.undo();
        redoStack.Push(rec);
        ShowStatus($"[Undo] {rec.description}");
    }

    public void Redo()
    {
        if (redoStack.Count == 0) { ShowStatus("[!] Tidak ada yang bisa di-redo"); return; }
        var rec = redoStack.Pop();
        rec.redo();
        undoStack.Push(rec);
        ShowStatus($"[Redo] {rec.description}");
    }

    // ──────────────────────────────────────────────
    void Start()
    {
        // Background putih polos — matikan passthrough kamera AR (spt versi web).
        // AR plane detection & tracking tetap jalan di belakang layar, hanya
        // gambar kamera yang tak dirender.
        if (arCamera != null && !PointerInput.IsXR)   // VR: latar diatur XRSupport (transparan)
        {
            var camBg = arCamera.GetComponent<ARCameraBackground>();
            if (camBg != null) camBg.enabled = false;
            arCamera.clearFlags       = CameraClearFlags.SolidColor;
            arCamera.backgroundColor  = Color.white;
        }

        // Button listeners
        btnAddConcept.onClick.AddListener(StartAddConcept);
        btnAddLink   .onClick.AddListener(StartAddLink);
        btnReset     .onClick.AddListener(ResetMap);
        btnSave      .onClick.AddListener(SaveToFile);

        // Preview line
        if (previewLinePrefab != null)
            previewLR = Instantiate(previewLinePrefab).GetComponent<LineRenderer>();
        if (previewLR == null)
            previewLR = new GameObject("PreviewLine").AddComponent<LineRenderer>();

        // PAKSA setting di kode — prefab bisa punya width default 1m (kotak raksasa!)
        previewLR.useWorldSpace  = true;
        previewLR.positionCount  = 2;
        previewLR.startWidth     = 0.008f;
        previewLR.endWidth       = 0.008f;
        previewLR.numCapVertices = 4;
        previewLR.textureMode    = LineTextureMode.Tile;
        previewLR.textureScale   = new Vector2(30f, 1f);   // ±3.3cm per segmen putus-putus
        previewSolidMat  = new Material(Shader.Find("Sprites/Default"));
        previewDashedMat = new Material(Shader.Find("Sprites/Default")) { mainTexture = MakeDashTexture() };
        previewLR.sharedMaterial = previewSolidMat;
        previewLR.enabled        = false;

        // Ujung garis drag seperti VIRTUAL-EDGE kbx: segitiga (kanan) / lingkaran (kiri)
        previewArrow = ConnectionLine.CreateArrowHead(previewLR.transform, Color.white);
        previewDot   = ConnectionLine.CreateEndDot(previewLR.transform, Color.white);
        previewArrow.SetActive(false);
        previewDot.SetActive(false);

        // Sembunyikan visualisasi AR plane
        if (arPlanes != null)
        {
            arPlanes.planesChanged += OnPlanesChanged;
            // Hide planes yang sudah ada
            foreach (var plane in arPlanes.trackables)
                SetPlaneVisible(plane, false);
        }

        // Data loading is handled by ApiLoader → ApplyMapJson()
        mapFile = new ConceptMapFile { canvas = new CanvasData(),
                                       map    = new MapInfo { direction = "multi" } };
    }

    // Sembunyikan mesh AR plane saat terdeteksi
    void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        foreach (var plane in args.added)
            SetPlaneVisible(plane, false);
        foreach (var plane in args.updated)
            SetPlaneVisible(plane, false);
    }

    void SetPlaneVisible(ARPlane plane, bool visible)
    {
        foreach (var mr in plane.GetComponentsInChildren<MeshRenderer>())
            mr.enabled = visible;
        foreach (var lr in plane.GetComponentsInChildren<LineRenderer>())
            lr.enabled = visible;
    }

    void Update()
    {
        UpdateTrackingStatus();
        HandlePlacementTap();
        HandleEmptyTap();
        HandlePinchZoom();
    }

    void HandleEmptyTap()
    {
        if (currentMode != Mode.None) return;
        foreach (var p in PointerInput.Samples)
        {
            if (p.phase == TouchPhase.Began)
            {
                bool onNode = Physics.Raycast(p.ray, out RaycastHit hit)
                           && hit.transform.GetComponentInParent<MapNode>() != null;
                if (!p.overUI && !onNode && !isDragging && !MapNode.GestureActive)
                { emptyTapId = p.id; emptyTapBegin = p; }
            }
            else if (p.id == emptyTapId)
            {
                if (p.phase == TouchPhase.Moved && PointerInput.BeyondDragThreshold(emptyTapBegin, p))
                    emptyTapId = int.MinValue;
                else if (p.phase == TouchPhase.Ended)
                {
                    emptyTapId = int.MinValue;
                    MapNode.DeselectAll();
                }
                else if (p.phase == TouchPhase.Canceled) emptyTapId = int.MinValue;
            }
        }
    }

    // ── Pinch to scale semua node ─────────────────
    void HandlePinchZoom()
    {
        // Saat menyeret node, thumbstick dipakai untuk menjauh/mendekat — bukan skala
        float axis = MapNode.GestureActive ? 0f : PointerInput.ScaleAxis;
        if (Mathf.Abs(axis) > 0.2f)
        {
            nodeScale = Mathf.Clamp(nodeScale + axis * Time.deltaTime, MIN_SCALE, MAX_SCALE);
            ApplyScaleToAll();
        }

        if (Input.touchCount != 2) { lastPinchDist = -1f; return; }

        Touch t0 = Input.GetTouch(0);
        Touch t1 = Input.GetTouch(1);
        float dist = Vector2.Distance(t0.position, t1.position);

        if (lastPinchDist < 0f) { lastPinchDist = dist; return; }

        float delta = dist - lastPinchDist;
        lastPinchDist = dist;

        nodeScale = Mathf.Clamp(nodeScale + delta * 0.001f, MIN_SCALE, MAX_SCALE);
        ApplyScaleToAll();
    }

    void ApplyScaleToAll()
    {
        // Kalikan basis auto-size tiap node (bukan skala fix) agar teks tetap pas
        foreach (var node in MapNode.All)
            if (node) node.transform.localScale = node.AutoBaseScale * nodeScale;
    }

    // ── Tracking status label ─────────────────────
    // Canvas dibiarkan bersih (tanpa teks status) — hanya tombol yang tampil.
    void UpdateTrackingStatus()
    {
        if (trackingText != null && trackingText.gameObject.activeSelf)
            trackingText.gameObject.SetActive(false);
    }

    // ─────────────────────────────────────────────
    //  JSON LOAD
    // ─────────────────────────────────────────────
    public void ApplyMapJson(string json)
    {
        if (PointerInput.IsXR && !XRSupport.Instance.HeadTracked)
        {
            StartCoroutine(ApplyMapJsonWhenTracked(json));
            return;
        }
        ApplyMapJsonNow(json);
    }

    IEnumerator ApplyMapJsonWhenTracked(string json)
    {
        float waited = 0f;
        while (!XRSupport.Instance.HeadTracked && waited < 3f)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        ApplyMapJsonNow(json);
    }

    void ApplyMapJsonNow(string json)
    {
        try
        {
            mapFile = JsonConvert.DeserializeObject<ConceptMapFile>(json);
            RebuildScene();
            ShowStatus($"[OK] Dimuat — {mapFile.canvas.concepts.Count} konsep, {mapFile.canvas.links.Count} relasi");
            undoStack.Clear(); redoStack.Clear();
        }
        catch (Exception e)
        {
            ShowStatus("[ERROR] JSON error: " + e.Message);
        }
    }

    void RebuildScene()
    {
        ClearScene();

        var canvas = mapFile.canvas;

        // Auto-fit coordinates to a 2m × 1.2m AR plane in front of camera
        var allNodes = new List<(float x, float y)>();
        canvas.concepts.ForEach(c => allNodes.Add((c.x, c.y)));
        canvas.links   .ForEach(l => allNodes.Add((l.x, l.y)));

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var (x, y) in allNodes)
        {
            minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
            minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
        }
        float rangeX = Mathf.Max(maxX - minX, 1f);
        float rangeY = Mathf.Max(maxY - minY, 1f);

        // Fit ke FOV kamera: seluruh peta langsung terlihat tanpa pinch-zoom.
        // Dihitung dari sudut pandang kamera → otomatis benar di portrait & landscape.
        float DIST = 1.0f;                             // jarak peta dari kamera
        float vFov   = arCamera.fieldOfView * Mathf.Deg2Rad;
        float worldH = 2f * DIST * Mathf.Tan(vFov * 0.5f) * 0.65f;   // 65% tinggi layar
        float worldW = worldH * arCamera.aspect * 0.9f;               // 90% lebar layar
        if (PointerInput.IsXR)
        {
            DIST   = XR_MAP_DIST;
            worldH = XR_MAP_HEIGHT;
            worldW = XR_MAP_WIDTH;
        }

        float scaleX = worldW / rangeX, scaleY = worldH / rangeY;
        float scale  = Mathf.Min(scaleX, scaleY);

        GetViewBasis(out Vector3 camPos, out Vector3 fwd, out Vector3 right, out Vector3 up);
        Vector3 origin = camPos
                       + fwd   * DIST
                       - right * worldW * 0.5f
                       - up    * worldH * 0.5f;

        Vector3 WorldPos(float x, float y)
        {
            float nx = (x - minX) * scale;
            float ny = (y - minY) * scale;
            // Flip Y (screen Y down, Unity Y up)
            return origin + right * nx + up * (worldH - ny);
        }

        // Spawn concepts
        foreach (var c in canvas.concepts)
        {
            cCounter = Mathf.Max(cCounter, ParseIdNum(c.cid));
            SpawnConcept(c, WorldPos(c.x, c.y));
        }

        // Spawn links
        foreach (var l in canvas.links)
        {
            lCounter = Mathf.Max(lCounter, ParseIdNum(l.lid));
            SpawnLink(l, WorldPos(l.x, l.y));
        }

        // Draw connections
        DrawAllConnections();
    }

    static int ParseIdNum(string id)
    {
        var digits = System.Text.RegularExpressions.Regex.Replace(id ?? "", @"\D", "");
        return int.TryParse(digits, out int n) ? n : 0;
    }

    // ─────────────────────────────────────────────
    //  SPAWN NODES
    // ─────────────────────────────────────────────
    void SpawnConcept(ConceptData data, Vector3 worldPos)
    {
        var go   = Instantiate(conceptNodePrefab, worldPos, Quaternion.identity);
        var node = go.GetComponent<ConceptNode>();
        node.Setup(data);
        conceptObjects[data.cid] = node;
    }

    void SpawnLink(LinkData data, Vector3 worldPos)
    {
        var go   = Instantiate(linkNodePrefab, worldPos, Quaternion.identity);
        var node = go.GetComponent<LinkNode>();
        node.Setup(data);
        linkObjects[data.lid] = node;
    }

    // ─────────────────────────────────────────────
    //  DRAW CONNECTIONS
    // ─────────────────────────────────────────────
    void DrawAllConnections()
    {
        // Clear old
        connections.ForEach(c => { if (c) Destroy(c.gameObject); });
        connections.Clear();

        foreach (var lk in mapFile.canvas.links)
        {
            // Source edge: concept → link
            if (!string.IsNullOrEmpty(lk.source_cid)
                && conceptObjects.TryGetValue(lk.source_cid, out var srcNode)
                && linkObjects.TryGetValue(lk.lid, out var lkNode))
            {
                AddConnectionLine(srcNode.transform, lkNode.transform, ConnectionLine.ConnectionType.Source, lk.lid, lk.source_cid);
            }
        }

        // Target edges: link → concept(s)
        foreach (var lt in mapFile.canvas.linktargets)
        {
            if (linkObjects.TryGetValue(lt.lid, out var lkNode)
                && conceptObjects.TryGetValue(lt.target_cid, out var tgtNode))
            {
                AddConnectionLine(lkNode.transform, tgtNode.transform, ConnectionLine.ConnectionType.Target, lt.lid, lt.target_cid);
            }
        }

        // Badge sisa target di setiap relasi (drawLinkRemaining di kbx)
        foreach (var kv in linkObjects)
        {
            int limit = GetLinkLimit(kv.Key);
            kv.Value.SetRemaining(limit == int.MaxValue ? 0 : limit - TargetCount(kv.Key));
        }

        MapNode.RefreshSelectedHandles();
    }

    void AddConnectionLine(Transform from, Transform to, ConnectionLine.ConnectionType type, string lid, string cid)
    {
        var go  = Instantiate(connectionLinePrefab);
        var cl  = go.GetComponent<ConnectionLine>();
        cl.Init(from, to, type, IsBidirectional);
        cl.Lid       = lid;
        cl.ConceptId = cid;
        connections.Add(cl);
    }

    // ─────────────────────────────────────────────
    //  ADD CONCEPT / LINK (toolbar buttons)
    // ─────────────────────────────────────────────
    void StartAddConcept()
    {
        currentMode = Mode.PlaceConcept;
        pendingId   = "c" + (++cCounter);
        ShowStatus("Tap permukaan untuk letakkan Konsep");
    }

    void StartAddLink()
    {
        currentMode = Mode.PlaceLink;
        pendingId   = "l" + (++lCounter);
        ShowStatus("Tap permukaan untuk letakkan Relasi");
    }

    void HandlePlacementTap()
    {
        if (currentMode == Mode.None) return;

        foreach (var p in PointerInput.Samples)
        {
            if (p.phase != TouchPhase.Ended) continue;

            // Don't place if touch is on UI
            if (p.overUI) continue;

            Vector3 worldPos;

            // Try AR raycast against detected planes
            if (arRaycast.Raycast(p.ray, arHits, TrackableType.PlaneWithinPolygon))
            {
                worldPos = arHits[0].pose.position;
            }
            else
            {
                // Fallback: 1.5m in front of camera
                worldPos = p.xr
                    ? p.ray.GetPoint(XR_PLACE_DIST)
                    : arCamera.transform.position + arCamera.transform.forward * 1.5f;
            }

            PlaceNodeAt(worldPos);
            return;
        }
    }

    void PlaceNodeAt(Vector3 worldPos)
    {
        if (currentMode == Mode.PlaceConcept)
        {
            var data = new ConceptData
            {
                cid   = pendingId,
                label = "Konsep " + cCounter,
                x     = 0, y = 0,
                data  = "{\"color\":\"#000000\",\"background-color\":\"#FFBF40\",\"width\":70,\"height\":22,\"type\":\"concept\"}"
            };
            mapFile.canvas.concepts.Add(data);
            SpawnConcept(data, worldPos);
            ShowStatus($"[OK] Konsep '{data.label}' ditambahkan");
        }
        else if (currentMode == Mode.PlaceLink)
        {
            var data = new LinkData
            {
                lid        = pendingId,
                label      = "relasi " + lCounter,
                x          = 0, y = 0,
                source_cid = null,
                data       = "{\"color\":\"#000000\",\"background-color\":\"#DEDEDE\",\"width\":60,\"height\":22,\"type\":\"link\",\"limit\":9}"
            };
            mapFile.canvas.links.Add(data);
            SpawnLink(data, worldPos);
            DrawAllConnections();   // tampilkan badge sisa target relasi baru
            ShowStatus($"[OK] Relasi '{data.label}' ditambahkan");
        }

        currentMode = Mode.None;
        pendingId   = null;
    }

    // ─────────────────────────────────────────────
    //  DRAG-TO-CONNECT  (called by MapNode)
    // ─────────────────────────────────────────────
    /// Peta bidirectional (map.direction "bi") → semua ujung koneksi berupa lingkaran
    public bool IsBidirectional => mapFile?.map?.direction == "bi";

    /// false bila pointer lain (tangan satunya) sedang menarik koneksi
    public bool BeginConnectionDrag(MapNode fromNode, PointerSample pointer, bool sourceHandle)
    {
        if (isDragging) return false;
        isDragging       = true;
        dragPointerId    = pointer.id;
        dragFromNode     = fromNode;
        dragCandidate    = null;
        dragSourceHandle = sourceHandle;

        // Warna garis = warna border handle (kbx: drawDragEdge)
        Color c = sourceHandle ? MapNode.LEFT_BORDER : MapNode.RIGHT_BORDER;
        if (previewLR != null)
        {
            previewLR.startColor = c;
            previewLR.endColor   = c;
            previewLR.enabled    = true;
        }
        previewArrow.GetComponent<MeshRenderer>().material.color = c;
        previewDot  .GetComponent<MeshRenderer>().material.color = c;
        UpdateConnectionDrag(pointer);
        ShowStatus("Lepas di node Konsep untuk menyambung");
        return true;
    }

    public void UpdateConnectionDrag(PointerSample pointer)
    {
        if (!isDragging || pointer.id != dragPointerId || previewLR == null) return;

        Vector3 from = dragFromNode.transform.position;

        // Seperti kbx: saat melewati konsep, garis "snap" ke konsep itu
        // (CANDIDATE edge putus-putus, VIRTUAL-EDGE disembunyikan)
        dragCandidate = FindConceptAt(pointer);

        Vector3 to;
        if (dragCandidate != null) to = dragCandidate.transform.position;
        else
        {
            // Proyeksikan jari ke bidang sejajar kamera di kedalaman node,
            // supaya ujung garis selalu tepat di bawah jari
            Plane p = new Plane(arCamera.transform.forward, from);
            Ray   r = pointer.ray;
            to = p.Raycast(r, out float d) ? r.GetPoint(d) : r.origin + r.direction * 1.5f;
        }

        Vector3 dir = (to - from).normalized;
        from += dir * MapNode.BoundaryDistance(dragFromNode.transform, dir);
        if (dragCandidate != null) to -= dir * MapNode.BoundaryDistance(dragCandidate.transform, -dir);

        previewLR.sharedMaterial = dragCandidate != null ? previewDashedMat : previewSolidMat;
        previewLR.SetPosition(0, from);
        previewLR.SetPosition(1, to);

        bool marker = dragCandidate == null;
        bool dot    = dragSourceHandle || IsBidirectional;
        previewArrow.SetActive(marker && !dot);
        previewDot  .SetActive(marker && dot);
        if (marker)
        {
            previewDot.transform.position   = to;
            previewArrow.transform.position = to;
            if (dir.sqrMagnitude > 1e-6f)
                previewArrow.transform.rotation = Quaternion.LookRotation(dir, -arCamera.transform.forward);
        }
    }

    static Texture2D MakeDashTexture()
    {
        var tex = new Texture2D(8, 1, TextureFormat.RGBA32, false)
        {
            wrapMode   = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point,
        };
        for (int x = 0; x < 8; x++) tex.SetPixel(x, 0, x < 5 ? Color.white : Color.clear);
        tex.Apply();
        return tex;
    }

    public void CancelConnectionDrag()
    {
        isDragging    = false;
        dragCandidate = null;
        if (previewLR != null) previewLR.enabled = false;
        if (previewArrow) previewArrow.SetActive(false);
        if (previewDot)   previewDot.SetActive(false);
    }

    /// Drag handle kiri / kanan relasi dilepas: sambungkan source / target baru
    public void EndConnectionDrag(PointerSample pointer, MapNode fromNode, bool backward = false)
    {
        if (!isDragging || pointer.id != dragPointerId) return;
        CancelConnectionDrag();

        var target = FindConceptAt(pointer);
        if (target == null) { ShowStatus("[!] Lepaskan di dekat node Konsep"); return; }

        if (backward) TryConnect(target, fromNode);
        else          TryConnect(fromNode, target);
    }

    /// Drag handle koneksi yang sudah ada dilepas: di konsep lain = pindah, di ruang kosong = putus
    public void EndEdgeMove(PointerSample pointer, MapNode linkNode, EdgeRef edge)
    {
        if (!isDragging || pointer.id != dragPointerId) return;
        CancelConnectionDrag();
        if (edge == null) return;

        var target = FindConceptAt(pointer);
        if (target == null)               Disconnect(edge);
        else if (target.NodeId != edge.ConceptId) MoveEdge(edge, target);
    }

    // Konsep di bawah pointer: raycast langsung, lalu sensing lebar
    // (sudut kecil di XR, 10% tinggi layar di HP)
    MapNode FindConceptAt(PointerSample pointer)
    {
        Ray ray = pointer.ray;
        if (Physics.Raycast(ray, out RaycastHit hit, 10f))
        {
            var hitNode = hit.transform.GetComponentInParent<MapNode>();
            if (hitNode is ConceptNode) return hitNode;
        }

        MapNode best = null;
        if (pointer.xr)
        {
            float bestA = XR_PICK_DEGREES;
            foreach (var n in conceptObjects.Values)
            {
                if (n == null) continue;
                float a = Vector3.Angle(ray.direction, n.transform.position - ray.origin);
                if (a < bestA) { bestA = a; best = n; }
            }
        }
        else
        {
            float bestD = Screen.height * 0.10f;
            foreach (var n in conceptObjects.Values)
            {
                if (n == null) continue;
                Vector3 sp = arCamera.WorldToScreenPoint(n.transform.position);
                if (sp.z < 0f) continue;
                float d = Vector2.Distance((Vector2)sp, pointer.screenPos);
                if (d < bestD) { bestD = d; best = n; }
            }
        }
        return best;
    }

    public void TryConnect(MapNode from, MapNode to)
    {
        // Concept → Link: set source
        if (from is ConceptNode cNode && to is LinkNode lNode)
        {
            var lData = FindLink(lNode.NodeId);
            if (lData == null) return;
            if (IsTargetOf(lData.lid, cNode.NodeId))
            { ShowStatus("[!] Source dan target tidak boleh sama"); return; }

            string oldSource = lData.source_cid;
            string newSource = cNode.NodeId;
            lData.source_cid = newSource;
            DrawAllConnections();
            ShowStatus($"[OK] Source: {cNode.Label} → {lNode.Label}");
            PushUndo($"source {cNode.Label} → {lNode.Label}",
                undo: () => { lData.source_cid = oldSource; DrawAllConnections(); },
                redo: () => { lData.source_cid = newSource; DrawAllConnections(); });
        }
        // Link → Concept: add target
        else if (from is LinkNode lNode2 && to is ConceptNode cNode2)
        {
            var lData = FindLink(lNode2.NodeId);
            if (lData == null) return;
            if (lData.source_cid == cNode2.NodeId)
            { ShowStatus("[!] Source dan target tidak boleh sama"); return; }
            if (IsTargetOf(lData.lid, cNode2.NodeId))
            { ShowStatus("[!] Target sudah terhubung"); return; }
            if (TargetCount(lData.lid) >= GetLinkLimit(lData.lid))
            { ShowStatus("[!] Jumlah target relasi ini sudah maksimal"); return; }

            var newTarget = new LinkTargetData
            {
                lid         = lNode2.NodeId,
                target_cid  = cNode2.NodeId,
                target_data = TargetDataJson(lNode2.NodeId, cNode2.NodeId)
            };
            mapFile.canvas.linktargets.Add(newTarget);
            DrawAllConnections();
            ShowStatus($"[OK] Target: {lNode2.Label} → {cNode2.Label}");
            PushUndo($"target {lNode2.Label} → {cNode2.Label}",
                undo: () => { mapFile.canvas.linktargets.Remove(newTarget); DrawAllConnections(); },
                redo: () => { mapFile.canvas.linktargets.Add(newTarget); DrawAllConnections(); });
        }
        else
        {
            ShowStatus("[!] Sambungkan Konsep→Relasi atau Relasi→Konsep");
        }
    }

    // ─────────────────────────────────────────────
    //  EDGE (koneksi) — dipakai handle per koneksi
    // ─────────────────────────────────────────────
    public class EdgeRef
    {
        public LinkData       link;
        public LinkTargetData target;   // null = edge source
        public bool   isSource  => target == null;
        public string ConceptId => isSource ? link.source_cid : target.target_cid;
    }

    public List<EdgeRef> GetEdges(string lid)
    {
        var list  = new List<EdgeRef>();
        var lData = FindLink(lid);
        if (lData == null) return list;
        if (!string.IsNullOrEmpty(lData.source_cid) && conceptObjects.ContainsKey(lData.source_cid))
            list.Add(new EdgeRef { link = lData });
        foreach (var lt in mapFile.canvas.linktargets)
            if (lt.lid == lid && conceptObjects.ContainsKey(lt.target_cid))
                list.Add(new EdgeRef { link = lData, target = lt });
        return list;
    }

    public MapNode GetNode(string id)
    {
        if (id == null) return null;
        if (conceptObjects.TryGetValue(id, out var c)) return c;
        if (linkObjects.TryGetValue(id, out var l))    return l;
        return null;
    }

    /// Batas jumlah target relasi (field "limit" di data). Tanpa limit = tak terbatas.
    public int GetLinkLimit(string lid)
    {
        var lData = FindLink(lid);
        return lData?.Visual.limit ?? int.MaxValue;
    }

    LinkData FindLink(string lid) => mapFile?.canvas.links.Find(l => l.lid == lid);

    bool IsTargetOf(string lid, string cid) =>
        mapFile.canvas.linktargets.Exists(t => t.lid == lid && t.target_cid == cid);

    int TargetCount(string lid) =>
        mapFile.canvas.linktargets.FindAll(t => t.lid == lid).Count;

    static string TargetDataJson(string lid, string cid) =>
        JsonConvert.SerializeObject(new { source = lid, target = cid, type = "right" });

    string LabelOf(string id) => GetNode(id)?.Label ?? id;

    /// Pindahkan ujung konsep sebuah koneksi ke konsep lain (move-connect di kbx)
    void MoveEdge(EdgeRef e, MapNode concept)
    {
        var    lData  = e.link;
        string newCid = concept.NodeId;
        string oldCid = e.ConceptId;

        if (e.isSource)
        {
            if (IsTargetOf(lData.lid, newCid))
            { ShowStatus("[!] Source dan target tidak boleh sama"); return; }
            lData.source_cid = newCid;
            DrawAllConnections();
            PushUndo($"pindah source {LabelOf(lData.lid)} ke {concept.Label}",
                undo: () => { lData.source_cid = oldCid; DrawAllConnections(); },
                redo: () => { lData.source_cid = newCid; DrawAllConnections(); });
        }
        else
        {
            if (lData.source_cid == newCid)
            { ShowStatus("[!] Source dan target tidak boleh sama"); return; }
            if (IsTargetOf(lData.lid, newCid))
            { ShowStatus("[!] Target sudah terhubung"); return; }

            var    lt      = e.target;
            string oldData = lt.target_data;
            string newData = TargetDataJson(lData.lid, newCid);
            lt.target_cid  = newCid; lt.target_data = newData;
            DrawAllConnections();
            PushUndo($"pindah target {LabelOf(lData.lid)} ke {concept.Label}",
                undo: () => { lt.target_cid = oldCid; lt.target_data = oldData; DrawAllConnections(); },
                redo: () => { lt.target_cid = newCid; lt.target_data = newData; DrawAllConnections(); });
        }
        ShowStatus($"[OK] Koneksi {LabelOf(lData.lid)} dipindah ke {concept.Label}");
    }

    /// Putus satu koneksi (disconnect di kbx)
    void Disconnect(EdgeRef e)
    {
        var    lData = e.link;
        string desc  = $"putus {LabelOf(lData.lid)} – {LabelOf(e.ConceptId)}";

        if (e.isSource)
        {
            string oldCid = lData.source_cid;
            lData.source_cid = null;
            DrawAllConnections();
            PushUndo(desc,
                undo: () => { lData.source_cid = oldCid; DrawAllConnections(); },
                redo: () => { lData.source_cid = null;   DrawAllConnections(); });
        }
        else
        {
            var lt    = e.target;
            int index = mapFile.canvas.linktargets.IndexOf(lt);
            mapFile.canvas.linktargets.Remove(lt);
            DrawAllConnections();
            PushUndo(desc,
                undo: () => {
                    var list = mapFile.canvas.linktargets;
                    list.Insert(Mathf.Clamp(index, 0, list.Count), lt);
                    DrawAllConnections();
                },
                redo: () => { mapFile.canvas.linktargets.Remove(lt); DrawAllConnections(); });
        }
        ShowStatus($"[OK] {desc}");
    }

    // ─────────────────────────────────────────────
    //  SELEKSI & PINDAH NODE
    // ─────────────────────────────────────────────
    /// Double-tap: pilih node + tetangganya (seperti dbltap di kbx)
    public void SelectNeighborhood(MapNode node)
    {
        var group = new List<MapNode> { node };
        var canvas = mapFile.canvas;

        if (node is LinkNode)
        {
            var lData = FindLink(node.NodeId);
            if (lData != null) group.Add(GetNode(lData.source_cid));
            foreach (var lt in canvas.linktargets)
                if (lt.lid == node.NodeId) group.Add(GetNode(lt.target_cid));
        }
        else
        {
            foreach (var l in canvas.links)
                if (l.source_cid == node.NodeId) group.Add(GetNode(l.lid));
            foreach (var lt in canvas.linktargets)
                if (lt.target_cid == node.NodeId) group.Add(GetNode(lt.lid));
        }

        group.RemoveAll(n => n == null);
        MapNode.SelectGroup(group);
        ShowStatus($"{group.Count} node dipilih — drag salah satu untuk memindah semua");
    }

    /// Tool centroid (kbx): pindahkan relasi ke titik tengah konsep-konsep yang tersambung
    public void CentroidLink(MapNode link)
    {
        var concepts = new List<MapNode>();
        foreach (var e in GetEdges(link.NodeId))
        {
            var c = GetNode(e.ConceptId);
            if (c != null && !concepts.Contains(c)) concepts.Add(c);
        }
        if (concepts.Count == 0) return;

        Vector3 to = Vector3.zero;
        foreach (var c in concepts) to += c.transform.position;
        to /= concepts.Count;

        Vector3 from = link.transform.position;
        var before = new List<(MapNode, Vector3)> { (link, from) };
        var after  = new List<(MapNode, Vector3)> { (link, to) };
        PushUndo($"centroid {link.Label}", undo: () => ApplyPositions(before), redo: () => ApplyPositions(after));
        StartCoroutine(AnimateMove(link.transform, from, to, 0.3f));
        ShowStatus($"[OK] {link.Label} dipindah ke tengah konsepnya");
    }

    static IEnumerator AnimateMove(Transform t, Vector3 from, Vector3 to, float duration)
    {
        for (float e = 0f; e < duration && t != null; e += Time.deltaTime)
        {
            t.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, e / duration));
            yield return null;
        }
        if (t != null) t.position = to;
    }

    /// Dipanggil MapNode setelah drag selesai → masuk undo
    public void CommitMove(List<(MapNode n, Vector3 from)> moved)
    {
        var before = new List<(MapNode, Vector3)>(moved);
        var after  = new List<(MapNode, Vector3)>();
        foreach (var (n, _) in moved) if (n) after.Add((n, n.transform.position));
        if (after.Count == 0) return;

        string desc = after.Count == 1 ? $"pindah {after[0].Item1.Label}" : $"pindah {after.Count} node";
        PushUndo(desc, undo: () => ApplyPositions(before), redo: () => ApplyPositions(after));
    }

    static List<(MapNode, Vector3)> RecordPositions()
    {
        var list = new List<(MapNode, Vector3)>();
        foreach (var n in MapNode.All) if (n) list.Add((n, n.transform.position));
        return list;
    }

    void PushPositionUndo(string desc, List<(MapNode, Vector3)> before)
    {
        var after = RecordPositions();
        PushUndo(desc, undo: () => ApplyPositions(before), redo: () => ApplyPositions(after));
    }

    static void ApplyPositions(List<(MapNode, Vector3)> positions)
    {
        foreach (var (n, pos) in positions) if (n) n.transform.position = pos;
    }

    // ─────────────────────────────────────────────
    //  AUTO LAYOUT — rapikan semua node di depan kamera
    // ─────────────────────────────────────────────
    /// Rapikan LEMBUT: node di-snap ke grid terdekat dari posisi sekarang.
    /// Susunan relatif & koneksi tidak berubah — hanya diratakan (align)
    /// dan dijamin tidak bertumpuk. Node bergeser seminimal mungkin.
    public void AutoLayoutNodes()
    {
        var nodes = new List<MapNode>();
        foreach (var n in MapNode.All) if (n != null) nodes.Add(n);
        if (nodes.Count == 0) return;
        var before = RecordPositions();

        GetViewBasis(out Vector3 camPos, out Vector3 fwd, out Vector3 right, out Vector3 up);

        // Bidang layout = jarak rata-rata node dari kamera (bukan reset ke 0.7m)
        float dist = 0f;
        foreach (var n in nodes) dist += Vector3.Dot(n.transform.position - camPos, fwd);
        dist = Mathf.Max(dist / nodes.Count, 0.35f);
        Vector3 origin = camPos + fwd * dist;

        // Ukuran sel grid dari node terbesar + jarak antar
        float cellW = 0.12f, cellH = 0.06f;
        foreach (var n in nodes)
        {
            cellW = Mathf.Max(cellW, n.transform.localScale.x);
            cellH = Mathf.Max(cellH, n.transform.localScale.y);
        }
        cellW *= 1.3f;
        cellH *= 1.9f;

        // Urutkan dari kiri-atas agar hasil deterministik
        nodes.Sort((a, b) =>
        {
            Vector3 pa = a.transform.position - origin, pb = b.transform.position - origin;
            float ya = Vector3.Dot(pa, up), yb = Vector3.Dot(pb, up);
            if (Mathf.Abs(ya - yb) > 0.01f) return yb.CompareTo(ya);
            return Vector3.Dot(pa, right).CompareTo(Vector3.Dot(pb, right));
        });

        var used = new HashSet<(int, int)>();
        foreach (var n in nodes)
        {
            Vector3 rel = n.transform.position - origin;
            int gx = Mathf.RoundToInt(Vector3.Dot(rel, right) / cellW);
            int gy = Mathf.RoundToInt(Vector3.Dot(rel, up)    / cellH);

            (int fx, int fy) = FindFreeCell(gx, gy, used);
            used.Add((fx, fy));
            n.transform.position = origin + right * (fx * cellW) + up * (fy * cellH);
        }

        PushPositionUndo("rapikan layout", before);
        ShowStatus("[OK] Layout dirapikan");
    }

    /// Centerize: pindahkan SELURUH peta ke depan kamera sebagai satu grup.
    /// Susunan antar-node dipertahankan (offset relatif terhadap pusat grup
    /// di-remap ke basis kamera sekarang), koneksi tidak berubah.
    public void CenterNodes()
    {
        var nodes = new List<MapNode>();
        foreach (var n in MapNode.All) if (n != null) nodes.Add(n);
        if (nodes.Count == 0) return;
        var before = RecordPositions();

        // Pusat grup saat ini
        Vector3 centroid = Vector3.zero;
        foreach (var n in nodes) centroid += n.transform.position;
        centroid /= nodes.Count;

        GetViewBasis(out Vector3 camPos, out Vector3 fwd, out Vector3 right, out Vector3 up);

        // Simpan offset tiap node pada basis kamera (kedalaman diratakan)
        var offsets = new List<(MapNode n, float x, float y)>();
        foreach (var n in nodes)
        {
            Vector3 rel = n.transform.position - centroid;
            offsets.Add((n, Vector3.Dot(rel, right), Vector3.Dot(rel, up)));
        }

        // Tempatkan pusat grup di depan kamera
        Vector3 newCenter = camPos + fwd * (PointerInput.IsXR ? XR_MAP_DIST : 1.0f);
        foreach (var (n, x, y) in offsets)
            n.transform.position = newCenter + right * x + up * y;

        PushPositionUndo("pusatkan peta", before);
        ShowStatus("[OK] Peta dipusatkan ke depan kamera");
    }

    void GetViewBasis(out Vector3 pos, out Vector3 fwd, out Vector3 right, out Vector3 up)
    {
        var cam = arCamera.transform;
        pos = cam.position;
        if (!PointerInput.IsXR)
        {
            fwd = cam.forward; right = cam.right; up = cam.up;
            return;
        }

        fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(cam.up, Vector3.up);
        fwd.Normalize();
        up    = Vector3.up;
        right = Vector3.Cross(up, fwd);
    }

    // Cari sel grid kosong terdekat (spiral keluar dari sel asal)
    static (int, int) FindFreeCell(int gx, int gy, HashSet<(int, int)> used)
    {
        if (!used.Contains((gx, gy))) return (gx, gy);
        for (int r = 1; r <= 8; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue;
                    if (!used.Contains((gx + dx, gy + dy))) return (gx + dx, gy + dy);
                }
        return (gx, gy); // penuh — biarkan (tidak mungkin dalam praktik)
    }

    // ─────────────────────────────────────────────
    //  PENILAIAN OTOMATIS (Analyzer Kit-Build)
    // ─────────────────────────────────────────────
    public class AssessmentResult
    {
        public int   match, miss, excess, total;
        public float Score => total > 0 ? (float)match / total : 0f;
    }

    // Warna compare map kbx: match hijau, excess biru
    static readonly Color MATCH_COLOR  = new Color32(0x3A, 0xB7, 0x95, 0xFF);
    static readonly Color EXCESS_COLOR = new Color32(0x5B, 0xC0, 0xEB, 0xFF);

    public bool HasAnswerKey => mapFile?.meta?.kunci_jawaban != null && mapFile.meta.kunci_jawaban.Count > 0;

    /// Bandingkan proposisi siswa dengan kunci_jawaban; warnai garis match/excess.
    /// Proposisi yang kurang (miss) hanya dihitung, tidak ditampilkan. Null bila tanpa kunci.
    public AssessmentResult Assess()
    {
        if (!HasAnswerKey) return null;

        var key     = new HashSet<string>();
        var example = new HashSet<string>();
        foreach (var k in mapFile.meta.kunci_jawaban)
            if (ParseProposition(k) is string p) key.Add(p);
        if (ParseProposition(mapFile.meta.proposisi_contoh) is string ex && !key.Contains(ex)) example.Add(ex);

        var concepts = new Dictionary<string, string>();
        foreach (var c in mapFile.canvas.concepts) concepts[c.cid] = c.label;

        // Status tiap koneksi target (lid|cid) → match / excess / contoh
        var targetState = new Dictionary<string, Color>();
        var linkState   = new Dictionary<string, Color>();
        var found       = new HashSet<string>();
        var result      = new AssessmentResult { total = key.Count };

        foreach (var lt in mapFile.canvas.linktargets)
        {
            var link = FindLink(lt.lid);
            if (link == null || string.IsNullOrEmpty(link.source_cid)) continue;
            if (!concepts.TryGetValue(link.source_cid, out var src) || !concepts.TryGetValue(lt.target_cid, out var tgt)) continue;

            string prop = PropKey(src, link.label, tgt);
            string alt  = IsBidirectional ? PropKey(tgt, link.label, src) : null;
            string hit  = key.Contains(prop) ? prop : alt != null && key.Contains(alt) ? alt : null;

            Color color;
            if (hit != null)                                         { color = MATCH_COLOR; found.Add(hit); }
            else if (example.Contains(prop) || (alt != null && example.Contains(alt))) color = MATCH_COLOR;
            else                                                     { color = EXCESS_COLOR; result.excess++; }

            targetState[lt.lid + "|" + lt.target_cid] = color;
            // Source relasi hijau bila ada satu proposisinya yang benar
            if (!linkState.TryGetValue(lt.lid, out var prev) || prev != MATCH_COLOR) linkState[lt.lid] = color;
        }

        result.match = found.Count;
        result.miss  = key.Count - found.Count;

        foreach (var cl in connections)
        {
            if (cl == null) continue;
            var  states = cl.Type == ConnectionLine.ConnectionType.Source ? linkState : targetState;
            string id   = cl.Type == ConnectionLine.ConnectionType.Source ? cl.Lid : cl.Lid + "|" + cl.ConceptId;
            if (states.TryGetValue(id, out var c)) cl.SetColor(c);   // koneksi tanpa proposisi utuh tetap abu-abu
        }

        ShowStatus($"[Nilai] benar {result.match}/{result.total}, kurang {result.miss}, lebih {result.excess}");
        return result;
    }

    // "Beowulf --[is]--> Germanic Epic  (catatan)" → kunci ternormalisasi
    static string ParseProposition(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^\s*(.+?)\s*--\[(.+?)\]-->\s*(.+?)\s*(\(.*\))?\s*$");
        return m.Success ? PropKey(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value) : null;
    }

    static string PropKey(string s, string l, string t) => $"{Norm(s)}|{Norm(l)}|{Norm(t)}";

    static string Norm(string s) =>
        System.Text.RegularExpressions.Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

    // ─────────────────────────────────────────────
    //  SAVE
    // ─────────────────────────────────────────────
    public string GetCurrentJson()
    {
        if (mapFile == null) return "{}";
        SyncPositionsToData();
        return JsonConvert.SerializeObject(mapFile, Formatting.Indented);
    }

    void SyncPositionsToData()
    {
        foreach (var kv in conceptObjects)
        {
            var data = mapFile.canvas.concepts.Find(c => c.cid == kv.Key);
            if (data == null) continue;
            data.x = kv.Value.transform.position.x * 100f;
            data.y = kv.Value.transform.position.y * 100f;
        }
        foreach (var kv in linkObjects)
        {
            var data = mapFile.canvas.links.Find(l => l.lid == kv.Key);
            if (data == null) continue;
            data.x = kv.Value.transform.position.x * 100f;
            data.y = kv.Value.transform.position.y * 100f;
        }
    }

    public void SaveToFile()
    {
        if (mapFile == null) { ShowStatus("Tidak ada data untuk disimpan"); return; }

        string json = GetCurrentJson();
        string path = Path.Combine(Application.persistentDataPath,
            $"concept-map-{DateTime.Now:yyyyMMdd_HHmm}.json");
        File.WriteAllText(path, json);
        ShowStatus($"[Saved] Tersimpan:\n{path}");
    }

    // ─────────────────────────────────────────────
    //  RESET
    // ─────────────────────────────────────────────
    void ResetMap()
    {
        ClearScene();
        if (mapFile != null)
        {
            mapFile.canvas.concepts.Clear();
            mapFile.canvas.links.Clear();
            mapFile.canvas.linktargets.Clear();
        }
        cCounter = 0; lCounter = 0;
        undoStack.Clear(); redoStack.Clear();
        ShowStatus("Reset selesai");
    }

    void ClearScene()
    {
        MapNode.DeselectAll();
        CancelConnectionDrag();
        foreach (var kv in conceptObjects) if (kv.Value) Destroy(kv.Value.gameObject);
        foreach (var kv in linkObjects)    if (kv.Value) Destroy(kv.Value.gameObject);
        connections.ForEach(c => { if (c) Destroy(c.gameObject); });
        conceptObjects.Clear();
        linkObjects   .Clear();
        connections   .Clear();
    }

    // ─────────────────────────────────────────────
    //  HELPERS
    // ─────────────────────────────────────────────
    public void ShowStatus(string msg)
    {
        // Canvas dijaga bersih — status hanya ke log (untuk debugging).
        // Di VR tidak ada log yang terlihat → peringatan "[!]" tampil sebagai toast.
        Debug.Log($"[Status] {msg}");
        if (PointerInput.IsXR && msg.StartsWith("[!]"))
        {
            if (toolbar == null) toolbar = FindFirstObjectByType<AppToolbar>();
            if (toolbar != null) toolbar.ShowToast(msg.Substring(3).Trim(), 2.5f);
        }
        if (statusText != null && statusText.gameObject.activeSelf)
            statusText.gameObject.SetActive(false);
    }
}
