using System;
using System.IO;
using ShoalingWithKin;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>
/// Offline preview of the shoal: steps the real FishShoal simulation (boids, camp gestures) and
/// renders it with the real mesh and shader from an iPhone-like camera onto a transparent PNG,
/// for compositing over a camera photo. Needs graphics (no -nographics).
/// Unity -batchmode -projectPath app -buildTarget iOS -executeMethod ShoalingPreview.Render -quit
/// </summary>
public static class ShoalingPreview
{
    const string OutputDir = "Builds/Preview";
    const string FishMaterialPath = "Assets/ShoalingWithKin/Materials/Fish.mat";
    const int Width = 1600, Height = 1200;
    const float Dt = 1f / 60f;

    // Viewport points (x right, y up) of the hand in tools/testset photos, and its depth in metres.
    static readonly (string name, string kin, Vector2 hand, float depth, float roam, float camp)[] Shots =
    {
        ("roaming", null, default, 0f, 6f, 0f),
        ("rock", "rock", new Vector2(0.60f, 0.50f), 0.55f, 4f, 1.3f),
        ("tree", "tree", new Vector2(0.74f, 0.55f), 0.60f, 4f, 2.6f),
    };

    [MenuItem("Shoaling With Kin/Render Preview")]
    public static void Render()
    {
        try
        {
            Directory.CreateDirectory(OutputDir);
            foreach (var shot in Shots)
                RenderShot(shot.name, shot.kin, shot.hand, shot.depth, shot.roam, shot.camp);
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ShoalingPreview] {e}");
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
        }
    }

    static void RenderShot(string name, string kinId, Vector2 hand, float depth, float roamSeconds, float campSeconds)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camera = new GameObject("Preview Camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.fieldOfView = 54f;  // iPhone wide camera, 4:3 landscape
        camera.nearClipPlane = 0.05f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
        camera.targetTexture = target;
        camera.aspect = Width / (float)Height;

        var shoal = new GameObject("Fish Shoal").AddComponent<FishShoal>();
        var so = new SerializedObject(shoal);
        so.FindProperty("m_Material").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>(FishMaterialPath)
                                                            ?? throw new Exception($"No material at {FishMaterialPath}; run Setup first.");
        so.ApplyModifiedPropertiesWithoutUndo();
        UnityEngine.Random.InitState(7);
        shoal.EnsureInitialized();

        for (float t = 0f; t < roamSeconds; t += Dt)
            shoal.Step(Dt);
        if (kinId != null)
        {
            var kin = Array.Find(KinDefinition.Defaults(), k => k.Id == kinId);
            var point = camera.ViewportToWorldPoint(new Vector3(hand.x, hand.y, depth));
            shoal.Camp(kin, point);
            for (float t = 0f; t < campSeconds; t += Dt)
                shoal.Step(Dt);
        }

        var commands = new CommandBuffer { name = "Fish" };
        shoal.Record(commands);
        camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, commands);
        camera.Render();
        camera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, commands);

        var previous = RenderTexture.active;
        RenderTexture.active = target;
        var image = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
        image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        image.Apply();
        RenderTexture.active = previous;
        var path = Path.Combine(OutputDir, $"fish_{name}.png");
        File.WriteAllBytes(path, image.EncodeToPNG());
        Debug.Log($"[ShoalingPreview] {path}");

        commands.Release();
        Object.DestroyImmediate(image);
        target.Release();
    }
}
