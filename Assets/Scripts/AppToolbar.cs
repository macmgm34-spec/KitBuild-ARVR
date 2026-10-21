using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Toolbar bawah (collapse lewat tombol menu): Muat, Undo/Redo, Rapi,
/// Pusat, Simpan, dan lihat JSON. Dibangun penuh dari kode.
/// </summary>
public class AppToolbar : MonoBehaviour
{
    private ConceptMapManager mapManager;

    private GameObject bottomPanel;

    // Overlay view JSON
    private GameObject      jsonOverlay;
    private TextMeshProUGUI jsonText;

    // Panel muat JSON
    private GameObject loadPanel;
    private Transform  fileListContent;

    // Scan QR / ketik link soal
    private QrScanner           qrScanner;
    private TouchScreenKeyboard linkKeyboard;

    // Toast notification
    private GameObject      toastGO;
    private TextMeshProUGUI toastTxt;
    private float           toastTimer;

    const string SAVE_FOLDER = "Download/ConceptMapAR";

    void Start()
    {
        mapManager = FindFirstObjectByType<ConceptMapManager>();
        qrScanner  = gameObject.AddComponent<QrScanner>();
        BuildToolbar();

        // Receiver hasil file picker native (UnitySendMessage target)
        var cb = new GameObject("JsonPickerCallback");
        cb.AddComponent<JsonPickerCallback>().toolbar = this;
    }

    void Update()
    {
        PollLinkKeyboard();

        if (toastTimer > 0f)
        {
            toastTimer -= Time.deltaTime;
            if (toastTimer <= 0f) toastGO.SetActive(false);
        }
    }

    // ── Dipanggil dari JsonFilePicker.java via UnitySendMessage ──
    public void HandlePickedJson(string json) => LoadJson(json);

    public void HandlePickError(string msg)
    {
        if (msg == "dibatalkan") return; // user menutup picker, bukan error
        ShowToast($"❌ {msg}", 3.5f);
    }

    // ─────────────────────────────────────────────
    //  BUILD UI
    // ─────────────────────────────────────────────
    void BuildToolbar()
    {
        // ── Canvas ────────────────────────────────
        var canvasGO = new GameObject("ToolbarCanvas");
        var canvas   = canvasGO.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 99;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(390, 844);
        scaler.screenMatchMode     = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight  = 0.5f;   // seimbang → aman portrait & landscape
        canvasGO.AddComponent<GraphicRaycaster>();

        // ── Tombol menu (selalu tampil, toggle panel) ─
        var menuGO = new GameObject("MenuBtn");
        menuGO.transform.SetParent(canvasGO.transform, false);
        var menuRT = menuGO.AddComponent<RectTransform>();
        menuRT.anchorMin = new Vector2(1, 0);
        menuRT.anchorMax = new Vector2(1, 0);
        menuRT.pivot     = new Vector2(1, 0);
        // Di atas area panel (80px) supaya tidak tertutup saat panel terbuka
        menuRT.anchoredPosition = new Vector2(-10, 92);
        menuRT.sizeDelta = new Vector2(72, 48);
        menuGO.AddComponent<Image>().color = new Color(0.2f, 0.45f, 0.85f, 0.95f);
        menuGO.AddComponent<Button>().onClick.AddListener(ToggleToolbar);
        AddIcon(menuGO.transform, ToolbarIcons.Menu(), 28);

        // ── Panel bawah (default tersembunyi) ─────
        var panel = MakeRect("BottomPanel", canvasGO.transform);
        panel.gameObject.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.1f, 0.85f);
        panel.anchorMin = new Vector2(0, 0);
        panel.anchorMax = new Vector2(1, 0);
        panel.offsetMin = new Vector2(0, 0);
        panel.offsetMax = new Vector2(0, 80);
        bottomPanel = panel.gameObject;

