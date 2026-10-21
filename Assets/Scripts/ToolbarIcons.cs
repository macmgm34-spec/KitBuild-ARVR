using UnityEngine;

/// <summary>
/// Ikon toolbar digambar prosedural (64×64, putih, latar transparan).
/// Dipakai karena font TMP default tidak punya glyph icon/emoji.
/// </summary>
public static class ToolbarIcons
{
    const int S = 64;

    // ── Helper gambar ─────────────────────────────
    static Texture2D NewTex()
    {
        var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
        var clear = new Color(0, 0, 0, 0);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
                t.SetPixel(x, y, clear);
        return t;
    }

    static void Rect(Texture2D t, int x0, int y0, int x1, int y1, Color c)
    {
        for (int y = Mathf.Max(y0, 0); y <= Mathf.Min(y1, S - 1); y++)
            for (int x = Mathf.Max(x0, 0); x <= Mathf.Min(x1, S - 1); x++)
                t.SetPixel(x, y, c);
    }

    static void Circle(Texture2D t, int cx, int cy, float rOut, float rIn, Color c)
    {
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy));
                if (d <= rOut && d >= rIn) t.SetPixel(x, y, c);
            }
    }

    // Segitiga panah horizontal; dir=+1 menunjuk kanan, -1 kiri
    static void TriangleH(Texture2D t, int tipX, int cy, int len, int halfH, int dir, Color c)
    {
        for (int i = 0; i < len; i++)
        {
            int x = tipX - dir * i;
            int h = Mathf.RoundToInt(halfH * (float)i / len);
            Rect(t, x, cy - h, x, cy + h, c);
        }
    }

    static Sprite ToSprite(Texture2D t)
    {
        t.Apply();
        return Sprite.Create(t, new UnityEngine.Rect(0, 0, S, S), new Vector2(0.5f, 0.5f));
    }

    static readonly Color W = Color.white;

    // ── Ikon-ikon ─────────────────────────────────
    /// ≡ tiga garis (hamburger menu)
    public static Sprite Menu()
    {
        var t = NewTex();
        Rect(t, 12, 42, 52, 48, W);
        Rect(t, 12, 29, 52, 35, W);
        Rect(t, 12, 16, 52, 22, W);
        return ToSprite(t);
    }

    /// Panah turun ke baki (muat / load)
    public static Sprite Load()
    {
        var t = NewTex();
        Rect(t, 12, 12, 16, 26, W);            // dinding kiri
        Rect(t, 48, 12, 52, 26, W);            // dinding kanan
        Rect(t, 12, 12, 52, 16, W);            // dasar baki
        Rect(t, 29, 34, 35, 54, W);            // batang panah
        TriangleH(t, 32, 24, 1, 0, 1, W);      // (tip digambar oleh segitiga bawah)
        for (int i = 0; i < 12; i++)           // kepala panah ke bawah
            Rect(t, 32 - i, 22 + i, 32 + i, 22 + i, W);
        return ToSprite(t);
    }

    /// Panah kiri (undo)
    public static Sprite Undo()
    {
        var t = NewTex();
        Rect(t, 20, 28, 52, 36, W);
        TriangleH(t, 10, 32, 14, 12, -1, W);
        return ToSprite(t);
    }

    /// Panah kanan (redo)
    public static Sprite Redo()
    {
        var t = NewTex();
        Rect(t, 12, 28, 44, 36, W);
        TriangleH(t, 54, 32, 14, 12, 1, W);
        return ToSprite(t);
    }

    /// 4 kotak grid (rapikan / align)
    public static Sprite Grid()
    {
        var t = NewTex();
        Rect(t, 12, 36, 28, 52, W);
        Rect(t, 36, 36, 52, 52, W);
        Rect(t, 12, 12, 28, 28, W);
        Rect(t, 36, 12, 52, 28, W);
        return ToSprite(t);
    }

    /// Lingkaran + titik pusat (centerize)
    public static Sprite Center()
    {
        var t = NewTex();
        Circle(t, 32, 32, 18f, 13f, W);        // cincin
        Circle(t, 32, 32, 5f, 0f, W);          // titik pusat
        Rect(t, 30, 52, 34, 60, W);            // tick atas
        Rect(t, 30, 4, 34, 12, W);             // tick bawah
        Rect(t, 4, 30, 12, 34, W);             // tick kiri
        Rect(t, 52, 30, 60, 34, W);            // tick kanan
        return ToSprite(t);
    }

    /// Disket (simpan)
    public static Sprite Save()
    {
        var t = NewTex();
        Rect(t, 12, 12, 52, 52, W);            // badan
        var clear = new Color(0, 0, 0, 0);
        Rect(t, 22, 40, 42, 52, clear);        // label atas (lubang)
        Rect(t, 20, 16, 44, 32, clear);        // area label bawah
        Rect(t, 46, 46, 52, 52, clear);        // potongan sudut
        return ToSprite(t);
    }

    /// Centang ✓ (periksa jawaban)
    public static Sprite Check()
    {
        var t = NewTex();
        Stroke(t, 12, 32, 26, 18, 5, W);
        Stroke(t, 26, 18, 52, 46, 5, W);
        return ToSprite(t);
    }

    // Garis tebal dari (x0,y0) ke (x1,y1)
    static void Stroke(Texture2D t, int x0, int y0, int x1, int y1, int r, Color c)
    {
        int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
        for (int i = 0; i <= steps; i++)
        {
            int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, i / (float)steps));
            int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, i / (float)steps));
            Rect(t, x - r, y - r, x + r, y + r, c);
        }
    }

    /// Kurung kurawal { } (JSON)
    public static Sprite Json()
    {
        var t = NewTex();
        // "{" kiri
        Rect(t, 18, 14, 22, 50, W);
        Rect(t, 22, 14, 28, 18, W);
        Rect(t, 22, 46, 28, 50, W);
        Rect(t, 12, 30, 18, 34, W);
        // "}" kanan
        Rect(t, 42, 14, 46, 50, W);
        Rect(t, 36, 14, 42, 18, W);
        Rect(t, 36, 46, 42, 50, W);
        Rect(t, 46, 30, 52, 34, W);
        return ToSprite(t);
    }
}

