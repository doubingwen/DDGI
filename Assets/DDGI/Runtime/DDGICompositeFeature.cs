using UnityEngine;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Dou.DDGI
{
    public sealed class DDGICompositeFeature : ScriptableRendererFeature
    {
        public static string RuntimeStatus { get; private set; } = "Feature has not been created.";
        public static string LastRenderedCamera { get; private set; }
        public static int RenderedVolumeCount { get; private set; }

        static void SetRuntimeStatus(string status)
        {
            if (RuntimeStatus == status)
                return;

            RuntimeStatus = status;
            Debug.Log($"DDGI Composite: {status}");
        }

        sealed class DDGICompositePass : ScriptableRenderPass
        {
            static readonly ProfilingSampler ProfilingSampler =
                new ProfilingSampler("Dou DDGI: Composite Indirect Diffuse");

            readonly Material compositeMaterial;
            readonly List<DDGIProbeVolume> volumes = new List<DDGIProbeVolume>();
            readonly List<DDGIProbeVolume> readyVolumes = new List<DDGIProbeVolume>();
            static readonly int AccumulatedIndirectId = Shader.PropertyToID("_DDGI_AccumulatedIndirectTexture");
            static readonly int DebugViewId = Shader.PropertyToID("_DDGI_CompositeDebugView");
            RTHandle cameraColor;
            RTHandle temporaryColor;
            RTHandle accumulatedIndirect;

            public DDGICompositePass(Material material)
            {
                compositeMaterial = material;
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public void SetCameraColor(RTHandle target)
            {
                cameraColor = target;
            }

            public override void OnCameraSetup(
                CommandBuffer commandBuffer,
                ref RenderingData renderingData)
            {
                RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
                descriptor.depthBufferBits = 0;
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref temporaryColor,
                    descriptor,
                    FilterMode.Bilinear,
                    TextureWrapMode.Clamp,
                    name: "_DDGICompositeColor");
                descriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                descriptor.msaaSamples = 1;
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref accumulatedIndirect, descriptor, FilterMode.Bilinear,
                    TextureWrapMode.Clamp, name: "_DDGIAccumulatedIndirect");
            }

            public override void Execute(
                ScriptableRenderContext context,
                ref RenderingData renderingData)
            {
                DDGIProbeVolumeRegistry.GetActiveVolumesByDensity(volumes);
                RenderedVolumeCount = 0;
                if (compositeMaterial == null)
                {
                    SetRuntimeStatus("Skipped because the composite material is unavailable.");
                    return;
                }
                if (cameraColor == null)
                {
                    SetRuntimeStatus("Skipped because the camera color target is unavailable.");
                    return;
                }
                if (volumes.Count == 0)
                {
                    SetRuntimeStatus("Skipped because no active DDGI Probe Volume is registered.");
                    return;
                }

                CommandBuffer commandBuffer = CommandBufferPool.Get();
                string updateIssue = null;
                try
                {
                    using (new ProfilingScope(commandBuffer, ProfilingSampler))
                    {
                        bool canUpdateVolume = renderingData.cameraData.renderType ==
                            CameraRenderType.Base &&
                            (renderingData.cameraData.cameraType == CameraType.Game ||
                             renderingData.cameraData.cameraType == CameraType.SceneView);
                        int mainLightIndex = renderingData.lightData.mainLightIndex;
                        Light mainLight = mainLightIndex >= 0
                            ? renderingData.lightData.visibleLights[mainLightIndex].light
                            : null;
                        bool hasShadowedMainLight = renderingData.shadowData.supportsMainLightShadows &&
                            mainLight != null && mainLight.type == LightType.Directional &&
                            mainLight.shadows != LightShadows.None && mainLight.shadowStrength > 0.0f;
                        int cascadeCount = hasShadowedMainLight
                            ? renderingData.shadowData.mainLightShadowCascadesCount : 0;
                        bool missingMainLightShadows = cascadeCount == 0 &&
                            ((mainLight != null && mainLight.type == LightType.Directional) ||
                             (mainLight == null && HasEnabledDirectionalLight()));

                        readyVolumes.Clear();
                        foreach (DDGIProbeVolume volume in volumes)
                        {
                            if (canUpdateVolume)
                                updateIssue = TryUpdateVolume(commandBuffer, volume,
                                    renderingData.cameraData.cameraType, mainLight, cascadeCount,
                                    missingMainLightShadows) ?? updateIssue;
                            if (volume.HasBlendedProbeData)
                                readyVolumes.Add(volume);
                        }

                        if (readyVolumes.Count > 0)
                        {
                            int debugView = (int)readyVolumes[0].CompositeDebugView;
                            CoreUtils.SetRenderTarget(commandBuffer, accumulatedIndirect, ClearFlag.Color, Color.clear);
                            foreach (DDGIProbeVolume volume in readyVolumes)
                            {
                                volume.BindShaderGlobals(commandBuffer);
                                // All volumes use the densest ready volume's debug mode.
                                commandBuffer.SetGlobalInt(DebugViewId, debugView);
                                Blitter.BlitCameraTexture(commandBuffer, cameraColor, accumulatedIndirect,
                                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                                    compositeMaterial, 0);
                            }
                            commandBuffer.SetGlobalTexture(AccumulatedIndirectId, accumulatedIndirect.nameID);
                            Blitter.BlitCameraTexture(commandBuffer, cameraColor, temporaryColor, compositeMaterial, 1);
                            Blitter.BlitCameraTexture(commandBuffer, temporaryColor, cameraColor);
                            RenderedVolumeCount = readyVolumes.Count;
                        }
                    }

                    context.ExecuteCommandBuffer(commandBuffer);
                }
                finally
                {
                    CommandBufferPool.Release(commandBuffer);
                }
                LastRenderedCamera = renderingData.cameraData.camera.name;
                SetRuntimeStatus(RenderedVolumeCount > 0
                    ? $"Rendered {RenderedVolumeCount} DDGI volumes." +
                        (updateIssue == null ? "" : $" Update skipped: {updateIssue}")
                    : updateIssue ?? "No blended probe data. Enable automatic capture or capture manually in the editor.");
            }

            static string TryUpdateVolume(CommandBuffer commandBuffer, DDGIProbeVolume volume,
                CameraType cameraType, Light mainLight, int cascadeCount, bool missingMainLightShadows)
            {
                bool automatic = volume.CaptureVolumeEveryFrame &&
                    (Application.isPlaying ? cameraType == CameraType.Game
                        : volume.CaptureVolumeInEditMode && cameraType == CameraType.SceneView);
                bool initial = Application.isPlaying && cameraType == CameraType.Game && !volume.HasBlendedProbeData;
                if (!automatic && !initial && !volume.HasPendingShadowedEvaluation)
                    return null;
                if (missingMainLightShadows)
                    return $"{volume.name}: main light shadow map is unavailable.";
                if (!volume.HasCaptureShaders || !volume.HasRadianceShader || !volume.HasProbeBlendShader)
                    return $"{volume.name}: capture, radiance or ProbeBlend shader is missing.";
                if (automatic || initial)
                {
                    if (!SystemInfo.supportsRayTracingShaders)
                        return $"{volume.name}: ray tracing shaders are unsupported.";
                    volume.RecordRealtimeCaptureAndBlend(commandBuffer, mainLight, cascadeCount);
                }
                else
                    volume.RecordPendingShadowedRadianceAndBlend(commandBuffer, mainLight, cascadeCount);
                return null;
            }

            public override void OnCameraCleanup(CommandBuffer commandBuffer)
            {
                cameraColor = null;
            }

            public void Dispose()
            {
                temporaryColor?.Release();
                temporaryColor = null;
                accumulatedIndirect?.Release();
                accumulatedIndirect = null;
                cameraColor = null;
            }
        }

        const string CompositeShaderName = "DouDDGI/Composite";

        static bool HasEnabledDirectionalLight()
        {
            Light[] lights = Object.FindObjectsByType<Light>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            foreach (Light light in lights)
            {
                if (light != null && light.enabled && light.intensity > 0.0f &&
                    light.type == LightType.Directional)
                    return true;
            }

            return false;
        }

        Material compositeMaterial;
        DDGICompositePass compositePass;

        public override void Create()
        {
            Shader shader = Shader.Find(CompositeShaderName);
            if (shader != null)
            {
                compositeMaterial = CoreUtils.CreateEngineMaterial(shader);
                compositePass = new DDGICompositePass(compositeMaterial);
                SetRuntimeStatus("Feature created and waiting for a camera.");
            }
            else
            {
                SetRuntimeStatus($"Shader '{CompositeShaderName}' was not found.");
            }
        }

        public override void SetupRenderPasses(
            ScriptableRenderer renderer,
            in RenderingData renderingData)
        {
            compositePass?.SetCameraColor(renderer.cameraColorTargetHandle);
        }

        public override void AddRenderPasses(
            ScriptableRenderer renderer,
            ref RenderingData renderingData)
        {
            CameraData cameraData = renderingData.cameraData;
            if (compositePass == null ||
                cameraData.camera == null ||
                cameraData.isPreviewCamera ||
                cameraData.cameraType == CameraType.Reflection)
            {
                return;
            }

            renderer.EnqueuePass(compositePass);
        }

        protected override void Dispose(bool disposing)
        {
            compositePass?.Dispose();
            compositePass = null;
            CoreUtils.Destroy(compositeMaterial);
            compositeMaterial = null;
        }
    }
}