        var buttons = new (string label, Color color, UnityAction cb, Sprite icon)[]
        {
            ("Muat",   new Color(0.8f,  0.5f,  0.15f), OnLoadPanel,  ToolbarIcons.Load()),
            ("Undo",   new Color(0.55f, 0.42f, 0.18f), OnUndo,       ToolbarIcons.Undo()),
            ("Redo",   new Color(0.55f, 0.42f, 0.18f), OnRedo,       ToolbarIcons.Redo()),
            ("Rapi",   new Color(0.15f, 0.55f, 0.6f),  OnAutoLayout, ToolbarIcons.Grid()),
            ("Pusat",  new Color(0.25f, 0.45f, 0.7f),  OnCenter,     ToolbarIcons.Center()),
            ("Periksa", new Color(0.23f, 0.72f, 0.58f), OnAssess,    ToolbarIcons.Check()),
            ("Simpan", new Color(0.2f,  0.6f,  0.3f),  OnSave,       ToolbarIcons.Save()),
            ("JSON",   new Color(0.2f,  0.4f,  0.8f),  OnViewJson,   ToolbarIcons.Json()),
        };
        float w = 1f / buttons.Length;
        for (int i = 0; i < buttons.Length; i++)
        {
            var b = buttons[i];
            MakeButton(panel, b.label, b.color,
                new Vector2(i * w, 0f), new Vector2((i + 1) * w, 1f), b.cb, b.icon);
        }

        BuildJsonOverlay(canvasGO.transform);
        BuildLoadPanel(canvasGO.transform);
        BuildToast(canvasGO.transform);

