using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace Dou.DDGI
{
    public enum DDGICompositeDebugView
    {
        Composite = 0,
        IndirectOnly = 1,
        IrradianceOnly = 2,
        PassCheck = 3
    }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Dou DDGI/DDGI Probe Volume")]
    public sealed class DDGIProbeVolume : MonoBehaviour
    {
        static readonly int IrradianceAtlasId = Shader.PropertyToID("_DDGI_IrradianceAtlas");
        static readonly int DistanceMomentsAtlasId = Shader.PropertyToID("_DDGI_DistanceMomentsAtlas");
        static readonly int ProbeCountsId = Shader.PropertyToID("_DDGI_ProbeCounts");
        static readonly int ProbeSpacingId = Shader.PropertyToID("_DDGI_ProbeSpacing");
        static readonly int VolumeToWorldId = Shader.PropertyToID("_DDGI_VolumeToWorld");
        static readonly int WorldToVolumeId = Shader.PropertyToID("_DDGI_WorldToVolume");
        static readonly int AtlasTileCountsId = Shader.PropertyToID("_DDGI_AtlasTileCounts");
        static readonly int IrradianceAtlasResolutionId = Shader.PropertyToID("_DDGI_IrradianceAtlasResolution");
        static readonly int DistanceAtlasResolutionId = Shader.PropertyToID("_DDGI_DistanceAtlasResolution");
        static readonly int RayRadianceTextureId = Shader.PropertyToID("_DDGI_RayRadianceTexture");
        static readonly int MinimumLocalProbePositionId =
            Shader.PropertyToID("_DDGI_MinimumLocalProbePosition");
        static readonly int SurfaceNormalBiasId = Shader.PropertyToID("_DDGI_SurfaceNormalBias");
        static readonly int SurfaceViewBiasId = Shader.PropertyToID("_DDGI_SurfaceViewBias");
        static readonly int MinimumProbeWeightId = Shader.PropertyToID("_DDGI_MinimumProbeWeight");
        static readonly int MinimumDistanceVarianceId =
            Shader.PropertyToID("_DDGI_MinimumDistanceVariance");
        static readonly int IndirectDiffuseIntensityId =
            Shader.PropertyToID("_DDGI_IndirectDiffuseIntensity");
        static readonly int CompositeDebugViewId = Shader.PropertyToID("_DDGI_CompositeDebugView");

        [Header("Probe Grid")]
        [SerializeField] Vector3Int probeCounts = new Vector3Int(8, 8, 8);
        [SerializeField] Vector3 probeSpacing = new Vector3(2.0f, 2.0f, 2.0f);
        [SerializeField, Min(1)] int raysPerProbe = DDGIRayGBuffer.DefaultRaysPerProbe;

        [Header("Atlas Layout")]
        [Tooltip("Zero selects a near-square atlas automatically.")]
        [SerializeField, Min(0)] int atlasTilesPerRow;

        [Header("Debug")]
        [SerializeField] bool drawVolumeBounds = true;

        [Header("Ray Tracing Capture")]
        [SerializeField] RayTracingShader rayTracingShader;
        [SerializeField] Shader rayTracingSurfaceShader;
        [SerializeField] LayerMask geometryLayers = ~0;
        [SerializeField, Min(0.0f)] float minimumRayDistance = 0.01f;
        [SerializeField, Min(0.01f)] float maximumRayDistance = 100.0f;
        [SerializeField] Vector3 rayRotationEuler;

        [Header("Direct Radiance - Blinn-Phong")]
        [SerializeField] ComputeShader radianceComputeShader;
        [SerializeField, ColorUsage(false, true)] Color ambientColor = Color.black;
        [SerializeField, Min(0.0f)] float diffuseIntensity = 1.0f;
        [SerializeField, ColorUsage(false, true)] Color specularColor = Color.white;
        [SerializeField, Min(0.0f)] float specularIntensity = 0.15f;
        [SerializeField, Min(1.0f)] float shininess = 32.0f;
        [SerializeField, Range(0.0f, 1.0f)] float indirectBounceIntensity = 1.0f;
        [Tooltip("Rebuild and capture each rendered game frame. When disabled, Play still captures once to initialize GPU data.")]
        [FormerlySerializedAs("updateShadowedRadianceEveryFrame")]
        [SerializeField] bool captureVolumeEveryFrame = true;
        [Tooltip("Also recapture on Scene View renders outside Play mode. Editor preview refreshes at 10 Hz.")]
        [SerializeField] bool captureVolumeInEditMode = true;

        [Header("ProbeBlend")]
        [SerializeField] ComputeShader probeBlendComputeShader;
        [SerializeField, Min(1.0f)] float distanceSharpness = 50.0f;

        [Header("Temporal Supersampling")]
        [SerializeField] bool enableTemporalAccumulation = true;
        [SerializeField] bool rotateRayDirections = true;
        [Tooltip("History weight. Larger values reduce noise but slow lighting changes.")]
        [SerializeField, Range(0.0f, 0.999f)] float irradianceHysteresis = 0.97f;
        [SerializeField, Range(0.0f, 0.999f)] float distanceHysteresis = 0.9f;
        [Tooltip("Irradiance blending exponent, not display gamma. One uses linear blending.")]
        [SerializeField, Min(1.0f)] float temporalGamma = 5.0f;

        [Header("Runtime Sampling")]
        [Tooltip("World-space normal offset for probe selection and visibility testing.")]
        [SerializeField, Min(0.0f)] float surfaceNormalBias = 0.2f;
        [Tooltip("World-space offset toward the viewer for probe selection and visibility testing.")]
        [SerializeField, Min(0.0f)] float surfaceViewBias = 0.2f;
        [SerializeField, Min(0.000001f)] float minimumProbeWeight = 0.0001f;
        [SerializeField, Min(0.000001f)] float minimumDistanceVariance = 0.0001f;
        [SerializeField, Min(0.0f)] float indirectDiffuseIntensity = 1.0f;
        [SerializeField] DDGICompositeDebugView compositeDebugView;

        readonly List<DDGIProbe> probes = new List<DDGIProbe>();
        DDGIRayGBuffer rayGBuffer;
        ComputeBuffer probePositionBuffer;
        RenderTexture irradianceAtlas;
        RenderTexture previousIrradianceAtlas;
        RenderTexture previousDistanceMomentsAtlas;
        RenderTexture distanceMomentsAtlas;
        Material rayTracingMaterial;
        DDGIRayTracingScene rayTracingScene;
        DDGIRayTracingDispatcher rayTracingDispatcher;
        DDGIRadianceEvaluator radianceEvaluator;
        DDGIProbeBlender probeBlender;
        int lastRadianceUpdateFrame = -1;
        bool pendingShadowedEvaluation;
        bool hasValidRadianceHistory;
        bool hasPreparedProbeHistory;
        uint captureSequence;
        Matrix4x4 capturedRayRotation = Matrix4x4.identity;
        Matrix4x4 lastBlendedVolumeMatrix;
        Vector3Int lastBlendedProbeCounts;
        Vector3 lastBlendedProbeSpacing;
        int lastBlendedGeometryLayers;
        float lastBlendedMaximumRayDistance;

        public Vector3Int ProbeCounts => probeCounts;
        public Vector3 ProbeSpacing => probeSpacing;
        public int RaysPerProbe => raysPerProbe;
        public int ProbeCount => CalculateProbeCount(probeCounts);
        public int AtlasTilesPerRow => ResolveAtlasTilesPerRow(ProbeCount);
        public int AtlasTileRows => DivideRoundUp(ProbeCount, AtlasTilesPerRow);
        public Vector2Int AtlasTileCounts => new Vector2Int(AtlasTilesPerRow, AtlasTileRows);
        public Vector2Int IrradianceAtlasResolution => AtlasTileCounts * DDGIProbe.IrradianceTileSize;
        public Vector2Int DistanceAtlasResolution => AtlasTileCounts * DDGIProbe.DistanceTileSize;
        public Vector3 MinimumLocalProbePosition => -0.5f * Vector3.Scale(
            new Vector3(probeCounts.x - 1, probeCounts.y - 1, probeCounts.z - 1),
            probeSpacing);

        public RenderTexture IrradianceAtlas => irradianceAtlas;
        public RenderTexture DistanceMomentsAtlas => distanceMomentsAtlas;
        public DDGIRayGBuffer RayGBuffer => rayGBuffer;
        public RenderTexture RadianceTexture => radianceEvaluator?.RadianceTexture;
        public RenderTexture ShadowVisibilityTexture => radianceEvaluator?.ShadowVisibilityTexture;
        public ComputeBuffer ProbePositionBuffer => probePositionBuffer;
        public IReadOnlyList<DDGIProbe> Probes => probes;
        public bool HasCaptureShaders => rayTracingShader != null && rayTracingSurfaceShader != null;
        public bool HasRadianceShader => radianceComputeShader != null;
        public bool HasProbeBlendShader => probeBlendComputeShader != null;
        public uint LastCapturedGeometryCount { get; private set; }
        public int LastCapturedProbeCount { get; private set; }
        public int LastEvaluatedLightCount { get; private set; }
        public int LastBlendedProbeCount { get; private set; }
        public bool HasBlendedProbeData =>
            LastBlendedProbeCount == ProbeCount &&
            irradianceAtlas != null && irradianceAtlas.IsCreated() &&
            distanceMomentsAtlas != null && distanceMomentsAtlas.IsCreated();
        public bool CaptureVolumeEveryFrame => captureVolumeEveryFrame;
        public bool CaptureVolumeInEditMode => captureVolumeInEditMode;
        public int RecordedUpdateCount { get; private set; }
        public int AccumulatedFrameCount { get; private set; }
        public bool HasPendingShadowedEvaluation => pendingShadowedEvaluation;

        public bool HasValidResources =>
            irradianceAtlas != null && irradianceAtlas.IsCreated() &&
            previousIrradianceAtlas != null && previousIrradianceAtlas.IsCreated() &&
            previousDistanceMomentsAtlas != null && previousDistanceMomentsAtlas.IsCreated() &&
            distanceMomentsAtlas != null && distanceMomentsAtlas.IsCreated() &&
            rayGBuffer != null && rayGBuffer.IsCreated &&
            probePositionBuffer != null && probePositionBuffer.IsValid() &&
            probePositionBuffer.count == ProbeCount;

        void OnEnable()
        {
            DDGIProbeVolumeRegistry.Register(this);
            RefreshProbeCache();
            EnsureResources();
        }

        void OnDisable()
        {
            DDGIProbeVolumeRegistry.Unregister(this);
            ReleaseRayTracingResources();
            ReleaseResources();
        }

        void OnValidate()
        {
            probeCounts = new Vector3Int(
                Mathf.Max(1, probeCounts.x),
                Mathf.Max(1, probeCounts.y),
                Mathf.Max(1, probeCounts.z));
            probeSpacing = new Vector3(
                Mathf.Max(0.01f, probeSpacing.x),
                Mathf.Max(0.01f, probeSpacing.y),
                Mathf.Max(0.01f, probeSpacing.z));
            raysPerProbe = Mathf.Max(1, raysPerProbe);
            atlasTilesPerRow = Mathf.Max(0, atlasTilesPerRow);
            minimumRayDistance = Mathf.Max(0.0f, minimumRayDistance);
            maximumRayDistance = Mathf.Max(minimumRayDistance + 0.01f, maximumRayDistance);
            diffuseIntensity = Mathf.Max(0.0f, diffuseIntensity);
            specularIntensity = Mathf.Max(0.0f, specularIntensity);
            shininess = Mathf.Max(1.0f, shininess);
            indirectBounceIntensity = Mathf.Clamp01(indirectBounceIntensity);
            distanceSharpness = Mathf.Max(1.0f, distanceSharpness);
            irradianceHysteresis = Mathf.Clamp(irradianceHysteresis, 0.0f, 0.999f);
            distanceHysteresis = Mathf.Clamp(distanceHysteresis, 0.0f, 0.999f);
            temporalGamma = Mathf.Max(1.0f, temporalGamma);
            surfaceNormalBias = Mathf.Max(0.0f, surfaceNormalBias);
            surfaceViewBias = Mathf.Max(0.0f, surfaceViewBias);
            minimumProbeWeight = Mathf.Max(0.000001f, minimumProbeWeight);
            minimumDistanceVariance = Mathf.Max(0.000001f, minimumDistanceVariance);
            indirectDiffuseIntensity = Mathf.Max(0.0f, indirectDiffuseIntensity);

            RefreshProbeCache();
            UpdateExistingProbeTransforms();
            EnsureResources();
        }

        public void ConfigureCaptureShaders(
            RayTracingShader probeRayTracingShader,
            Shader surfaceShader)
        {
            if (rayTracingShader == probeRayTracingShader && rayTracingSurfaceShader == surfaceShader)
                return;

            ReleaseRayTracingResources();
            rayTracingShader = probeRayTracingShader;
            rayTracingSurfaceShader = surfaceShader;
        }

        public void ConfigureRadianceShader(ComputeShader computeShader)
        {
            if (radianceComputeShader == computeShader)
                return;

            radianceEvaluator?.Dispose();
            radianceEvaluator = null;
            radianceComputeShader = computeShader;
        }

        public void ConfigureProbeBlendShader(ComputeShader computeShader)
        {
            if (probeBlendComputeShader == computeShader)
                return;

            probeBlender = null;
            probeBlendComputeShader = computeShader;
        }

        [ContextMenu("Capture Volume Ray G-Buffer")]
        public void CaptureRayGBuffer()
        {
            if (!SystemInfo.supportsRayTracingShaders)
                throw new NotSupportedException("DDGI volume capture requires Direct3D 12 ray tracing support.");
            if (!HasCaptureShaders)
                throw new InvalidOperationException("Assign the DDGI ray tracing and surface shaders before capture.");

            CommandBuffer commandBuffer = CommandBufferPool.Get("DDGI Volume Ray G-Buffer Capture");
            try
            {
                RecordRayGBufferCapture(commandBuffer);
                Graphics.ExecuteCommandBuffer(commandBuffer);
            }
            finally
            {
                CommandBufferPool.Release(commandBuffer);
            }

            LastCapturedGeometryCount = rayTracingScene.BuiltInstanceCount;
            LastCapturedProbeCount = ProbeCount;
            LastBlendedProbeCount = 0;
            ResetTemporalHistory();
            pendingShadowedEvaluation = true;
            Debug.Log(
                $"DDGI G-Buffer captured: {raysPerProbe} rays x {ProbeCount} probes " +
                $"({raysPerProbe * ProbeCount} total rays), " +
                $"{LastCapturedGeometryCount} geometry instances. " +
                "Radiance will update when a camera shadow map is available.",
                this);
        }

        [ContextMenu("Evaluate Volume Radiance")]
        public void EvaluateRadiance()
        {
            if (LastCapturedProbeCount != ProbeCount || !HasValidResources)
                throw new InvalidOperationException("Capture the DDGI ray G-Buffer before evaluating radiance.");

            pendingShadowedEvaluation = true;
            Debug.Log("DDGI radiance evaluation queued for the next camera shadow pass.", this);
        }

        [ContextMenu("Blend Probe Atlases")]
        public void BlendProbes()
        {
            if (pendingShadowedEvaluation)
                throw new InvalidOperationException("Wait for shadowed radiance evaluation before ProbeBlend.");
            EnsureResources();

            CommandBuffer commandBuffer = CommandBufferPool.Get("DDGI ProbeBlend");
            try
            {
                RecordProbeBlend(commandBuffer);
                Graphics.ExecuteCommandBuffer(commandBuffer);
            }
            finally
            {
                CommandBufferPool.Release(commandBuffer);
            }

            Debug.Log($"DDGI ProbeBlend completed for {LastBlendedProbeCount} probes.", this);
        }

        [ContextMenu("Rebuild Probe Grid")]
        public void RebuildProbeGrid()
        {
            RefreshProbeCache();
            int requiredProbeCount = ProbeCount;
            int tilesPerRow = AtlasTilesPerRow;

            for (int probeIndex = 0; probeIndex < requiredProbeCount; probeIndex++)
            {
                DDGIProbe probe = probeIndex < probes.Count
                    ? probes[probeIndex]
                    : CreateProbe(probeIndex);
                ConfigureProbe(probe, probeIndex, tilesPerRow);
            }

            for (int probeIndex = probes.Count - 1; probeIndex >= requiredProbeCount; probeIndex--)
                DestroyProbe(probes[probeIndex]);

            RefreshProbeCache();
            EnsureResources();
            UpdateProbePositionBuffer();
        }

        [ContextMenu("Clear Probe Grid")]
        public void ClearProbeGrid()
        {
            RefreshProbeCache();
            for (int index = probes.Count - 1; index >= 0; index--)
                DestroyProbe(probes[index]);
            probes.Clear();
            ReleaseResources();
        }

        [ContextMenu("Allocate GPU Resources")]
        public void EnsureResources()
        {
            ValidateTextureDimensions();
            bool resourcesChanged = false;
            bool layoutChanged = HasBlendedProbeData &&
                (lastBlendedVolumeMatrix != transform.localToWorldMatrix ||
                 lastBlendedProbeCounts != probeCounts ||
                 lastBlendedProbeSpacing != probeSpacing ||
                 lastBlendedGeometryLayers != geometryLayers.value ||
                 lastBlendedMaximumRayDistance != maximumRayDistance);

            Vector2Int irradianceResolution = IrradianceAtlasResolution;
            if (!Matches(irradianceAtlas, irradianceResolution))
            {
                resourcesChanged = true;
                ReleaseTexture(ref irradianceAtlas);
                irradianceAtlas = CreateAtlas(
                    "DDGI Irradiance Atlas",
                    irradianceResolution,
                    GraphicsFormat.R16G16B16A16_SFloat);
            }

            if (!Matches(previousIrradianceAtlas, irradianceResolution))
            {
                resourcesChanged = true;
                ReleaseTexture(ref previousIrradianceAtlas);
                previousIrradianceAtlas = CreateAtlas(
                    "DDGI Previous Irradiance Atlas",
                    irradianceResolution,
                    GraphicsFormat.R16G16B16A16_SFloat);
            }

            Vector2Int distanceResolution = DistanceAtlasResolution;
            if (!Matches(distanceMomentsAtlas, distanceResolution))
            {
                resourcesChanged = true;
                ReleaseTexture(ref distanceMomentsAtlas);
                distanceMomentsAtlas = CreateAtlas(
                    "DDGI Distance Moments Atlas",
                    distanceResolution,
                    GraphicsFormat.R16G16_SFloat);
            }

            if (!Matches(previousDistanceMomentsAtlas, distanceResolution))
            {
                resourcesChanged = true;
                ReleaseTexture(ref previousDistanceMomentsAtlas);
                previousDistanceMomentsAtlas = CreateAtlas(
                    "DDGI Previous Distance Moments Atlas",
                    distanceResolution,
                    GraphicsFormat.R16G16_SFloat);
            }

            rayGBuffer ??= new DDGIRayGBuffer();
            resourcesChanged |= rayGBuffer.EnsureCreated(ProbeCount, raysPerProbe);
            if (radianceComputeShader != null)
            {
                radianceEvaluator ??= new DDGIRadianceEvaluator(radianceComputeShader);
                radianceEvaluator.EnsureCreated(raysPerProbe, ProbeCount);
            }
            EnsureProbePositionBuffer();
            UpdateProbePositionBuffer();

            if (resourcesChanged || layoutChanged)
            {
                LastCapturedProbeCount = 0;
                LastEvaluatedLightCount = 0;
                LastBlendedProbeCount = 0;
                lastRadianceUpdateFrame = -1;
                RecordedUpdateCount = 0;
                pendingShadowedEvaluation = false;
                hasValidRadianceHistory = false;
                hasPreparedProbeHistory = false;
                AccumulatedFrameCount = 0;
                captureSequence = 0;
            }
        }

        [ContextMenu("Release GPU Resources")]
        public void ReleaseResources()
        {
            ReleaseTexture(ref irradianceAtlas);
            ReleaseTexture(ref previousIrradianceAtlas);
            ReleaseTexture(ref previousDistanceMomentsAtlas);
            ReleaseTexture(ref distanceMomentsAtlas);
            rayGBuffer?.Release();
            radianceEvaluator?.Release();
            ReleaseProbePositionBuffer();
            LastCapturedProbeCount = 0;
            LastEvaluatedLightCount = 0;
            LastBlendedProbeCount = 0;
            lastRadianceUpdateFrame = -1;
            RecordedUpdateCount = 0;
            pendingShadowedEvaluation = false;
            hasValidRadianceHistory = false;
            hasPreparedProbeHistory = false;
            AccumulatedFrameCount = 0;
            captureSequence = 0;
        }

        [ContextMenu("Reset Temporal History")]
        public void ResetTemporalHistory()
        {
            hasValidRadianceHistory = false;
            hasPreparedProbeHistory = false;
            AccumulatedFrameCount = 0;
            lastRadianceUpdateFrame = -1;
            if (LastCapturedProbeCount == ProbeCount && HasValidResources)
                pendingShadowedEvaluation = true;
        }

        public void ClearAtlases(CommandBuffer commandBuffer)
        {
            if (commandBuffer == null)
                throw new ArgumentNullException(nameof(commandBuffer));

            ClearTexture(commandBuffer, irradianceAtlas);
            ClearTexture(commandBuffer, distanceMomentsAtlas);
        }

        public void BindShaderGlobals(CommandBuffer commandBuffer)
        {
            if (commandBuffer == null)
                throw new ArgumentNullException(nameof(commandBuffer));
            if (!HasValidResources)
                throw new InvalidOperationException("Allocate the DDGI volume resources before binding them.");

            Vector2Int tileCounts = AtlasTileCounts;
            Vector2Int irradianceResolution = IrradianceAtlasResolution;
            Vector2Int distanceResolution = DistanceAtlasResolution;

            commandBuffer.SetGlobalTexture(IrradianceAtlasId, irradianceAtlas);
            commandBuffer.SetGlobalTexture(DistanceMomentsAtlasId, distanceMomentsAtlas);
            commandBuffer.SetGlobalVector(
                ProbeCountsId,
                new Vector4(probeCounts.x, probeCounts.y, probeCounts.z, ProbeCount));
            commandBuffer.SetGlobalVector(
                ProbeSpacingId,
                new Vector4(probeSpacing.x, probeSpacing.y, probeSpacing.z, 0.0f));
            commandBuffer.SetGlobalMatrix(VolumeToWorldId, transform.localToWorldMatrix);
            commandBuffer.SetGlobalMatrix(WorldToVolumeId, transform.worldToLocalMatrix);
            Vector3 minimumLocalPosition = MinimumLocalProbePosition;
            commandBuffer.SetGlobalVector(
                MinimumLocalProbePositionId,
                new Vector4(
                    minimumLocalPosition.x,
                    minimumLocalPosition.y,
                    minimumLocalPosition.z,
                    0.0f));
            commandBuffer.SetGlobalVector(
                AtlasTileCountsId,
                new Vector4(tileCounts.x, tileCounts.y, 0.0f, 0.0f));
            commandBuffer.SetGlobalVector(
                IrradianceAtlasResolutionId,
                new Vector4(irradianceResolution.x, irradianceResolution.y,
                    1.0f / irradianceResolution.x, 1.0f / irradianceResolution.y));
            commandBuffer.SetGlobalVector(
                DistanceAtlasResolutionId,
                new Vector4(distanceResolution.x, distanceResolution.y,
                    1.0f / distanceResolution.x, 1.0f / distanceResolution.y));
            commandBuffer.SetGlobalFloat(SurfaceNormalBiasId, surfaceNormalBias);
            commandBuffer.SetGlobalFloat(SurfaceViewBiasId, surfaceViewBias);
            commandBuffer.SetGlobalFloat(MinimumProbeWeightId, minimumProbeWeight);
            commandBuffer.SetGlobalFloat(MinimumDistanceVarianceId, minimumDistanceVariance);
            commandBuffer.SetGlobalFloat(IndirectDiffuseIntensityId, indirectDiffuseIntensity);
            commandBuffer.SetGlobalInt(CompositeDebugViewId, (int)compositeDebugView);
            rayGBuffer.BindGlobals(commandBuffer);
            if (RadianceTexture != null && RadianceTexture.IsCreated())
                commandBuffer.SetGlobalTexture(RayRadianceTextureId, RadianceTexture);
        }

        public bool RecordRealtimeCaptureAndBlend(
            CommandBuffer commandBuffer,
            Light mainLight,
            int mainLightCascadeCount)
        {
            if (Application.isPlaying && lastRadianceUpdateFrame == Time.frameCount)
                return false;
            if (!SystemInfo.supportsRayTracingShaders ||
                !HasCaptureShaders || !HasRadianceShader || !HasProbeBlendShader)
            {
                return false;
            }

            RecordRayGBufferCapture(commandBuffer);

            RecordRadianceEvaluation(commandBuffer, mainLight, mainLightCascadeCount);
            RecordProbeBlend(commandBuffer);
            hasValidRadianceHistory = true;
            pendingShadowedEvaluation = false;
            LastCapturedGeometryCount = rayTracingScene.BuiltInstanceCount;
            LastCapturedProbeCount = ProbeCount;
            lastRadianceUpdateFrame = Time.frameCount;
            RecordedUpdateCount++;
            return true;
        }

        public bool RecordPendingShadowedRadianceAndBlend(
            CommandBuffer commandBuffer,
            Light mainLight,
            int mainLightCascadeCount)
        {
            if (!pendingShadowedEvaluation ||
                LastCapturedProbeCount != ProbeCount || !HasValidResources)
            {
                return false;
            }

            RecordRadianceEvaluation(commandBuffer, mainLight, mainLightCascadeCount);
            RecordProbeBlend(commandBuffer);
            hasValidRadianceHistory = true;
            pendingShadowedEvaluation = false;
            RecordedUpdateCount++;
            return true;
        }

        void RecordRayGBufferCapture(CommandBuffer commandBuffer)
        {
            EnsureResources();
            UpdateProbePositionBuffer();
            EnsureRayTracingResources();
            rayTracingScene.Rebuild(geometryLayers, commandBuffer);

            capturedRayRotation = GetCaptureRayRotation();
            rayGBuffer.Clear(commandBuffer);
            rayTracingDispatcher.RecordTrace(
                commandBuffer,
                rayTracingScene.AccelerationStructure,
                probePositionBuffer,
                rayGBuffer,
                minimumRayDistance,
                maximumRayDistance,
                capturedRayRotation);
        }

        Matrix4x4 GetCaptureRayRotation()
        {
            Quaternion rotation = Quaternion.Euler(rayRotationEuler);
            if (enableTemporalAccumulation && rotateRayDirections)
            {
                // A deterministic unit-quaternion sequence avoids touching Unity's global Random state.
                double sequence = ++captureSequence;
                float u = (float)((sequence * 0.7548776662466927) % 1.0);
                float v = (float)((sequence * 0.5698402909980532) % 1.0);
                float w = (float)((sequence * 0.438579021) % 1.0);
                float angleA = 2.0f * Mathf.PI * v;
                float angleB = 2.0f * Mathf.PI * w;
                float radiusA = Mathf.Sqrt(1.0f - u);
                float radiusB = Mathf.Sqrt(u);
                rotation *= new Quaternion(
                    radiusA * Mathf.Sin(angleA), radiusA * Mathf.Cos(angleA),
                    radiusB * Mathf.Sin(angleB), radiusB * Mathf.Cos(angleB));
            }
            return Matrix4x4.Rotate(rotation);
        }

        void RecordRadianceEvaluation(
            CommandBuffer commandBuffer,
            Light mainLight = null,
            int mainLightCascadeCount = 0)
        {
            if (!HasRadianceShader)
                throw new InvalidOperationException("Assign the DDGI radiance compute shader before evaluation.");

            bool hasPreviousVolume = HasBlendedProbeData && hasValidRadianceHistory;
            hasPreparedProbeHistory = hasPreviousVolume;
            if (hasPreviousVolume)
            {
                commandBuffer.CopyTexture(irradianceAtlas, previousIrradianceAtlas);
                commandBuffer.CopyTexture(distanceMomentsAtlas, previousDistanceMomentsAtlas);
            }
            BindShaderGlobals(commandBuffer);

            radianceEvaluator ??= new DDGIRadianceEvaluator(radianceComputeShader);
            radianceEvaluator.RecordEvaluation(
                commandBuffer,
                rayGBuffer,
                probePositionBuffer,
                ambientColor,
                diffuseIntensity,
                specularColor,
                specularIntensity,
                shininess,
                previousIrradianceAtlas,
                previousDistanceMomentsAtlas,
                hasPreviousVolume,
                indirectBounceIntensity,
                mainLight,
                mainLightCascadeCount);
            LastEvaluatedLightCount = radianceEvaluator.LightCount;
        }

        void RecordProbeBlend(CommandBuffer commandBuffer)
        {
            if (!HasProbeBlendShader)
                throw new InvalidOperationException("Assign the DDGI ProbeBlend compute shader before blending.");

            probeBlender ??= new DDGIProbeBlender(probeBlendComputeShader);
            ClearAtlases(commandBuffer);
            probeBlender.RecordBlend(
                commandBuffer,
                rayGBuffer,
                RadianceTexture,
                probePositionBuffer,
                irradianceAtlas,
                distanceMomentsAtlas,
                AtlasTilesPerRow,
                maximumRayDistance,
                distanceSharpness,
                capturedRayRotation,
                previousIrradianceAtlas,
                previousDistanceMomentsAtlas,
                enableTemporalAccumulation && hasPreparedProbeHistory,
                irradianceHysteresis,
                distanceHysteresis,
                temporalGamma);
            AccumulatedFrameCount = enableTemporalAccumulation && hasPreparedProbeHistory
                ? AccumulatedFrameCount + 1
                : 1;
            hasPreparedProbeHistory = false;
            LastBlendedProbeCount = ProbeCount;
            lastBlendedVolumeMatrix = transform.localToWorldMatrix;
            lastBlendedProbeCounts = probeCounts;
            lastBlendedProbeSpacing = probeSpacing;
            lastBlendedGeometryLayers = geometryLayers.value;
            lastBlendedMaximumRayDistance = maximumRayDistance;
        }

        public Vector3Int GetGridCoordinate(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)ProbeCount)
                throw new ArgumentOutOfRangeException(nameof(probeIndex));

            int x = probeIndex % probeCounts.x;
            int yzIndex = probeIndex / probeCounts.x;
            int y = yzIndex % probeCounts.y;
            int z = yzIndex / probeCounts.y;
            return new Vector3Int(x, y, z);
        }

        public int GetLinearIndex(Vector3Int coordinate)
        {
            if (coordinate.x < 0 || coordinate.x >= probeCounts.x ||
                coordinate.y < 0 || coordinate.y >= probeCounts.y ||
                coordinate.z < 0 || coordinate.z >= probeCounts.z)
            {
                throw new ArgumentOutOfRangeException(nameof(coordinate));
            }

            return coordinate.x + probeCounts.x * (coordinate.y + probeCounts.y * coordinate.z);
        }

        public Vector3 GetProbeWorldPosition(int probeIndex)
        {
            Vector3Int coordinate = GetGridCoordinate(probeIndex);
            Vector3 localPosition = MinimumLocalProbePosition + Vector3.Scale(
                new Vector3(coordinate.x, coordinate.y, coordinate.z),
                probeSpacing);
            return transform.TransformPoint(localPosition);
        }

        public bool TryGetProbe(int probeIndex, out DDGIProbe probe)
        {
            RefreshProbeCache();
            if ((uint)probeIndex < (uint)probes.Count)
            {
                probe = probes[probeIndex];
                return probe != null && probe.LinearIndex == probeIndex;
            }

            probe = null;
            return false;
        }

        public void UpdateProbePositionBuffer()
        {
            if (probePositionBuffer == null || !probePositionBuffer.IsValid() ||
                probePositionBuffer.count != ProbeCount)
            {
                return;
            }

            var positions = new Vector3[ProbeCount];
            for (int probeIndex = 0; probeIndex < positions.Length; probeIndex++)
                positions[probeIndex] = GetProbeWorldPosition(probeIndex);
            probePositionBuffer.SetData(positions);
        }

        void RefreshProbeCache()
        {
            probes.Clear();
            GetComponentsInChildren(true, probes);
            probes.RemoveAll(probe => probe == null || probe.transform == transform);
            probes.Sort((left, right) => left.LinearIndex.CompareTo(right.LinearIndex));
        }

        void UpdateExistingProbeTransforms()
        {
            int tilesPerRow = AtlasTilesPerRow;
            foreach (DDGIProbe probe in probes)
            {
                if ((uint)probe.LinearIndex >= (uint)ProbeCount)
                    continue;
                ConfigureProbe(probe, probe.LinearIndex, tilesPerRow);
            }
        }

        DDGIProbe CreateProbe(int probeIndex)
        {
            var probeObject = new GameObject($"DDGI Probe {probeIndex:D4}");
            probeObject.transform.SetParent(transform, false);
            DDGIProbe probe = probeObject.AddComponent<DDGIProbe>();
            probes.Add(probe);
            return probe;
        }

        void ConfigureProbe(DDGIProbe probe, int probeIndex, int tilesPerRow)
        {
            Vector3Int coordinate = GetGridCoordinate(probeIndex);
            probe.Configure(probeIndex, coordinate, tilesPerRow);
            probe.name = $"DDGI Probe {probeIndex:D4} [{coordinate.x}, {coordinate.y}, {coordinate.z}]";
            probe.transform.localPosition = MinimumLocalProbePosition + Vector3.Scale(
                new Vector3(coordinate.x, coordinate.y, coordinate.z),
                probeSpacing);
            probe.transform.localRotation = Quaternion.identity;
            probe.transform.localScale = Vector3.one;
        }

        void DestroyProbe(DDGIProbe probe)
        {
            if (probe == null)
                return;

            if (Application.isPlaying)
                Destroy(probe.gameObject);
            else
                DestroyImmediate(probe.gameObject);
        }

        void EnsureProbePositionBuffer()
        {
            if (probePositionBuffer != null && probePositionBuffer.IsValid() &&
                probePositionBuffer.count == ProbeCount)
            {
                return;
            }

            ReleaseProbePositionBuffer();
            probePositionBuffer = new ComputeBuffer(
                ProbeCount,
                sizeof(float) * 3,
                ComputeBufferType.Structured);
            probePositionBuffer.name = "DDGI Probe Positions";
        }

        void ReleaseProbePositionBuffer()
        {
            probePositionBuffer?.Release();
            probePositionBuffer = null;
        }

        void EnsureRayTracingResources()
        {
            if (rayTracingMaterial == null)
            {
                rayTracingMaterial = new Material(rayTracingSurfaceShader)
                {
                    name = "DDGI Ray Tracing Surface Material",
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            rayTracingScene ??= new DDGIRayTracingScene(rayTracingMaterial);
            rayTracingDispatcher ??= new DDGIRayTracingDispatcher(rayTracingShader);
        }

        void ReleaseRayTracingResources()
        {
            rayTracingScene?.Dispose();
            rayTracingScene = null;
            rayTracingDispatcher = null;

            if (rayTracingMaterial != null)
            {
                if (Application.isPlaying)
                    Destroy(rayTracingMaterial);
                else
                    DestroyImmediate(rayTracingMaterial);
                rayTracingMaterial = null;
            }

            LastCapturedGeometryCount = 0;
            LastCapturedProbeCount = 0;
        }

        int ResolveAtlasTilesPerRow(int probeCount)
        {
            if (atlasTilesPerRow > 0)
                return Mathf.Min(atlasTilesPerRow, probeCount);
            return Mathf.CeilToInt(Mathf.Sqrt(probeCount));
        }

        void ValidateTextureDimensions()
        {
            ValidateResolution(IrradianceAtlasResolution, "irradiance");
            ValidateResolution(DistanceAtlasResolution, "distance moments");
            if (raysPerProbe > SystemInfo.maxTextureSize)
                throw new InvalidOperationException("The DDGI ray count exceeds the maximum texture width.");
            if (ProbeCount > SystemInfo.maxTextureSize)
                throw new InvalidOperationException("The DDGI probe count exceeds the maximum texture height.");
        }

        static void ValidateResolution(Vector2Int resolution, string atlasName)
        {
            if (resolution.x > SystemInfo.maxTextureSize || resolution.y > SystemInfo.maxTextureSize)
            {
                throw new InvalidOperationException(
                    $"The DDGI {atlasName} atlas ({resolution.x} x {resolution.y}) exceeds " +
                    $"the maximum texture size ({SystemInfo.maxTextureSize}).");
            }
        }

        static RenderTexture CreateAtlas(string textureName, Vector2Int resolution, GraphicsFormat format)
        {
            if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.LoadStore))
                throw new NotSupportedException($"The graphics device does not support UAV writes to {format}.");

            var descriptor = new RenderTextureDescriptor(
                resolution.x,
                resolution.y,
                format,
                GraphicsFormat.None)
            {
                dimension = TextureDimension.Tex2D,
                volumeDepth = 1,
                msaaSamples = 1,
                mipCount = 1,
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = true
            };

            var texture = new RenderTexture(descriptor)
            {
                name = textureName,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            texture.Create();
            return texture;
        }

        static bool Matches(RenderTexture texture, Vector2Int resolution)
        {
            return texture != null && texture.IsCreated() &&
                   texture.width == resolution.x && texture.height == resolution.y;
        }

        static void ReleaseTexture(ref RenderTexture texture)
        {
            if (texture == null)
                return;

            texture.Release();
            if (Application.isPlaying)
                Destroy(texture);
            else
                DestroyImmediate(texture);
            texture = null;
        }

        static void ClearTexture(CommandBuffer commandBuffer, RenderTexture texture)
        {
            if (texture == null || !texture.IsCreated())
                return;
            commandBuffer.SetRenderTarget(texture);
            commandBuffer.ClearRenderTarget(false, true, Color.clear);
        }

        static int CalculateProbeCount(Vector3Int counts)
        {
            long count = (long)counts.x * counts.y * counts.z;
            if (count <= 0 || count > int.MaxValue)
                throw new InvalidOperationException("The DDGI probe count is outside the supported range.");
            return (int)count;
        }

        static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        void OnDrawGizmosSelected()
        {
            if (!drawVolumeBounds)
                return;

            Matrix4x4 previousMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.1f, 0.75f, 1.0f, 0.8f);
            Vector3 size = Vector3.Scale(
                new Vector3(probeCounts.x - 1, probeCounts.y - 1, probeCounts.z - 1),
                probeSpacing);
            Gizmos.DrawWireCube(Vector3.zero, size);
            Gizmos.matrix = previousMatrix;
        }
    }
}
