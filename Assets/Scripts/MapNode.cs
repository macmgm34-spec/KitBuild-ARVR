using System.Collections;
using UnityEngine;
using TMPro;

/// <summary>
/// Base class for ConceptNode and LinkNode. Gesture mengikuti Kit-Build (kbx):
/// - Tap              → pilih node; relasi terpilih memunculkan handle
/// - Double-tap       → pilih node + semua tetangganya
/// - Drag             → pindahkan node (atau seluruh seleksi), bisa di-undo
/// - VR: saat drag, thumbstick ↑/↓ atau gerakan tangan maju/mundur → node menjauh/mendekat
/// - Drag handle kiri (merah) / kanan (biru) → sambungkan source / target baru ke konsep
/// - Drag handle koneksi → lepas di konsep lain = pindah, di ruang kosong = putus
/// - Tap tombol centroid (bawah relasi, bila ≥ 2 koneksi) → relasi ke tengah konsepnya
/// - Box auto-size menyesuaikan panjang teks + padding
/// </summary>
public abstract class MapNode : MonoBehaviour
{
    [Header("References")]
    public TextMeshPro    labelText;
    public Transform      connectorPort;
    public SpriteRenderer bgSprite;
    public SpriteRenderer portSprite;

    [HideInInspector] public string NodeId;
    [HideInInspector] public string Label;
    [HideInInspector] public Vector3 AutoBaseScale = Vector3.one;  // hasil auto-size (basis pinch-zoom)

    // Registry semua node aktif (dipakai OffscreenIndicator & pinch)
    public static readonly System.Collections.Generic.List<MapNode> All = new();

    protected ConceptMapManager manager;
    protected Camera            arCamera;
    private   MeshRenderer      meshRenderer;
    private   MaterialPropertyBlock propBlock;

    // Konstanta ukuran (dalam meter world-space)
    protected const float LABEL_WORLD_SCALE = 0.02f;  // 2cm per TMP unit → char 10cm
    protected const float TMP_FONT_SIZE     = 6.5f;
    protected const float MAX_NODE_WIDTH    = 0.6f;
    protected const float PAD_X             = 0.032f; // dipepetkan, teks isi penuh kotak
    protected const float PAD_Y             = 0.024f;
    protected const float BTN_SIZE          = 0.05f;  // tombol 5cm
    // Di VR node dilihat dari ±1.4m & ditunjuk ray yang goyang → target lebih besar
    protected static float BtnSize => PointerInput.IsXR ? BTN_SIZE * 1.6f : BTN_SIZE;

    // Handle relasi seperti kbx: kiri = source (left), kanan = target (right)
    private GameObject       connectFwdBtn;   // handle kanan (target baru)
    private GameObject       connectBwdBtn;   // handle kiri (source baru)
    private GameObject       centroidBtn;     // KitBuildCentroidTool
    private GameObject       remainingBadge;  // sisa target (drawLinkRemaining di kbx)
    private TextMeshPro      remainingText;

    // Handle per koneksi yang sudah ada (hanya relasi terpilih)
    private readonly System.Collections.Generic.List<(GameObject go, ConceptMapManager.EdgeRef edge)> edgeHandles = new();

    // Gesture
    private enum DragKind { None, Node, NewTarget, NewSource, MoveEdge, Centroid }
    private bool          isTracking;
    private int           trackedPointerId = -1;
    private PointerSample beganSample;
    private Vector3       dragOffset;
    private float         dragDist;     // XR: jarak node di sepanjang ray
    private float         lastReach;    // XR: jarak kepala → tangan saat frame sebelumnya
    private bool          hasDragged;
    private DragKind      dragKind;
    private ConceptMapManager.EdgeRef dragEdge;
    private readonly System.Collections.Generic.List<(MapNode n, Vector3 from)> moveSet = new();
    private bool          selectAfterMove;

    // Seleksi (bisa lebih dari satu node, mis. hasil double-tap)
    private static readonly System.Collections.Generic.List<MapNode> selection = new();
    public  bool IsSelected => selection.Contains(this);
    static  MapNode Primary => selection.Count == 1 ? selection[0] : null;

