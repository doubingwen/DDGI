Shader "DouDDGI/Composite"
{
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityGBuffer.hlsl"
            #include "Assets/DDGI/Shaders/DDGIVolumeSampling.hlsl"

            TEXTURE2D_X(_CameraDepthTexture);
            TEXTURE2D_X_HALF(_GBuffer0);
            TEXTURE2D_X_HALF(_GBuffer2);
            SamplerState sampler_point_clamp;
            TEXTURE2D_X_HALF(_DDGI_AccumulatedIndirectTexture);

            bool DDGIReadSurface(Varyings input, out float3 worldPosition,
                out float3 albedo, out float3 normalWS)
            {
                worldPosition = albedo = normalWS = 0.0;
                float2 screenUv = input.positionCS.xy / _ScaledScreenParams.xy;
                float deviceDepth = SAMPLE_DEPTH_TEXTURE(
                    _CameraDepthTexture,
                    sampler_point_clamp,
                    screenUv);

                #if UNITY_REVERSED_Z
                    if (deviceDepth <= 0.00001)
                        return false;
                #else
                    if (deviceDepth >= 0.99999)
                        return false;
                #endif

                worldPosition = ComputeWorldSpacePosition(
                    screenUv,
                    deviceDepth,
                    UNITY_MATRIX_I_VP);
                albedo = SAMPLE_TEXTURE2D_X_LOD(
                    _GBuffer0,
                    sampler_point_clamp,
                    screenUv,
                    0).rgb;
                float3 packedNormal = SAMPLE_TEXTURE2D_X_LOD(
                    _GBuffer2,
                    sampler_point_clamp,
                    screenUv,
                    0).xyz;
                normalWS = normalize(UnpackNormal(packedNormal));
                return true;
            }

            float4 DDGIAccumulateVolume(Varyings input) : SV_Target
            {
                float3 worldPosition, albedo, normalWS;
                if (!DDGIReadSurface(input, worldPosition, albedo, normalWS))
                    return 0.0;
                float weight = DDGIGetVolumeBlendWeight(worldPosition);
                if (weight <= 0.0)
                    return 0.0;
                float3 irradiance = DDGISampleIrradiance(
                    worldPosition,
                    normalWS,
                    GetWorldSpaceViewDir(worldPosition));
                float3 indirectDiffuse = irradiance * saturate(albedo) *
                    (_DDGI_IndirectDiffuseIntensity / DDGI_PI);

                float3 contribution = _DDGI_CompositeDebugView == 2 ? irradiance : indirectDiffuse;
                return float4(contribution * weight, weight);
            }

            float4 DDGICompositeFragment(Varyings input) : SV_Target
            {
                float4 sceneColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);
                if (_DDGI_CompositeDebugView == 3)
                    return float4(1.0, 0.0, 1.0, 1.0);
                float3 positionWS, albedo, normalWS;
                if (!DDGIReadSurface(input, positionWS, albedo, normalWS))
                    return sceneColor;

                float4 accumulated = SAMPLE_TEXTURE2D_X_LOD(
                    _DDGI_AccumulatedIndirectTexture, sampler_LinearClamp, input.texcoord, 0);
                float3 mixedLight = accumulated.rgb / max(accumulated.a, 1e-6);
                // Retain outer-volume fade after normalization when coverage is below one.
                float3 indirect = mixedLight * saturate(accumulated.a);
                if (_DDGI_CompositeDebugView == 1 || _DDGI_CompositeDebugView == 2)
                    return float4(indirect, 1.0);
                sceneColor.rgb += indirect;
                return sceneColor;
            }
        ENDHLSL

        Pass
        {
            Name "Accumulate Volume"
            // Dense volumes render first: each subsequent volume fills remaining coverage.
            Blend OneMinusDstAlpha One, OneMinusDstAlpha One
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DDGIAccumulateVolume
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }

        Pass
        {
            Name "DDGI Composite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DDGICompositeFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
    }
}
