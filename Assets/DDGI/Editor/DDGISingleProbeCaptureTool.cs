using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Dou.DDGI.Editor
{
    public static class DDGISingleProbeCaptureTool
    {
        const string RayTracingShaderPath = "Assets/DDGI/Shaders/TraceProbeGBuffer.raytrace";
        const string OutputDirectory = "Assets/DDGI/Captures";
        const string CaptureRequestPath = "Library/DDGI.SingleProbeCapture.request";
        const string TestProbeName = "DDGI Test Probe";
        const int PreviewHeight = 32;

        [InitializeOnLoadMethod]
        static void CaptureWhenRequested()
        {
            if (!File.Exists(CaptureRequestPath))
                return;

            File.Delete(CaptureRequestPath);
            EditorApplication.delayCall += CaptureSampleSceneSafely;
        }

        [MenuItem("Dou DDGI/Capture Selected Probe GBuffer")]
        public static void CaptureSelectedProbe()
        {
            if (Selection.activeTransform == null)
            {
                Debug.LogError("Select a GameObject to use as the DDGI probe position.");
                return;
            }

            CaptureAt(Selection.activeTransform.position);
        }

        public static void CaptureSampleSceneForBatch()
        {
            Scene scene = EditorSceneManager.OpenScene(
                "Assets/Scenes/SampleScene.unity",
                OpenSceneMode.Single);
            DDGIProbe probe = FindOrCreateTestProbe(scene);
            CaptureAt(probe.Position);
        }

        static DDGIProbe FindOrCreateTestProbe(Scene scene)
        {
            DDGIProbe existingProbe = Object.FindFirstObjectByType<DDGIProbe>();
            if (existingProbe != null)
                return existingProbe;

            var probeObject = new GameObject(TestProbeName);
            SceneManager.MoveGameObjectToScene(probeObject, scene);
            Camera mainCamera = Camera.main;
            probeObject.transform.position = mainCamera != null
                ? mainCamera.transform.position
                : Vector3.zero;

            DDGIProbe probe = probeObject.AddComponent<DDGIProbe>();
            probe.Configure(0, Vector3Int.zero, 1);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = probeObject;
            return probe;
        }

        static void CaptureSampleSceneSafely()
        {
            try
            {
                CaptureSampleSceneForBatch();
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        static void CaptureAt(Vector3 probePosition)
        {
            if (!SystemInfo.supportsRayTracingShaders)
                throw new System.NotSupportedException(
                    $"Ray tracing shaders are unavailable with {SystemInfo.graphicsDeviceType}. Use Direct3D 12.");

            RayTracingShader rayTracingShader = AssetDatabase.LoadAssetAtPath<RayTracingShader>(RayTracingShaderPath);
            Shader surfaceShader = Shader.Find("DouDDGI/RayTracingSurface");
            if (rayTracingShader == null)
                throw new FileNotFoundException("The DDGI ray tracing shader could not be loaded.", RayTracingShaderPath);
            if (surfaceShader == null)
                throw new FileNotFoundException("The DDGI ray tracing surface shader could not be found.");

            var surfaceMaterial = new Material(surfaceShader) { hideFlags = HideFlags.HideAndDontSave };
            var rayGBuffer = new DDGIRayGBuffer();
            var rayTracingScene = new DDGIRayTracingScene(surfaceMaterial);
            ComputeBuffer probePositions = null;
            ComputeBuffer probeStates = null;
            CommandBuffer commandBuffer = null;

            try
            {
                rayGBuffer.EnsureCreated(1);
                rayTracingScene.Rebuild(~0);

                probePositions = new ComputeBuffer(1, sizeof(float) * 3);
                probePositions.SetData(new[] { probePosition });
                probeStates = new ComputeBuffer(1, sizeof(uint) * 2);
                probeStates.SetData(new[] { new Vector2Int((int)DDGIProbeState.Vigilant, 1) });

                var dispatcher = new DDGIRayTracingDispatcher(rayTracingShader);
                commandBuffer = new CommandBuffer { name = "DDGI Single Probe Capture" };
                rayGBuffer.Clear(commandBuffer);
                dispatcher.RecordTrace(
                    commandBuffer,
                    rayTracingScene.AccelerationStructure,
                    probePositions,
                    rayGBuffer,
                    0.01f,
                    100.0f,
                    Matrix4x4.identity,
                    probeStates);
                Graphics.ExecuteCommandBuffer(commandBuffer);

                SavePreviews(rayGBuffer);
                Debug.Log(
                    $"DDGI single-probe capture completed at {probePosition}. " +
                    $"Traced {rayGBuffer.RayCount} rays against {rayTracingScene.BuiltInstanceCount} " +
                    $"of {rayTracingScene.InstanceCount} submitted sub-mesh instances " +
                    $"({rayTracingScene.AccelerationStructureSize} bytes). " +
                    $"Previews: {OutputDirectory}");
            }
            finally
            {
                commandBuffer?.Release();
                probePositions?.Release();
                probeStates?.Release();
                rayTracingScene.Dispose();
                rayGBuffer.Dispose();
                Object.DestroyImmediate(surfaceMaterial);
            }
        }

        static void SavePreviews(DDGIRayGBuffer rayGBuffer)
        {
            Directory.CreateDirectory(OutputDirectory);

            Color[] positionPixels = ReadPixels(rayGBuffer.PositionTexture, TextureFormat.RGBAFloat);
            Color[] normalPixels = ReadPixels(rayGBuffer.NormalTexture, TextureFormat.RGBAHalf);
            Color[] albedoPixels = ReadPixels(rayGBuffer.AlbedoTexture, TextureFormat.RGBA32);

            int hitCount = 0;
            Vector3 minimumPosition = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 maximumPosition = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            foreach (Color pixel in positionPixels)
            {
                if (pixel.a < 0.5f)
                    continue;

                hitCount++;
                Vector3 position = new Vector3(pixel.r, pixel.g, pixel.b);
                minimumPosition = Vector3.Min(minimumPosition, position);
                maximumPosition = Vector3.Max(maximumPosition, position);
            }

            Vector3 positionRange = maximumPosition - minimumPosition;
            for (int index = 0; index < positionPixels.Length; index++)
            {
                if (positionPixels[index].a < 0.5f || hitCount == 0)
                {
                    positionPixels[index] = Color.black;
                    normalPixels[index] = Color.black;
                    albedoPixels[index] = Color.black;
                    continue;
                }

                Color position = positionPixels[index];
                positionPixels[index] = new Color(
                    Normalize(position.r, minimumPosition.x, positionRange.x),
                    Normalize(position.g, minimumPosition.y, positionRange.y),
                    Normalize(position.b, minimumPosition.z, positionRange.z),
                    1.0f);

                Color normal = normalPixels[index];
                normalPixels[index] = new Color(
                    normal.r * 0.5f + 0.5f,
                    normal.g * 0.5f + 0.5f,
                    normal.b * 0.5f + 0.5f,
                    1.0f);
                albedoPixels[index].a = 1.0f;
            }

            SaveExpandedPng("SingleProbe_Position.png", positionPixels);
            SaveExpandedPng("SingleProbe_Normal.png", normalPixels);
            SaveExpandedPng("SingleProbe_Albedo.png", albedoPixels);
            File.WriteAllText(
                Path.Combine(OutputDirectory, "SingleProbe_Summary.txt"),
                $"Rays: {rayGBuffer.RayCount}\nHits: {hitCount}\nMisses: {rayGBuffer.RayCount - hitCount}\n");
            AssetDatabase.Refresh();
        }

        static Color[] ReadPixels(RenderTexture source, TextureFormat format)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            var readable = new Texture2D(source.width, source.height, format, false, true);
            readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readable.Apply();
            Color[] pixels = readable.GetPixels();
            Object.DestroyImmediate(readable);
            RenderTexture.active = previous;
            return pixels;
        }

        static void SaveExpandedPng(string fileName, Color[] sourcePixels)
        {
            int width = sourcePixels.Length;
            var preview = new Texture2D(width, PreviewHeight, TextureFormat.RGBA32, false, true);
            var expandedPixels = new Color[width * PreviewHeight];
            for (int y = 0; y < PreviewHeight; y++)
                System.Array.Copy(sourcePixels, 0, expandedPixels, y * width, width);

            preview.SetPixels(expandedPixels);
            preview.Apply();
            File.WriteAllBytes(Path.Combine(OutputDirectory, fileName), preview.EncodeToPNG());
            Object.DestroyImmediate(preview);
        }

        static float Normalize(float value, float minimum, float range)
        {
            return range > 1e-5f ? Mathf.Clamp01((value - minimum) / range) : 0.5f;
        }
    }
}
