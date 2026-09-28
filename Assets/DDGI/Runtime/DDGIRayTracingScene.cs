using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Dou.DDGI
{
    public sealed class DDGIRayTracingScene : IDisposable
    {
        static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        static readonly int MainTextureId = Shader.PropertyToID("_MainTex");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int BaseMapScaleOffsetId = Shader.PropertyToID("_BaseMap_ST");
        static readonly int MainTextureScaleOffsetId = Shader.PropertyToID("_MainTex_ST");

        readonly Material rayTracingMaterial;
        readonly List<MaterialPropertyBlock> instanceProperties = new List<MaterialPropertyBlock>();
        int builtGeometryLayerMask = int.MinValue;

        public RayTracingAccelerationStructure AccelerationStructure { get; private set; }
        public int InstanceCount { get; private set; }
        public uint BuiltInstanceCount { get; private set; }
        public ulong AccelerationStructureSize { get; private set; }
        public int StaticBatchedRendererCount { get; private set; }

        public DDGIRayTracingScene(Material rayTracingMaterial)
        {
            this.rayTracingMaterial = rayTracingMaterial != null
                ? rayTracingMaterial
                : throw new ArgumentNullException(nameof(rayTracingMaterial));
        }

        public void Rebuild(LayerMask geometryLayers, CommandBuffer commandBuffer = null)
        {
            if (AccelerationStructure == null || builtGeometryLayerMask != geometryLayers.value)
            {
                ReleaseAccelerationStructure();
                var settings = new RayTracingAccelerationStructure.Settings(
                    RayTracingAccelerationStructure.ManagementMode.Manual,
                    RayTracingAccelerationStructure.RayTracingModeMask.Everything,
                    geometryLayers);
                AccelerationStructure = new RayTracingAccelerationStructure(settings);
                builtGeometryLayerMask = geometryLayers.value;
            }
            else
            {
                AccelerationStructure.ClearInstances();
            }

            instanceProperties.Clear();
            InstanceCount = 0;
            StaticBatchedRendererCount = 0;

            MeshRenderer[] renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            foreach (MeshRenderer meshRenderer in renderers)
                AddMeshRenderer(meshRenderer, geometryLayers);

            if (commandBuffer != null)
                commandBuffer.BuildRayTracingAccelerationStructure(AccelerationStructure);
            else
                AccelerationStructure.Build();
            BuiltInstanceCount = AccelerationStructure.GetInstanceCount();
            AccelerationStructureSize = commandBuffer == null
                ? AccelerationStructure.GetSize()
                : 0;
        }

        public void Dispose()
        {
            ReleaseAccelerationStructure();
        }

        void AddMeshRenderer(MeshRenderer meshRenderer, LayerMask geometryLayers)
        {
            if (!IsCaptureGeometry(meshRenderer, geometryLayers)) return;

            MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (mesh == null)
                return;

            Material[] sourceMaterials = meshRenderer.sharedMaterials;
            int firstSubMesh = meshRenderer.subMeshStartIndex;
            int subMeshCount = GetRendererSubMeshCount(mesh, firstSubMesh, sourceMaterials.Length);
            if (meshRenderer.isPartOfStaticBatch) StaticBatchedRendererCount++;
            for (int materialIndex = 0; materialIndex < subMeshCount; materialIndex++)
            {
                Material sourceMaterial = sourceMaterials[materialIndex];
                if (sourceMaterial == null)
                    continue;

                MaterialPropertyBlock properties = CreateMaterialProperties(sourceMaterial);
                instanceProperties.Add(properties);

                var config = new RayTracingMeshInstanceConfig(
                    mesh,
                    // Static batching combines meshes, but each renderer owns only its submesh range.
                    (uint)(firstSubMesh + materialIndex),
                    rayTracingMaterial)
                {
                    materialProperties = properties,
                    subMeshFlags = RayTracingSubMeshFlags.Enabled |
                                   RayTracingSubMeshFlags.ClosestHitOnly,
                    enableTriangleCulling = false,
                    frontTriangleCounterClockwise = false,
                    mask = 0xff,
                    layer = meshRenderer.gameObject.layer,
                    dynamicGeometry = false
                };

                AccelerationStructure.AddInstance(
                    config,
                    meshRenderer.localToWorldMatrix,
                    null,
                    unchecked((uint)meshRenderer.GetInstanceID()));
                InstanceCount++;
            }
        }

        static bool IsProbeVisualization(MeshRenderer meshRenderer)
        {
            return meshRenderer.GetComponentInParent<DDGIProbe>() != null ||
                   meshRenderer.GetComponent("RadianceProbe") != null;
        }

        internal static bool IsCaptureGeometry(MeshRenderer renderer, LayerMask layers)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                (layers.value & (1 << renderer.gameObject.layer)) == 0 || IsProbeVisualization(renderer))
                return false;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return false;
            Material[] materials = renderer.sharedMaterials;
            int count = GetRendererSubMeshCount(filter.sharedMesh, renderer.subMeshStartIndex, materials.Length);
            for (int i = 0; i < count; ++i)
                if (materials[i] != null) return true;
            return false;
        }

        static int GetRendererSubMeshCount(Mesh mesh, int firstSubMesh, int materialCount)
        {
            if (firstSubMesh < 0 || firstSubMesh >= mesh.subMeshCount) return 0;
            return Mathf.Min(mesh.subMeshCount - firstSubMesh, materialCount);
        }

        static MaterialPropertyBlock CreateMaterialProperties(Material sourceMaterial)
        {
            var properties = new MaterialPropertyBlock();

            Texture baseMap = sourceMaterial.HasProperty(BaseMapId)
                ? sourceMaterial.GetTexture(BaseMapId)
                : sourceMaterial.HasProperty(MainTextureId)
                    ? sourceMaterial.GetTexture(MainTextureId)
                    : null;
            properties.SetTexture(BaseMapId, baseMap != null ? baseMap : Texture2D.whiteTexture);

            Color baseColor = sourceMaterial.HasProperty(BaseColorId)
                ? sourceMaterial.GetColor(BaseColorId)
                : sourceMaterial.HasProperty(ColorId)
                    ? sourceMaterial.GetColor(ColorId)
                    : Color.white;
            properties.SetColor(BaseColorId, baseColor);

            Vector4 scaleOffset = sourceMaterial.HasProperty(BaseMapScaleOffsetId)
                ? sourceMaterial.GetVector(BaseMapScaleOffsetId)
                : sourceMaterial.HasProperty(MainTextureScaleOffsetId)
                    ? sourceMaterial.GetVector(MainTextureScaleOffsetId)
                    : new Vector4(1.0f, 1.0f, 0.0f, 0.0f);
            properties.SetVector(BaseMapScaleOffsetId, scaleOffset);
            return properties;
        }

        void ReleaseAccelerationStructure()
        {
            AccelerationStructure?.Dispose();
            AccelerationStructure = null;
            builtGeometryLayerMask = int.MinValue;
            InstanceCount = 0;
            BuiltInstanceCount = 0;
            AccelerationStructureSize = 0;
            StaticBatchedRendererCount = 0;
            instanceProperties.Clear();
        }
    }
}
