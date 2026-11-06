using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Ikon kursor tangan untuk ray VR, digambar prosedural (putih + tepi hitam):
/// Point = menunjuk (default), Open = di atas node, Grab = sedang memegang.
/// Bentuk disusun dari kapsul & kotak membulat, digambar berlapis supaya
/// garis antar jari tetap terlihat.
/// </summary>
public static class HandCursor
{
    public enum Pose { Point, Open, Grab }

    const int   N       = 128;
    const float OUTLINE = 5f;

    static readonly Dictionary<Pose, Texture2D> cache = new();

    /// Titik tunjuk di tekstur (uv): ujung telunjuk untuk Point, tengah untuk lainnya
    public static Vector2 Hotspot(Pose p) => p == Pose.Point ? new Vector2(52f / N, 123f / N) : new Vector2(0.5f, 0.5f);

    public static Texture2D Get(Pose p)
    {
        if (cache.TryGetValue(p, out var t) && t != null) return t;
        t = Draw(p switch
        {
            Pose.Point => new[]
            {
                Capsule(42, 44, 22, 62, 11),           // jempol
                Box(64, 42, 26, 26, 14),               // telapak
                Capsule(72, 66, 72, 70, 11),           // jari tengah tertekuk
                Capsule(88, 60, 88, 64, 10),           // jari manis/kelingking tertekuk
                Capsule(52, 60, 52, 112, 11),          // telunjuk
            },
            Pose.Open => new[]
            {
                Capsule(38, 40, 14, 60, 11),           // jempol
                Box(64, 42, 28, 24, 14),               // telapak
                Capsule(40, 60, 34, 100, 10.5f),
                Capsule(56, 62, 54, 114, 10.5f),
                Capsule(72, 62, 74, 112, 10.5f),
                Capsule(88, 58, 94, 98, 10),
            },
            _ => new[]
            {
                Box(64, 50, 34, 30, 18),               // kepalan
                Capsule(40, 76, 40, 76, 11),           // buku-buku jari
                Capsule(57, 80, 57, 80, 11),
                Capsule(74, 80, 74, 80, 11),
                Capsule(90, 76, 90, 76, 11),
                Capsule(34, 52, 70, 58, 10),           // jempol melintang
            },
        });
        cache[p] = t;
        return t;
    }

    // ── SDF sederhana ─────────────────────────────
    delegate float Sdf(Vector2 p);

    static Sdf Capsule(float ax, float ay, float bx, float by, float r)
    {
        var a = new Vector2(ax, ay); var b = new Vector2(bx, by);
        return p =>
        {
            Vector2 pa = p - a, ba = b - a;
            float h = ba.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(pa, ba) / ba.sqrMagnitude) : 0f;
            return (pa - ba * h).magnitude - r;
        };
    }

    static Sdf Box(float cx, float cy, float hx, float hy, float r)
    {
        var c = new Vector2(cx, cy); var half = new Vector2(hx - r, hy - r);
        return p =>
        {
            Vector2 q = new Vector2(Mathf.Abs(p.x - c.x), Mathf.Abs(p.y - c.y)) - half;
            return new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
        };
    }

    // Lapis demi lapis: tiap bentuk = isi putih + tepi hitam di atas lapisan sebelumnya
    static Texture2D Draw(Sdf[] shapes)
    {
        var px = new Color[N * N];
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            Color c = new Color(0f, 0f, 0f, 0f);
            foreach (var s in shapes)
            {
                float d = s(p);
                float a = Mathf.Clamp01(0.5f - d);          // anti-alias tepi luar
                if (a <= 0f) continue;
                Color shape = d > -OUTLINE ? new Color(0.08f, 0.08f, 0.1f, 1f) : Color.white;
                c = Color.Lerp(c, shape, a);
                c.a = Mathf.Max(c.a, a);
            }
            px[y * N + x] = c;
        }
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }
}
