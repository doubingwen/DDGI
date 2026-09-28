using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Dou.DDGI
{
    public sealed class DDGIRayTracingDispatcher
    {
        const string ShaderPassName = "DDGIRayTracing";
        const string RayGenerationName = "DDGIRayGeneration";

        static readonly int AccelerationStructureId = Shader.PropertyToID("_DDGI_AccelerationStructure");
        static readonly int ProbePositionsId = Shader.PropertyToID("_DDGI_ProbePositions");
        static readonly int RayCountId = Shader.PropertyToID("_DDGI_RayCount");
        static readonly int MinimumRayDistanceId = Shader.PropertyToID("_DDGI_MinRayDistance");
        static readonly int MaximumRayDistanceId = Shader.PropertyToID("_DDGI_MaxRayDistance");
        static readonly int RayRotationId = Shader.PropertyToID("_DDGI_RayRotation");

        readonly RayTracingShader rayTracingShader;

        public DDGIRayTracingDispatcher(RayTracingShader rayTracingShader)
        {
            this.rayTracingShader = rayTracingShader != null
                ? rayTracingShader
                : throw new ArgumentNullException(nameof(rayTracingShader));
        }

        public void RecordTrace(
            CommandBuffer commandBuffer,
            RayTracingAccelerationStructure accelerationStructure,
            ComputeBuffer probePositions,
            DDGIRayGBuffer target,
            float minimumRayDistance,
            float maximumRayDistance,
            Matrix4x4 rayRotation)
        {
            ValidateArguments(
                commandBuffer,
                accelerationStructure,
                probePositions,
                target,
                minimumRayDistance,
                maximumRayDistance);

            commandBuffer.SetRayTracingShaderPass(rayTracingShader, ShaderPassName);
            commandBuffer.SetRayTracingAccelerationStructure(
                rayTracingShader,
                AccelerationStructureId,
                accelerationStructure);
            commandBuffer.SetRayTracingBufferParam(
                rayTracingShader,
                ProbePositionsId,
                probePositions);
            commandBuffer.SetRayTracingTextureParam(
                rayTracingShader,
                DDGIRayGBuffer.PositionTextureId,
                target.PositionTexture);
            commandBuffer.SetRayTracingTextureParam(
                rayTracingShader,
                DDGIRayGBuffer.NormalTextureId,
                target.NormalTexture);
            commandBuffer.SetRayTracingTextureParam(
                rayTracingShader,
                DDGIRayGBuffer.AlbedoTextureId,
                target.AlbedoTexture);
            commandBuffer.SetRayTracingTextureParam(
                rayTracingShader,
                DDGIRayGBuffer.DistanceMomentsTextureId,
                target.DistanceMomentsTexture);
            commandBuffer.SetRayTracingIntParam(rayTracingShader, RayCountId, target.RayCount);
            commandBuffer.SetRayTracingFloatParam(
                rayTracingShader,
                MinimumRayDistanceId,
                minimumRayDistance);
            commandBuffer.SetRayTracingFloatParam(
                rayTracingShader,
                MaximumRayDistanceId,
                maximumRayDistance);
            commandBuffer.SetRayTracingMatrixParam(rayTracingShader, RayRotationId, rayRotation);
            commandBuffer.DispatchRays(
                rayTracingShader,
                RayGenerationName,
                (uint)target.RayCount,
                (uint)target.ProbeCount,
                1);
        }

        static void ValidateArguments(
            CommandBuffer commandBuffer,
            RayTracingAccelerationStructure accelerationStructure,
            ComputeBuffer probePositions,
            DDGIRayGBuffer target,
            float minimumRayDistance,
            float maximumRayDistance)
        {
            if (!SystemInfo.supportsRayTracingShaders)
                throw new NotSupportedException("Ray tracing shaders require a compatible GPU and graphics API.");
            if (commandBuffer == null)
                throw new ArgumentNullException(nameof(commandBuffer));
            if (accelerationStructure == null)
                throw new ArgumentNullException(nameof(accelerationStructure));
            if (probePositions == null)
                throw new ArgumentNullException(nameof(probePositions));
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (!target.IsCreated)
                throw new InvalidOperationException("Create the DDGI ray G-Buffer before tracing rays.");
            if (probePositions.count < target.ProbeCount)
                throw new ArgumentException("The probe position buffer is smaller than the G-Buffer probe count.", nameof(probePositions));
            if (minimumRayDistance < 0.0f)
                throw new ArgumentOutOfRangeException(nameof(minimumRayDistance));
            if (maximumRayDistance <= minimumRayDistance)
                throw new ArgumentOutOfRangeException(nameof(maximumRayDistance));
        }
    }
}
