using UnityEditor;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Dou.DDGI.Editor
{

    [CustomEditor(typeof(DDGIProbeVolume))]
    public sealed class DDGIProbeVolumeEditor : UnityEditor.Editor
    {
        static bool rendererInvalidatedThisDomain;
        static double nextPreviewRefreshTime;
        static readonly List<DDGIProbeVolume> PreviewVolumes = new List<DDGIProbeVolume>();

        const string RayTracingShaderPath = "Assets/DDGI/Shaders/TraceProbeGBuffer.raytrace";
        const string SurfaceShaderName = "DouDDGI/RayTracingSurface";
        const string RadianceComputeShaderPath = "Assets/DDGI/Shaders/EvaluateProbeRadiance.compute";
        const string ProbeBlendComputeShaderPath = "Assets/DDGI/Shaders/DDGIProbeBlend.compute";
        const string DefaultRendererDataPath = "Assets/Settings/PC_Renderer.asset";

        [InitializeOnLoadMethod]
        static void RefreshDDGIRendererAfterScriptReload()
        {
            EditorApplication.update -= RefreshRealtimePreview;
            EditorApplication.update += RefreshRealtimePreview;
            EditorApplication.delayCall += () =>
            {
                ScriptableRendererData rendererData =
                    AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(DefaultRendererDataPath);
                if (rendererData == null ||
                    !rendererData.rendererFeatures.Exists(
                        feature => feature is DDGICompositeFeature))
                {
                    return;
                }

                RepairRendererFeatureMap(rendererData);
                rendererData.SetDirty();
                rendererInvalidatedThisDomain = true;
                string featureNames = string.Join(
                    ", ",
                    rendererData.rendererFeatures.ConvertAll(
                        feature => feature == null
                            ? "<missing>"
                            : $"{feature.name} ({feature.GetType().Name})"));
                Debug.Log($"DDGI renderer refresh: {featureNames}", rendererData);
                SceneView.RepaintAll();
            };
        }

        static void RefreshRealtimePreview()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.timeSinceStartup < nextPreviewRefreshTime ||
                SceneView.sceneViews.Count == 0 || !SystemInfo.supportsRayTracingShaders)
                return;

            DDGIProbeVolumeRegistry.GetActiveVolumesByDensity(PreviewVolumes);
            foreach (DDGIProbeVolume volume in PreviewVolumes)
            {
                if (!volume.CaptureVolumeEveryFrame || !volume.CaptureVolumeInEditMode ||
                    !volume.HasCaptureShaders || !volume.HasRadianceShader || !volume.HasProbeBlendShader)
                    continue;
                // Repaint drives all volumes' camera shadow passes while the editor is idle.
                nextPreviewRefreshTime = EditorApplication.timeSinceStartup + 0.1;
                SceneView.RepaintAll();
                break;
            }
        }

        public override bool RequiresConstantRepaint()
        {
            var volume = target as DDGIProbeVolume;
            return volume != null && volume.CaptureVolumeEveryFrame &&
                (Application.isPlaying || volume.CaptureVolumeInEditMode);
        }

        [MenuItem("GameObject/Dou DDGI/DDGI Probe Volume", false, 10)]
        static void CreateProbeVolume(MenuCommand menuCommand)
        {
            var volumeObject = new GameObject("DDGI Probe Volume");
            GameObjectUtility.SetParentAndAlign(volumeObject, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(volumeObject, "Create DDGI Probe Volume");
            DDGIProbeVolume volume = Undo.AddComponent<DDGIProbeVolume>(volumeObject);
            AssignDefaultCaptureShaders(volume);
            Selection.activeGameObject = volumeObject;
            volume.RebuildProbeGrid();
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var volume = (DDGIProbeVolume)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Generated Layout", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Probe Count", volume.ProbeCount.ToString());
            EditorGUILayout.LabelField("World Probe Density", volume.WorldProbeDensity.ToString("G5"));
            EditorGUILayout.LabelField("Atlas Tiles", volume.AtlasTileCounts.ToString());
            EditorGUILayout.LabelField(
                "Irradiance Atlas",
                $"{volume.IrradianceAtlasResolution.x} x {volume.IrradianceAtlasResolution.y}");
            EditorGUILayout.LabelField(
                "Distance Atlas",
                $"{volume.DistanceAtlasResolution.x} x {volume.DistanceAtlasResolution.y}");
            EditorGUILayout.HelpBox(
                "DDGI rendering uses the independent DDGI Composite Feature. " +
                "Add it to a Deferred URP Renderer Data when you want to preview this volume.",
                MessageType.Info);
            DrawRendererFeatureInstaller();
            EditorGUILayout.LabelField(
                "Composite Runtime Status",
                DDGICompositeFeature.RuntimeStatus,
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("Recorded GI Updates", volume.RecordedUpdateCount.ToString());
            EditorGUILayout.LabelField("Accumulated Frames", volume.AccumulatedFrameCount.ToString());
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Probe State Statistics (Async GPU)", EditorStyles.boldLabel);
            if (volume.HasProbeStateStatistics)
            {
                foreach (DDGIProbeState state in System.Enum.GetValues(typeof(DDGIProbeState)))
                    EditorGUILayout.LabelField(state.ToString(), volume.GetProbeStateCount(state).ToString());
                EditorGUILayout.LabelField("Scheduled Probe Updates", $"{volume.LastUpdatedProbeCount} / {volume.ProbeCount}");
                EditorGUILayout.LabelField("Scheduled Rays", (volume.LastUpdatedProbeCount * volume.RaysPerProbe).ToString());
            }
            else EditorGUILayout.LabelField("Waiting for GPU state readback");
            if (GUILayout.Button("Reclassify Probes"))
            {
                volume.ReclassifyProbes();
                SceneView.RepaintAll();
            }
            EditorGUILayout.HelpBox("Mark fixed geometry Static. Unmarked MeshRenderers are treated as dynamic. " +
                "Off probes are excluded from interpolation; sleeping probes retain their atlases. " +
                "Statistics refresh asynchronously about twice per second.", MessageType.Info);
            EditorGUILayout.LabelField("Rendered Volume Count", DDGICompositeFeature.RenderedVolumeCount.ToString());
            if (!string.IsNullOrEmpty(DDGICompositeFeature.LastRenderedCamera))
                EditorGUILayout.LabelField("Last Rendered Camera", DDGICompositeFeature.LastRenderedCamera);
            if (!volume.CaptureVolumeEveryFrame)
                EditorGUILayout.HelpBox(
                    "Automatic capture is off. Play generates the volume once; enable Capture Volume Every Frame " +
                    "for dynamic lighting and temporal supersampling.", MessageType.Info);

            EditorGUILayout.Space();
            if (GUILayout.Button("Rebuild Probe Grid"))
            {
                Undo.RegisterFullObjectHierarchyUndo(volume.gameObject, "Rebuild DDGI Probe Grid");
                volume.RebuildProbeGrid();
                EditorUtility.SetDirty(volume);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Allocate Resources"))
                    volume.EnsureResources();
                if (GUILayout.Button("Release Resources"))
                    volume.ReleaseResources();
            }

            if (GUILayout.Button("Capture G-Buffer (Shade With Camera Shadows)"))
            {
                AssignDefaultCaptureShaders(volume);
                volume.CaptureRayGBuffer();
                SceneView.RepaintAll();
                Repaint();
            }

            if (GUILayout.Button("Evaluate Radiance With Camera Shadows"))
            {
                AssignDefaultCaptureShaders(volume);
                volume.EvaluateRadiance();
                SceneView.RepaintAll();
                Repaint();
            }

            if (GUILayout.Button("Blend Probes"))
            {
                AssignDefaultCaptureShaders(volume);
                volume.BlendProbes();
                Repaint();
            }

            if (GUILayout.Button("Reset Temporal History"))
            {
                volume.ResetTemporalHistory();
                SceneView.RepaintAll();
                Repaint();
            }

            if (volume.LastCapturedProbeCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Captured {volume.RaysPerProbe} x {volume.LastCapturedProbeCount} rays " +
                    $"against {volume.LastCapturedGeometryCount} geometry instances. " +
                    $"Evaluated {volume.LastEvaluatedLightCount} active lights and " +
                    $"blended {volume.LastBlendedProbeCount} probes.",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("GPU Resources", EditorStyles.boldLabel);
            DrawTexturePreview("Irradiance", volume.IrradianceAtlas);
            DrawTexturePreview("Distance Moments", volume.DistanceMomentsAtlas);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Ray G-Buffer Preview", EditorStyles.boldLabel);
            DrawTexturePreview("Position", volume.RayGBuffer?.PositionTexture);
            DrawTexturePreview("Normal", volume.RayGBuffer?.NormalTexture);
            DrawTexturePreview("Albedo", volume.RayGBuffer?.AlbedoTexture);
            DrawTexturePreview("Ray Distance Moments", volume.RayGBuffer?.DistanceMomentsTexture);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Lighting Result", EditorStyles.boldLabel);
            DrawTexturePreview("Radiance", volume.RadianceTexture);
            DrawTexturePreview("Main Light Visibility", volume.ShadowVisibilityTexture);
        }

        static void AssignDefaultCaptureShaders(DDGIProbeVolume volume)
        {
            if (!volume.HasCaptureShaders)
            {
                RayTracingShader rayTracingShader =
                    AssetDatabase.LoadAssetAtPath<RayTracingShader>(RayTracingShaderPath);
                Shader surfaceShader = Shader.Find(SurfaceShaderName);
                volume.ConfigureCaptureShaders(rayTracingShader, surfaceShader);
            }

            if (!volume.HasRadianceShader)
            {
                ComputeShader radianceComputeShader =
                    AssetDatabase.LoadAssetAtPath<ComputeShader>(RadianceComputeShaderPath);
                volume.ConfigureRadianceShader(radianceComputeShader);
            }

            if (!volume.HasProbeBlendShader)
            {
                ComputeShader probeBlendComputeShader =
                    AssetDatabase.LoadAssetAtPath<ComputeShader>(ProbeBlendComputeShaderPath);
                volume.ConfigureProbeBlendShader(probeBlendComputeShader);
            }
            EditorUtility.SetDirty(volume);
        }

        static void DrawRendererFeatureInstaller()
        {
            ScriptableRendererData rendererData =
                AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(DefaultRendererDataPath);
            if (rendererData == null)
            {
                EditorGUILayout.HelpBox(
                    $"Renderer Data was not found at {DefaultRendererDataPath}.",
                    MessageType.Warning);
                return;
            }

            bool isInstalled = rendererData.rendererFeatures.Exists(
                feature => feature is DDGICompositeFeature);
            if (isInstalled)
            {
                RepairRendererFeatureMap(rendererData);
                if (!rendererInvalidatedThisDomain)
                {
                    rendererData.SetDirty();
                    rendererInvalidatedThisDomain = true;
                }
                EditorGUILayout.LabelField("DDGI Composite Feature", "Installed");
                return;
            }

            if (GUILayout.Button("Install DDGI Composite Feature"))
                InstallRendererFeature(rendererData);
        }

        static void InstallRendererFeature(ScriptableRendererData rendererData)
        {
            var feature = CreateInstance<DDGICompositeFeature>();
            feature.name = "DDGI Composite";
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            rendererData.rendererFeatures.Add(feature);
            RepairRendererFeatureMap(rendererData);
            feature.Create();
            rendererData.SetDirty();
            rendererInvalidatedThisDomain = true;
            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static void RepairRendererFeatureMap(ScriptableRendererData rendererData)
        {
            var rendererDataObject = new SerializedObject(rendererData);
            SerializedProperty featureMap = rendererDataObject.FindProperty("m_RendererFeatureMap");
            if (featureMap == null)
                return;

            bool mapChanged = featureMap.arraySize != rendererData.rendererFeatures.Count;
            if (mapChanged)
                featureMap.arraySize = rendererData.rendererFeatures.Count;

            for (int index = 0; index < rendererData.rendererFeatures.Count; index++)
            {
                ScriptableRendererFeature feature = rendererData.rendererFeatures[index];
                if (feature == null ||
                    !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        feature,
                        out string _,
                        out long localId))
                {
                    continue;
                }

                SerializedProperty mapElement = featureMap.GetArrayElementAtIndex(index);
                if (mapElement.longValue == localId)
                    continue;

                mapElement.longValue = localId;
                mapChanged = true;
            }

            if (!mapChanged)
                return;

            rendererDataObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssetIfDirty(rendererData);
        }

        static void DrawTexturePreview(string label, RenderTexture texture)
        {
            EditorGUILayout.ObjectField(label, texture, typeof(RenderTexture), false);
            if (texture == null || !texture.IsCreated())
            {
                EditorGUILayout.HelpBox("Texture has not been allocated.", MessageType.None);
                return;
            }

            Rect previewRect = GUILayoutUtility.GetRect(1.0f, 180.0f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawPreviewTexture(previewRect, texture, null, ScaleMode.ScaleToFit);
            EditorGUILayout.LabelField(
                $"{texture.width} x {texture.height}  {texture.graphicsFormat}",
                EditorStyles.miniLabel);
        }
    }
}
