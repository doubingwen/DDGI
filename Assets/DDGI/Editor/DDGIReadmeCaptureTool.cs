using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Dou.DDGI.Editor
{
    [InitializeOnLoad]
    public static class DDGIReadmeCaptureTool
    {
        const string RequestPath = "Library/DDGI.ReadmeCapture.request";
        const string OutputDirectory = "pictures/ddgi";
        static readonly BindingFlags PrivateFields = BindingFlags.Instance | BindingFlags.NonPublic;

        static DDGIReadmeCaptureTool()
        {
            EditorApplication.update += CheckRequest;
        }

        static void CheckRequest()
        {
            if (!File.Exists(RequestPath) || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            DDGIProbeVolume[] volumes = GetActiveVolumes();
            if (volumes.Length == 0 || Array.Exists(volumes, volume => !volume.HasBlendedProbeData ||
                volume.AccumulatedFrameCount < 128))
            {
                SceneView.RepaintAll();
                return;
            }
            File.Delete(RequestPath);
            Capture();
        }

        [MenuItem("Dou DDGI/Export README Screenshots")]
        public static void Capture()
        {
            DDGIProbeVolume[] volumes = GetActiveVolumes();
            Camera sourceCamera = Camera.main;
            if (sourceCamera == null && SceneView.lastActiveSceneView != null)
                sourceCamera = SceneView.lastActiveSceneView.camera;
            if (sourceCamera == null || volumes.Length == 0 ||
                Array.Exists(volumes, volume => !volume.HasBlendedProbeData))
            {
                Debug.LogError("DDGI README capture needs a camera and initialized active volumes.");
                return;
            }

            var strengths = new float[volumes.Length];
            var modes = new DDGICompositeDebugView[volumes.Length];
            var automatic = new bool[volumes.Length];
            for (int i = 0; i < volumes.Length; ++i)
            {
                strengths[i] = GetField<float>(volumes[i], "indirectDiffuseIntensity");
                modes[i] = volumes[i].CompositeDebugView;
                automatic[i] = volumes[i].CaptureVolumeEveryFrame;
            }
            GameObject captureObject = null;
            RenderTexture target = null;
            try
            {
                Directory.CreateDirectory(OutputDirectory);
                for (int i = 0; i < volumes.Length; ++i)
                    SetField(volumes[i], "captureVolumeEveryFrame", false);

                captureObject = new GameObject("DDGI README Capture") { hideFlags = HideFlags.HideAndDontSave };
                Camera camera = captureObject.AddComponent<Camera>();
                camera.CopyFrom(sourceCamera);
                camera.transform.SetPositionAndRotation(sourceCamera.transform.position, sourceCamera.transform.rotation);
                camera.cameraType = CameraType.Game;
                camera.enabled = false;
                camera.aspect = 16.0f / 9.0f;
                var cameraData = captureObject.AddComponent<UniversalAdditionalCameraData>();
                if (sourceCamera.TryGetComponent(out UniversalAdditionalCameraData sourceData))
                {
                    cameraData.renderPostProcessing = sourceData.renderPostProcessing;
                    cameraData.volumeLayerMask = sourceData.volumeLayerMask;
                    cameraData.antialiasing = sourceData.antialiasing;
                    cameraData.renderShadows = sourceData.renderShadows;
                    var serializedData = new SerializedObject(sourceData);
                    cameraData.SetRenderer(serializedData.FindProperty("m_RendererIndex").intValue);
                }
                target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32)
                    { name = "DDGI README Screenshot", antiAliasing = 1 };
                target.Create();
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
                if (!RenderPipeline.SupportsRenderRequest(camera, request))
                    throw new InvalidOperationException("The active pipeline does not support URP camera render requests.");

                for (int mode = 0; mode < 3; ++mode)
                {
                    for (int i = 0; i < volumes.Length; ++i)
                    {
                        SetField(volumes[i], "indirectDiffuseIntensity", mode == 0 ? 0.0f : strengths[i]);
                        SetField(volumes[i], "compositeDebugView", mode == 2
                            ? DDGICompositeDebugView.IndirectOnly : DDGICompositeDebugView.Composite);
                    }
                    RenderPipeline.SubmitRenderRequest(camera, request);
                    string name = mode == 0 ? "Scene_WithoutDDGI.png" : mode == 1
                        ? "Scene_WithDDGI.png" : "Scene_IndirectOnly.png";
                    SaveScene(target, name);
                }

                DDGIProbeVolume volume = volumes[0];
                Color[] positions = ReadLinear(volume.RayGBuffer.PositionTexture);
                Vector3 minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                int hits = 0;
                foreach (Color position in positions)
                {
                    if (position.a <= 0.0f) continue;
                    Vector3 point = new Vector3(position.r, position.g, position.b);
                    minimum = Vector3.Min(minimum, point);
                    maximum = Vector3.Max(maximum, point);
                    hits++;
                }
                if (hits == 0) throw new InvalidOperationException("Ray G-Buffer has no hits; refusing to export misleading previews.");
                Vector3 range = maximum - minimum;
                for (int i = 0; i < positions.Length; ++i)
                {
                    Color point = positions[i];
                    positions[i] = point.a > 0.0f ? new Color(
                        (point.r - minimum.x) / Mathf.Max(range.x, 0.0001f),
                        (point.g - minimum.y) / Mathf.Max(range.y, 0.0001f),
                        (point.b - minimum.z) / Mathf.Max(range.z, 0.0001f), 1.0f) : Color.black;
                }
                SavePreview(volume.RayGBuffer.PositionTexture, positions, "Buffer_Position.png", true, color => color);
                SavePreview(volume.RayGBuffer.NormalTexture, ReadLinear(volume.RayGBuffer.NormalTexture),
                    "Buffer_Normal.png", true, color => Mathf.Abs(color.a) > 0.0f
                        ? new Color(color.r * 0.5f + 0.5f, color.g * 0.5f + 0.5f, color.b * 0.5f + 0.5f, 1.0f) : Color.black);
                SavePreview(volume.RayGBuffer.AlbedoTexture, ReadLinear(volume.RayGBuffer.AlbedoTexture),
                    "Buffer_Albedo.png", true, color => color.gamma);
                SavePreview(volume.RadianceTexture, ReadLinear(volume.RadianceTexture),
                    "Buffer_Radiance.png", true, ToneMap);
                SavePreview(volume.ShadowVisibilityTexture, ReadLinear(volume.ShadowVisibilityTexture),
                    "Buffer_MainLightVisibility.png", true, color => new Color(color.r, color.r, color.r, 1.0f));
                SavePreview(volume.IrradianceAtlas, ReadLinear(volume.IrradianceAtlas),
                    "Atlas_Irradiance.png", false, ToneMap);
                float maximumDistance = GetField<float>(volume, "maximumRayDistance");
                SavePreview(volume.DistanceMomentsAtlas, ReadLinear(volume.DistanceMomentsAtlas),
                    "Atlas_Distance.png", false, color => new Color(
                        Mathf.Clamp01(color.r / maximumDistance),
                        Mathf.Clamp01(color.r / maximumDistance),
                        Mathf.Clamp01(color.r / maximumDistance), 1.0f));
                string report = $"Scene: {sourceCamera.gameObject.scene.path}\nCamera: {sourceCamera.name}\n" +
                    $"Camera position: {sourceCamera.transform.position}\nCamera rotation: {sourceCamera.transform.eulerAngles}\n" +
                    $"Buffer volume: {volume.name}\nNative Ray G-Buffer: {volume.RaysPerProbe} x {volume.ProbeCount}\n" +
                    $"Ray hits: {hits}/{positions.Length}\nAccumulated frames: {volume.AccumulatedFrameCount}\n" +
                    $"Position normalization bounds: {minimum} .. {maximum}\n" +
                    $"Irradiance atlas: {volume.IrradianceAtlasResolution}\nDistance atlas: {volume.DistanceAtlasResolution}\n" +
                    $"Rendered volumes: {DDGICompositeFeature.RenderedVolumeCount}\nRuntime status: {DDGICompositeFeature.RuntimeStatus}\n";
                for (int i = 0; i < volumes.Length; ++i)
                    report += $"{volumes[i].name}: indirect intensity={strengths[i]}, probes={volumes[i].ProbeCount}\n";
                File.WriteAllText(Path.Combine(OutputDirectory, "CaptureInfo.txt"), report);
                Debug.Log("DDGI README capture completed. " + report);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                for (int i = 0; i < volumes.Length; ++i)
                {
                    if (volumes[i] == null) continue;
                    SetField(volumes[i], "indirectDiffuseIntensity", strengths[i]);
                    SetField(volumes[i], "compositeDebugView", modes[i]);
                    SetField(volumes[i], "captureVolumeEveryFrame", automatic[i]);
                }
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (captureObject != null) UnityEngine.Object.DestroyImmediate(captureObject);
                SceneView.RepaintAll();
            }
        }

        static DDGIProbeVolume[] GetActiveVolumes()
        {
            var volumes = new List<DDGIProbeVolume>();
            DDGIProbeVolumeRegistry.GetActiveVolumesByDensity(volumes);
            return volumes.ToArray();
        }

        static T GetField<T>(DDGIProbeVolume volume, string name) =>
            (T)typeof(DDGIProbeVolume).GetField(name, PrivateFields).GetValue(volume);

        static void SetField<T>(DDGIProbeVolume volume, string name, T value) =>
            typeof(DDGIProbeVolume).GetField(name, PrivateFields).SetValue(volume, value);

        static Color[] ReadLinear(RenderTexture source)
        {
            if (source == null || !source.IsCreated()) throw new InvalidOperationException("DDGI texture is unavailable.");
            RenderTexture previous = RenderTexture.active;
            var readable = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = source;
                readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                readable.Apply();
                return readable.GetPixels();
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(readable); }
        }

        static void SaveScene(RenderTexture source, string name)
        {
            RenderTexture previous = RenderTexture.active;
            var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = source;
                readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                readable.Apply();
                File.WriteAllBytes(Path.Combine(OutputDirectory, name), readable.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(readable); }
        }

        static Color ToneMap(Color color) => new Color(
            Mathf.Max(0.0f, color.r) / (1.0f + Mathf.Max(0.0f, color.r)),
            Mathf.Max(0.0f, color.g) / (1.0f + Mathf.Max(0.0f, color.g)),
            Mathf.Max(0.0f, color.b) / (1.0f + Mathf.Max(0.0f, color.b)), 1.0f).gamma;

        static void SavePreview(RenderTexture source, Color[] data, string name, bool transpose, Func<Color, Color> convert)
        {
            int width = transpose ? source.height : source.width;
            int height = transpose ? source.width : source.height;
            int scale = transpose ? 2 : 3;
            var preview = new Texture2D(width * scale, height * scale, TextureFormat.RGBA32, false, true);
            try
            {
                var pixels = new Color[preview.width * preview.height];
                for (int y = 0; y < preview.height; ++y)
                    for (int x = 0; x < preview.width; ++x)
                    {
                        int sx = transpose ? y / scale : x / scale;
                        int sy = transpose ? x / scale : y / scale;
                        Color color = convert(data[sy * source.width + sx]);
                        color.a = 1.0f;
                        pixels[y * preview.width + x] = color;
                    }
                preview.SetPixels(pixels);
                preview.Apply();
                File.WriteAllBytes(Path.Combine(OutputDirectory, name), preview.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(preview); }
        }
    }
}