    // Double-tap
    const float DOUBLE_TAP_SEC    = 0.4f;
    const float XR_DOUBLE_TAP_SEC = 0.55f;   // pinch/trigger 2× lebih lambat dari tap layar
    private static MapNode lastTapNode;
    private static float   lastTapTime;

    // Jumlah gesture yang sedang berjalan di node mana pun (dua tangan di VR)
    static int activeGestures;
    public static bool GestureActive => activeGestures > 0;

    // Skin rounded-rect (desain versi web)
    private Texture2D texNormal;    // sudut membulat polos
    private Texture2D texSelected;  // + border merah (node terpilih)
    private static Texture2D texLeftHandle, texRightHandle, texCentroid, texBadge;
    static readonly Color SELECT_BORDER = new Color(0.90f, 0.42f, 0.45f, 1f); // merah muda web

    // Handle koneksi hanya ada di relasi (seperti kbx); konsep cukup tap & drag
    protected virtual bool HasConnectButtons => false;

    // ──────────────────────────────────────────────
    void Awake()
    {
        arCamera     = Camera.main;
        manager      = FindFirstObjectByType<ConceptMapManager>();
        meshRenderer = GetComponent<MeshRenderer>();
        propBlock    = new MaterialPropertyBlock();
    }

    void OnEnable()  { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void Update()
    {
        Billboard();
        HandleTouch();
    }

    void LateUpdate() => PositionEdgeHandles();

    // ── Billboard ─────────────────────────────────
    void Billboard()
    {
        if (arCamera == null) return;
        // Up = sumbu dunia (bukan kamera) → node tetap tegak/steady
        // walau HP diputar portrait ⇄ landscape
        Vector3 facing = PointerInput.IsXR
            ? transform.position - arCamera.transform.position
            : arCamera.transform.rotation * Vector3.forward;
        transform.LookAt(transform.position + facing, Vector3.up);
    }

    // ── Init ──────────────────────────────────────
    public virtual void Init(string id, string label, Color bg, Color textColor)
    {
        NodeId = id;
        Label  = label;

        SetMeshColor(bg);
        SetupLabel(label);
        CreateButtons();
        StartCoroutine(AutoSizeCo());
    }

    void SetupLabel(string label)
    {
        if (labelText == null) return;
        labelText.text           = label;
        labelText.color          = Color.black;
        labelText.fontSize       = TMP_FONT_SIZE;
        labelText.enableAutoSizing    = false;
        labelText.enableWordWrapping  = true;
        labelText.overflowMode        = TMPro.TextOverflowModes.Overflow;
        labelText.alignment           = TMPro.TextAlignmentOptions.Center;
        labelText.transform.localRotation = Quaternion.identity;
    }

    // ── Auto-size box sesuai teks ─────────────────
    IEnumerator AutoSizeCo()
    {
        yield return null; // tunggu 1 frame

        if (labelText == null) yield break;

        // Set node ke ukuran sementara (1,1,0.01) agar bisa hitung world scale
        transform.localScale = new Vector3(1f, 1f, 0.01f);
        ApplyLabelScale(1f, 1f);

        // Set lebar wrapping
        var rt = labelText.GetComponent<RectTransform>();
        float maxWidthTMP = MAX_NODE_WIDTH / LABEL_WORLD_SCALE;
        if (rt != null) rt.sizeDelta = new Vector2(maxWidthTMP, 10000f);

        labelText.ForceMeshUpdate();

        // Ukuran teks dalam TMP local → konversi ke world
        Vector2 tSize     = labelText.GetRenderedValues(false);
        float textWorldW  = tSize.x * LABEL_WORLD_SCALE;
        float textWorldH  = tSize.y * LABEL_WORLD_SCALE;

        // Ukuran node final
        float nodeW = Mathf.Clamp(textWorldW + PAD_X * 2f, 0.16f, MAX_NODE_WIDTH);
        float nodeH = Mathf.Max(textWorldH + PAD_Y * 2f, 0.075f);

        // Terapkan scale — teks dirender lebih besar dari kotak (FONT_BOOST),
        // kotak (nodeW/nodeH) sendiri TIDAK berubah dari hasil pengukuran di atas.
        transform.localScale = new Vector3(nodeW, nodeH, 0.01f);
        ApplyLabelScale(nodeW, nodeH, FONT_BOOST);

        // Update rect width sesuai lebar node final
        if (rt != null) rt.sizeDelta = new Vector2(nodeW / LABEL_WORLD_SCALE, 10000f);
        labelText.transform.localPosition = new Vector3(0f, 0f, -0.5f);

        // Update collider
        var col = GetComponent<BoxCollider>();
        if (col != null) col.size = new Vector3(1f, 1f, 0.5f);

        // Update posisi tombol
        UpdateButtonPositions(nodeW, nodeH);

        // Simpan basis skala untuk pinch-zoom
        AutoBaseScale = transform.localScale;

        // Terapkan skin rounded-rect sesuai aspek node (desain web)
        ApplyRoundedSkin(nodeW, nodeH);
    }

    // ── Skin rounded-rect ─────────────────────────
    void ApplyRoundedSkin(float nodeW, float nodeH)
    {
        if (meshRenderer == null) return;

        int tw = 256;
        int th = Mathf.Clamp(Mathf.RoundToInt(tw * nodeH / nodeW), 32, 512);
        // Radius sudut ±1.5cm world, konsisten berapapun ukuran node
        float radius = Mathf.Min(tw * (0.015f / nodeW), th * 0.5f - 1f);
        float border = Mathf.Min(tw * (0.012f / nodeW), th * 0.25f); // tebal border merah

        texNormal   = MakeRoundedTex(tw, th, radius, 0f,     SELECT_BORDER);
        texSelected = MakeRoundedTex(tw, th, radius, border, SELECT_BORDER);

        // Material flat transparan (seperti web, tanpa lighting)
        meshRenderer.material = new Material(Shader.Find("Sprites/Default"));
        SetMeshTexture(IsSelected ? texSelected : texNormal);
        SetMeshColor(GetBaseColor());
    }

    void SetMeshTexture(Texture2D t)
    {
        if (meshRenderer == null || t == null) return;
        meshRenderer.GetPropertyBlock(propBlock);
        propBlock.SetTexture("_MainTex", t);
        meshRenderer.SetPropertyBlock(propBlock);
    }

    /// Rounded-rect putih (di-tint via _Color); borderPx>0 → cincin border berwarna.
    static Texture2D MakeRoundedTex(int w, int h, float radius, float borderPx, Color borderCol)
        => MakeRoundedTex(w, h, radius, borderPx, borderCol, Color.white);

    static Texture2D MakeRoundedTex(int w, int h, float radius, float borderPx, Color borderCol, Color fill)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        float hw = w * 0.5f, hh = h * 0.5f;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            // Signed distance ke rounded rect (negatif = di dalam)
            float dx = Mathf.Max(Mathf.Abs(x + 0.5f - hw) - (hw - radius), 0f);
            float dy = Mathf.Max(Mathf.Abs(y + 0.5f - hh) - (hh - radius), 0f);
            float d  = Mathf.Sqrt(dx * dx + dy * dy) - radius;

            float alpha = Mathf.Clamp01(0.5f - d);          // anti-alias 1px
            Color c = (borderPx > 0f && d > -borderPx) ? borderCol : fill;
            c.a = alpha;
            tex.SetPixel(x, y, c);
        }
        tex.Apply();
        return tex;
    }

    // FONT_BOOST: teks dirender lebih besar dari "pas" di kotak, TANPA
    // mengubah ukuran kotak — dipakai hanya saat render final (bukan saat
    // mengukur), jadi visualnya jadi padding lebih kecil, kotak tetap sama.
    protected const float FONT_BOOST = 1.3f;

    void ApplyLabelScale(float nodeW, float nodeH, float boost = 1f)
    {
        if (labelText == null) return;
        // Label world scale selalu LABEL_WORLD_SCALE, apapun ukuran node
        labelText.transform.localScale = new Vector3(
            boost * LABEL_WORLD_SCALE / nodeW,
            boost * LABEL_WORLD_SCALE / nodeH,
            1f);
    }

    // ── Handle relasi (gaya kbx) ──────────────────
    // KitBuildLeftHandleTool / KitBuildRightHandleTool: bg + border, ikon → hitam
    // (● bila peta bidirectional), di tengah sisi kiri/kanan node.
    static readonly Color LEFT_BG   = new Color32(0xFF, 0xB5, 0xB8, 0xFF); // #ffb5b8
    static readonly Color RIGHT_BG  = new Color32(0xA6, 0xEF, 0xFF, 0xFF); // #a6efff
    static readonly Color TOOL_BORDER = new Color32(0x77, 0x77, 0x77, 0xCC); // #777777cc (tool default)
    public static readonly Color LEFT_BORDER  = new Color32(0xEE, 0x22, 0x22, 0xFF); // #EE2222
    public static readonly Color RIGHT_BORDER = new Color32(0x5B, 0xC0, 0xEB, 0xFF); // #5BC0EB
    const float HANDLE_MARGIN = 8f / 36f;   // margin 8px untuk handle 36px di kbx

    void CreateButtons()
    {
        if (!HasConnectButtons) return;
        connectFwdBtn = CreateHandle("RightHandle", source: false);
        connectBwdBtn = CreateHandle("LeftHandle",  source: true);
        connectFwdBtn.SetActive(false);
        connectBwdBtn.SetActive(false);

        centroidBtn = GameObject.CreatePrimitive(PrimitiveType.Quad);
        centroidBtn.name = "CentroidTool";
        centroidBtn.transform.SetParent(transform, false);
        if (texCentroid == null) texCentroid = MakeCentroidTex(64);
        var mr = centroidBtn.GetComponent<MeshRenderer>();
        mr.material = new Material(Shader.Find("Sprites/Default")) { mainTexture = texCentroid };
        centroidBtn.SetActive(false);

        // Badge sisa target: putih, border #DEDEDE, angka merah #CC0000, selalu tampil
        remainingBadge = GameObject.CreatePrimitive(PrimitiveType.Quad);
        remainingBadge.name = "RemainingBadge";
        remainingBadge.transform.SetParent(transform, false);
        Destroy(remainingBadge.GetComponent<Collider>());   // indikator saja, tidak bisa di-tap
        if (texBadge == null) texBadge = MakeBadgeTex(64);
        remainingBadge.GetComponent<MeshRenderer>().material =
            new Material(Shader.Find("Sprites/Default")) { mainTexture = texBadge };

        var lbl = new GameObject("Lbl");
        lbl.transform.SetParent(remainingBadge.transform, false);
        remainingText           = lbl.AddComponent<TextMeshPro>();
        remainingText.fontSize  = TMP_FONT_SIZE;
        remainingText.color     = new Color32(0xCC, 0x00, 0x00, 0xFF);
        remainingText.alignment = TMPro.TextAlignmentOptions.Center;
        remainingText.enableAutoSizing = false;
        lbl.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        lbl.transform.localScale    = new Vector3(0.5f, 0.5f, 1f);
        remainingBadge.SetActive(false);
    }

    /// Sisa target yang masih bisa disambung (limit − target sekarang); ≤ 0 → disembunyikan
    public void SetRemaining(int remaining)
    {
        if (remainingBadge == null) return;
        remainingBadge.SetActive(remaining > 0);
        if (remaining > 0) remainingText.text = remaining.ToString();
    }

    // Kotak putih border #DEDEDE, sudut membulat kecuali kiri-bawah (seperti kbx)
    static Texture2D MakeBadgeTex(int n)
    {
        Color border = new Color32(0xDE, 0xDE, 0xDE, 0xFF);
        float radius = n * 5f / 26f, bw = n * 3f / 26f;
        var tex = MakeRoundedTex(n, n, radius, bw, border, Color.white);
        for (int y = 0; y < radius; y++)
        for (int x = 0; x < radius; x++)
            tex.SetPixel(x, y, (x < bw || y < bw) ? border : Color.white);
        tex.Apply();
        return tex;
    }

    // Tool putih + border abu-abu, ikon 4 sudut menyempit (bi-fullscreen-exit di kbx)
    static Texture2D MakeCentroidTex(int n)
    {
        var tex = MakeRoundedTex(n, n, 18f, 5f, TOOL_BORDER, Color.white);
        int mid = n / 2, gap = n * 6 / 100, len = n * 14 / 100, th = n * 5 / 100;
        void Corner(int sx, int sy)
        {
            // titik sudut L di dekat pusat, kaki mengarah keluar
            int cx = mid + sx * gap, cy = mid + sy * gap;
            for (int i = 0; i <= len; i++)
            for (int t = 0; t < th; t++)
            {
                tex.SetPixel(cx + sx * i, cy + sy * t, Color.black);   // kaki horizontal
                tex.SetPixel(cx + sx * t, cy + sy * i, Color.black);   // kaki vertikal
            }
        }
        Corner(-1, -1); Corner(1, -1); Corner(-1, 1); Corner(1, 1);
        tex.Apply();
        return tex;
    }

    GameObject CreateHandle(string objName, bool source)
    {
        var btn = GameObject.CreatePrimitive(PrimitiveType.Quad);
        btn.name = objName;
        btn.transform.SetParent(transform, false);

        if (texLeftHandle == null)
        {
            texLeftHandle  = MakeRoundedTex(64, 64, 18f, 5f, LEFT_BORDER,  LEFT_BG);
            texRightHandle = MakeRoundedTex(64, 64, 18f, 5f, RIGHT_BORDER, RIGHT_BG);
        }
        var mr = btn.GetComponent<MeshRenderer>();
        mr.material = new Material(Shader.Find("Sprites/Default"));
        var block = new MaterialPropertyBlock();
        block.SetTexture("_MainTex", source ? texLeftHandle : texRightHandle);
        block.SetColor("_Color", Color.white);
        mr.SetPropertyBlock(block);

        var lbl = new GameObject("Lbl");
        lbl.transform.SetParent(btn.transform, false);
        var tmp       = lbl.AddComponent<TextMeshPro>();
        tmp.text      = manager != null && manager.IsBidirectional ? "●" : "→";
        tmp.fontSize  = TMP_FONT_SIZE;
        tmp.color     = Color.black;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.enableAutoSizing = false;
        lbl.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        lbl.transform.localScale    = new Vector3(0.46f, 0.46f, 1f);

        return btn;
    }

    void UpdateButtonPositions(float nodeW, float nodeH)
    {
        // kbx: gridPos x=-1 / x=1, y=0 → di luar tepi kiri/kanan, tengah vertikal
        float btnLocal    = BtnSize / nodeW;
        float btnLocalTop = BtnSize / nodeH;
        float sideX       = 0.5f + (BtnSize * HANDLE_MARGIN + BtnSize * 0.5f) / nodeW;

        if (connectFwdBtn != null)
        {
            connectFwdBtn.transform.localScale    = new Vector3(btnLocal, btnLocalTop, 1f);
            connectFwdBtn.transform.localPosition = new Vector3(sideX, 0f, -0.5f);
        }
        if (connectBwdBtn != null)
        {
            connectBwdBtn.transform.localScale    = new Vector3(btnLocal, btnLocalTop, 1f);
            connectBwdBtn.transform.localPosition = new Vector3(-sideX, 0f, -0.5f);
        }
        if (centroidBtn != null)
        {
            float belowY = -(0.5f + (BtnSize * HANDLE_MARGIN + BtnSize * 0.5f) / nodeH);
            centroidBtn.transform.localScale    = new Vector3(btnLocal, btnLocalTop, 1f);
            centroidBtn.transform.localPosition = new Vector3(0f, belowY, -0.5f);
        }
        if (remainingBadge != null)
        {
            // kbx: badge 26px berpusat di pojok kanan-atas node (handle 36px)
            float bs = BtnSize * 26f / 36f;
            remainingBadge.transform.localScale    = new Vector3(bs / nodeW, bs / nodeH, 1f);
            remainingBadge.transform.localPosition = new Vector3(0.5f, 0.5f, -0.6f);
        }
    }

    /// Jarak dari pusat node ke tepi kotaknya sepanjang arah dir (setara endpoint edge di kbx)
    public static float BoundaryDistance(Transform node, Vector3 dir)
    {
        Vector3 s  = node.lossyScale;
        float   dx = Mathf.Abs(Vector3.Dot(dir, node.right));
        float   dy = Mathf.Abs(Vector3.Dot(dir, node.up));
        float   tx = dx > 1e-4f ? s.x * 0.5f / dx : float.MaxValue;
        float   ty = dy > 1e-4f ? s.y * 0.5f / dy : float.MaxValue;
        float   t  = Mathf.Min(tx, ty);
        return t == float.MaxValue ? 0f : t;
    }

    // ── Touch ─────────────────────────────────────
    void HandleTouch()
    {
        foreach (var p in PointerInput.Samples)
        {
            if (p.phase == TouchPhase.Began && !isTracking)
            {
                if (p.overUI) continue;
                var hit = RaycastMe(p.ray);
                if (hit == null) continue;

                Transform t = hit.Value.transform;
                dragKind = DragKind.Node;
                dragEdge = null;
                if      (IsBtn(t, connectFwdBtn)) dragKind = DragKind.NewTarget;
                else if (IsBtn(t, connectBwdBtn)) dragKind = DragKind.NewSource;
                else if (IsBtn(t, centroidBtn))   dragKind = DragKind.Centroid;
                else
                {
                    foreach (var h in edgeHandles)
                        if (IsBtn(t, h.go)) { dragKind = DragKind.MoveEdge; dragEdge = h.edge; break; }
                }
                if (dragKind == DragKind.Node) ComputeDragOffset(p.ray);

                isTracking = true; trackedPointerId = p.id;
                beganSample = p; hasDragged = false;
                activeGestures++;
            }
            else if (isTracking && p.id == trackedPointerId)
            {
                if (p.phase == TouchPhase.Moved)
                {
                    // VR: thumbstick saja (tanpa menggeser ray) juga memulai drag dorong/tarik
                    bool depthInput = p.xr && dragKind == DragKind.Node && Mathf.Abs(p.depthAxis) > STICK_DEADZONE;
                    if (!hasDragged && !depthInput && !PointerInput.BeyondDragThreshold(beganSample, p)) continue;
                    if (!hasDragged) { hasDragged = true; OnDragStart(p); }

                    if (dragKind == DragKind.Node) MoveNode(p);
                    else if (dragKind != DragKind.None) manager.UpdateConnectionDrag(p);
                }
                else if (p.phase == TouchPhase.Ended || p.phase == TouchPhase.Canceled)
                {
                    bool canceled = p.phase == TouchPhase.Canceled;
                    if (hasDragged) OnDragEnd(p, canceled);
                    else if (!canceled) OnTap();
                    isTracking = false; trackedPointerId = -1;
                    dragKind = DragKind.None; dragEdge = null;
                    activeGestures = Mathf.Max(0, activeGestures - 1);
                }
            }
        }
    }

    void OnDragStart(PointerSample p)
    {
        if (dragKind == DragKind.Centroid) { dragKind = DragKind.None; return; }
        if (dragKind != DragKind.Node)
        {
            bool sourceHandle = dragKind == DragKind.NewSource
                             || (dragKind == DragKind.MoveEdge && dragEdge != null && dragEdge.isSource);
            if (!manager.BeginConnectionDrag(this, p, sourceHandle))
                dragKind = DragKind.None;   // tangan lain sedang menarik koneksi
            return;
        }

        // Drag node yang belum terpilih → seleksi lain dilepas (seperti kbx)
        selectAfterMove = !IsSelected;
        if (selectAfterMove) DeselectAll();

        if (p.xr)
        {
            // Pegang node di jaraknya sekarang sepanjang ray; offset tegak lurus ray dipertahankan
            dragDist   = Mathf.Max(Vector3.Dot(transform.position - p.ray.origin, p.ray.direction), MIN_DRAG_DIST);
            dragOffset = transform.position - p.ray.GetPoint(dragDist);
            lastReach  = p.reach;
        }

        moveSet.Clear();
        if (IsSelected)
            foreach (var n in selection) { if (n) moveSet.Add((n, n.transform.position)); }
        else
            moveSet.Add((this, transform.position));
    }

    void OnDragEnd(PointerSample p, bool canceled)
    {
        switch (dragKind)
        {
            case DragKind.Node:
                manager.CommitMove(moveSet);
                moveSet.Clear();
                if (selectAfterMove) SelectMe();
                break;
            case DragKind.NewTarget:
            case DragKind.NewSource:
                if (canceled) manager.CancelConnectionDrag();
                else manager.EndConnectionDrag(p, this, dragKind == DragKind.NewSource);
                break;
            case DragKind.MoveEdge:
                if (canceled) manager.CancelConnectionDrag();
                else manager.EndEdgeMove(p, this, dragEdge);
                break;
        }
    }

    // Seperti kbx, tap pada handle tidak melakukan apa-apa — handle hanya untuk di-drag
    void OnTap()
    {
        if      (dragKind == DragKind.Node)     OnNodeTapped();
        else if (dragKind == DragKind.Centroid) manager.CentroidLink(this);
    }

    RaycastHit? RaycastMe(Ray ray)
    {
        if (!Physics.Raycast(ray, out RaycastHit hit)) return null;
        return (hit.transform == transform || hit.transform.IsChildOf(transform)) ? hit : (RaycastHit?)null;
    }

    bool IsBtn(Transform t, GameObject btn) =>
        btn != null && btn.activeSelf && (t == btn.transform || t.IsChildOf(btn.transform));

    void ComputeDragOffset(Ray r)
    {
        Plane p = new Plane(arCamera.transform.forward, transform.position);
        if (p.Raycast(r, out float d)) dragOffset = transform.position - r.GetPoint(d);
    }

    // Dorong/tarik di VR: thumbstick (m/detik) + gerakan tangan maju/mundur diperkuat
    const float STICK_DEADZONE = 0.2f;
    const float DEPTH_SPEED    = 1.2f;
    const float REACH_GAIN     = 3f;
    const float MIN_DRAG_DIST  = 0.3f;
    const float MAX_DRAG_DIST  = 6f;

    void MoveNode(PointerSample s)
    {
        Vector3 target;
        if (s.xr)
        {
            float stick = Mathf.Abs(s.depthAxis) > STICK_DEADZONE ? s.depthAxis : 0f;
            dragDist += stick * DEPTH_SPEED * Time.deltaTime + (s.reach - lastReach) * REACH_GAIN;
            dragDist  = Mathf.Clamp(dragDist, MIN_DRAG_DIST, MAX_DRAG_DIST);
            lastReach = s.reach;
            target    = s.ray.GetPoint(dragDist) + dragOffset;
        }
        else
        {
            Plane p = new Plane(arCamera.transform.forward, transform.position);
            if (!p.Raycast(s.ray, out float d)) return;
            target = s.ray.GetPoint(d) + dragOffset;
        }

        Vector3 delta = target - transform.position;
        foreach (var (n, _) in moveSet)
            if (n) n.transform.position += delta;
    }

    // ── Tap logic ─────────────────────────────────
    void OnNodeTapped()
    {
        float now = Time.unscaledTime;
        float window = PointerInput.IsXR ? XR_DOUBLE_TAP_SEC : DOUBLE_TAP_SEC;
        if (lastTapNode == this && now - lastTapTime < window)
        {
            lastTapNode = null;
            manager.SelectNeighborhood(this);
            return;
        }
        lastTapNode = this;
        lastTapTime = now;

        if (IsSelected && selection.Count == 1) { DeselectAll(); return; }
        SelectMe();
    }

    // ── Seleksi ───────────────────────────────────
    void SelectMe()
    {
        DeselectAll();
        selection.Add(this);
        SetSelectedVisual(true);
        RefreshHandles();
        manager.ShowStatus(this is LinkNode
            ? "Drag handle kiri/kanan ke konsep | drag handle koneksi untuk pindah/putus"
            : "Drag untuk pindah | double-tap pilih tetangga");
    }

    public static void SelectGroup(System.Collections.Generic.IEnumerable<MapNode> nodes)
    {
        DeselectAll();
        foreach (var n in nodes)
        {
            if (n == null || selection.Contains(n)) continue;
            selection.Add(n);
            n.SetSelectedVisual(true);
        }
        if (selection.Count == 1) selection[0].RefreshHandles();
    }

    public static void DeselectAll()
    {
        foreach (var n in selection)
        {
            if (n == null) continue;
            n.SetSelectedVisual(false);
            n.HideHandles();
        }
        selection.Clear();
    }

    /// Dipanggil manager setiap kali koneksi berubah (connect, putus, undo, ...)
    public static void RefreshSelectedHandles()
    {
        var p = Primary;
        if (p != null) p.RefreshHandles();
    }

    void RefreshHandles()
    {
        HideHandles();
        if (!HasConnectButtons || manager == null) return;

        // Seperti kbx: handle kiri hanya bila belum ada source, kanan selama target < limit
        var edges = manager.GetEdges(NodeId);
        int targets = 0;
        bool hasSource = false;
        foreach (var e in edges)
        {
            if (e.isSource) hasSource = true; else targets++;
            edgeHandles.Add((CreateHandle("EdgeHandle", e.isSource), e));
        }
        if (connectBwdBtn != null) connectBwdBtn.SetActive(!hasSource);
        if (connectFwdBtn != null) connectFwdBtn.SetActive(targets < manager.GetLinkLimit(NodeId));
        if (centroidBtn   != null) centroidBtn.SetActive(edges.Count > 1);   // kbx: connectedEdges > 1
        PositionEdgeHandles();
    }

    void HideHandles()
    {
        if (connectFwdBtn != null) connectFwdBtn.SetActive(false);
        if (connectBwdBtn != null) connectBwdBtn.SetActive(false);
        if (centroidBtn   != null) centroidBtn.SetActive(false);
        foreach (var h in edgeHandles) if (h.go) Destroy(h.go);
        edgeHandles.Clear();
    }

    // Handle koneksi diletakkan tepat di ujung garis pada tepi konsep
    // (renderedTargetEndpoint di kbx).
    void PositionEdgeHandles()
    {
        if (edgeHandles.Count == 0 || manager == null) return;

        Vector3 ls   = transform.lossyScale;
        float   zoom = AutoBaseScale.x > 0f ? transform.localScale.x / AutoBaseScale.x : 1f;
        float   size = BtnSize * zoom;
        Vector3 toCam = arCamera != null ? (arCamera.transform.position - transform.position).normalized : Vector3.zero;

        foreach (var (go, e) in edgeHandles)
        {
            var c = manager.GetNode(e.ConceptId);
            if (go == null || c == null) continue;

            Vector3 cp  = c.transform.position;
            Vector3 dir = transform.position - cp;
            if (dir.sqrMagnitude < 1e-6f) dir = c.transform.right;
            dir.Normalize();

            go.transform.position   = cp + dir * BoundaryDistance(c.transform, dir) + toCam * 0.01f;
            go.transform.localScale = new Vector3(size / ls.x, size / ls.y, 1f);

            // kbx drawImageRot: ikon diputar searah edge relasi→konsep,
            // +180° untuk handle kiri (jadi menunjuk konsep→relasi)
            Vector3 v   = e.isSource ? dir : -dir;
            float angle = Mathf.Atan2(Vector3.Dot(v, go.transform.up), Vector3.Dot(v, go.transform.right)) * Mathf.Rad2Deg;
            if (go.transform.childCount > 0)
                go.transform.GetChild(0).localRotation = Quaternion.Euler(0f, 0f, angle);
        }
    }

    void SetSelectedVisual(bool on)
    {
        // Versi web: node terpilih dapat border merah, warna isi tetap
        if (texSelected != null) SetMeshTexture(on ? texSelected : texNormal);
        else SetMeshColor(on ? Color.Lerp(GetBaseColor(), Color.white, 0.45f) : GetBaseColor());
    }

    void SetMeshColor(Color c)
    {
        if (meshRenderer == null) return;
        meshRenderer.GetPropertyBlock(propBlock);
        propBlock.SetColor("_Color", c);
        meshRenderer.SetPropertyBlock(propBlock);
    }

    protected abstract Color GetBaseColor();
}
