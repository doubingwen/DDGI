using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Scripting.APIUpdating;

namespace Dou.GI
{
    [MovedFrom(true, null, null, "PRTRelight")]
    public sealed class RadianceFieldUpdateFeature : ScriptableRendererFeature
    {
        sealed class UpdateRadianceFieldPass : ScriptableRenderPass
        {
            static readonly ProfilingSampler ProfilingSampler = new ProfilingSampler("Dou GI: Update Radiance Field");

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                IReadOnlyList<RadianceFieldVolume> volumes = RadianceFieldRegistry.Volumes;
                if (volumes.Count == 0)
                    return;

                CommandBuffer commandBuffer = CommandBufferPool.Get();
                using (new ProfilingScope(commandBuffer, ProfilingSampler))
                {
                    for (int volumeIndex = 0; volumeIndex < volumes.Count; volumeIndex++)
                    {
                        RadianceFieldVolume volume = volumes[volumeIndex];
                        if (volume == null || !volume.isActiveAndEnabled || !volume.HasCoefficientHistory)
                            continue;

                        volume.BeginLightingFrame(commandBuffer);
                        foreach (RadianceProbe probe in volume.Probes)
                            //computer shader计算
                            probe.RecordRadianceUpdate(commandBuffer, volume);
                    }
                    //将computer shader计算的局部结果传给全局的shader用
                    RadianceFieldRegistry.PrimaryVolume?.BindGlobalShaderState(commandBuffer);
                }

                context.ExecuteCommandBuffer(commandBuffer);
                CommandBufferPool.Release(commandBuffer);
            }
        }

        UpdateRadianceFieldPass updatePass;

        public override void Create()
        {
            updatePass = new UpdateRadianceFieldPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            if (camera == null || camera.GetComponent<RadianceCaptureCamera>() != null)
                return;

            renderer.EnqueuePass(updatePass);
        }
    }
}
