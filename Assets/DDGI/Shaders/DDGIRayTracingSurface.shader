Shader "DouDDGI/RayTracingSurface"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Pass
        {
            Name "DDGIRayTracing"
            Tags { "LightMode" = "DDGIRayTracing" }

            HLSLPROGRAM
            #pragma raytracing DDGIRayTracing

            #include "UnityRayTracingMeshUtils.cginc"//提供命中三角形数据的函数
            #include "Assets/DDGI/Shaders/DDGIRayPayload.hlsl"

            Texture2D<float4> _BaseMap;
            SamplerState sampler_BaseMap;
            float4 _BaseColor;
            float4 _BaseMap_ST;

            float3 InterpolateNormal(float3 barycentrics, uint3 indices)
            {
                float3 normal0 = UnityRayTracingFetchVertexAttribute3(indices.x, kVertexAttributeNormal);
                float3 normal1 = UnityRayTracingFetchVertexAttribute3(indices.y, kVertexAttributeNormal);
                float3 normal2 = UnityRayTracingFetchVertexAttribute3(indices.z, kVertexAttributeNormal);
                return normal0 * barycentrics.x
                    + normal1 * barycentrics.y
                    + normal2 * barycentrics.z;
            }

            float2 InterpolateUv(float3 barycentrics, uint3 indices)
            {
                float2 uv0 = UnityRayTracingFetchVertexAttribute2(indices.x, kVertexAttributeTexCoord0);
                float2 uv1 = UnityRayTracingFetchVertexAttribute2(indices.y, kVertexAttributeTexCoord0);
                float2 uv2 = UnityRayTracingFetchVertexAttribute2(indices.z, kVertexAttributeTexCoord0);
                return uv0 * barycentrics.x + uv1 * barycentrics.y + uv2 * barycentrics.z;
            }

            [shader("closesthit")]
            void DDGIClosestHit(
                inout DDGIRayPayload payload : SV_RayPayload,
                DDGIIntersectionAttributes attributes : SV_IntersectionAttributes)
            {
                float3 barycentrics = float3(
                    1.0 - attributes.barycentrics.x - attributes.barycentrics.y,
                    attributes.barycentrics.x,
                    attributes.barycentrics.y);
                uint3 indices = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());

                float3 normalOS = normalize(InterpolateNormal(barycentrics, indices));
                float3 normalWS = normalize(mul(normalOS, (float3x3)WorldToObject3x4()));
                float2 uv = InterpolateUv(barycentrics, indices);
                uv = uv * _BaseMap_ST.xy + _BaseMap_ST.zw;

                payload.positionWS = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
                payload.hitDistance = RayTCurrent();
                payload.normalWS = normalWS;
                payload.albedo = saturate(_BaseMap.SampleLevel(sampler_BaseMap, uv, 0).rgb * _BaseColor.rgb);
                payload.hit = 1;
            }
            ENDHLSL
        }
    }
}
