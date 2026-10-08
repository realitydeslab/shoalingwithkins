#if UNITY_IOS
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HoloKit;
using HoloKit.Editor;
using HoloKit.iOS;
using ShoalingWithKin;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.XR.ARFoundation;
using Object = UnityEngine.Object;

/// <summary>
/// Batchmode setup and build for Shoaling With Kin (pattern from holokit-simple-app).
/// Setup:  Unity -batchmode -nographics -projectPath app -buildTarget iOS -executeMethod ShoalingWithKinSetup.Setup
///         (no -quit: the SDK builds its rig in EditorApplication.delayCall, so Setup exits itself)
/// Build:  Unity -batchmode -nographics -projectPath app -buildTarget iOS -executeMethod ShoalingWithKinSetup.BuildIOS -quit
/// Setup copies OPENROUTER_API_KEY into StreamingAssets (gitignored) so the app can call Luna Decisions.
/// </summary>
public static class ShoalingWithKinSetup
{
    const string ScenePath = "Assets/Scenes/ShoalingWithKin.unity";
    const string MaterialDir = "Assets/ShoalingWithKin/Materials";
    const string BundleId = "io.holokit.shoalingwithkin";
    const string TeamId = "KR9H35SQQ9";
    const string BuildPath = "Builds/iOS";
    const int TicksToWait = 20;

    [MenuItem("Shoaling With Kin/Rebuild Scene")]
    public static void Setup()
    {
        ConfigurePlayer();
        WriteKeyFile();
        BuildScene();
        int ticks = 0;
        void Wait()
        {
            if (++ticks < TicksToWait)
                return;
            EditorApplication.update -= Wait;
            Finish();
        }
        EditorApplication.update += Wait;
    }

