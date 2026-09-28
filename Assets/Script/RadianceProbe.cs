using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Scripting.APIUpdating;
using UnityEngine.Serialization;

namespace Dou.GI
{
    public struct ProbeSurfaceSample
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector3 albedo;
        public float skyVisibility;
    }

    public enum RadianceProbeDebugView
    {
        None = 0,
        SampleSphere = 1,
        CaptureRays = 2,
        SurfaceSamples = 3,
        SurfaceRadiance = 4
    }

    [ExecuteAlways]
    [MovedFrom(true, null, null, "Probe")]
    public sealed class RadianceProbe : MonoBehaviour
    {
        public const int ThreadGroupWidth = 32;
        public const int ThreadGroupHeight = 16;
        public const int SurfaceSampleCount = ThreadGroupWidth * ThreadGroupHeight;

        const int SurfaceSampleStride = sizeof(float) * 10;
        const int ShValueCount = 9 * 3;
        const string CaptureKernelName = "CaptureSurfaceSamples";
        const string IntegrateKernelName = "IntegrateProbeRadiance";

        [Header("Capture Targets")]
        [FormerlySerializedAs("worldPositionCubemap")]
        [FormerlySerializedAs("RT_WorldPos")]
        [SerializeField] RenderTexture worldPositionCapture;

        [FormerlySerializedAs("normalCubemap")]
        [FormerlySerializedAs("RT_Normal")]
        [SerializeField] RenderTexture normalCapture;

        [FormerlySerializedAs("albedoCubemap")]
        [FormerlySerializedAs("RT_Albedo")]
        [SerializeField] RenderTexture albedoCapture;

        [Header("Compute Programs")]
        [FormerlySerializedAs("surfelSamplingComputeShader")]
        [FormerlySerializedAs("surfelSampleCS")]
        [SerializeField] ComputeShader surfaceCaptureProgram;

        [FormerlySerializedAs("surfelRelightingComputeShader")]
        [FormerlySerializedAs("surfelReLightCS")]
        [SerializeField] ComputeShader radianceIntegrationProgram;

        [Header("Debug")]
        [FormerlySerializedAs("debugMode")]
        [SerializeField] RadianceProbeDebugView debugView;

        [HideInInspector]
        [FormerlySerializedAs("volumeProbeIndex")]
        [FormerlySerializedAs("indexInProbeVolume")]
        [SerializeField] int gridIndex = -1;

        readonly int[] shClearValues = new int[ShValueCount];
        readonly int[] fallbackValues = new int[1];

        MaterialPropertyBlock debugProperties;
        ProbeSurfaceSample[] surfaceSampleCache;
        Vector3[] radianceReadback;
        ComputeBuffer surfaceSampleBuffer;
        ComputeBuffer surfaceRadianceBuffer;
        ComputeBuffer probeCoefficientBuffer;
        ComputeBuffer fallbackCoefficientBuffer;
        int captureKernel = -1;
        int integrateKernel = -1;

        //让上面变量成为只读数据
        //=> 等同于访问SurfaceSamples  return surfaceSampleCache  防止其他改变
        public int GridIndex => gridIndex;
        public IReadOnlyList<ProbeSurfaceSample> SurfaceSamples => surfaceSampleCache;
        internal ComputeBuffer SurfaceSampleBuffer => surfaceSampleBuffer;
        internal ComputeBuffer ProbeCoefficientBuffer => probeCoefficientBuffer;

        void OnEnable()
        {
            EnsureResources();
        }

        void OnValidate()
        {
            captureKernel = -1;
            integrateKernel = -1;
        }

        void OnDestroy()
        {
            ReleaseBuffer(ref surfaceSampleBuffer);
            ReleaseBuffer(ref surfaceRadianceBuffer);
            ReleaseBuffer(ref probeCoefficientBuffer);
            ReleaseBuffer(ref fallbackCoefficientBuffer);
        }

        public void ConfigureGridIndex(int index)
        {
            gridIndex = index;
        }

        public void EnsureResources()
        {
            surfaceSampleCache ??= new ProbeSurfaceSample[SurfaceSampleCount];
            radianceReadback ??= new Vector3[SurfaceSampleCount];
            debugProperties ??= new MaterialPropertyBlock();

            surfaceSampleBuffer ??= new ComputeBuffer(SurfaceSampleCount, SurfaceSampleStride);
            surfaceRadianceBuffer ??= new ComputeBuffer(SurfaceSampleCount, sizeof(float) * 3);

            if (probeCoefficientBuffer == null)
            {
                probeCoefficientBuffer = new ComputeBuffer(ShValueCount, sizeof(int));
                probeCoefficientBuffer.SetData(shClearValues);
            }

            if (fallbackCoefficientBuffer == null)
            {
                fallbackCoefficientBuffer = new ComputeBuffer(1, sizeof(int));
                fallbackCoefficientBuffer.SetData(fallbackValues);
            }
        }

        public void BakeSurfaceCache()
        {
            EnsureResources();

            if (!HasCaptureTargets())
            {
                Debug.LogError("Radiance probe capture targets are incomplete.", this);
                return;
            }

            Shader positionShader = Shader.Find("DouGI/Capture/WorldPosition");
            Shader normalShader = Shader.Find("DouGI/Capture/Normal");
            Shader albedoShader = Shader.Find("DouGI/Capture/Albedo");
            if (positionShader == null || normalShader == null || albedoShader == null)
            {
                Debug.LogError("Radiance probe capture shaders could not be found.", this);
                return;
            }

            GameObject cameraObject = CreateCaptureCamera(out Camera captureCamera);
            Dictionary<Material, Shader> originalShaders = CollectSceneMaterialShaders();
            try
            {
                RenderCapture(captureCamera, originalShaders.Keys, positionShader, worldPositionCapture);
                RenderCapture(captureCamera, originalShaders.Keys, normalShader, normalCapture);
                RenderCapture(captureCamera, originalShaders.Keys, albedoShader, albedoCapture);
                DispatchSurfaceCapture();
            }
            finally
            {
                RestoreMaterialShaders(originalShaders);
                DestroyImmediate(cameraObject);
            }
        }

        public void RecordRadianceUpdate(CommandBuffer commandBuffer, RadianceFieldVolume volume)
        {
            if (commandBuffer == null || radianceIntegrationProgram == null)
                return;

            EnsureResources();
            integrateKernel = ResolveKernel(radianceIntegrationProgram, integrateKernel, IntegrateKernelName);

            bool hasField = volume != null && volume.HasCoefficientHistory;
            ComputeBuffer currentField = hasField ? volume.CurrentCoefficients : fallbackCoefficientBuffer;
            ComputeBuffer historyField = hasField ? volume.HistoryCoefficients : fallbackCoefficientBuffer;
            Vector3Int dimensions = hasField ? volume.GridDimensions : Vector3Int.zero;
            Vector3 origin = hasField ? volume.Origin : Vector3.zero;
            float spacing = hasField ? volume.ProbeSpacing : 1.0f;
            int fieldIndex = hasField ? gridIndex : -1;

            commandBuffer.SetComputeVectorParam(radianceIntegrationProgram, RadianceFieldShaderIds.ProbePosition, transform.position);
            commandBuffer.SetComputeBufferParam(radianceIntegrationProgram, integrateKernel, RadianceFieldShaderIds.SurfaceSamples, surfaceSampleBuffer);
            commandBuffer.SetComputeBufferParam(radianceIntegrationProgram, integrateKernel, RadianceFieldShaderIds.SurfaceRadiance, surfaceRadianceBuffer);
            commandBuffer.SetComputeBufferParam(radianceIntegrationProgram, integrateKernel, RadianceFieldShaderIds.ProbeCoefficients, probeCoefficientBuffer);
            commandBuffer.SetComputeBufferParam(radianceIntegrationProgram, integrateKernel, RadianceFieldShaderIds.FieldCoefficients, currentField);
            commandBuffer.SetComputeBufferParam(radianceIntegrationProgram, integrateKernel, RadianceFieldShaderIds.HistoryCoefficients, historyField);
            commandBuffer.SetComputeIntParam(radianceIntegrationProgram, RadianceFieldShaderIds.ProbeGridIndex, fieldIndex);
            commandBuffer.SetComputeFloatParam(radianceIntegrationProgram, RadianceFieldShaderIds.FieldSpacing, spacing);
            commandBuffer.SetComputeVectorParam(radianceIntegrationProgram, RadianceFieldShaderIds.FieldOrigin, origin);
            commandBuffer.SetComputeVectorParam(radianceIntegrationProgram, RadianceFieldShaderIds.FieldDimensions, new Vector4(dimensions.x, dimensions.y, dimensions.z, 0.0f));
            commandBuffer.SetComputeFloatParam(radianceIntegrationProgram, RadianceFieldShaderIds.EnvironmentIntensity, hasField ? volume.EnvironmentIntensity : 0.0f);
            commandBuffer.SetComputeFloatParam(radianceIntegrationProgram, RadianceFieldShaderIds.BounceIntensity, hasField ? volume.BounceIntensity : 0.0f);
            commandBuffer.SetBufferData(probeCoefficientBuffer, shClearValues);
            commandBuffer.DispatchCompute(radianceIntegrationProgram, integrateKernel, 1, 1, 1);
        }

        public void CopySurfaceSamplesTo(ProbeSurfaceSample[] destination, int destinationIndex)
        {
            EnsureResources();
            System.Array.Copy(surfaceSampleCache, 0, destination, destinationIndex, SurfaceSampleCount);
        }

        public void UploadSurfaceSamples(ProbeSurfaceSample[] source, int sourceIndex)
        {
            EnsureResources();
            System.Array.Copy(source, sourceIndex, surfaceSampleCache, 0, SurfaceSampleCount);
            surfaceSampleBuffer.SetData(surfaceSampleCache);
        }

        void OnDrawGizmos()
        {
            EnsureResources();
            DrawProbeMesh();

            if (debugView == RadianceProbeDebugView.None)
                return;

            surfaceSampleBuffer.GetData(surfaceSampleCache);
            surfaceRadianceBuffer.GetData(radianceReadback);

            Vector3 probePosition = transform.position;
            for (int index = 0; index < SurfaceSampleCount; index++)
                DrawSurfaceSample(probePosition, surfaceSampleCache[index], radianceReadback[index]);
        }

        void DrawProbeMesh()
        {
            MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                return;

            meshRenderer.enabled = !Application.isPlaying;
            Shader debugShader = Shader.Find("DouGI/Debug/RadianceLobe");
            if (meshRenderer.sharedMaterial != null && debugShader != null)
                meshRenderer.sharedMaterial.shader = debugShader;

            debugProperties.SetBuffer(RadianceFieldShaderIds.ProbeCoefficients, probeCoefficientBuffer);
            meshRenderer.SetPropertyBlock(debugProperties);
        }

        void DrawSurfaceSample(Vector3 probePosition, ProbeSurfaceSample sample, Vector3 radiance)
        {
            Vector3 direction = (sample.position - probePosition).normalized;
            bool samplesSky = sample.skyVisibility >= 0.995f;
            Gizmos.color = samplesSky ? Color.blue : Color.yellow;

            switch (debugView)
            {
                case RadianceProbeDebugView.SampleSphere:
                    Gizmos.DrawSphere(probePosition + direction, 0.025f);
                    break;
                case RadianceProbeDebugView.CaptureRays:
                    Gizmos.DrawLine(probePosition, samplesSky ? probePosition + direction * 25.0f : sample.position);
                    if (!samplesSky)
                        Gizmos.DrawSphere(sample.position, 0.05f);
                    break;
                case RadianceProbeDebugView.SurfaceSamples when !samplesSky:
                    Gizmos.DrawSphere(sample.position, 0.05f);
                    Gizmos.DrawLine(sample.position, sample.position + sample.normal * 0.25f);
                    break;
                case RadianceProbeDebugView.SurfaceRadiance when !samplesSky:
                    Gizmos.color = new Color(radiance.x, radiance.y, radiance.z);
                    Gizmos.DrawSphere(sample.position, 0.05f);
                    break;
            }
        }

        bool HasCaptureTargets()
        {
            return worldPositionCapture != null && normalCapture != null && albedoCapture != null;
        }

        GameObject CreateCaptureCamera(out Camera captureCamera)
        {
            var cameraObject = new GameObject("Radiance Field Capture Camera")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            cameraObject.transform.SetPositionAndRotation(transform.position, Quaternion.identity);
            captureCamera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<RadianceCaptureCamera>();
            captureCamera.clearFlags = CameraClearFlags.SolidColor;
            captureCamera.backgroundColor = Color.clear;
            return cameraObject;
        }

        void DispatchSurfaceCapture()
        {
            if (surfaceCaptureProgram == null)
                return;

            captureKernel = ResolveKernel(surfaceCaptureProgram, captureKernel, CaptureKernelName);
            surfaceCaptureProgram.SetVector(RadianceFieldShaderIds.ProbePosition, transform.position);
            surfaceCaptureProgram.SetFloat(RadianceFieldShaderIds.CaptureSeed, Random.value);
            surfaceCaptureProgram.SetTexture(captureKernel, RadianceFieldShaderIds.WorldPositionCube, worldPositionCapture);
            surfaceCaptureProgram.SetTexture(captureKernel, RadianceFieldShaderIds.NormalCube, normalCapture);
            surfaceCaptureProgram.SetTexture(captureKernel, RadianceFieldShaderIds.AlbedoCube, albedoCapture);
            surfaceCaptureProgram.SetBuffer(captureKernel, RadianceFieldShaderIds.SurfaceSamples, surfaceSampleBuffer);
            surfaceCaptureProgram.Dispatch(captureKernel, 1, 1, 1);
            surfaceSampleBuffer.GetData(surfaceSampleCache);

            int validSurfaceCount = 0;
            foreach (ProbeSurfaceSample sample in surfaceSampleCache)
            {
                if (sample.skyVisibility < 0.5f && sample.normal.sqrMagnitude > 0.25f)
                    validSurfaceCount++;
            }

            if (validSurfaceCount == 0)
                Debug.LogWarning("Radiance probe captured no valid surfaces.", this);
        }

        static int ResolveKernel(ComputeShader program, int cachedKernel, string kernelName)
        {
            return cachedKernel >= 0 ? cachedKernel : program.FindKernel(kernelName);
        }

        static void RenderCapture(Camera camera, Dictionary<Material, Shader>.KeyCollection materials, Shader shader, RenderTexture target)
        {
            foreach (Material material in materials)
                material.shader = shader;
            camera.RenderToCubemap(target);
        }

        static Dictionary<Material, Shader> CollectSceneMaterialShaders()
        {
            var shaderByMaterial = new Dictionary<Material, Shader>();
            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (Renderer sceneRenderer in renderers)
            {
                foreach (Material material in sceneRenderer.sharedMaterials)
                {
                    if (material != null && !shaderByMaterial.ContainsKey(material))
                        shaderByMaterial.Add(material, material.shader);
                }
            }

            return shaderByMaterial;
        }

        static void RestoreMaterialShaders(Dictionary<Material, Shader> shaderByMaterial)
        {
            foreach (KeyValuePair<Material, Shader> entry in shaderByMaterial)
            {
                if (entry.Key != null)
                    entry.Key.shader = entry.Value;
            }
        }

        static void ReleaseBuffer(ref ComputeBuffer buffer)
        {
            buffer?.Release();
            buffer = null;
        }
    }
}
