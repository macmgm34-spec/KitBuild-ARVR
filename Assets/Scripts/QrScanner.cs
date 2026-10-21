using System;
using System.Collections;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using ZXing;

/// <summary>
/// Scan QR berisi link soal. Selama scan, passthrough (Quest) dinyalakan supaya
/// pengguna melihat HP/QR di depannya; setelah selesai/batal dimatikan lagi.
/// Frame diambil dari kamera yang sama lewat ARCameraManager CPU image. Dekode QR (ZXing) berjalan di thread terpisah supaya
/// frame VR tidak tersendat. Di tengah pandangan tampil bingkai bidik.
/// </summary>
public class QrScanner : MonoBehaviour
{
    const string HEADSET_CAMERA  = "horizonos.permission.HEADSET_CAMERA";
    const float  DECODE_INTERVAL = 0.2f;
    const float  TIMEOUT_SEC     = 45f;
    const int    MAX_DECODE_W    = 960;     // cukup untuk QR, hemat CPU Quest
    const float  FRAME_DIST      = 0.6f;
    const float  FRAME_SIZE      = 0.22f;

    public bool IsScanning { get; private set; }

    ARCameraManager cameraManager;
    Transform       aimFrame;
    Task<string>    decoding;
    Action<string>  onResult;
    Action<string>  onError;

    readonly BarcodeReaderGeneric reader = new()
    {
        AutoRotate = true,
        Options    = new ZXing.Common.DecodingOptions
        {
            TryHarder       = true,
            PossibleFormats = new[] { BarcodeFormat.QR_CODE },
        },
    };

    public void StartScan(Action<string> result, Action<string> error)
    {
        if (IsScanning) return;
        onResult   = result;
        onError    = error;
        IsScanning = true;
        if (PointerInput.IsXR) XRSupport.Instance.Passthrough = true;
        StartCoroutine(ScanRoutine());
    }

    public void Stop()
    {
        IsScanning = false;
        if (PointerInput.IsXR) XRSupport.Instance.Passthrough = false;
        if (aimFrame != null) { Destroy(aimFrame.gameObject); aimFrame = null; }
    }

    void OnDestroy() => Stop();

    IEnumerator ScanRoutine()
    {
        yield return RequestCameraPermission();
        if (!IsScanning) yield break;

        cameraManager = FindAnyObjectByType<ARCameraManager>();
        if (cameraManager == null)
        {
            Fail("Kamera AR tidak ditemukan.");
            yield break;
        }
        yield return null;   // beri satu frame agar XRSupport menyalakan passthrough/kamera

        ShowAimFrame();
        float start = Time.unscaledTime;
        bool  gotFrame = false;

        while (IsScanning)
        {
            if (Time.unscaledTime - start > TIMEOUT_SEC)
            {
                Fail(gotFrame ? "QR tidak terbaca. Coba lebih dekat & tahan diam."
                              : "Gambar kamera tidak tersedia (perlu Quest 3/3S, Horizon OS v74+).");
                yield break;
            }

            if (decoding != null && decoding.IsCompleted)
            {
                string text = decoding.Status == TaskStatus.RanToCompletion ? decoding.Result : null;
                decoding = null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var cb = onResult;
                    Stop();
                    cb?.Invoke(text.Trim());
                    yield break;
                }
            }

            if (decoding == null && StartDecode()) gotFrame = true;
            yield return new WaitForSecondsRealtime(DECODE_INTERVAL);
        }
    }

    IEnumerator RequestCameraPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Quest: izin kamera passthrough. HP: izin kamera sudah diminta ARCore.
        if (!PointerInput.IsXR) yield break;
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(HEADSET_CAMERA)) yield break;

        bool? granted = null;
        var callbacks = new UnityEngine.Android.PermissionCallbacks();
        callbacks.PermissionGranted += _ => granted = true;
        callbacks.PermissionDenied  += _ => granted = false;
        UnityEngine.Android.Permission.RequestUserPermission(HEADSET_CAMERA, callbacks);
        while (granted == null && IsScanning) yield return null;
        if (granted == false) Fail("Izin kamera ditolak. Aktifkan di Settings → Apps → Permissions.");
#else
        yield break;
#endif
    }

    // Ambil CPU image → luminance 8-bit (R8) → dekode ZXing di thread pool
    bool StartDecode()
    {
        if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) return false;
        using (image)
        {
            int  div  = Mathf.Max(1, Mathf.CeilToInt(image.width / (float)MAX_DECODE_W));
            bool gray = image.FormatSupported(TextureFormat.R8);
            if (!gray && !image.FormatSupported(TextureFormat.RGBA32)) return false;

            var conv = new XRCpuImage.ConversionParams(image, gray ? TextureFormat.R8 : TextureFormat.RGBA32)
            {
                outputDimensions = new Vector2Int(image.width / div, image.height / div),
            };
            int w = conv.outputDimensions.x, h = conv.outputDimensions.y;
            var buffer = new NativeArray<byte>(image.GetConvertedDataSize(conv), Allocator.Temp);
            try
            {
                image.Convert(conv, buffer);
                byte[] lum = gray ? buffer.ToArray() : ToLuminance(buffer, w * h);
                decoding = Task.Run(() =>
                {
                    var source = new RGBLuminanceSource(lum, w, h, RGBLuminanceSource.BitmapFormat.Gray8);
                    return reader.Decode(source)?.Text;
                });
            }
            finally { buffer.Dispose(); }
        }
        return true;
    }

    static byte[] ToLuminance(NativeArray<byte> rgba, int count)
    {
        var lum = new byte[count];
        for (int i = 0; i < count; i++)
            lum[i] = (byte)((rgba[i * 4] * 77 + rgba[i * 4 + 1] * 150 + rgba[i * 4 + 2] * 29) >> 8);
        return lum;
    }

    // Bingkai bidik di tengah pandangan: sudut-sudut putih, tengah transparan
    void ShowAimFrame()
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "QrAimFrame";
        Destroy(quad.GetComponent<Collider>());
        quad.GetComponent<MeshRenderer>().material =
            new Material(Shader.Find("Sprites/Default")) { mainTexture = MakeFrameTex(128) };
        quad.transform.localScale = Vector3.one * FRAME_SIZE;
        aimFrame = quad.transform;
    }

    static Texture2D MakeFrameTex(int n)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        int len = n / 4, th = Mathf.Max(3, n / 32);
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            bool nearX = x < th || x >= n - th, nearY = y < th || y >= n - th;
            bool cornerX = x < len || x >= n - len, cornerY = y < len || y >= n - len;
            bool on = (nearX && cornerY) || (nearY && cornerX);
            px[y * n + x] = on ? new Color32(255, 255, 255, 230) : new Color32(0, 0, 0, 0);
        }
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    void LateUpdate()
    {
        if (aimFrame == null || Camera.main == null) return;
        var head = Camera.main.transform;
        aimFrame.position = head.position + head.forward * FRAME_DIST;
        aimFrame.rotation = Quaternion.LookRotation(head.forward, head.up);
    }

    void Fail(string msg)
    {
        var cb = onError;
        Stop();
        cb?.Invoke(msg);
    }
}
