using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Scripting.APIUpdating;
using UnityEngine.Serialization;

namespace Dou.GI
{
    public enum RadianceFieldDebugView
    {
        None = 0,
        ProbeCells = 1,
        ProbeRadiance = 2
    }

    [ExecuteAlways]
    [MovedFrom(true, null, null, "ProbeVolume")]
    public sealed class RadianceFieldVolume : MonoBehaviour
    {
        const int ShValueCount = 9 * 3;

        [Header("Probe Layout")]
        [FormerlySerializedAs("probePrefab")]
        [SerializeField] GameObject probeTemplate;

        [FormerlySerializedAs("probeCountX")]
        [FormerlySerializedAs("probeSizeX")]
        [Min(1)] [SerializeField] int gridCountX = 8;

        [FormerlySerializedAs("probeCountY")]
        [FormerlySerializedAs("probeSizeY")]
        [Min(1)] [SerializeField] int gridCountY = 4;

        [FormerlySerializedAs("probeCountZ")]
        [FormerlySerializedAs("probeSizeZ")]
        [Min(1)] [SerializeField] int gridCountZ = 8;

        [FormerlySerializedAs("probeSpacing")]
        [FormerlySerializedAs("probeGridSize")]
        [Min(0.01f)] [SerializeField] float probeSpacing = 2.0f;

        [FormerlySerializedAs("probeData")]
        [FormerlySerializedAs("data")]
        [SerializeField] RadianceFieldBakeData bakedSurfaceData;

        [Header("Lighting")]
        [FormerlySerializedAs("skyLightIntensity")]
        [Range(0.0f, 50.0f)] [SerializeField] float environmentIntensity = 1.0f;

        [FormerlySerializedAs("indirectLightIntensity")]
        [FormerlySerializedAs("GIIntensity")]
        [Range(0.0f, 50.0f)] [SerializeField] float bounceIntensity = 1.0f;

        [Range(0.0f, 10.0f)] [SerializeField] float outputIntensity = 3.0f;

        [Header("Debug")]
        [FormerlySerializedAs("debugMode")]
        [SerializeField] RadianceFieldDebugView debugView = RadianceFieldDebugView.ProbeRadiance;

        [HideInInspector]
        [FormerlySerializedAs("probeObjects")]
        [FormerlySerializedAs("probes")]
        [SerializeField] GameObject[] probeInstances;

        readonly List<RadianceProbe> probeCache = new List<RadianceProbe>();
        int[] coefficientClearValues;
        ComputeBuffer currentCoefficients;
        ComputeBuffer historyCoefficients;

        public Vector3Int GridDimensions => new Vector3Int(gridCountX, gridCountY, gridCountZ);
        public int TotalProbeCount => gridCountX * gridCountY * gridCountZ;
        public float ProbeSpacing => probeSpacing;
        public float EnvironmentIntensity => environmentIntensity;
        public float BounceIntensity => bounceIntensity;
        public float OutputIntensity => outputIntensity;
        public Vector3 Origin => transform.position;
        public IReadOnlyList<RadianceProbe> Probes => probeCache;
        public bool HasCoefficientHistory => currentCoefficients != null && historyCoefficients != null;
        internal ComputeBuffer CurrentCoefficients => currentCoefficients;
        internal ComputeBuffer HistoryCoefficients => historyCoefficients;

        void OnEnable()
        {
            RadianceFieldRegistry.Register(this);
            EnsureInitialized();
        }

        void OnDisable()
        {
            RadianceFieldRegistry.Unregister(this);
            ReleaseCoefficientBuffers();
        }
        //根据是否创建判断，走创建还是走重置数据或加载数据
        public void EnsureInitialized()
        {
            if (!HasCompleteGrid())
                RebuildProbeGrid();
            else
                RebuildProbeCache();

            if (!HasCoefficientHistory)
                AllocateCoefficientBuffers();

            if (bakedSurfaceData != null)
                bakedSurfaceData.TryRestore(this);
        }

        public void RebuildProbeGrid()
        {
            DestroyProbeInstances();
            ReleaseCoefficientBuffers();
            probeCache.Clear();

            if (probeTemplate == null)
            {
                Debug.LogError("A radiance probe template is required to build the field.", this);
                probeInstances = System.Array.Empty<GameObject>();
                return;
            }

            probeInstances = new GameObject[TotalProbeCount];
            for (int x = 0; x < gridCountX; x++)
            {
                for (int y = 0; y < gridCountY; y++)
                {
                    for (int z = 0; z < gridCountZ; z++)
                        CreateProbeInstance(x, y, z);
                }
            }

            AllocateCoefficientBuffers();
        }

        public void BakeSurfaceData()
        {
            EnsureInitialized();
            SetProbeMeshesVisible(false);

            foreach (RadianceProbe probe in probeCache)
                probe.BakeSurfaceCache();

            if (bakedSurfaceData != null)
                bakedSurfaceData.CaptureFrom(this);
        }

        public void BeginLightingFrame(CommandBuffer commandBuffer)
        {
            if (!HasCoefficientHistory)
                return;

            (currentCoefficients, historyCoefficients) = (historyCoefficients, currentCoefficients);
            commandBuffer.SetBufferData(currentCoefficients, coefficientClearValues);
        }

