using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Dou.DDGI
{
    public sealed class DDGIRadianceEvaluator : IDisposable
    {
        const string KernelName = "EvaluateRadiance";

        static readonly int PositionTextureId = Shader.PropertyToID("_DDGI_RayPositionTexture");
        static readonly int NormalTextureId = Shader.PropertyToID("_DDGI_RayNormalTexture");
        static readonly int AlbedoTextureId = Shader.PropertyToID("_DDGI_RayAlbedoTexture");
        static readonly int RadianceTextureId = Shader.PropertyToID("_DDGI_RayRadianceTexture");
        static readonly int ShadowVisibilityTextureId = Shader.PropertyToID("_DDGI_RayShadowVisibilityTexture");
        static readonly int ProbePositionsId = Shader.PropertyToID("_DDGI_ProbePositions");
        static readonly int LightsId = Shader.PropertyToID("_DDGI_Lights");
        static readonly int LightCountId = Shader.PropertyToID("_DDGI_LightCount");
        static readonly int TextureSizeId = Shader.PropertyToID("_DDGI_RayTextureSize");
        static readonly int AmbientColorId = Shader.PropertyToID("_DDGI_AmbientColor");
        static readonly int DiffuseIntensityId = Shader.PropertyToID("_DDGI_DiffuseIntensity");
        static readonly int SpecularColorId = Shader.PropertyToID("_DDGI_SpecularColor");
        static readonly int SpecularIntensityId = Shader.PropertyToID("_DDGI_SpecularIntensity");
        static readonly int ShininessId = Shader.PropertyToID("_DDGI_Shininess");
        static readonly int MainLightCascadeCountId = Shader.PropertyToID("_DDGI_MainLightCascadeCount");
        static readonly int PreviousIrradianceAtlasId = Shader.PropertyToID("_DDGI_PreviousIrradianceAtlas");
        static readonly int DistanceMomentsAtlasId = Shader.PropertyToID("_DDGI_DistanceMomentsAtlas");
        static readonly int HasPreviousVolumeId = Shader.PropertyToID("_DDGI_HasPreviousVolume");
        static readonly int IndirectBounceIntensityId = Shader.PropertyToID("_DDGI_IndirectBounceIntensity");

        readonly ComputeShader computeShader;
        readonly int kernelIndex;
        readonly List<GpuLight> gpuLights = new List<GpuLight>();

        ComputeBuffer lightBuffer;

        public RenderTexture RadianceTexture { get; private set; }
        public RenderTexture ShadowVisibilityTexture { get; private set; }
        public int LightCount { get; private set; }

        public bool IsCreated => RadianceTexture != null && RadianceTexture.IsCreated() &&
                                 ShadowVisibilityTexture != null && ShadowVisibilityTexture.IsCreated();

        public DDGIRadianceEvaluator(ComputeShader computeShader)
        {
            this.computeShader = computeShader != null
                ? computeShader
                : throw new ArgumentNullException(nameof(computeShader));
            kernelIndex = computeShader.FindKernel(KernelName);
        }

        public void EnsureCreated(int width, int height)
        {
            if (IsCreated && RadianceTexture.width == width && RadianceTexture.height == height &&
                ShadowVisibilityTexture.width == width && ShadowVisibilityTexture.height == height)
                return;

            ReleaseTexture();
            var descriptor = new RenderTextureDescriptor(
                width,
                height,
                GraphicsFormat.R16G16B16A16_SFloat,
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

            RadianceTexture = new RenderTexture(descriptor)
            {
                name = "DDGI Ray Radiance",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            RadianceTexture.Create();

            descriptor.graphicsFormat = GraphicsFormat.R32_SFloat;
            ShadowVisibilityTexture = new RenderTexture(descriptor)
            {
                name = "DDGI Ray Shadow Visibility",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            ShadowVisibilityTexture.Create();
        }

        public void RecordEvaluation(
            CommandBuffer commandBuffer,
            DDGIRayGBuffer gBuffer,
            ComputeBuffer probePositions,
            Color ambientColor,
            float diffuseIntensity,
            Color specularColor,
            float specularIntensity,
            float shininess,
            RenderTexture previousIrradianceAtlas,
            RenderTexture previousDistanceMomentsAtlas,
            bool hasPreviousVolume,
            float indirectBounceIntensity,
            Light shadowedMainLight = null,
            int mainLightCascadeCount = 0,
            ComputeBuffer probeStates = null)
        {
            if (commandBuffer == null)
                throw new ArgumentNullException(nameof(commandBuffer));
            if (gBuffer == null || !gBuffer.IsCreated)
                throw new InvalidOperationException("Create the DDGI ray G-Buffer before evaluating radiance.");
            if (probePositions == null || !probePositions.IsValid())
                throw new InvalidOperationException("Create the DDGI probe position buffer before evaluating radiance.");
            if (probeStates == null || !probeStates.IsValid() || probeStates.count < gBuffer.ProbeCount)
                throw new InvalidOperationException("Create the DDGI probe state buffer before evaluating radiance.");
            if (previousIrradianceAtlas == null || !previousIrradianceAtlas.IsCreated() ||
                previousDistanceMomentsAtlas == null || !previousDistanceMomentsAtlas.IsCreated())
            {
                throw new InvalidOperationException("Create the DDGI probe atlases before evaluating radiance.");
            }

            EnsureCreated(gBuffer.RayCount, gBuffer.ProbeCount);
            CollectSceneLights(shadowedMainLight, mainLightCascadeCount > 0);
            EnsureLightBuffer(Mathf.Max(1, gpuLights.Count));
            if (gpuLights.Count > 0)
                lightBuffer.SetData(gpuLights);
            else
                lightBuffer.SetData(new GpuLight[1]);

            Color linearAmbient = ToActiveColorSpace(ambientColor);
            Color linearSpecular = ToActiveColorSpace(specularColor);

            commandBuffer.SetComputeTextureParam(computeShader, kernelIndex, PositionTextureId, gBuffer.PositionTexture);
            commandBuffer.SetComputeTextureParam(computeShader, kernelIndex, NormalTextureId, gBuffer.NormalTexture);
            commandBuffer.SetComputeTextureParam(computeShader, kernelIndex, AlbedoTextureId, gBuffer.AlbedoTexture);
            commandBuffer.SetComputeTextureParam(computeShader, kernelIndex, RadianceTextureId, RadianceTexture);
            commandBuffer.SetComputeTextureParam(
                computeShader, kernelIndex, ShadowVisibilityTextureId, ShadowVisibilityTexture);
            commandBuffer.SetComputeTextureParam(
                computeShader, kernelIndex, PreviousIrradianceAtlasId, previousIrradianceAtlas);
            commandBuffer.SetComputeTextureParam(
                computeShader, kernelIndex, DistanceMomentsAtlasId, previousDistanceMomentsAtlas);
            commandBuffer.SetComputeBufferParam(computeShader, kernelIndex, ProbePositionsId, probePositions);
            commandBuffer.SetComputeBufferParam(computeShader, kernelIndex, "_DDGI_ProbeStates", probeStates);
            commandBuffer.SetComputeBufferParam(computeShader, kernelIndex, LightsId, lightBuffer);
            commandBuffer.SetComputeIntParam(computeShader, LightCountId, gpuLights.Count);
            commandBuffer.SetComputeVectorParam(
                computeShader,
                TextureSizeId,
                new Vector4(gBuffer.RayCount, gBuffer.ProbeCount, 0.0f, 0.0f));
            commandBuffer.SetComputeVectorParam(computeShader, AmbientColorId, linearAmbient);
            commandBuffer.SetComputeFloatParam(computeShader, DiffuseIntensityId, Mathf.Max(0.0f, diffuseIntensity));
            commandBuffer.SetComputeVectorParam(computeShader, SpecularColorId, linearSpecular);
            commandBuffer.SetComputeFloatParam(computeShader, SpecularIntensityId, Mathf.Max(0.0f, specularIntensity));
            commandBuffer.SetComputeFloatParam(computeShader, ShininessId, Mathf.Max(1.0f, shininess));
            commandBuffer.SetComputeIntParam(computeShader, MainLightCascadeCountId, mainLightCascadeCount);
            commandBuffer.SetComputeIntParam(computeShader, HasPreviousVolumeId, hasPreviousVolume ? 1 : 0);
            commandBuffer.SetComputeFloatParam(
                computeShader, IndirectBounceIntensityId, Mathf.Max(0.0f, indirectBounceIntensity));

            int threadGroupsX = DivideRoundUp(gBuffer.RayCount, 8);
            int threadGroupsY = DivideRoundUp(gBuffer.ProbeCount, 8);
            commandBuffer.DispatchCompute(computeShader, kernelIndex, threadGroupsX, threadGroupsY, 1);
            LightCount = gpuLights.Count;
        }

        public void Release()
        {
            ReleaseTexture();
            lightBuffer?.Release();
            lightBuffer = null;
            LightCount = 0;
        }

        public void Dispose()
        {
            Release();
        }

        void CollectSceneLights(Light shadowedMainLight, bool useMainLightShadows)
        {
            gpuLights.Clear();
            Light[] sceneLights = UnityEngine.Object.FindObjectsByType<Light>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            foreach (Light light in sceneLights)
            {
                if (light == null || !light.enabled || !light.gameObject.activeInHierarchy || light.intensity <= 0.0f)
                    continue;
                if (light.type != LightType.Directional &&
                    light.type != LightType.Point &&
                    light.type != LightType.Spot)
                {
                    continue;
                }

                bool useShadowMap = useMainLightShadows && light == shadowedMainLight &&
                    light.type == LightType.Directional;
                if (light.type == LightType.Directional && !useShadowMap)
                    continue;

                gpuLights.Add(GpuLight.FromLight(light, useShadowMap));
            }
        }

        void EnsureLightBuffer(int requiredCount)
        {
            if (lightBuffer != null && lightBuffer.IsValid() && lightBuffer.count == requiredCount)
                return;

            lightBuffer?.Release();
            lightBuffer = new ComputeBuffer(requiredCount, GpuLight.Stride, ComputeBufferType.Structured)
            {
                name = "DDGI Direct Lights"
            };
        }

        void ReleaseTexture()
        {
            ReleaseRenderTexture(RadianceTexture);
            ReleaseRenderTexture(ShadowVisibilityTexture);
            RadianceTexture = null;
            ShadowVisibilityTexture = null;
        }

        static void ReleaseRenderTexture(RenderTexture texture)
        {
            if (texture == null)
                return;

            texture.Release();
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(texture);
            else
                UnityEngine.Object.DestroyImmediate(texture);
        }

        static Color ToActiveColorSpace(Color color)
        {
            return QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;
        }

        static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GpuLight
        {
            public const int Stride = sizeof(float) * 16;

            public Vector4 positionAndType;
            public Vector4 directionAndRange;
            public Vector4 color;
            public Vector4 spotAngles;

            public static GpuLight FromLight(Light light, bool useMainLightShadows)
            {
                float type = light.type switch
                {
                    LightType.Directional => 0.0f,
                    LightType.Point => 1.0f,
                    LightType.Spot => 2.0f,
                    _ => -1.0f
                };

                Color linearColor = ToActiveColorSpace(light.color) * light.intensity;
                float outerCosine = Mathf.Cos(0.5f * light.spotAngle * Mathf.Deg2Rad);
                float innerCosine = Mathf.Cos(0.5f * light.innerSpotAngle * Mathf.Deg2Rad);

                return new GpuLight
                {
                    positionAndType = new Vector4(
                        light.transform.position.x,
                        light.transform.position.y,
                        light.transform.position.z,
                        type),
                    directionAndRange = new Vector4(
                        light.transform.forward.x,
                        light.transform.forward.y,
                        light.transform.forward.z,
                        Mathf.Max(0.001f, light.range)),
                    color = new Vector4(linearColor.r, linearColor.g, linearColor.b, 0.0f),
                    spotAngles = new Vector4(
                        outerCosine,
                        1.0f / Mathf.Max(0.001f, innerCosine - outerCosine),
                        useMainLightShadows ? 1.0f : 0.0f,
                        0.0f)
                };
            }
        }
    }
}