    static void ConfigurePlayer()
    {
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, BundleId);
        PlayerSettings.productName = "Shoaling With Kin";
        PlayerSettings.iOS.appleDeveloperTeamID = TeamId;
        PlayerSettings.iOS.appleEnableAutomaticSigning = true;
        PlayerSettings.iOS.cameraUsageDescription = "Shoaling With Kin uses the camera for AR, hand tracking, and asking Luna what your hand is touching.";
        // HoloKit's Stereo is landscape left; locking Mono to it keeps the frames sent to Luna upright.
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        foreach (var rule in HoloKitProjectValidation.iOSRules.Where(r => !r.CheckPredicate()))
        {
            Debug.Log($"[ShoalingWithKinSetup] Fixing: {rule.FixItMessage}");
            rule.FixIt();
        }
        AssetDatabase.SaveAssets();
    }

    static void WriteKeyFile()
    {
        var key = Environment.GetEnvironmentVariable(OpenRouterKey.EnvironmentVariable);
        var path = Path.Combine("Assets/StreamingAssets", OpenRouterKey.FileName);
        if (string.IsNullOrWhiteSpace(key))
        {
            Debug.LogWarning(File.Exists(path)
                ? $"[ShoalingWithKinSetup] {OpenRouterKey.EnvironmentVariable} not set; keeping the existing {path}."
                : $"[ShoalingWithKinSetup] {OpenRouterKey.EnvironmentVariable} not set; the app will have no key.");
            return;
        }
        Directory.CreateDirectory("Assets/StreamingAssets");
        File.WriteAllText(path, key.Trim());
        AssetDatabase.ImportAsset(path);
        Debug.Log($"[ShoalingWithKinSetup] Wrote the OpenRouter key ({key.Trim().Length} chars) to {path} (gitignored).");
    }

    static Material CreateMaterial(string name, float wiggle, float shade, bool instancing)
    {
        Directory.CreateDirectory(MaterialDir);
        var path = $"{MaterialDir}/{name}.mat";
        var shader = Shader.Find("ShoalingWithKin/Fish") ?? throw new Exception("Shader ShoalingWithKin/Fish not found.");
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        material.shader = shader;
        material.SetFloat("_WiggleAmp", wiggle);
        material.SetFloat("_Shade", shade);
        material.enableInstancing = instancing;
        EditorUtility.SetDirty(material);
        return material;
    }

    static void Assign(Object target, string field, Object value)
    {
        var so = new SerializedObject(target);
        var property = so.FindProperty(field) ?? throw new Exception($"{target.GetType().Name} has no field {field}.");
        property.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void BuildScene()
    {
        var fishMaterial = CreateMaterial("Fish", 0.12f, 1f, instancing: true);
        var barMaterial = CreateMaterial("HudBar", 0f, 0f, instancing: false);
        AssetDatabase.SaveAssets();

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("AR Session", typeof(ARSession), typeof(ARInputManager));

        // The SDK's GameObject > XR > HoloKit > HoloKit XR Origin menu code (private, hence reflection).
        var utils = Type.GetType("HoloKit.Editor.HoloKitCreateUtils, HoloKit.Editor", throwOnError: true);
        utils.GetMethod("CreateHoloKitXROriginWithParent", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { null });

        // HoloKit's Mono allows Portrait by default; frames sent to Luna are only upright in landscape left.
        var holoKitCamera = Object.FindFirstObjectByType<HoloKitCameraManager>();
        var monoOrientations = new SerializedObject(holoKitCamera);
        monoOrientations.FindProperty("m_SupportedMonoScreenOrientations").intValue = (int)MonoScreenOrientation.LandscapeLeft;
        monoOrientations.ApplyModifiedPropertiesWithoutUndo();

        // Hand tracking needs LiDAR depth from an AROcclusionManager.
        var cameraManager = Object.FindFirstObjectByType<ARCameraManager>();
        var occlusion = cameraManager.gameObject.AddComponent<AROcclusionManager>();
        occlusion.requestedEnvironmentDepthMode = UnityEngine.XR.ARSubsystems.EnvironmentDepthMode.Fastest;
        occlusion.environmentDepthTemporalSmoothingRequested = false;

        var hands = new GameObject("Hand Tracking Manager").AddComponent<HandTrackingManager>();

        var canvasPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(HoloKitCreateUtils.DefaultUICanvasPrefabPath);
        PrefabUtility.InstantiatePrefab(canvasPrefab);
        new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        var root = new GameObject("Shoaling With Kin");
        var detector = root.AddComponent<TouchKinDetector>();
        Assign(detector, "m_CameraManager", cameraManager);
        Assign(detector, "m_HandTracking", hands);

        var shoal = new GameObject("Fish Shoal").AddComponent<FishShoal>();
        shoal.transform.SetParent(root.transform);
        Assign(shoal, "m_Material", fishMaterial);

        var soundscape = new GameObject("Kin Soundscape").AddComponent<KinSoundscape>();
        soundscape.transform.SetParent(root.transform);

        var choreographer = root.AddComponent<KinChoreographer>();
        Assign(choreographer, "m_Detector", detector);
        Assign(choreographer, "m_Shoal", shoal);
        Assign(choreographer, "m_Soundscape", soundscape);

        var hud = root.AddComponent<KinHud>();
        Assign(hud, "m_Detector", detector);
        Assign(hud, "m_BarMaterial", barMaterial);
    }

    static void Finish()
    {
        try
        {
            var camera = Object.FindFirstObjectByType<HoloKitCameraManager>();
            var hands = Object.FindFirstObjectByType<HandTrackingManager>();
            if (camera.transform.Find("Stereo Tracked Pose") == null)
                throw new Exception("HoloKit Camera rig was not built (no Stereo Tracked Pose).");
            if (hands.transform.childCount != 2 || hands.transform.GetChild(0).childCount != 21)
                throw new Exception($"Hand joints were not built ({hands.transform.childCount} hands).");

            Directory.CreateDirectory("Assets/Scenes");
            if (!EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath))
                throw new Exception("Saving the scene failed.");
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[ShoalingWithKinSetup] Scene saved to {ScenePath}");
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ShoalingWithKinSetup] {e}");
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
        }
    }

    [MenuItem("Shoaling With Kin/Build iOS")]
    public static void BuildIOS()
    {
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = BuildPath,
            target = BuildTarget.iOS,
            targetGroup = BuildTargetGroup.iOS,
            options = BuildOptions.None,
        });
        Debug.Log($"[ShoalingWithKinSetup] Build {report.summary.result}: {report.summary.totalErrors} errors -> {BuildPath}");
        if (report.summary.result != BuildResult.Succeeded && Application.isBatchMode)
            EditorApplication.Exit(1);
    }
}
#endif