        bottomPanel.SetActive(false);   // mulai tersembunyi — canvas bersih
    }

    void ToggleToolbar()
    {
        bool show = !bottomPanel.activeSelf;
        bottomPanel.SetActive(show);
        if (!show)
        {
            jsonOverlay.SetActive(false);
            loadPanel.SetActive(false);
        }
    }

    // ─────────────────────────────────────────────
    //  LOAD
    // ─────────────────────────────────────────────
    void LoadJson(string json)
    {
        if (mapManager == null) return;
        try { Newtonsoft.Json.Linq.JToken.Parse(json); }
        catch (Exception e)
        {
            ShowToast($"❌ JSON tidak valid: {e.Message}", 4f);
            return;
        }

        mapManager.ApplyMapJson(json);
        loadPanel.SetActive(false);
        ShowToast("Peta konsep dimuat.", 2f);
    }

    void BuildLoadPanel(Transform canvasT)
    {
        loadPanel = new GameObject("LoadPanel");
        loadPanel.transform.SetParent(canvasT, false);
        loadPanel.AddComponent<Image>().color = new Color(0.05f, 0.05f, 0.08f, 0.95f);
        var rt = loadPanel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.05f, 0.15f);
        rt.anchorMax = new Vector2(0.95f, 0.85f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        MakeLabel(loadPanel.transform, "Muat Peta Konsep", 16, Color.white,
            new Vector2(0, 0.9f), new Vector2(1, 1));

        // Scan QR berisi link soal (JSON) dari layar HP — kamera passthrough Quest
        MakeOverlayBtn(loadPanel.transform, "Scan QR Soal (tap lagi untuk batal)",
            new Color(0.15f, 0.6f, 0.35f),
            new Vector2(0.05f, 0.80f), new Vector2(0.95f, 0.89f),
            OnScanQr);

        MakeOverlayBtn(loadPanel.transform, "Ketik Link",
            new Color(0.35f, 0.35f, 0.75f),
            new Vector2(0.05f, 0.70f), new Vector2(0.5f, 0.79f),
            OnTypeLink);

        // Pilih file lewat file manager sistem (SAF — tanpa permission)
        MakeOverlayBtn(loadPanel.transform, "Pilih File",
            new Color(0.2f, 0.5f, 0.8f),
            new Vector2(0.5f, 0.70f), new Vector2(0.95f, 0.79f),
            OnPickFile);

        MakeOverlayBtn(loadPanel.transform, "Dari Clipboard (JSON / URL)",
            new Color(0.8f, 0.5f, 0.15f),
            new Vector2(0.05f, 0.60f), new Vector2(0.95f, 0.69f),
            OnLoadFromClipboard);

        MakeLabel(loadPanel.transform, "Atau tap file di bawah:", 10, new Color(0.8f, 0.8f, 0.8f),
            new Vector2(0.03f, 0.54f), new Vector2(0.97f, 0.60f));

        // Scroll daftar file
        var scrollGO = new GameObject("FileScroll");
        scrollGO.transform.SetParent(loadPanel.transform, false);
        var scroll   = scrollGO.AddComponent<ScrollRect>();
        var scrollRT = scrollGO.GetComponent<RectTransform>();
        scrollRT.anchorMin = new Vector2(0.03f, 0.12f);
        scrollRT.anchorMax = new Vector2(0.97f, 0.53f);
        scrollRT.offsetMin = Vector2.zero; scrollRT.offsetMax = Vector2.zero;
        scrollGO.AddComponent<RectMask2D>();

        var content = new GameObject("Content");
        content.transform.SetParent(scrollGO.transform, false);
        var contentRT = content.AddComponent<RectTransform>();
        contentRT.anchorMin = new Vector2(0, 1);
        contentRT.anchorMax = new Vector2(1, 1);
        contentRT.pivot     = new Vector2(0.5f, 1);
        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.childControlHeight = false; vlg.childControlWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.spacing = 4; vlg.padding = new RectOffset(4, 4, 4, 4);
        content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content    = contentRT;
        scroll.viewport   = scrollRT;
        scroll.horizontal = false;
        fileListContent = content.transform;

        MakeOverlayBtn(loadPanel.transform, "Tutup",
            new Color(0.7f, 0.2f, 0.2f),
            new Vector2(0.3f, 0.02f), new Vector2(0.7f, 0.1f),
            () => loadPanel.SetActive(false));

        loadPanel.SetActive(false);
    }

    void OnLoadPanel()
    {
        if (loadPanel.activeSelf) { loadPanel.SetActive(false); return; }
        RefreshFileList();
        loadPanel.SetActive(true);
    }

    void RefreshFileList()
    {
        foreach (Transform c in fileListContent) Destroy(c.gameObject);

        var dirs = new List<string>
        {
            Application.persistentDataPath,
            "/storage/emulated/0/" + SAVE_FOLDER,
            "/storage/emulated/0/Download"
        };

        int count = 0;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            string[] files;
            try   { files = Directory.GetFiles(dir, "*.json"); }
            catch { continue; }

            foreach (var path in files)
            {
                if (count >= 30) break;
                var row = new GameObject("File");
                row.transform.SetParent(fileListContent, false);
                row.AddComponent<RectTransform>().sizeDelta = new Vector2(0, 30);
                row.AddComponent<Image>().color = new Color(0.2f, 0.3f, 0.45f);
                row.AddComponent<Button>().onClick.AddListener(() => LoadFromFile(path));
                var lbl = MakeLabel(row.transform, Path.GetFileName(path), 10, Color.white,
                    Vector2.zero, Vector2.one);
                lbl.rectTransform.offsetMin = new Vector2(6, 0);
                lbl.rectTransform.offsetMax = new Vector2(-6, 0);
                count++;
            }
        }

        if (count == 0)
        {
            var empty = new GameObject("Empty");
            empty.transform.SetParent(fileListContent, false);
            empty.AddComponent<RectTransform>().sizeDelta = new Vector2(0, 30);
            var t = empty.AddComponent<TextMeshProUGUI>();
            t.text = "(tidak ada file .json ditemukan)";
            t.fontSize = 10; t.color = new Color(0.7f, 0.7f, 0.7f);
            t.alignment = TextAlignmentOptions.Center;
        }
    }

    void LoadFromFile(string path)
    {
        try { LoadJson(File.ReadAllText(path)); }
        catch (Exception e)
        {
            ShowToast($"❌ Gagal baca file:\n{e.Message}\nCoba lewat clipboard.", 4f);
        }
    }

    void OnPickFile()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var picker = new AndroidJavaClass("com.zidane.conceptmapar.JsonFilePicker");
            picker.CallStatic("pick");
        }
        catch (Exception e)
        {
            ShowToast($"❌ File picker gagal: {e.Message}\nPakai clipboard saja.", 4f);
        }
#else
        ShowToast("File picker hanya di Android — pakai clipboard di Editor.", 3f);
