using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Dou.DDGI
{
    public sealed class DDGIProbeBlender
    {
        const string IrradianceKernelName = "BlendIrradiance";
        const string DistanceKernelName = "BlendDistanceMoments";

        static readonly int PositionTextureId = Shader.PropertyToID("_DDGI_RayPositionTexture");
        static readonly int RadianceTextureId = Shader.PropertyToID("_DDGI_RayRadianceTexture");
        static readonly int IrradianceAtlasId = Shader.PropertyToID("_DDGI_IrradianceAtlas");
        static readonly int DistanceMomentsAtlasId = Shader.PropertyToID("_DDGI_DistanceMomentsAtlas");
        static readonly int ProbePositionsId = Shader.PropertyToID("_DDGI_ProbePositions");
        static readonly int ProbeCountId = Shader.PropertyToID("_DDGI_ProbeCount");
        static readonly int RayCountId = Shader.PropertyToID("_DDGI_RayCount");
        static readonly int AtlasTilesPerRowId = Shader.PropertyToID("_DDGI_AtlasTilesPerRow");
        static readonly int MaximumRayDistanceId = Shader.PropertyToID("_DDGI_MaxRayDistance");
        static readonly int DistanceSharpnessId = Shader.PropertyToID("_DDGI_DistanceSharpness");
        static readonly int RayRotationId = Shader.PropertyToID("_DDGI_RayRotation");
        static readonly int PreviousIrradianceAtlasId = Shader.PropertyToID("_DDGI_PreviousIrradianceAtlas");
        static readonly int PreviousDistanceMomentsAtlasId = Shader.PropertyToID("_DDGI_PreviousDistanceMomentsAtlas");
        static readonly int HasBlendHistoryId = Shader.PropertyToID("_DDGI_HasBlendHistory");
        static readonly int IrradianceHysteresisId = Shader.PropertyToID("_DDGI_IrradianceHysteresis");
        static readonly int DistanceHysteresisId = Shader.PropertyToID("_DDGI_DistanceHysteresis");
        static readonly int TemporalGammaId = Shader.PropertyToID("_DDGI_TemporalGamma");

        readonly ComputeShader computeShader;
        readonly int irradianceKernelIndex;
        readonly int distanceKernelIndex;

        public DDGIProbeBlender(ComputeShader computeShader)
        {
            this.computeShader = computeShader != null
                ? computeShader
                : throw new ArgumentNullException(nameof(computeShader));
            irradianceKernelIndex = computeShader.FindKernel(IrradianceKernelName);
            distanceKernelIndex = computeShader.FindKernel(DistanceKernelName);
        }

        public void RecordBlend(
            CommandBuffer commandBuffer,
            DDGIRayGBuffer gBuffer,
            RenderTexture radianceTexture,
            ComputeBuffer probePositions,
            RenderTexture irradianceAtlas,
            RenderTexture distanceMomentsAtlas,
            int atlasTilesPerRow,
            float maximumRayDistance,
            float distanceSharpness,
            Matrix4x4 rayRotation,
            RenderTexture previousIrradianceAtlas,
            RenderTexture previousDistanceMomentsAtlas,
            bool hasHistory,
            float irradianceHysteresis,
            float distanceHysteresis,
            float temporalGamma)
        {
            ValidateArguments(
                commandBuffer,
                gBuffer,
                radianceTexture,
                probePositions,
                irradianceAtlas,
                distanceMomentsAtlas,
                atlasTilesPerRow);

            SetCommonParameters(
                commandBuffer,
                gBuffer,
                radianceTexture,
                probePositions,
                atlasTilesPerRow,
                maximumRayDistance,
                distanceSharpness,
                rayRotation);

            if (previousIrradianceAtlas == null || !previousIrradianceAtlas.IsCreated() ||
                previousDistanceMomentsAtlas == null || !previousDistanceMomentsAtlas.IsCreated())
                throw new InvalidOperationException("Create the history atlases before ProbeBlend.");

            commandBuffer.SetComputeTextureParam(computeShader, irradianceKernelIndex,
                PreviousIrradianceAtlasId, previousIrradianceAtlas);
            commandBuffer.SetComputeTextureParam(computeShader, distanceKernelIndex,
                PreviousDistanceMomentsAtlasId, previousDistanceMomentsAtlas);
            commandBuffer.SetComputeIntParam(computeShader, HasBlendHistoryId, hasHistory ? 1 : 0);
            commandBuffer.SetComputeFloatParam(computeShader, IrradianceHysteresisId,
                Mathf.Clamp(irradianceHysteresis, 0.0f, 0.999f));
            commandBuffer.SetComputeFloatParam(computeShader, DistanceHysteresisId,
                Mathf.Clamp(distanceHysteresis, 0.0f, 0.999f));
            commandBuffer.SetComputeFloatParam(computeShader, TemporalGammaId, Mathf.Max(1.0f, temporalGamma));

            int atlasTileRows = DivideRoundUp(gBuffer.ProbeCount, atlasTilesPerRow);

            commandBuffer.SetComputeTextureParam(
                computeShader,
                irradianceKernelIndex,
                IrradianceAtlasId,
                irradianceAtlas);
            commandBuffer.DispatchCompute(
                computeShader,
                irradianceKernelIndex,
                atlasTilesPerRow,
                atlasTileRows,
                1);

            commandBuffer.SetComputeTextureParam(
                computeShader,
                distanceKernelIndex,
                DistanceMomentsAtlasId,
                distanceMomentsAtlas);
            commandBuffer.DispatchCompute(
                computeShader,
                distanceKernelIndex,
                atlasTilesPerRow,
                atlasTileRows,
                1);
        }

        void SetCommonParameters(
            CommandBuffer commandBuffer,
            DDGIRayGBuffer gBuffer,
            RenderTexture radianceTexture,
            ComputeBuffer probePositions,
            int atlasTilesPerRow,
            float maximumRayDistance,
            float distanceSharpness,
            Matrix4x4 rayRotation)
        {
            commandBuffer.SetComputeTextureParam(
                computeShader,
                irradianceKernelIndex,
                RadianceTextureId,
                radianceTexture);
            commandBuffer.SetComputeTextureParam(
                computeShader,
                distanceKernelIndex,
                PositionTextureId,
                gBuffer.PositionTexture);
            commandBuffer.SetComputeBufferParam(
                computeShader,
                distanceKernelIndex,
                ProbePositionsId,
                probePositions);

            commandBuffer.SetComputeIntParam(computeShader, ProbeCountId, gBuffer.ProbeCount);
            commandBuffer.SetComputeIntParam(computeShader, RayCountId, gBuffer.RayCount);
            commandBuffer.SetComputeIntParam(computeShader, AtlasTilesPerRowId, atlasTilesPerRow);
            commandBuffer.SetComputeFloatParam(computeShader, MaximumRayDistanceId, maximumRayDistance);
            commandBuffer.SetComputeFloatParam(
                computeShader,
                DistanceSharpnessId,
                Mathf.Max(1.0f, distanceSharpness));
            commandBuffer.SetComputeMatrixParam(computeShader, RayRotationId, rayRotation);
        }

        static void ValidateArguments(
            CommandBuffer commandBuffer,
            DDGIRayGBuffer gBuffer,
            RenderTexture radianceTexture,
            ComputeBuffer probePositions,
            RenderTexture irradianceAtlas,
            RenderTexture distanceMomentsAtlas,
            int atlasTilesPerRow)
        {
            if (commandBuffer == null)
                throw new ArgumentNullException(nameof(commandBuffer));
            if (gBuffer == null || !gBuffer.IsCreated)
                throw new InvalidOperationException("Create the ray G-Buffer before ProbeBlend.");
            if (radianceTexture == null || !radianceTexture.IsCreated())
                throw new InvalidOperationException("Evaluate ray radiance before ProbeBlend.");
            if (probePositions == null || !probePositions.IsValid())
                throw new InvalidOperationException("Create the probe position buffer before ProbeBlend.");
            if (irradianceAtlas == null || !irradianceAtlas.IsCreated())
                throw new InvalidOperationException("Create the irradiance atlas before ProbeBlend.");
            if (distanceMomentsAtlas == null || !distanceMomentsAtlas.IsCreated())
                throw new InvalidOperationException("Create the distance moments atlas before ProbeBlend.");
            if (atlasTilesPerRow <= 0)
                throw new ArgumentOutOfRangeException(nameof(atlasTilesPerRow));
        }

        static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }
    }
}
