Shader "DouDDGI/Composite"
{
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "DDGI Composite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DDGICompositeFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityGBuffer.hlsl"
            #include "Assets/DDGI/Shaders/DDGIVolumeSampling.hlsl"

            TEXTURE2D_X(_CameraDepthTexture);
            TEXTURE2D_X_HALF(_GBuffer0);
            TEXTURE2D_X_HALF(_GBuffer2);
            SamplerState sampler_point_clamp;

            float4 DDGICompositeFragment(Varyings input) : SV_Target
            {
                float2 sourceUv = input.texcoord;
                float2 screenUv = input.positionCS.xy / _ScaledScreenParams.xy;
                float4 sceneColor = SAMPLE_TEXTURE2D_X(
                    _BlitTexture,
                    sampler_LinearClamp,
                    sourceUv);

                if (_DDGI_CompositeDebugView == 3)
                    return float4(1.0, 0.0, 1.0, 1.0);

                float deviceDepth = SAMPLE_DEPTH_TEXTURE(
                    _CameraDepthTexture,
                    sampler_point_clamp,
                    screenUv);

                #if UNITY_REVERSED_Z
                    if (deviceDepth <= 0.00001)
                        return sceneColor;
                #else
                    if (deviceDepth >= 0.99999)
                        return sceneColor;
                #endif

                float3 worldPosition = ComputeWorldSpacePosition(
                    screenUv,
                    deviceDepth,
                    UNITY_MATRIX_I_VP);
                float3 albedo = SAMPLE_TEXTURE2D_X_LOD(
                    _GBuffer0,
                    sampler_point_clamp,
                    screenUv,
                    0).rgb;
                float3 packedNormal = SAMPLE_TEXTURE2D_X_LOD(
                    _GBuffer2,
                    sampler_point_clamp,
                    screenUv,
                    0).xyz;
                float3 normalWS = normalize(UnpackNormal(packedNormal));

                float3 irradiance = DDGISampleIrradiance(
                    worldPosition,
                    normalWS,
                    GetWorldSpaceViewDir(worldPosition));
                float3 indirectDiffuse = irradiance * saturate(albedo) *
                    (_DDGI_IndirectDiffuseIntensity / DDGI_PI);

                if (_DDGI_CompositeDebugView == 1)
                    return float4(indirectDiffuse, 1.0);
                if (_DDGI_CompositeDebugView == 2)
                    return float4(irradiance, 1.0);

                sceneColor.rgb += indirectDiffuse;
                return sceneColor;
            }
            ENDHLSL
        }
    }
}