#endif
    }

    void OnLoadFromClipboard()
    {
        string clip = (GUIUtility.systemCopyBuffer ?? "").Trim();
        if (clip.StartsWith("http"))
        {
            ShowToast("⏳ Mengunduh dari URL…", 2f);
            StartCoroutine(LoadFromUrl(clip));
        }
        else if (clip.StartsWith("{"))
        {
            LoadJson(clip);
        }
        else
        {
            ShowToast("❌ Clipboard bukan JSON atau URL.", 3.5f);
        }
    }

    // ── Scan QR ──────────────────────────────────
    void OnScanQr()
    {
        if (qrScanner.IsScanning)
        {
            qrScanner.Stop();
            ShowToast("Scan dibatalkan.", 2f);
            return;
        }
        ShowToast("Arahkan pandangan ke QR di layar HP…", 4f);
        qrScanner.StartScan(OnQrResult, err => ShowToast($"❌ {err}", 4f));
    }

    void OnQrResult(string text)
    {
        if (text.StartsWith("http"))
        {
            ShowToast("⏳ QR terbaca, mengunduh soal…", 3f);
            StartCoroutine(LoadFromUrl(text));
        }
        else if (text.StartsWith("{"))
            LoadJson(text);
        else
            ShowToast($"❌ QR bukan link soal:\n{text}", 4f);
    }

    // ── Ketik link (keyboard sistem Quest/HP) ─────
    void OnTypeLink()
    {
        linkKeyboard = TouchScreenKeyboard.Open("https://", TouchScreenKeyboardType.URL, false, false, false, false,
                                                "Link soal (JSON)");
        if (linkKeyboard == null) ShowToast("❌ Keyboard tidak tersedia — pakai Clipboard.", 3.5f);
    }

    void PollLinkKeyboard()
    {
        if (linkKeyboard == null) return;
        switch (linkKeyboard.status)
        {
            case TouchScreenKeyboard.Status.Done:
                string url = (linkKeyboard.text ?? "").Trim();
                linkKeyboard = null;
                if (url.StartsWith("http") && url.Length > 8)
                {
                    ShowToast("⏳ Mengunduh dari URL…", 2f);
                    StartCoroutine(LoadFromUrl(url));
                }
                else ShowToast("❌ Link tidak valid.", 3f);
                break;
            case TouchScreenKeyboard.Status.Canceled:
            case TouchScreenKeyboard.Status.LostFocus:
                linkKeyboard = null;
                break;
        }
    }

    IEnumerator LoadFromUrl(string url)
    {
        using var req = UnityWebRequest.Get(url);
        req.timeout = 15;
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
            LoadJson(req.downloadHandler.text);
        else
            ShowToast($"❌ Gagal unduh:\n{req.error}", 4f);
    }

    // ─────────────────────────────────────────────
    //  SAVE
    // ─────────────────────────────────────────────
    void OnSave()
    {
        if (mapManager == null) { ShowToast("❌ MapManager tidak ditemukan"); return; }

        string json     = mapManager.GetCurrentJson();
        string fileName = $"concept-map-{DateTime.Now:yyyyMMdd_HHmmss}.json";

        try
        {
            string saved = SaveToStorage(fileName, json);
            GUIUtility.systemCopyBuffer = json;
            ShowToast($"✅ Tersimpan!\n{saved}", 4f);
        }
        catch (Exception e)
        {
            ShowToast($"❌ Gagal simpan:\n{e.Message}", 4f);
        }
    }

    /// Simpan file. Android: ke folder publik Download/ConceptMapAR (terlihat
    /// di Files app). Platform lain / gagal: persistentDataPath.
    static string SaveToStorage(string fileName, string content)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var playerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            var activity = playerClass.GetStatic<AndroidJavaObject>("currentActivity");
            var resolver = activity.Call<AndroidJavaObject>("getContentResolver");

            var values = new AndroidJavaObject("android.content.ContentValues");
            values.Call("put", "_display_name", fileName);
            values.Call("put", "mime_type", "application/json");
            values.Call("put", "relative_path", SAVE_FOLDER);

            using var downloads = new AndroidJavaClass("android.provider.MediaStore$Downloads");
            var collection = downloads.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI");
            var uri = resolver.Call<AndroidJavaObject>("insert", collection, values);

            var stream = resolver.Call<AndroidJavaObject>("openOutputStream", uri);
            stream.Call("write", System.Text.Encoding.UTF8.GetBytes(content));
            stream.Call("flush");
            stream.Call("close");
            return $"{SAVE_FOLDER}/{fileName}";
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AppToolbar] MediaStore gagal ({e.Message}) — fallback persistentDataPath");
        }