        public void BindGlobalShaderState(CommandBuffer commandBuffer)
        {
            Vector3Int dimensions = GridDimensions;
            commandBuffer.SetGlobalFloat(RadianceFieldShaderIds.FieldSpacing, probeSpacing);
            commandBuffer.SetGlobalVector(RadianceFieldShaderIds.FieldDimensions, new Vector4(dimensions.x, dimensions.y, dimensions.z, 0.0f));
            commandBuffer.SetGlobalVector(RadianceFieldShaderIds.FieldOrigin, Origin);
            commandBuffer.SetGlobalBuffer(RadianceFieldShaderIds.FieldCoefficients, currentCoefficients);
            commandBuffer.SetGlobalBuffer(RadianceFieldShaderIds.HistoryCoefficients, historyCoefficients);
            commandBuffer.SetGlobalFloat(RadianceFieldShaderIds.EnvironmentIntensity, environmentIntensity);
            commandBuffer.SetGlobalFloat(RadianceFieldShaderIds.BounceIntensity, bounceIntensity);
            commandBuffer.SetGlobalFloat(RadianceFieldShaderIds.OutputIntensity, outputIntensity);
        }

        public void BindCompositeMaterial(Material material)
        {
            Vector3Int dimensions = GridDimensions;
            material.SetFloat(RadianceFieldShaderIds.FieldSpacing, probeSpacing);
            material.SetVector(RadianceFieldShaderIds.FieldDimensions, new Vector4(dimensions.x, dimensions.y, dimensions.z, 0.0f));
            material.SetVector(RadianceFieldShaderIds.FieldOrigin, Origin);
            material.SetBuffer(RadianceFieldShaderIds.FieldCoefficients, currentCoefficients);
            material.SetFloat(RadianceFieldShaderIds.OutputIntensity, outputIntensity);
        }

        public void LogCoefficientSummary()
        {
            EnsureInitialized();
            if (!HasCoefficientHistory)
            {
                Debug.LogWarning("Radiance field coefficient buffers are unavailable.", this);
                return;
            }

            int[] coefficients = new int[TotalProbeCount * ShValueCount];
            currentCoefficients.GetData(coefficients);

            int nonZeroCount = 0;
            long maximumAbsoluteValue = 0;
            foreach (int coefficient in coefficients)
            {
                if (coefficient != 0)
                    nonZeroCount++;
                maximumAbsoluteValue = System.Math.Max(maximumAbsoluteValue, System.Math.Abs((long)coefficient));
            }

            Debug.Log($"Radiance Field SH: {nonZeroCount}/{coefficients.Length} non-zero values, maximum encoded magnitude {maximumAbsoluteValue}.", this);
        }

        void OnDrawGizmos()
        {
            if (probeInstances == null)
                return;

            foreach (GameObject probeObject in probeInstances)
            {
                if (probeObject == null)
                    continue;

                if (debugView == RadianceFieldDebugView.ProbeCells)
                {
                    Vector3 cellSize = Vector3.one * probeSpacing;
                    Gizmos.DrawWireCube(probeObject.transform.position + cellSize * 0.5f, cellSize);
                }

                MeshRenderer meshRenderer = probeObject.GetComponent<MeshRenderer>();
                if (meshRenderer != null)
                    meshRenderer.enabled = !Application.isPlaying && debugView != RadianceFieldDebugView.None;
            }
        }

        bool HasCompleteGrid()
        {
            if (probeInstances == null || probeInstances.Length != TotalProbeCount)
                return false;

            foreach (GameObject probeObject in probeInstances)
            {
                if (probeObject == null || probeObject.GetComponent<RadianceProbe>() == null)
                    return false;
            }

            return true;
        }

        void RebuildProbeCache()
        {
            probeCache.Clear();
            for (int index = 0; index < probeInstances.Length; index++)
            {
                RadianceProbe probe = probeInstances[index].GetComponent<RadianceProbe>();
                probe.ConfigureGridIndex(index);
                probe.EnsureResources();
                probeCache.Add(probe);
            }
        }

        void CreateProbeInstance(int x, int y, int z)
        {
            int index = ToLinearIndex(x, y, z);
            GameObject probeObject = Instantiate(probeTemplate, transform);
            probeObject.name = $"Radiance Probe [{x}, {y}, {z}]";
            probeObject.transform.localPosition = new Vector3(x, y, z) * probeSpacing;

            RadianceProbe probe = probeObject.GetComponent<RadianceProbe>();
            if (probe == null)
            {
                Debug.LogError("The radiance probe template does not contain a RadianceProbe component.", probeObject);
            }
            else
            {
                probe.ConfigureGridIndex(index);
                probe.EnsureResources();
                probeCache.Add(probe);
            }

            probeInstances[index] = probeObject;
        }

        int ToLinearIndex(int x, int y, int z)
        {
            return x * gridCountY * gridCountZ + y * gridCountZ + z;
        }

        void AllocateCoefficientBuffers()
        {
            ReleaseCoefficientBuffers();
            int elementCount = TotalProbeCount * ShValueCount;
            coefficientClearValues = new int[elementCount];
            currentCoefficients = new ComputeBuffer(elementCount, sizeof(int));
            historyCoefficients = new ComputeBuffer(elementCount, sizeof(int));
            currentCoefficients.SetData(coefficientClearValues);
            historyCoefficients.SetData(coefficientClearValues);
        }

        void SetProbeMeshesVisible(bool visible)
        {
            if (probeInstances == null)
                return;

            foreach (GameObject probeObject in probeInstances)
            {
                MeshRenderer meshRenderer = probeObject != null ? probeObject.GetComponent<MeshRenderer>() : null;
                if (meshRenderer != null)
                    meshRenderer.enabled = visible;
            }
        }

        void DestroyProbeInstances()
        {
            if (probeInstances == null)
                return;

            foreach (GameObject probeObject in probeInstances)
            {
                if (probeObject != null)
                    DestroyImmediate(probeObject);
            }

            probeInstances = null;
        }

        void ReleaseCoefficientBuffers()
        {
            currentCoefficients?.Release();
            historyCoefficients?.Release();
            currentCoefficients = null;
            historyCoefficients = null;
        }
    }
}
