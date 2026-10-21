# ConceptMapAR

Aplikasi AR (Unity 6 + AR Foundation) untuk menyusun peta konsep di ruang 3D.
Node **Konsep** (kuning) dan **Relasi** (abu-abu) melayang di depan kamera dan
bisa disambungkan menjadi proposisi `Konsep --[relasi]--> Konsep`.

## Fitur

- Muat peta konsep dari file JSON (file picker Android), clipboard (JSON/URL), atau API
- Gesture mengikuti Kit-Build (kbx):
  - Tap relasi → muncul handle kiri (merah, source) & kanan (biru, target), plus handle di
    ujung tiap koneksi yang ada
  - Drag handle kiri/kanan ke konsep untuk menyambung; saat melewati konsep garis *snap*
    jadi putus-putus. Tap handle tidak melakukan apa-apa (sama seperti kbx)
  - Drag handle koneksi: lepas di konsep lain = pindah koneksi, lepas di ruang kosong = putus
  - Handle kiri hanya muncul bila relasi belum punya source; handle kanan hilang bila jumlah
    target mencapai `limit`
  - Double-tap node → pilih node + tetangganya, lalu drag untuk memindah semuanya
  - Tap ruang kosong → lepas seleksi
- Pindahkan node dengan drag, pinch untuk mengubah ukuran semua node
- Undo / Redo: sambung, pindah/putus koneksi, pindah node, rapikan, pusatkan
- Rapikan node ke grid, pusatkan peta ke depan kamera
- Indikator tepi layar untuk node yang berada di luar pandangan
- Simpan hasil ke `Download/ConceptMapAR/` (Android) atau `persistentDataPath`

## Kebutuhan

- Unity **6000.4.6f1**
- Android (ARCore, min API 29), iOS (ARKit), atau Meta Quest 3/3S (OpenXR)

## Menjalankan

1. Buka folder project lewat Unity Hub.
2. Buka scene `Assets/Scenes/Main.unity`.
3. Pilih platform Android/iOS di **File → Build Profiles**, lalu build.

Build APK dari command line:

```bash
Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod BuildAndroid.Build
```

Hasilnya ada di `Builds/ConceptMapAR.apk`.

## Meta Quest 3S (passthrough AR)

Versi Quest memakai OpenXR + **Unity OpenXR: Meta** (`com.unity.xr.meta-openxr`)
untuk passthrough, plane detection, dan raycast. Kode yang sama dipakai HP & Quest;
`XRSupport` aktif otomatis saat headset terdeteksi.

### Siapkan headset (sekali saja)

1. Install app **Meta Horizon** di HP, login, pair Quest 3S.
2. Buat organisasi developer di <https://developers.meta.com/horizon/> (gratis).
3. Di app Meta Horizon: **Devices → Headset settings → Developer mode → ON**, lalu restart Quest.
4. Colok Quest ke Mac pakai kabel USB-C (data). Di headset muncul dialog
   **Allow USB debugging** → centang *Always allow* → **Allow**.
5. Cek koneksi:

   ```bash
   ~/Library/Android/sdk/platform-tools/adb devices
   ```

   (atau `adb` bawaan Unity: `.../Unity.app/Contents/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb`).
   Status harus `device`, bukan `unauthorized`.

### Build & jalankan

1. Menu **ConceptMapAR → Target: Meta Quest 3S (OpenXR)** — mengganti loader ARCore ke OpenXR,
   menyalakan fitur Meta (Session, Camera/Passthrough, Planes, Raycast), profil controller
   Touch Plus & hand tracking, Vulkan, ARM64, min API 32.
2. Buka **Project Settings → XR Plug-in Management → Project Validation**, klik **Fix All** bila ada.
3. **File → Build Profiles → Android → Run Device** pilih Quest → **Build And Run**,
   atau menu **ConceptMapAR → Build Quest APK** lalu:

   ```bash
   adb install -r Builds/ConceptMapAR-Quest.apk
   ```

   App muncul di headset: **Library → Unknown Sources**.
4. Kembali ke HP: **ConceptMapAR → Target: Android Phone (ARCore)**.

Build Quest dari command line:

```bash
Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod QuestSetup.BuildQuest
```

### Kontrol di Quest

| Aksi                         | Controller                     | Tangan              |
|------------------------------|--------------------------------|---------------------|
| Pilih node / tekan tombol UI | Trigger                        | Pinch               |
| Pilih node + tetangga        | Trigger 2× cepat               | Pinch 2× cepat      |
| Pindah node / seleksi        | Tahan trigger di node + arahkan | Tahan pinch + arahkan |
| Sambungkan                   | Tahan trigger di handle kiri/kanan relasi, lepas di konsep | sama, pakai pinch |
| Pindah / putus koneksi       | Tahan trigger di handle koneksi, lepas di konsep lain / ruang kosong | sama, pakai pinch |
| Jauhkan / dekatkan node      | Saat drag: thumbstick ↑/↓ atau gerakkan controller maju/mundur | Saat drag: gerakkan tangan maju/mundur |
| Ubah ukuran semua node       | Thumbstick kanan atas/bawah (saat tidak drag) | –         |
| Pindahkan panel menu ke depan | Tombol ≡ (kiri) / B (kanan)   | –                   |

Panel toolbar mengikuti kepala secara lembut; indikator tepi layar dimatikan di Quest.
Controller tampil sebagai model sederhana (trigger menyala biru saat ditekan) dan
disembunyikan otomatis saat memakai hand tracking.
Izin *spatial data* (`USE_SCENE`) diminta saat pertama dibuka — izinkan agar node bisa
ditaruh di permukaan meja/lantai.

## Sumber data

`ApiLoader` (di scene) menentukan peta awal:

| Field          | Keterangan                                                        |
|----------------|-------------------------------------------------------------------|
| `useSampleMap` | `true` → pakai `Assets/StreamingAssets/map.json`                  |
| `apiBaseUrl`   | Base URL API, dipanggil sebagai `GET {apiBaseUrl}/maps/{mapId}`   |
| `mapId`        | ID peta konsep                                                    |

Bila API gagal, aplikasi otomatis memakai `map.json`.

## Format JSON

```json
{
  "canvas": {
    "concepts":    [{ "cid": "c1", "label": "Gempa Bumi", "x": 280, "y": 130, "data": "{...}" }],
    "links":       [{ "lid": "l1", "label": "menyebabkan", "x": 460, "y": 160, "source_cid": "c1", "data": "{...}" }],
    "linktargets": [{ "lid": "l1", "target_cid": "c2", "target_data": "{...}" }],
    "concepts_ext": [], "links_ext": [], "linktargets_ext": []
  },
  "map": { "cmid": "...", "direction": "multi" }
}
```

Koordinat `x`/`y` adalah koordinat kanvas 2D; saat dimuat, peta diskalakan
otomatis agar pas di depan kamera.

## Struktur script

| File                         | Peran                                                    |
|------------------------------|----------------------------------------------------------|
| `ConceptMapManager.cs`       | Controller utama: load/save, spawn node, koneksi, undo   |
| `MapNode.cs`                 | Base node: billboard, auto-size, gesture, tombol koneksi |
| `ConceptNode.cs` / `LinkNode.cs` | Node konsep / relasi                                 |
| `ConnectionLine.cs`          | Garis koneksi (titik untuk source, panah untuk target)   |
| `AppToolbar.cs`              | Toolbar UI, panel muat, overlay JSON, toast              |
| `ToolbarIcons.cs`            | Ikon toolbar prosedural                                  |
| `OffscreenIndicator.cs`      | Indikator node di luar layar                             |
| `ApiLoader.cs`               | Memuat peta awal dari API / `map.json`                   |
| `ConceptMapData.cs`          | Model data JSON                                          |
| `CameraPermissionHandler.cs` | Meminta izin kamera sebelum ARSession aktif              |
| `PointerInput.cs`            | Input terpadu: sentuh / mouse / ray controller XR        |
| `XRSupport.cs`               | Rig Quest: passthrough, ray controller & tangan, UI world-space |
| `Editor/QuestSetup.cs`       | Menu switch target Quest ⇄ HP dan build APK Quest        |