#endif
        string path = Path.Combine(Application.persistentDataPath, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    // ─────────────────────────────────────────────
    //  JSON OVERLAY
    // ─────────────────────────────────────────────
    void BuildJsonOverlay(Transform canvasT)
    {
        jsonOverlay = new GameObject("JsonOverlay");
        jsonOverlay.transform.SetParent(canvasT, false);
        jsonOverlay.AddComponent<Image>().color = new Color(0, 0, 0, 0.92f);
        var bgRT = jsonOverlay.GetComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = new Vector2(0, 80);   // di atas toolbar
        bgRT.offsetMax = Vector2.zero;

        var scrollGO = new GameObject("Scroll");
        scrollGO.transform.SetParent(jsonOverlay.transform, false);
        var scroll   = scrollGO.AddComponent<ScrollRect>();
        var scrollRT = scrollGO.GetComponent<RectTransform>();
        scrollRT.anchorMin = new Vector2(0, 0.06f);
        scrollRT.anchorMax = new Vector2(1, 1f);
        scrollRT.offsetMin = new Vector2(12, 0);
        scrollRT.offsetMax = new Vector2(-12, -12);
        scrollGO.AddComponent<RectMask2D>();

        // Teks langsung jadi content scroll (pola standar TMP + ContentSizeFitter)
        var txtGO = new GameObject("JsonText");
        txtGO.transform.SetParent(scrollGO.transform, false);
        jsonText = txtGO.AddComponent<TextMeshProUGUI>();
        jsonText.fontSize  = 11;
        jsonText.color     = new Color(0.9f, 1f, 0.9f);
        jsonText.enableWordWrapping = true;
        jsonText.alignment = TextAlignmentOptions.TopLeft;
        var txtRT = txtGO.GetComponent<RectTransform>();
        txtRT.anchorMin = new Vector2(0, 1);
        txtRT.anchorMax = new Vector2(1, 1);
        txtRT.pivot     = new Vector2(0.5f, 1);
        txtRT.offsetMin = Vector2.zero;
        txtRT.offsetMax = Vector2.zero;
        txtGO.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.content    = txtRT;
        scroll.viewport   = scrollRT;
        scroll.horizontal = false;

        // Bottom bar: Tutup + Copy
        var bottomBar = MakeRect("BottomBar", jsonOverlay.transform);
        bottomBar.anchorMin = new Vector2(0, 0);
        bottomBar.anchorMax = new Vector2(1, 0.07f);
        bottomBar.offsetMin = Vector2.zero;
        bottomBar.offsetMax = Vector2.zero;

        MakeOverlayBtn(bottomBar, "Tutup",
            new Color(0.7f, 0.2f, 0.2f),
            new Vector2(0, 0), new Vector2(0.5f, 1),
            () => jsonOverlay.SetActive(false));

        MakeOverlayBtn(bottomBar, "Copy JSON",
            new Color(0.2f, 0.5f, 0.2f),
            new Vector2(0.5f, 0), new Vector2(1, 1),
            () => {
                GUIUtility.systemCopyBuffer = mapManager.GetCurrentJson();
                ShowToast("JSON di-copy ke clipboard!", 2.5f);
            });

        jsonOverlay.SetActive(false);
    }

    void OnViewJson()
    {
        if (jsonOverlay.activeSelf || mapManager == null) { jsonOverlay.SetActive(false); return; }
        jsonText.text = mapManager.GetCurrentJson();
        jsonOverlay.SetActive(true);
    }

    // ─────────────────────────────────────────────
    //  ACTIONS
    // ─────────────────────────────────────────────
    void OnUndo() { if (mapManager != null) mapManager.Undo(); }
    void OnRedo() { if (mapManager != null) mapManager.Redo(); }

    void OnAutoLayout()
    {
        if (mapManager == null) return;
        mapManager.AutoLayoutNodes();
        ShowToast("Node dirapikan", 1.5f);
    }

    // Penilaian otomatis terhadap kunci_jawaban soal
    void OnAssess()
    {
        if (mapManager == null) return;
        var r = mapManager.Assess();
        if (r == null) { ShowToast("Soal ini tidak punya kunci jawaban.", 3f); return; }

        int pct = Mathf.RoundToInt(r.Score * 100f);
        string head = r.match == r.total && r.excess == 0 ? "Sempurna! " : "";
        ShowToast($"{head}Benar {r.match} dari {r.total} ({pct}%)\nKurang {r.miss} · Lebih {r.excess}", 6f);
    }

    void OnCenter()
    {
        if (mapManager == null) return;
        mapManager.CenterNodes();
        ShowToast("Peta dipindah ke depan kamera", 1.5f);
    }

    // ─────────────────────────────────────────────
    //  TOAST
    // ─────────────────────────────────────────────
    void BuildToast(Transform canvasT)
    {
        toastGO = new GameObject("Toast");
        toastGO.transform.SetParent(canvasT, false);
        toastGO.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.1f, 0.9f);

        var rt = toastGO.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.05f, 0f);
        rt.anchorMax = new Vector2(0.95f, 0f);
        rt.pivot     = new Vector2(0.5f, 0f);
        rt.offsetMin = new Vector2(0, 90);
        rt.offsetMax = new Vector2(0, 150);

        toastTxt = MakeLabel(toastGO.transform, "", 12, Color.white, Vector2.zero, Vector2.one);
        toastTxt.rectTransform.offsetMin = new Vector2(8, 4);
        toastTxt.rectTransform.offsetMax = new Vector2(-8, -4);

        toastGO.SetActive(false);
    }

    public void ShowToast(string msg, float duration = 3f)
    {
        toastTxt.text = msg;
        toastGO.SetActive(true);
        toastTimer = duration;
    }

    // ─────────────────────────────────────────────
    //  UI HELPERS
    // ─────────────────────────────────────────────
    static RectTransform MakeRect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.AddComponent<RectTransform>();
    }

    static TextMeshProUGUI MakeLabel(Transform parent, string text, float size, Color color,
                                     Vector2 anchorMin, Vector2 anchorMax)
    {
        var go = new GameObject("Label");
        go.transform.SetParent(parent, false);
        var txt = go.AddComponent<TextMeshProUGUI>();
        txt.text      = text;
        txt.fontSize  = size;
        txt.color     = color;
        txt.alignment = TextAlignmentOptions.Center;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return txt;
    }

    // Ikon sprite (glyph font tidak lengkap di TMP default)
    static void AddIcon(Transform parent, Sprite icon, float size)
    {
        var go = new GameObject("Icon");
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.sprite         = icon;
        img.preserveAspect = true;
        img.raycastTarget  = false;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(size, size);
    }

    static void MakeButton(RectTransform parent, string label, Color color,
                           Vector2 anchorMin, Vector2 anchorMax, UnityAction cb, Sprite icon)
    {
        var rt = MakeRect(label, parent);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = new Vector2(6, 8);
        rt.offsetMax = new Vector2(-6, -8);
        rt.gameObject.AddComponent<Image>().color = color;
        rt.gameObject.AddComponent<Button>().onClick.AddListener(cb);
        AddIcon(rt, icon, 30);
    }

    static void MakeOverlayBtn(Transform parent, string label, Color color,
                               Vector2 anchorMin, Vector2 anchorMax, UnityAction cb)
    {
        var rt = MakeRect(label, parent);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = new Vector2(4, 4);
        rt.offsetMax = new Vector2(-4, -4);
        rt.gameObject.AddComponent<Image>().color = color;
        rt.gameObject.AddComponent<Button>().onClick.AddListener(cb);
        MakeLabel(rt, label, 13, Color.white, Vector2.zero, Vector2.one);
    }
}

/// <summary>
/// Receiver UnitySendMessage dari JsonFilePicker.java.
/// GameObject-nya harus bernama "JsonPickerCallback".
/// </summary>
public class JsonPickerCallback : MonoBehaviour
{
    [HideInInspector] public AppToolbar toolbar;

    public void OnFilePicked(string json)   => toolbar?.HandlePickedJson(json);
    public void OnFilePickError(string msg) => toolbar?.HandlePickError(msg);
}
