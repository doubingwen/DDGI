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
    public static class DDGIReadmeVideoTool
    {
        const string RequestPath = "Library/DDGI.ReadmeVideo.v2.request";
        const string FrameDirectory = "Library/DDGI.VideoFrames";
        const int WarmupUpdates = 120;
        const int FrameCount = 360;
        static readonly BindingFlags PrivateFields = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly List<DDGIProbeVolume> Volumes = new List<DDGIProbeVolume>();
        static bool[] originalAutomatic;
        static DDGICompositeDebugView[] originalModes;
        static Light mainLight;
        static Quaternion originalLightRotation;
        static Quaternion centeredLightRotation;
        static float centeringYaw;
        static float startingYaw;
        static GameObject cameraObject;
        static Camera captureCamera;
        static RenderTexture target;
        static Texture2D readable;
        static ScriptableRendererData rendererData;
        static VideoUpdateFeature updateFeature;
        static int warmup;
        static int frame;
        static bool recording;

        static DDGIReadmeVideoTool()
        {
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            EditorApplication.quitting += Cancel;
            EditorApplication.playModeStateChanged += _ => Cancel();
        }

        [MenuItem("Dou DDGI/Record README Light Rotation")]
        public static void Begin()
        {
            if (recording) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("DDGI README video capture runs in Edit mode. Exit Play first.");
                return;
            }
            DDGIProbeVolumeRegistry.GetActiveVolumesByDensity(Volumes);
            Camera source = Camera.main;
            if (source == null || Volumes.Count == 0)
            {
                Debug.LogError("DDGI video capture needs a Main Camera and active volumes.");
                return;
            }
            mainLight = RenderSettings.sun;
            if (mainLight == null || mainLight.type != LightType.Directional || !mainLight.isActiveAndEnabled)
            {
                mainLight = null;
                foreach (Light light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (light.type == LightType.Directional && light.isActiveAndEnabled &&
                        (mainLight == null || light.intensity > mainLight.intensity)) mainLight = light;
            }
            if (mainLight == null || mainLight.shadows == LightShadows.None)
            {
                Debug.LogError("DDGI video capture needs a shadow-casting main directional light.");
                return;
            }
            originalLightRotation = mainLight.transform.rotation;
            Vector3 lightHeading = Vector3.ProjectOnPlane(mainLight.transform.forward, Vector3.up).normalized;
            Vector3 cameraHeading = Vector3.ProjectOnPlane(source.transform.forward, Vector3.up).normalized;
            if (lightHeading.sqrMagnitude < 0.5f || cameraHeading.sqrMagnitude < 0.5f)
            {
                Debug.LogError("Use a nonvertical camera and directional light for the left-to-right demo.");
                return;
            }
            Vector3 centralHeading = Vector3.Dot(lightHeading, cameraHeading) >= 0.0f
                ? cameraHeading : -cameraHeading;
            centeringYaw = Vector3.SignedAngle(lightHeading, centralHeading, Vector3.up);
            centeredLightRotation = Quaternion.AngleAxis(centeringYaw, Vector3.up) * originalLightRotation;
            Vector3 positiveOffsetLightDirection = -(Quaternion.AngleAxis(45.0f, Vector3.up) *
                centeredLightRotation * Vector3.forward);
            startingYaw = Vector3.Dot(positiveOffsetLightDirection, source.transform.right) <= 0.0f ? 45.0f : -45.0f;
            originalAutomatic = new bool[Volumes.Count];
            originalModes = new DDGICompositeDebugView[Volumes.Count];
            for (int i = 0; i < Volumes.Count; ++i)
            {
                originalAutomatic[i] = Volumes[i].CaptureVolumeEveryFrame;
                originalModes[i] = Volumes[i].CompositeDebugView;
            }
            recording = true;
            try
            {
                File.WriteAllText("Library/DDGI.VideoCapture.status", "recording");
                mainLight.transform.rotation = Quaternion.AngleAxis(startingYaw, Vector3.up) * centeredLightRotation;
                Directory.CreateDirectory(Path.Combine(FrameDirectory, "Composite"));
                Directory.CreateDirectory(Path.Combine(FrameDirectory, "Indirect"));
                for (int i = 0; i < Volumes.Count; ++i)
                    SetField(Volumes[i], "captureVolumeEveryFrame", false);
                cameraObject = new GameObject("DDGI Video Capture") { hideFlags = HideFlags.HideAndDontSave };
                captureCamera = cameraObject.AddComponent<Camera>();
                captureCamera.CopyFrom(source);
                captureCamera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                captureCamera.cameraType = CameraType.Game;
                captureCamera.enabled = false;
                captureCamera.aspect = 16.0f / 9.0f;
                captureCamera.targetTexture = null;
                var data = cameraObject.AddComponent<UniversalAdditionalCameraData>();
                int rendererIndex = -1;
                if (source.TryGetComponent(out UniversalAdditionalCameraData sourceData))
                {
                    data.renderPostProcessing = sourceData.renderPostProcessing;
                    data.volumeLayerMask = sourceData.volumeLayerMask;
                    data.antialiasing = sourceData.antialiasing;
                    data.renderShadows = sourceData.renderShadows;
                    rendererIndex = new SerializedObject(sourceData).FindProperty("m_RendererIndex").intValue;
                    data.SetRenderer(rendererIndex);
                }
                var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                var serializedPipeline = new SerializedObject(pipeline);
                if (rendererIndex < 0) rendererIndex = serializedPipeline.FindProperty("m_DefaultRendererIndex").intValue;
                rendererData = serializedPipeline.FindProperty("m_RendererDataList")
                    .GetArrayElementAtIndex(rendererIndex).objectReferenceValue as ScriptableRendererData;
                if (rendererData == null) throw new InvalidOperationException("URP renderer data was not found.");
                target = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32)
                    { name = "DDGI Video Frame", antiAliasing = 1 };
                target.Create();
                readable = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
                updateFeature = ScriptableObject.CreateInstance<VideoUpdateFeature>();
                updateFeature.hideFlags = HideFlags.HideAndDontSave;
                updateFeature.name = "DDGI Temporary Video Update";
                updateFeature.captureCamera = captureCamera;
                updateFeature.volumes = Volumes;
                updateFeature.Create();
                foreach (DDGIProbeVolume volume in Volumes) volume.ResetTemporalHistory();
                warmup = frame = 0;
                Debug.Log("DDGI video capture started: 120 warmup updates, 360 frames at 30 playback fps.");
            }
            catch (Exception exception) { Debug.LogException(exception); Finish(false); }
        }

        [MenuItem("Dou DDGI/Cancel README Video Recording")]
        public static void Cancel()
        {
            if (recording) Finish(false);
        }

        static void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (!recording)
            {
                if (!File.Exists(RequestPath) || EditorApplication.isPlayingOrWillChangePlaymode) return;
                File.Delete(RequestPath);
                Begin();
                return;
            }
            try
            {
                if (mainLight == null || Volumes.Exists(volume => volume == null || !volume.isActiveAndEnabled))
                    throw new InvalidOperationException("Capture light or volume was removed/disabled.");
                // Install only during this synchronous render block; never save a temporary feature to assets.
                rendererData.rendererFeatures.Add(updateFeature);
                rendererData.SetDirty();
                try
                {
                    if (warmup < WarmupUpdates)
                    {
                        for (int i = 0; i < 4 && warmup < WarmupUpdates; ++i, ++warmup)
                            RenderFrame(true, DDGICompositeDebugView.Composite);
                        return;
                    }
                    float angle = startingYaw * Mathf.Cos(2.0f * Mathf.PI * frame / (FrameCount - 1));
                    mainLight.transform.rotation = Quaternion.AngleAxis(angle, Vector3.up) * centeredLightRotation;
                    RenderFrame(true, DDGICompositeDebugView.Composite);
                    SaveFrame("Composite");
                    RenderFrame(false, DDGICompositeDebugView.IndirectOnly);
                    SaveFrame("Indirect");
                    if (frame % 60 == 0) Debug.Log($"DDGI video capture: frame {frame}/{FrameCount}, light yaw offset={angle:F1} degrees.");
                    frame++;
                }
                finally
                {
                    rendererData.rendererFeatures.Remove(updateFeature);
                    rendererData.SetDirty();
                }
                if (frame >= FrameCount) Finish(true);
            }
            catch (Exception exception) { Debug.LogException(exception); Finish(false); }
        }

        static void RenderFrame(bool update, DDGICompositeDebugView mode)
        {
            foreach (DDGIProbeVolume volume in Volumes) SetField(volume, "compositeDebugView", mode);
            updateFeature.updateEnabled = update;
            updateFeature.didUpdate = false;
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
            if (!RenderPipeline.SupportsRenderRequest(captureCamera, request))
                throw new InvalidOperationException("URP camera render requests are unsupported.");
            RenderPipeline.SubmitRenderRequest(captureCamera, request);
            if (update && !updateFeature.didUpdate)
                throw new InvalidOperationException("DDGI capture did not update with the camera's main-light shadow map.");
            if (DDGICompositeFeature.RenderedVolumeCount != Volumes.Count)
                throw new InvalidOperationException("Not all capture volumes were composited.");
        }

        static void SaveFrame(string mode)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                readable.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                readable.Apply();
                File.WriteAllBytes(Path.Combine(FrameDirectory, mode, $"frame_{frame:D4}.png"), readable.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; }
        }

        static void Finish(bool completed)
        {
            recording = false;
            if (mainLight != null) mainLight.transform.rotation = originalLightRotation;
            for (int i = 0; i < Volumes.Count; ++i)
            {
                if (Volumes[i] == null) continue;
                SetField(Volumes[i], "captureVolumeEveryFrame", originalAutomatic[i]);
                SetField(Volumes[i], "compositeDebugView", originalModes[i]);
                Volumes[i].ResetTemporalHistory();
            }
            if (rendererData != null && updateFeature != null)
            {
                rendererData.rendererFeatures.Remove(updateFeature);
                rendererData.SetDirty();
            }
            if (updateFeature != null) UnityEngine.Object.DestroyImmediate(updateFeature);
            if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
            if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
            if (completed)
            {
                Directory.CreateDirectory("pictures/ddgi");
                File.WriteAllText("pictures/ddgi/VideoCaptureInfo.txt",
                    "Scene: " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().path + "\n" +
                    "Source: Main Camera; fixed pose; 1280 x 720 per view\n" +
                    "Frames: 360; playback: 30 fps; duration: 12 seconds\n" +
                    "Main light: " + mainLight.name + "\n" +
                    $"Light centering: world-Y adjustment={centeringYaw:F3} degrees to align with the camera's horizontal sight line\n" +
                    $"Light rotation: world-Y offset = {startingYaw:F1} * cos(2 * PI * frame / 359) degrees from the centered heading\n" +
                    "Motion: camera-left -> camera-right -> camera-left; identical first/last light direction\n" +
                    "Warmup: 120 DDGI updates at the left starting direction, from reset history\n" +
                    "One DDGI update per recorded frame, followed by IndirectOnly rendering from the same atlas\n" +
                    "Offline frame capture, not a real-time performance measurement\n" +
                    "Original light rotation, debug modes and automatic-update settings restored\n");
            }
            File.WriteAllText("Library/DDGI.VideoCapture.status", completed ? "completed: 360 frames" : $"cancelled: {frame} frames");
            Debug.Log(completed ? "DDGI video capture completed; original scene settings restored." : "DDGI video capture cancelled; original scene settings restored.");
            SceneView.RepaintAll();
        }

        static void SetField<T>(DDGIProbeVolume volume, string name, T value) =>
            typeof(DDGIProbeVolume).GetField(name, PrivateFields).SetValue(volume, value);

        public sealed class VideoUpdateFeature : ScriptableRendererFeature
        {
            public Camera captureCamera;
            public List<DDGIProbeVolume> volumes;
            public bool updateEnabled;
            public bool didUpdate;
            UpdatePass pass;
            public override void Create() => pass = new UpdatePass(this) { renderPassEvent = RenderPassEvent.AfterRenderingShadows };
            public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
            {
                if (updateEnabled && data.cameraData.camera == captureCamera) renderer.EnqueuePass(pass);
            }
            sealed class UpdatePass : ScriptableRenderPass
            {
                readonly VideoUpdateFeature owner;
                public UpdatePass(VideoUpdateFeature owner) => this.owner = owner;
                [Obsolete("Offline capture uses URP compatibility mode.")]
                public override void Execute(ScriptableRenderContext context, ref RenderingData data)
                {
                    int index = data.lightData.mainLightIndex;
                    Light light = index >= 0 ? data.lightData.visibleLights[index].light : null;
                    if (light == null || !data.shadowData.supportsMainLightShadows)
                        throw new InvalidOperationException("The capture camera has no main-light shadow map.");
                    CommandBuffer commands = CommandBufferPool.Get("DDGI Video Update With Camera Shadows");
                    try
                    {
                        foreach (DDGIProbeVolume volume in owner.volumes)
                            if (!volume.RecordRealtimeCaptureAndBlend(commands, light, data.shadowData.mainLightShadowCascadesCount))
                                throw new InvalidOperationException("Volume update failed: " + volume.name);
                        context.ExecuteCommandBuffer(commands);
                        owner.didUpdate = true;
                    }
                    finally { CommandBufferPool.Release(commands); }
                }
            }
        }
    }
}
