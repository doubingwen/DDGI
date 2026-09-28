using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using UnityEngine.Serialization;

namespace Dou.GI
{   //将数据打包成一个float数组
    [CreateAssetMenu(fileName = "RadianceFieldBakeData", menuName = "Dou GI/Radiance Field Bake Data")]
    [MovedFrom(true, null, null, "ProbeVolumeData")]
    public sealed class RadianceFieldBakeData : ScriptableObject
    {
        const int FloatsPerSample = 10;

        [FormerlySerializedAs("volumePosition")]
        [SerializeField] Vector3 capturedOrigin;

        [FormerlySerializedAs("serializedSurfelData")]
        [FormerlySerializedAs("surfelStorageBuffer")]
        [SerializeField] float[] packedSurfaceSamples;

        public void CaptureFrom(RadianceFieldVolume volume)
        {
            if (volume == null)
                return;

            int sampleCount = volume.TotalProbeCount * RadianceProbe.SurfaceSampleCount;
            var samples = new ProbeSurfaceSample[sampleCount];
            int sampleOffset = 0;
            foreach (RadianceProbe probe in volume.Probes)
            {
                probe.CopySurfaceSamplesTo(samples, sampleOffset);
                sampleOffset += RadianceProbe.SurfaceSampleCount;
            }

            Array.Resize(ref packedSurfaceSamples, sampleCount * FloatsPerSample);
            for (int sampleIndex = 0, dataIndex = 0; sampleIndex < samples.Length; sampleIndex++)
                PackSample(samples[sampleIndex], packedSurfaceSamples, ref dataIndex);

            capturedOrigin = volume.Origin;

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.AssetDatabase.SaveAssets();
#endif
        }

        public bool TryRestore(RadianceFieldVolume volume)
        {
            if (volume == null)
                return false;

            int sampleCount = volume.TotalProbeCount * RadianceProbe.SurfaceSampleCount;
            bool dataMatches = packedSurfaceSamples != null && packedSurfaceSamples.Length == sampleCount * FloatsPerSample;
            bool originMatches = Vector3.SqrMagnitude(volume.Origin - capturedOrigin) < 0.000001f;
            if (!dataMatches || !originMatches)
                return false;

            var samples = new ProbeSurfaceSample[sampleCount];
            for (int sampleIndex = 0, dataIndex = 0; sampleIndex < samples.Length; sampleIndex++)
                samples[sampleIndex] = UnpackSample(packedSurfaceSamples, ref dataIndex);

            int sampleOffset = 0;
            foreach (RadianceProbe probe in volume.Probes)
            {
                probe.UploadSurfaceSamples(samples, sampleOffset);
                sampleOffset += RadianceProbe.SurfaceSampleCount;
            }

            return true;
        }

        static void PackSample(ProbeSurfaceSample sample, float[] destination, ref int index)
        {
            destination[index++] = sample.position.x;
            destination[index++] = sample.position.y;
            destination[index++] = sample.position.z;
            destination[index++] = sample.normal.x;
            destination[index++] = sample.normal.y;
            destination[index++] = sample.normal.z;
            destination[index++] = sample.albedo.x;
            destination[index++] = sample.albedo.y;
            destination[index++] = sample.albedo.z;
            destination[index++] = sample.skyVisibility;
        }

        static ProbeSurfaceSample UnpackSample(float[] source, ref int index)
        {
            return new ProbeSurfaceSample
            {
                position = new Vector3(source[index++], source[index++], source[index++]),
                normal = new Vector3(source[index++], source[index++], source[index++]),
                albedo = new Vector3(source[index++], source[index++], source[index++]),
                skyVisibility = source[index++]
            };
        }
    }
}
