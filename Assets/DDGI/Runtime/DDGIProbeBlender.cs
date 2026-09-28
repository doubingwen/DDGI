using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Dou.DDGI
{
    // GPU owns classifications; CPU only supplies geometry bounds and asynchronous diagnostics.
    internal sealed class DDGIProbeStateScheduler : IDisposable
    {
        struct GeometryBounds
        {
            public Vector4 minimum;
            public Vector4 maximum;
            public GeometryBounds(Bounds bounds, bool dynamic)
            {
                Vector3 min = bounds.min;
                Vector3 max = bounds.max;
                minimum = new Vector4(min.x, min.y, min.z, dynamic ? 1.0f : 0.0f);
                maximum = new Vector4(max.x, max.y, max.z, 0.0f);
            }
        }

        struct GeometrySnapshot
        {
            public Bounds bounds;
            public Matrix4x4 matrix;
            public Mesh mesh;
            public int firstSubMesh;
            public bool dynamic;
        }

        readonly ComputeShader shader;
        readonly int prepareKernel;
        readonly int classifyKernel;
        readonly Dictionary<int, GeometrySnapshot> previousGeometry = new Dictionary<int, GeometrySnapshot>();
        readonly Dictionary<int, GeometrySnapshot> currentGeometry = new Dictionary<int, GeometrySnapshot>();
        readonly List<GeometryBounds> geometry = new List<GeometryBounds>();
        readonly List<GeometryBounds> changedGeometry = new List<GeometryBounds>();
        ComputeBuffer geometryBuffer;
        ComputeBuffer changedGeometryBuffer;
        uint updateSequence;
        bool readbackPending;
        double nextReadbackTime;
        bool resetRequested = true;
        bool previousClassificationEnabled;
        float previousInfluenceRadius;
        float previousBackfaceThreshold;
        public ComputeBuffer States { get; private set; }
        public int[] StateCounts { get; } = new int[7];
        public int UpdatedProbeCount { get; private set; }
        public bool HasStatistics { get; private set; }

        public DDGIProbeStateScheduler(ComputeShader shader, int probeCount)
        {
            this.shader = shader;
            prepareKernel = shader.FindKernel("PrepareProbeStates");
            classifyKernel = shader.FindKernel("ClassifyProbes");
            States = new ComputeBuffer(probeCount, sizeof(uint) * 2, ComputeBufferType.Structured)
                { name = "DDGI Probe States (state, flags)" };
            var initial = new Vector2Int[probeCount];
            for (int i = 0; i < initial.Length; ++i)
                initial[i] = new Vector2Int((int)DDGIProbeState.Uninitialized, 0);
            States.SetData(initial);
        }

        public void RequestReset() => resetRequested = true;

        public void RecordPrepare(CommandBuffer commands, ComputeBuffer positions, DDGIRayGBuffer gBuffer,
            LayerMask layers, float radius, float maxDistance, bool enabled, float backfaceThreshold,
            int recheckInterval)
        {
            if (previousClassificationEnabled != enabled || previousInfluenceRadius != radius ||
                previousBackfaceThreshold != backfaceThreshold)
                resetRequested = true;
            previousClassificationEnabled = enabled;
            previousInfluenceRadius = radius;
            previousBackfaceThreshold = backfaceThreshold;
            CollectGeometry(layers);
            EnsureBuffer(ref geometryBuffer, geometry.Count);
            EnsureBuffer(ref changedGeometryBuffer, changedGeometry.Count);
            if (geometry.Count > 0) commands.SetBufferData(geometryBuffer, geometry.ToArray());
            if (changedGeometry.Count > 0) commands.SetBufferData(changedGeometryBuffer, changedGeometry.ToArray());
            commands.SetComputeBufferParam(shader, prepareKernel, "_DDGI_ProbeStates", States);
            commands.SetComputeBufferParam(shader, prepareKernel, "_DDGI_ProbePositions", positions);
            commands.SetComputeBufferParam(shader, prepareKernel, "_DDGI_GeometryBounds", geometryBuffer);
            commands.SetComputeBufferParam(shader, prepareKernel, "_DDGI_ChangedGeometryBounds", changedGeometryBuffer);
            commands.SetComputeIntParam(shader, "_DDGI_ProbeCount", States.count);
            commands.SetComputeIntParam(shader, "_DDGI_GeometryCount", geometry.Count);
            commands.SetComputeIntParam(shader, "_DDGI_ChangedGeometryCount", changedGeometry.Count);
            commands.SetComputeIntParam(shader, "_DDGI_StateUpdateSequence", unchecked((int)updateSequence++));
            commands.SetComputeIntParam(shader, "_DDGI_StateRecheckInterval", Mathf.Max(1, recheckInterval));
            commands.SetComputeIntParam(shader, "_DDGI_EnableProbeClassification", enabled ? 1 : 0);
            commands.SetComputeIntParam(shader, "_DDGI_ResetProbeClassification", resetRequested ? 1 : 0);
            commands.SetComputeFloatParam(shader, "_DDGI_ProbeInfluenceRadius", radius);
            commands.SetComputeFloatParam(shader, "_DDGI_InteriorBackfaceThreshold", Mathf.Clamp(backfaceThreshold, 0.9f, 1.0f));
            commands.SetComputeFloatParam(shader, "_DDGI_MaxRayDistance", maxDistance);
            commands.SetComputeIntParam(shader, "_DDGI_RayCount", gBuffer.RayCount);
            commands.DispatchCompute(shader, prepareKernel, (States.count + 63) / 64, 1, 1);
            resetRequested = false;
        }

        public void RecordClassify(CommandBuffer commands, ComputeBuffer positions, DDGIRayGBuffer gBuffer)
        {
            commands.SetComputeBufferParam(shader, classifyKernel, "_DDGI_ProbeStates", States);
            commands.SetComputeBufferParam(shader, classifyKernel, "_DDGI_ProbePositions", positions);
            commands.SetComputeTextureParam(shader, classifyKernel, "_DDGI_RayPositionTexture", gBuffer.PositionTexture);
            commands.SetComputeTextureParam(shader, classifyKernel, "_DDGI_RayNormalTexture", gBuffer.NormalTexture);
            commands.DispatchCompute(shader, classifyKernel, (States.count + 63) / 64, 1, 1);
        }

        public void RecordStatistics(CommandBuffer commands, Action<Vector2Int[]> onReadback)
        {
            if (readbackPending || Time.realtimeSinceStartupAsDouble < nextReadbackTime ||
                !SystemInfo.supportsAsyncGPUReadback) return;
            readbackPending = true;
            nextReadbackTime = Time.realtimeSinceStartupAsDouble + 0.5;
            ComputeBuffer requestedBuffer = States;
            commands.RequestAsyncReadback(requestedBuffer, request =>
            {
                readbackPending = false;
                if (States != requestedBuffer || request.hasError) return;
                var data = request.GetData<Vector2Int>().ToArray();
                Array.Clear(StateCounts, 0, StateCounts.Length);
                UpdatedProbeCount = 0;
                foreach (Vector2Int value in data)
                {
                    if ((uint)value.x < StateCounts.Length) StateCounts[value.x]++;
                    if ((value.y & 1) != 0) UpdatedProbeCount++;
                }
                HasStatistics = true;
                onReadback(data);
            });
        }

        void CollectGeometry(LayerMask layers)
        {
            geometry.Clear();
            changedGeometry.Clear();
            currentGeometry.Clear();
            foreach (MeshRenderer renderer in UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!DDGIRayTracingScene.IsCaptureGeometry(renderer, layers)) continue;
                int id = renderer.GetInstanceID();
                var snapshot = new GeometrySnapshot
                {
                    bounds = renderer.bounds,
                    matrix = renderer.localToWorldMatrix,
                    mesh = renderer.GetComponent<MeshFilter>().sharedMesh,
                    firstSubMesh = renderer.subMeshStartIndex,
                    dynamic = !renderer.gameObject.isStatic
                };
                currentGeometry.Add(id, snapshot);
                geometry.Add(new GeometryBounds(snapshot.bounds, snapshot.dynamic));
                bool existed = previousGeometry.TryGetValue(id, out GeometrySnapshot old);
                if (!existed || old.bounds != snapshot.bounds || old.matrix != snapshot.matrix ||
                    old.mesh != snapshot.mesh || old.firstSubMesh != snapshot.firstSubMesh ||
                    old.dynamic != snapshot.dynamic)
                {
                    if (existed) changedGeometry.Add(new GeometryBounds(old.bounds, old.dynamic));
                    changedGeometry.Add(new GeometryBounds(snapshot.bounds, snapshot.dynamic));
                }
            }
            foreach (var entry in previousGeometry)
                if (!currentGeometry.ContainsKey(entry.Key))
                    changedGeometry.Add(new GeometryBounds(entry.Value.bounds, entry.Value.dynamic));
            previousGeometry.Clear();
            foreach (var entry in currentGeometry) previousGeometry.Add(entry.Key, entry.Value);
        }

        static void EnsureBuffer(ref ComputeBuffer buffer, int count)
        {
            if (buffer != null && buffer.count >= Mathf.Max(1, count)) return;
            buffer?.Release();
            buffer = new ComputeBuffer(Mathf.NextPowerOfTwo(Mathf.Max(1, count)), sizeof(float) * 8);
        }

        public void Dispose()
        {
            States?.Release();
            States = null;
            geometryBuffer?.Release();
            changedGeometryBuffer?.Release();
        }
    }

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
            float temporalGamma,
            ComputeBuffer probeStates)
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
            commandBuffer.SetComputeBufferParam(computeShader, irradianceKernelIndex,
                "_DDGI_ProbeStates", probeStates);
            commandBuffer.SetComputeBufferParam(computeShader, distanceKernelIndex,
                "_DDGI_ProbeStates", probeStates);

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
