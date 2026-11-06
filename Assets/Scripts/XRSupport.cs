using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using TouchPhase = UnityEngine.TouchPhase;
using XRController = UnityEngine.InputSystem.XR.XRController;

public class XRSupport : MonoBehaviour
{
    public static XRSupport Instance { get; private set; }

    public static bool Active => Instance != null && Instance.EnsureRunning();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("XRSupport");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<XRSupport>();
    }

    const int   RIGHT_ID      = 100;
    const int   LEFT_ID       = 101;
    const float RAY_LENGTH    = 4f;
    const float PANEL_DIST    = 0.9f;
    const float PANEL_DROP    = 0.2f;
    const float PANEL_SCALE   = 0.0009f;
    const float FOLLOW_ANGLE  = 40f;
    const float FOLLOW_SPEED  = 4f;
    const string SCENE_PERMISSION = "com.oculus.permission.USE_SCENE";

    static readonly Vector2 PANEL_SIZE = new Vector2(1000f, 620f);
    static readonly List<XRDisplaySubsystem> displays = new();

    class Hand
    {
        public int          id;
        public InputAction  position, rotation, select;
        public LineRenderer line;
        public Transform    cursor;           // ikon tangan di ujung ray
        public MeshRenderer cursorRenderer;
        public MaterialPropertyBlock cursorBlock;
        public HandCursor.Pose cursorPose = (HandCursor.Pose)(-1);
        public bool         grabbing;         // tekan dimulai di atas node/handle
        public bool         tracked;
        public Ray          ray;
        public InternedString usage;          // LeftHand / RightHand
        public XRController controller;       // null saat memakai hand tracking
        public Transform    model;            // model controller sederhana
        public Renderer     triggerRend;
    }

    static readonly Color MODEL_BODY    = new Color(0.16f, 0.16f, 0.18f, 1f);
    static readonly Color MODEL_FACE    = new Color(0.24f, 0.24f, 0.27f, 1f);
    static readonly Color MODEL_BUTTON  = new Color(0.55f, 0.55f, 0.6f, 1f);
    static readonly Color TRIGGER_IDLE  = new Color(0.35f, 0.35f, 0.4f, 1f);
    static readonly Color TRIGGER_PRESS = new Color(0.36f, 0.75f, 0.92f, 1f);   // #5BC0EB

    bool        running;
    Camera      cam;
    Transform   trackingOrigin;
    Hand[]      hands;
    InputAction scaleAction, recenterAction;

    readonly List<Canvas> canvases = new();
    float      scanTimer;
    bool       panelPlaced;
    Vector3    panelPos;
    Quaternion panelRot;

    public float ScaleAxis => scaleAction != null ? scaleAction.ReadValue<Vector2>().y : 0f;

    public bool HeadTracked => cam != null && cam.transform.localPosition.sqrMagnitude > 0f;

    /// Latar polos saat passthrough mati (default saat mengerjakan peta)
    static readonly Color XR_BACKGROUND = Color.black;

    /// Passthrough (dunia nyata) hanya dinyalakan saat perlu, mis. scan QR soal.
    /// Di Quest passthrough mengikuti ARCameraManager.enabled; latar kamera harus
    /// transparan saat ON dan polos saat OFF.
    public bool Passthrough { get; set; }

    bool EnsureRunning()
    {
        if (running) return true;
        displays.Clear();
        SubsystemManager.GetSubsystems(displays);
        foreach (var d in displays)
        {
            if (!d.running) continue;
            running = true;
            Setup();
            break;
        }
        return running;
    }

    void Setup()
    {
        cam            = Camera.main;
        trackingOrigin = cam != null ? cam.transform.parent : null;

        ConfigureCamera();
        CreateActions();
        ConfigureUIModule();
        RequestScenePermission();
    }

    void Update()
    {
        if (!EnsureRunning()) return;
        if (cam == null) cam = Camera.main;
        ConfigureCamera();   // tiap frame: latar & passthrough mengikuti state Passthrough

        ScanCanvases();
        if (recenterAction.WasPressedThisFrame()) panelPlaced = false;
        UpdateCenterCombo();
        FollowHead();
    }

    // ── Combo "Pusat": peta + menu pindah ke depan wajah ──────────
    // Kedua grip controller ditahan, ATAU trigger/pinch kedua tangan di ruang kosong ditahan.
    const float COMBO_HOLD = 0.6f;
    float comboTimer;
    bool  comboFired;

    void UpdateCenterCombo()
    {
        if (hands == null) return;
        bool bothSelect = hands[0].select.IsPressed() && hands[1].select.IsPressed()
                       && !hands[0].grabbing && !hands[1].grabbing;
        bool bothGrip   = GripPressed(hands[0]) && GripPressed(hands[1]);

        if (!bothSelect && !bothGrip) { comboTimer = 0f; comboFired = false; return; }

        comboTimer += Time.unscaledDeltaTime;
        if (comboFired || comboTimer < COMBO_HOLD) return;
        comboFired = true;

        var manager = FindFirstObjectByType<ConceptMapManager>();
        if (manager != null) manager.CenterNodes();
        panelPlaced = false;
        var toolbar = FindFirstObjectByType<AppToolbar>();
        if (toolbar != null) toolbar.ShowToast("Peta dipindah ke depan", 1.5f);
    }

    static bool GripPressed(Hand h)
    {
        if (h.controller == null) return false;
        var grip = h.controller.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>("gripPressed");
        return grip != null && grip.isPressed;
    }

    void LateUpdate()
    {
        if (!running) return;
        foreach (var h in hands) { UpdateController(h); UpdateRayVisual(h); }
    }

    void OnDestroy()
    {
        if (hands == null) return;
        foreach (var h in hands) { h.position.Dispose(); h.rotation.Dispose(); h.select.Dispose(); }
        scaleAction.Dispose();
        recenterAction.Dispose();
    }

    void ConfigureCamera()
    {
        if (cam == null) return;

        var manager = cam.GetComponent<ARCameraManager>();
        if (manager != null && manager.enabled != Passthrough) manager.enabled = Passthrough;

        Color target = Passthrough ? new Color(0f, 0f, 0f, 0f) : XR_BACKGROUND;
        if (cam.clearFlags == CameraClearFlags.SolidColor && cam.backgroundColor == target) return;
        var bg = cam.GetComponent<ARCameraBackground>();
        if (bg != null) bg.enabled = false;
        cam.clearFlags      = CameraClearFlags.SolidColor;
        cam.backgroundColor = target;
        cam.nearClipPlane   = 0.05f;
    }

    void CreateActions()
    {
        hands = new[] { MakeHand(RIGHT_ID, "RightHand"), MakeHand(LEFT_ID, "LeftHand") };

        scaleAction = new InputAction("Scale", InputActionType.Value, expectedControlType: "Vector2");
        scaleAction.AddBinding("<XRController>{RightHand}/thumbstick");
        scaleAction.Enable();

        recenterAction = new InputAction("Recenter", InputActionType.Button);
        recenterAction.AddBinding("<XRController>{LeftHand}/menu");
        recenterAction.AddBinding("<XRController>{RightHand}/secondaryButton");
        recenterAction.Enable();
    }

    Hand MakeHand(int id, string side)
    {
        var h = new Hand { id = id, usage = new InternedString(side) };

        h.position = new InputAction($"{side}Position", InputActionType.Value, expectedControlType: "Vector3");
        h.position.AddBinding($"<XRController>{{{side}}}/pointerPosition");

        h.rotation = new InputAction($"{side}Rotation", InputActionType.Value, expectedControlType: "Quaternion");
        h.rotation.AddBinding($"<XRController>{{{side}}}/pointerRotation");

        h.select = new InputAction($"{side}Select", InputActionType.Button);
        h.select.AddBinding($"<XRController>{{{side}}}/triggerPressed");
        h.select.AddBinding($"<HandInteraction>{{{side}}}/pinchValue");

        h.position.Enable();
        h.rotation.Enable();
        h.select.Enable();

        h.line = new GameObject($"{side}Ray").AddComponent<LineRenderer>();
        h.line.transform.SetParent(transform, false);
        h.line.useWorldSpace  = true;
        h.line.positionCount  = 2;
        h.line.startWidth     = 0.004f;
        h.line.endWidth       = 0.002f;
        h.line.numCapVertices = 4;
        h.line.material       = new Material(Shader.Find("Sprites/Default"));
        h.line.startColor     = new Color(1f, 1f, 1f, 0.9f);
        h.line.endColor       = new Color(1f, 1f, 1f, 0.15f);
        h.line.enabled        = false;

        // Sama seperti handle relasi: quad + Sprites/Default, tekstur lewat property block
        var cursor = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(cursor.GetComponent<Collider>());
        cursor.name = $"{side}Cursor";
        DontDestroyOnLoad(cursor);
        h.cursorRenderer = cursor.GetComponent<MeshRenderer>();
        h.cursorRenderer.material = new Material(Shader.Find("Sprites/Default"));
        h.cursorBlock = new MaterialPropertyBlock();
        cursor.SetActive(false);
        h.cursor = cursor.transform;

        BuildControllerModel(h, side);
        return h;
    }

    bool UpdateRay(Hand h)
    {
        Quaternion rot = h.rotation.ReadValue<Quaternion>();
        h.tracked = h.position.activeControl != null && rot != new Quaternion(0f, 0f, 0f, 0f);
        if (!h.tracked) return false;

        Vector3 pos = h.position.ReadValue<Vector3>();
        if (trackingOrigin != null)
        {
            pos = trackingOrigin.TransformPoint(pos);
            rot = trackingOrigin.rotation * rot;
        }
        h.ray = new Ray(pos, rot * Vector3.forward);
        return true;
    }

    public void CollectSamples(List<PointerSample> list)
    {
        if (hands == null) return;
        foreach (var h in hands)
        {
            bool pressed  = h.select.WasPressedThisFrame();
            bool released = h.select.WasReleasedThisFrame();
            bool held     = h.select.IsPressed();
            if (!pressed && !released && !held) continue;

            if (!UpdateRay(h))
            {
                if (released || held) list.Add(Sample(h, TouchPhase.Canceled, false));
                continue;
            }

            bool overUI = IsOverUI(h.ray);
            if (pressed)
                list.Add(Sample(h, TouchPhase.Began, overUI));
            if (released)
                list.Add(Sample(h, TouchPhase.Ended, overUI));
            else if (held && !pressed)
                list.Add(Sample(h, TouchPhase.Moved, overUI));
        }
    }

    PointerSample Sample(Hand h, TouchPhase phase, bool overUI) => new PointerSample
    {
        id        = h.id,
        phase     = phase,
        ray       = h.ray,
        xr        = true,
        overUI    = overUI,
        depthAxis = ThumbstickY(h),
        reach     = cam != null ? Vector3.Distance(cam.transform.position, h.ray.origin) : 0f,
    };

    static float ThumbstickY(Hand h)
    {
        if (h.controller == null) return 0f;
        var stick = h.controller.TryGetChildControl<UnityEngine.InputSystem.Controls.Vector2Control>("thumbstick");
        return stick != null ? stick.ReadValue().y : 0f;
    }

    // ── Model controller ──────────────────────────
    // Cari controller fisik (bukan device hand-interaction) yang sedang ter-track
    void UpdateController(Hand h)
    {
        h.controller = null;
        foreach (var d in InputSystem.devices)
        {
            if (d is not XRController xc || !d.usages.Contains(h.usage)) continue;
            if (d.layout.IndexOf("Hand", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (!xc.isTracked.isPressed) continue;
            h.controller = xc;
            break;
        }

        bool show = h.controller != null;
        if (h.model.gameObject.activeSelf != show) h.model.gameObject.SetActive(show);
        if (!show) return;

        Vector3    pos = h.controller.devicePosition.ReadValue();
        Quaternion rot = h.controller.deviceRotation.ReadValue();
        if (trackingOrigin != null)
        {
            pos = trackingOrigin.TransformPoint(pos);
            rot = trackingOrigin.rotation * rot;
        }
        h.model.SetPositionAndRotation(pos, rot);

        var trigger = h.controller.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>("triggerPressed");
        h.triggerRend.material.color = trigger != null && trigger.isPressed ? TRIGGER_PRESS : TRIGGER_IDLE;
    }

    // Bentuk kasar Touch Plus di grip pose: badan memanjang ke depan (+Z),
    // muka atas dengan thumbstick & 2 tombol, trigger di bawah depan.
    void BuildControllerModel(Hand h, string side)
    {
        var root = new GameObject($"{side}Controller").transform;
        root.SetParent(transform, false);
        h.model = root;

        Part(root, PrimitiveType.Capsule,  new Vector3(0f, -0.005f, -0.01f), new Vector3(90f, 0f, 0f),
             new Vector3(0.034f, 0.055f, 0.034f), MODEL_BODY);
        Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.012f, 0.04f), new Vector3(-20f, 0f, 0f),
             new Vector3(0.05f, 0.006f, 0.05f), MODEL_FACE);
        float sx = side == "LeftHand" ? 0.008f : -0.008f;
        Part(root, PrimitiveType.Cylinder, new Vector3(sx, 0.022f, 0.042f), new Vector3(-20f, 0f, 0f),
             new Vector3(0.014f, 0.006f, 0.014f), MODEL_BUTTON);
        Part(root, PrimitiveType.Sphere,   new Vector3(-sx * 1.4f, 0.017f, 0.052f), Vector3.zero,
             Vector3.one * 0.008f, MODEL_BUTTON);
        Part(root, PrimitiveType.Sphere,   new Vector3(-sx * 1.4f, 0.019f, 0.036f), Vector3.zero,
             Vector3.one * 0.008f, MODEL_BUTTON);
        h.triggerRend = Part(root, PrimitiveType.Cube, new Vector3(0f, -0.022f, 0.045f), new Vector3(25f, 0f, 0f),
             new Vector3(0.014f, 0.012f, 0.022f), TRIGGER_IDLE);

        root.gameObject.SetActive(false);
    }

    static Renderer Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 euler, Vector3 scale, Color color)
    {
        var go = GameObject.CreatePrimitive(type);
        Destroy(go.GetComponent<Collider>());   // jangan ikut kena raycast node
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localEulerAngles = euler;
        go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>();
        r.material = new Material(Shader.Find("Sprites/Default")) { color = color };
        return r;
    }

    bool IsOverUI(Ray ray)
    {
        if (!RaycastUI(ray, out float uiDist)) return false;
        return !Physics.Raycast(ray, out RaycastHit hit, RAY_LENGTH) || uiDist <= hit.distance;
    }

    bool RaycastUI(Ray ray, out float dist)
    {
        dist = float.MaxValue;
        bool found = false;
        foreach (var c in canvases)
        {
            if (c == null || !c.isActiveAndEnabled) continue;
            var graphics = GraphicRegistry.GetRaycastableGraphicsForCanvas(c);
            for (int i = 0; i < graphics.Count; i++)
            {
                var g = graphics[i];
                if (!g.isActiveAndEnabled || g.depth == -1 || g.canvasRenderer.cull) continue;
                var rt = g.rectTransform;
                if (!new Plane(rt.forward, rt.position).Raycast(ray, out float d) || d >= dist) continue;
                if (!rt.rect.Contains(rt.InverseTransformPoint(ray.GetPoint(d)))) continue;
                dist  = d;
                found = true;
            }
        }
        return found;
    }

    void UpdateRayVisual(Hand h)
    {
        if (!UpdateRay(h))
        {
            h.line.enabled = false;
            h.cursor.gameObject.SetActive(false);
            return;
        }

        float   length = RAY_LENGTH;
        bool    hit    = false;
        MapNode node   = null;   // node yang ditunjuk
        bool    onBody = false;  // kena badan kotak (bukan handle/tombol)
        if (Physics.Raycast(h.ray, out RaycastHit physHit, RAY_LENGTH))
        {
            length = physHit.distance; hit = true;
            node   = physHit.transform.GetComponentInParent<MapNode>();
            onBody = node != null && physHit.transform == node.transform;
        }
        if (RaycastUI(h.ray, out float uiDist) && uiDist < length) { length = uiDist; hit = true; node = null; onBody = false; }

        Vector3 end = h.ray.GetPoint(length);
        h.line.enabled = true;
        h.line.SetPosition(0, h.ray.origin);
        h.line.SetPosition(1, end);

        // Pose kursor: genggam saat memegang node/handle, terbuka di atas node, selain itu menunjuk
        if (h.select.WasPressedThisFrame()) h.grabbing = node != null;
        if (!h.select.IsPressed())          h.grabbing = false;
        var pose = h.grabbing ? HandCursor.Pose.Grab : onBody ? HandCursor.Pose.Open : HandCursor.Pose.Point;
        UpdateCursor(h, hit ? end : h.ray.GetPoint(CURSOR_IDLE_DIST), pose);
    }

    const float CURSOR_IDLE_DIST = 1.2f;   // posisi kursor saat ray tidak mengenai apa pun
    const float CURSOR_ANGLE     = 0.05f;  // ukuran ≈ 2,9° pandangan, berapa pun jaraknya

    void UpdateCursor(Hand h, Vector3 point, HandCursor.Pose pose)
    {
        if (cam == null) return;
        if (h.cursorPose != pose)
        {
            h.cursorPose = pose;
            h.cursorRenderer.GetPropertyBlock(h.cursorBlock);
            h.cursorBlock.SetTexture("_MainTex", HandCursor.Get(pose));
            h.cursorBlock.SetColor("_Color", Color.white);
            h.cursorRenderer.SetPropertyBlock(h.cursorBlock);
        }

        Transform head = cam.transform;
        float   dist = Vector3.Distance(head.position, point);
        float   size = Mathf.Max(0.02f, dist * CURSOR_ANGLE);
        Vector3 toCam = (head.position - point).normalized;
        Quaternion rot = Quaternion.LookRotation(-toCam, head.up);

        // Geser quad supaya titik tunjuk ikon (ujung telunjuk) tepat di titik yang ditunjuk
        Vector2 hs = HandCursor.Hotspot(pose) - new Vector2(0.5f, 0.5f);
        Vector3 offset = rot * new Vector3(-hs.x * size, -hs.y * size, 0f);

        h.cursor.gameObject.SetActive(true);
        h.cursor.SetPositionAndRotation(point + offset + toCam * 0.03f, rot);
        h.cursor.localScale = new Vector3(size, size, 1f);

    }


    void ConfigureUIModule()
    {
        var es = EventSystem.current != null ? EventSystem.current : FindAnyObjectByType<EventSystem>();
        if (es == null) es = new GameObject("EventSystem").AddComponent<EventSystem>();

        foreach (var legacy in es.GetComponents<StandaloneInputModule>()) legacy.enabled = false;
        var module = es.GetComponent<InputSystemUIInputModule>();
        if (module == null) module = es.gameObject.AddComponent<InputSystemUIInputModule>();

        var asset = ScriptableObject.CreateInstance<InputActionAsset>();
        var map   = asset.AddActionMap("XRUI");

        var position = map.AddAction("TrackedDevicePosition", InputActionType.PassThrough, expectedControlLayout: "Vector3");
        position.AddBinding("<XRController>/pointerPosition");

        var rotation = map.AddAction("TrackedDeviceOrientation", InputActionType.PassThrough, expectedControlLayout: "Quaternion");
        rotation.AddBinding("<XRController>/pointerRotation");

        var click = map.AddAction("Click", InputActionType.PassThrough, expectedControlLayout: "Button");
        click.AddBinding("<XRController>/triggerPressed");
        click.AddBinding("<HandInteraction>/pinchValue");

        module.actionsAsset             = asset;
        module.trackedDevicePosition    = InputActionReference.Create(position);
        module.trackedDeviceOrientation = InputActionReference.Create(rotation);
        module.leftClick                = InputActionReference.Create(click);
        module.xrTrackingOrigin         = trackingOrigin;
        asset.Enable();
    }

    void ScanCanvases()
    {
        scanTimer -= Time.unscaledDeltaTime;
        if (scanTimer > 0f) return;
        scanTimer = 0.5f;

        foreach (var c in FindObjectsByType<Canvas>())
        {
            if (!c.isRootCanvas || c.renderMode == RenderMode.WorldSpace || canvases.Contains(c)) continue;
            if (c.GetComponent<GraphicRaycaster>() == null) continue;
            ToWorldSpace(c);
        }
    }

    void ToWorldSpace(Canvas c)
    {
        var scaler = c.GetComponent<CanvasScaler>();
        if (scaler != null) scaler.enabled = false;

        c.renderMode  = RenderMode.WorldSpace;
        c.worldCamera = cam;

        var rt = (RectTransform)c.transform;
        rt.sizeDelta  = PANEL_SIZE;
        rt.localScale = Vector3.one * PANEL_SCALE;

        c.GetComponent<GraphicRaycaster>().enabled = false;
        if (c.GetComponent<TrackedDeviceRaycaster>() == null)
            c.gameObject.AddComponent<TrackedDeviceRaycaster>();

        canvases.Add(c);
        canvases.Sort((a, b) => a.sortingOrder.CompareTo(b.sortingOrder));
        PlacePanels(snap: true);
    }

    void FollowHead()
    {
        if (cam == null || canvases.Count == 0) return;

        Vector3 head = cam.transform.position;
        Vector3 fwd  = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) return;
        fwd.Normalize();

        Vector3 toPanel = Vector3.ProjectOnPlane(panelPos - head, Vector3.up);
        bool needsMove = !panelPlaced
                      || Vector3.Angle(fwd, toPanel) > FOLLOW_ANGLE
                      || Mathf.Abs(toPanel.magnitude - PANEL_DIST) > 0.4f;
        if (needsMove)
        {
            panelPos    = head + fwd * PANEL_DIST + Vector3.down * PANEL_DROP;
            panelRot    = Quaternion.LookRotation(fwd, Vector3.up);
            bool snap   = !panelPlaced;
            panelPlaced = true;
            PlacePanels(snap);
            return;
        }
        PlacePanels(snap: false);
    }

    void PlacePanels(bool snap)
    {
        float t = snap ? 1f : 1f - Mathf.Exp(-FOLLOW_SPEED * Time.unscaledDeltaTime);
        for (int i = 0; i < canvases.Count; i++)
        {
            var c = canvases[i];
            if (c == null) continue;
            Vector3 target = panelPos - panelRot * Vector3.forward * (0.003f * i);
            c.transform.position = Vector3.Lerp(c.transform.position, target, t);
            c.transform.rotation = Quaternion.Slerp(c.transform.rotation, panelRot, t);
        }
    }

    void RequestScenePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(SCENE_PERMISSION)) return;
        var callbacks = new UnityEngine.Android.PermissionCallbacks();
        callbacks.PermissionGranted += _ => RestartSceneManagers();
        UnityEngine.Android.Permission.RequestUserPermission(SCENE_PERMISSION, callbacks);
#endif
    }

    void RestartSceneManagers()
    {
        foreach (var m in FindObjectsByType<ARPlaneManager>())
        {
            if (!m.enabled) continue;
            m.enabled = false;
            m.enabled = true;
        }
    }
}
