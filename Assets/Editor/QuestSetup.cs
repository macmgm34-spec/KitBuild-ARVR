using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

public static class QuestSetup
{
    const string OPENXR_LOADER = "UnityEngine.XR.OpenXR.OpenXRLoader";
    const string ARCORE_LOADER = "UnityEngine.XR.ARCore.ARCoreLoader";
    const string XRSIM_LOADER  = "UnityEngine.XR.Simulation.SimulationLoader";
    const string ACTIVE_RUNTIME = "/usr/local/share/openxr/1/active_runtime.json";
    const string QUEST_APK     = "Builds/ConceptMapAR-Quest.apk";

    static readonly HashSet<string> QuestFeatures = new()
    {
        "MetaQuestFeature",
        "OpenXRCompositionLayersFeature",   // wajib: layer passthrough dibuat lewat composition layer
        "MetaQuestTouchPlusControllerProfile",
        "OculusTouchControllerProfile",
        "HandInteractionProfile",
        "ARSessionFeature",
        "ARCameraFeature",
        "ARPlaneFeature",
        "ARRaycastFeature",
    };

    [MenuItem("ConceptMapAR/Target: Meta Quest 3S (OpenXR)")]
    public static void UseQuest()
    {
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        var manager = XRManager(BuildTargetGroup.Android);
        XRPackageMetadataStore.RemoveLoader(manager, ARCORE_LOADER, BuildTargetGroup.Android);
        XRPackageMetadataStore.AssignLoader(manager, OPENXR_LOADER, BuildTargetGroup.Android);

        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
        var openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        var enabled = new List<string>();
        foreach (var feature in openxr.GetFeatures())
        {
            if (!QuestFeatures.Contains(feature.GetType().Name)) continue;
            feature.enabled = true;
            EditorUtility.SetDirty(feature);
            enabled.Add(feature.GetType().Name);
        }
        EditorUtility.SetDirty(openxr);

        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion       = AndroidSdkVersions.AndroidApiLevel32;

        AssetDatabase.SaveAssets();

        var missing = QuestFeatures.Except(enabled).ToList();
        Debug.Log($"[QuestSetup] Target Meta Quest aktif. Fitur OpenXR: {string.Join(", ", enabled)}");
        if (missing.Count > 0)
            Debug.LogWarning($"[QuestSetup] Fitur tidak ditemukan (cek Package Manager): {string.Join(", ", missing)}");
    }

    // Fitur OpenXR untuk Play Mode di Editor (Standalone). MetaQuestFeature khusus Android.
    static readonly HashSet<string> SimulatorFeatures = new()
    {
        "OpenXRCompositionLayersFeature",
        "MetaQuestTouchPlusControllerProfile",
        "OculusTouchControllerProfile",
        "HandInteractionProfile",
        "ARSessionFeature",
        "ARCameraFeature",
        "ARPlaneFeature",
        "ARRaycastFeature",
    };

    /// Play Mode di Editor memakai OpenXR → jalan di Meta XR Simulator
    /// (aktifkan dulu lewat slider di app Meta XR Simulator).
    [MenuItem("ConceptMapAR/Play Mode: Meta XR Simulator (OpenXR)")]
    public static void UseMetaXRSimulator()
    {
        var manager = XRManager(BuildTargetGroup.Standalone);
        XRPackageMetadataStore.RemoveLoader(manager, XRSIM_LOADER, BuildTargetGroup.Standalone);
        XRPackageMetadataStore.AssignLoader(manager, OPENXR_LOADER, BuildTargetGroup.Standalone);

        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Standalone);
        var openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
        var enabled = new List<string>();
        foreach (var feature in openxr.GetFeatures())
        {
            if (!SimulatorFeatures.Contains(feature.GetType().Name)) continue;
            feature.enabled = true;
            EditorUtility.SetDirty(feature);
            enabled.Add(feature.GetType().Name);
        }
        EditorUtility.SetDirty(openxr);
        AssetDatabase.SaveAssets();

        Debug.Log($"[QuestSetup] Play Mode → OpenXR (Meta XR Simulator). Fitur: {string.Join(", ", enabled)}");
        var missing = SimulatorFeatures.Except(enabled).ToList();
        if (missing.Count > 0)
            Debug.LogWarning($"[QuestSetup] Fitur tidak ditemukan untuk Standalone: {string.Join(", ", missing)}");

