using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public struct PointerSample
{
    public int        id;
    public TouchPhase phase;
    public Ray        ray;
    public Vector2    screenPos;
    public bool       xr;
    public bool       overUI;
    public float      depthAxis;   // XR: thumbstick Y controller yang memegang (+ = menjauh)
    public float      reach;       // XR: jarak kepala → tangan/controller (untuk dorong/tarik)
}

public static class PointerInput
{
    public const int MOUSE_ID = -99;

    const float TOUCH_DRAG_PX    = 15f;
    // Ray tangan/controller goyang saat pinch/trigger ditekan → ambang harus
    // cukup longgar supaya tap (dan double-tap) tidak terbaca sebagai drag.
    const float XR_DRAG_DEGREES  = 2.5f;
    const float XR_DRAG_METERS   = 0.04f;

    static readonly List<PointerSample> samples = new();
    static int cachedFrame = -1;

    public static bool IsXR => XRSupport.Active;

    public static float ScaleAxis => IsXR ? XRSupport.Instance.ScaleAxis : 0f;

    public static List<PointerSample> Samples
    {
        get { Refresh(); return samples; }
    }

    public static bool BeyondDragThreshold(in PointerSample begin, in PointerSample now) =>
        now.xr
            ? Vector3.Angle(begin.ray.direction, now.ray.direction) > XR_DRAG_DEGREES
              || Vector3.Distance(begin.ray.origin, now.ray.origin) > XR_DRAG_METERS
            : Vector2.Distance(begin.screenPos, now.screenPos) > TOUCH_DRAG_PX;

    static void Refresh()
    {
        if (cachedFrame == Time.frameCount) return;
        cachedFrame = Time.frameCount;
        samples.Clear();

        if (IsXR)
        {
            XRSupport.Instance.CollectSamples(samples);
            return;
        }

        var cam = Camera.main;
        if (cam == null) return;
        var es = EventSystem.current;

        foreach (Touch t in Input.touches)
        {
            samples.Add(new PointerSample
            {
                id        = t.fingerId,
                phase     = t.phase,
                ray       = cam.ScreenPointToRay(t.position),
                screenPos = t.position,
                overUI    = es != null && es.IsPointerOverGameObject(t.fingerId),
            });
        }

#if UNITY_EDITOR
        TouchPhase? mousePhase =
            Input.GetMouseButtonDown(0) ? TouchPhase.Began :
            Input.GetMouseButtonUp(0)   ? TouchPhase.Ended :
            Input.GetMouseButton(0)     ? TouchPhase.Moved : null;
        if (mousePhase != null)
        {
            samples.Add(new PointerSample
            {
                id        = MOUSE_ID,
                phase     = mousePhase.Value,
                ray       = cam.ScreenPointToRay(Input.mousePosition),
                screenPos = Input.mousePosition,
                overUI    = es != null && es.IsPointerOverGameObject(),
            });
        }
#endif
    }
}
