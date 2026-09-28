using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Dou.DDGI
{
    public sealed class DDGICompositeFeature : ScriptableRendererFeature
    {
        public static string RuntimeStatus { get; private set; } = "Feature has not been created.";
        public static string LastRenderedCamera { get; private set; }

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
            RTHandle cameraColor;
            RTHandle temporaryColor;

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
            }

            public override void Execute(
                ScriptableRenderContext context,
                ref RenderingData renderingData)
            {
                DDGIProbeVolume volume = DDGIProbeVolumeRegistry.PrimaryVolume;
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
                if (volume == null)
                {
                    SetRuntimeStatus("Skipped because no active DDGI Probe Volume is registered.");
                    return;
                }

                CommandBuffer commandBuffer = CommandBufferPool.Get();
                try
                {
                    using (new ProfilingScope(commandBuffer, ProfilingSampler))
                    {
                        bool canUpdateVolume = renderingData.cameraData.renderType ==
                            CameraRenderType.Base &&
                            (renderingData.cameraData.cameraType == CameraType.Game ||
                             renderingData.cameraData.cameraType == CameraType.SceneView);
                        bool captureAutomatically = volume.CaptureVolumeEveryFrame &&
                            (Application.isPlaying
                                ? renderingData.cameraData.cameraType == CameraType.Game
                                : volume.CaptureVolumeInEditMode &&
                                  renderingData.cameraData.cameraType == CameraType.SceneView);
                        bool needsInitialCapture = Application.isPlaying &&
                            renderingData.cameraData.cameraType == CameraType.Game &&
                            !volume.HasBlendedProbeData;
                        if (canUpdateVolume &&
                            (volume.HasPendingShadowedEvaluation || captureAutomatically || needsInitialCapture))
                        {
                            int mainLightIndex = renderingData.lightData.mainLightIndex;
                            Light mainLight = mainLightIndex >= 0
                                ? renderingData.lightData.visibleLights[mainLightIndex].light
                                : null;
                            bool hasShadowedMainLight = renderingData.shadowData.supportsMainLightShadows &&
                                               mainLight != null &&
                                               mainLight.type == LightType.Directional &&
                                               mainLight.shadows != LightShadows.None &&
                                               mainLight.shadowStrength > 0.0f;
                            int cascadeCount = hasShadowedMainLight
                                ? renderingData.shadowData.mainLightShadowCascadesCount
                                : 0;
                            if (cascadeCount == 0 &&
                                ((mainLight != null && mainLight.type == LightType.Directional) ||
                                 (mainLight == null && HasEnabledDirectionalLight())))
                            {
                                SetRuntimeStatus("Skipped DDGI update: enable main light shadows and ensure a camera shadow map is available.");
                                return;
                            }

                            if (captureAutomatically || needsInitialCapture)
                            {
                                if (!SystemInfo.supportsRayTracingShaders)
                                {
                                    SetRuntimeStatus("Skipped DDGI capture: ray tracing shaders are unsupported. Use a DX12 ray tracing device.");
                                    return;
                                }
                                if (!volume.HasCaptureShaders || !volume.HasRadianceShader ||
                                    !volume.HasProbeBlendShader)
                                {
                                    SetRuntimeStatus("Skipped DDGI capture: assign the ray tracing, surface, radiance and ProbeBlend shaders.");
                                    return;
                                }
                                volume.RecordRealtimeCaptureAndBlend(
                                    commandBuffer,
                                    mainLight,
                                    cascadeCount);
                            }
                            else
                            {
                                volume.RecordPendingShadowedRadianceAndBlend(
                                    commandBuffer,
                                    mainLight,
                                    cascadeCount);
                            }
                        }

                        if (!volume.HasBlendedProbeData)
                        {
                            SetRuntimeStatus(volume.CaptureVolumeEveryFrame
                                ? "Skipped because the active volume has no blended probe data."
                                : "No blended probe data. Automatic capture is off; Play initializes on the first game camera, or capture manually in the editor.");
                            return;
                        }

                        volume.BindShaderGlobals(commandBuffer);
                        Blitter.BlitCameraTexture(
                            commandBuffer,
                            cameraColor,
                            temporaryColor,
                            compositeMaterial,
                            0);
                        Blitter.BlitCameraTexture(commandBuffer, temporaryColor, cameraColor);
                    }

                    context.ExecuteCommandBuffer(commandBuffer);
                }
                finally
                {
                    CommandBufferPool.Release(commandBuffer);
                }
                LastRenderedCamera = renderingData.cameraData.camera.name;
                SetRuntimeStatus("Rendered DDGI indirect diffuse.");
            }

            public override void OnCameraCleanup(CommandBuffer commandBuffer)
            {
                cameraColor = null;
            }

            public void Dispose()
            {
                temporaryColor?.Release();
                temporaryColor = null;
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