        if (!File.Exists(ACTIVE_RUNTIME))
            Debug.LogWarning("[QuestSetup] Belum ada OpenXR runtime aktif. Buka app Meta XR Simulator " +
                             "dan nyalakan slider di kiri atas, lalu tekan Play.");
        else if (!File.ReadAllText(ACTIVE_RUNTIME).Contains("SIMULATOR", System.StringComparison.OrdinalIgnoreCase))
            Debug.LogWarning($"[QuestSetup] Runtime aktif ({ACTIVE_RUNTIME}) sepertinya bukan Meta XR Simulator.");
    }

    /// Kembalikan Play Mode ke XR Simulation bawaan AR Foundation (simulasi HP)
    [MenuItem("ConceptMapAR/Play Mode: AR Foundation XR Simulation")]
    public static void UseARSimulation()
    {
        var manager = XRManager(BuildTargetGroup.Standalone);
        XRPackageMetadataStore.RemoveLoader(manager, OPENXR_LOADER, BuildTargetGroup.Standalone);
        XRPackageMetadataStore.AssignLoader(manager, XRSIM_LOADER, BuildTargetGroup.Standalone);
        AssetDatabase.SaveAssets();
        Debug.Log("[QuestSetup] Play Mode → AR Foundation XR Simulation.");
    }

    [MenuItem("ConceptMapAR/Target: Android Phone (ARCore)")]
    public static void UsePhone()
    {
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        var manager = XRManager(BuildTargetGroup.Android);
        XRPackageMetadataStore.RemoveLoader(manager, OPENXR_LOADER, BuildTargetGroup.Android);
        XRPackageMetadataStore.AssignLoader(manager, ARCORE_LOADER, BuildTargetGroup.Android);

        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, true);
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;

        AssetDatabase.SaveAssets();
        Debug.Log("[QuestSetup] Target Android Phone (ARCore) aktif.");
    }

    [MenuItem("ConceptMapAR/Build Quest APK")]
    public static void BuildQuest()
    {
        UseQuest();

        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();
        if (scenes.Length == 0)
            scenes = new[] { "Assets/Scenes/Main.unity" };

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes           = scenes,
            locationPathName = QUEST_APK,
            target           = BuildTarget.Android,
            options          = BuildOptions.None,
        });
        Debug.Log($"[QuestSetup] Result: {report.summary.result}, " +
                  $"size: {report.summary.totalSize / (1024 * 1024)} MB, " +
                  $"errors: {report.summary.totalErrors}");

        if (Application.isBatchMode &&
            report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

    public static bool IsQuestTarget()
    {
        var general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
        var manager = general != null ? general.AssignedSettings : null;
        return manager != null && manager.activeLoaders.Any(l => l is OpenXRLoader);
    }

    static XRManagerSettings XRManager(BuildTargetGroup group)
    {
        var general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
        if (general == null)
            throw new BuildFailedException($"XR General Settings untuk {group} belum ada. Buka Project Settings > XR Plug-in Management sekali.");
        if (general.AssignedSettings == null)
        {
            var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
            manager.name = $"{group} Providers";
            AssetDatabase.AddObjectToAsset(manager, general);
            general.AssignedSettings = manager;
            EditorUtility.SetDirty(general);
        }
        return general.AssignedSettings;
    }
}

class QuestManifestPostprocessor : IPostGenerateGradleAndroidProject
{
    const string ANDROID_NS = "http://schemas.android.com/apk/res/android";

    public int callbackOrder => 100;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        if (!QuestSetup.IsQuestTarget()) return;

        string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath)) return;

        var doc = new XmlDocument();
        doc.Load(manifestPath);
        var root = doc.DocumentElement;

        AddElement(doc, root, "uses-permission", "com.oculus.permission.HAND_TRACKING", null);
        AddElement(doc, root, "uses-permission", "com.oculus.permission.USE_SCENE", null);
        AddElement(doc, root, "uses-feature", "oculus.software.handtracking", "false");
        // Scan QR soal: kamera passthrough (Horizon OS v74+) + keyboard sistem untuk "Ketik Link"
        AddElement(doc, root, "uses-permission", "horizonos.permission.HEADSET_CAMERA", null);
        AddElement(doc, root, "uses-feature", "oculus.software.overlay_keyboard", "false");

        doc.Save(manifestPath);
    }

    static void AddElement(XmlDocument doc, XmlElement root, string tag, string name, string required)
    {
        foreach (XmlElement e in root.GetElementsByTagName(tag))
            if (e.GetAttribute("name", ANDROID_NS) == name) return;

        var el = doc.CreateElement(tag);
        el.SetAttribute("name", ANDROID_NS, name);
        if (required != null) el.SetAttribute("required", ANDROID_NS, required);
        root.AppendChild(el);
    }
}
