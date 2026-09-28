#ifndef DOU_DDGI_VOLUME_SAMPLING_INCLUDED
#define DOU_DDGI_VOLUME_SAMPLING_INCLUDED

#define DDGI_PI 3.14159265359
#define DDGI_IRRADIANCE_RESOLUTION 6.0
#define DDGI_IRRADIANCE_TILE_SIZE 8.0
#define DDGI_DISTANCE_RESOLUTION 14.0
#define DDGI_DISTANCE_TILE_SIZE 16.0

#if defined(DDGI_SAMPLE_PREVIOUS_FRAME)
TEXTURE2D(_DDGI_PreviousIrradianceAtlas);
SAMPLER(sampler_DDGI_PreviousIrradianceAtlas);
#define DDGI_IRRADIANCE_TEXTURE _DDGI_PreviousIrradianceAtlas
#define DDGI_IRRADIANCE_SAMPLER sampler_DDGI_PreviousIrradianceAtlas
#else
TEXTURE2D(_DDGI_IrradianceAtlas);
SAMPLER(sampler_DDGI_IrradianceAtlas);
#define DDGI_IRRADIANCE_TEXTURE _DDGI_IrradianceAtlas
#define DDGI_IRRADIANCE_SAMPLER sampler_DDGI_IrradianceAtlas
#endif
TEXTURE2D(_DDGI_DistanceMomentsAtlas);
SAMPLER(sampler_DDGI_DistanceMomentsAtlas);

float4 _DDGI_ProbeCounts;
float4 _DDGI_ProbeSpacing;
float4x4 _DDGI_VolumeToWorld;
float4x4 _DDGI_WorldToVolume;
float4 _DDGI_AtlasTileCounts;
float4 _DDGI_IrradianceAtlasResolution;
float4 _DDGI_DistanceAtlasResolution;
float4 _DDGI_MinimumLocalProbePosition;
float _DDGI_SurfaceNormalBias;
float _DDGI_SurfaceViewBias;
float _DDGI_MinimumProbeWeight;
float _DDGI_MinimumDistanceVariance;
float _DDGI_IndirectDiffuseIntensity;
float _DDGI_BoundaryBlendDistance;
int _DDGI_CompositeDebugView;

static const int3 DDGI_PROBE_OFFSETS[8] =
{
    int3(0, 0, 0), int3(1, 0, 0),
    int3(0, 1, 0), int3(1, 1, 0),
    int3(0, 0, 1), int3(1, 0, 1),
    int3(0, 1, 1), int3(1, 1, 1)
};

float2 DDGIEncodeOctahedralDirection(float3 direction)
{
    direction /= max(abs(direction.x) + abs(direction.y) + abs(direction.z), 1e-6);
    if (direction.z < 0.0)
    {
        float2 signValue = float2(
            direction.x >= 0.0 ? 1.0 : -1.0,
            direction.y >= 0.0 ? 1.0 : -1.0);
        direction.xy = (1.0 - abs(direction.yx)) * signValue;
    }

    return direction.xy * 0.5 + 0.5;
}

int DDGIFlattenProbeCoordinate(int3 coordinate, int3 probeCounts)
{
    return coordinate.x +
        probeCounts.x * (coordinate.y + probeCounts.y * coordinate.z);
}

float3 DDGIGetProbeLocalPosition(int3 coordinate)
{
    return _DDGI_MinimumLocalProbePosition.xyz +
        (float3)coordinate * _DDGI_ProbeSpacing.xyz;
}

float3 DDGIGetProbeWorldPosition(int3 coordinate)
{
    return mul(_DDGI_VolumeToWorld, float4(DDGIGetProbeLocalPosition(coordinate), 1.0)).xyz;
}

float2 DDGIGetAtlasUv(
    int probeIndex,
    float2 octahedralUv,
    float interiorResolution,
    float tileSize,
    float2 inverseAtlasResolution)
{
    int tilesPerRow = (int)_DDGI_AtlasTileCounts.x;
    int2 tileCoordinate = int2(
        probeIndex % tilesPerRow,
        probeIndex / tilesPerRow);
    float2 atlasPixel = (float2)tileCoordinate * tileSize +
        1.0 +
        octahedralUv * interiorResolution;
    return atlasPixel * inverseAtlasResolution;
}

float3 DDGISampleProbeIrradiance(int probeIndex, float3 normalWS)
{
    float2 octahedralUv = DDGIEncodeOctahedralDirection(normalize(normalWS));
    float2 atlasUv = DDGIGetAtlasUv(
        probeIndex,
        octahedralUv,
        DDGI_IRRADIANCE_RESOLUTION,
        DDGI_IRRADIANCE_TILE_SIZE,
        _DDGI_IrradianceAtlasResolution.zw);
    return SAMPLE_TEXTURE2D_LOD(
        DDGI_IRRADIANCE_TEXTURE,
        DDGI_IRRADIANCE_SAMPLER,
        atlasUv,
        0).rgb;
}

float2 DDGISampleProbeDistanceMoments(
    int probeIndex,
    float3 probeToPointDirection)
{
    float2 octahedralUv = DDGIEncodeOctahedralDirection(
        normalize(probeToPointDirection));
    float2 atlasUv = DDGIGetAtlasUv(
        probeIndex,
        octahedralUv,
        DDGI_DISTANCE_RESOLUTION,
        DDGI_DISTANCE_TILE_SIZE,
        _DDGI_DistanceAtlasResolution.zw);
    return SAMPLE_TEXTURE2D_LOD(
        _DDGI_DistanceMomentsAtlas,
        sampler_DDGI_DistanceMomentsAtlas,
        atlasUv,
        0).rg;
}

float DDGIGetTrilinearWeight(float3 alpha, int3 probeOffset)
{
    float3 axisWeights = lerp(1.0 - alpha, alpha, (float3)probeOffset);
    axisWeights = max(axisWeights, _DDGI_MinimumProbeWeight.xxx);
    return axisWeights.x * axisWeights.y * axisWeights.z;
}

float DDGIGetDirectionWeight(
    float3 surfaceNormal,
    float3 pointToProbeDirection)
{
    float cosine = dot(surfaceNormal, pointToProbeDirection);
    float remappedCosine = (cosine + 1.0) * 0.5;
    return remappedCosine * remappedCosine + 0.2;
}

float DDGIGetChebyshevWeight(float distanceToProbe, float2 moments)
{
    float mean = moments.x;
    if (distanceToProbe <= mean)
        return 1.0;

    float variance = max(
        moments.y - mean * mean,
        _DDGI_MinimumDistanceVariance);
    float difference = distanceToProbe - mean;
    float visibility = variance / (variance + difference * difference);
    return visibility * visibility * visibility;
}

bool DDGIIsInsideVolume(float3 continuousProbeCoordinate, int3 probeCounts)
{
    return all(continuousProbeCoordinate >= 0.0) &&
        all(continuousProbeCoordinate <= (float3)(probeCounts - 1));
}

float DDGIGetVolumeBlendWeight(float3 worldPosition)
{
    float3 localPosition = mul(_DDGI_WorldToVolume, float4(worldPosition, 1.0)).xyz;
    float3 minimum = _DDGI_MinimumLocalProbePosition.xyz;
    float3 maximum = minimum + (_DDGI_ProbeCounts.xyz - 1.0) * _DDGI_ProbeSpacing.xyz;
    float3 localEdgeDistance = min(localPosition - minimum, maximum - localPosition);
    // Rows of the inverse transform convert local plane distances to world distances,
    // including nonuniform scale and parent-induced shear.
    float3 inversePlaneScale = float3(
        length(_DDGI_WorldToVolume[0].xyz),
        length(_DDGI_WorldToVolume[1].xyz),
        length(_DDGI_WorldToVolume[2].xyz));
    float3 worldEdgeDistance = localEdgeDistance / max(inversePlaneScale, 1e-6);
    float distanceToEdge = min(worldEdgeDistance.x, min(worldEdgeDistance.y, worldEdgeDistance.z));
    return saturate(distanceToEdge / max(_DDGI_BoundaryBlendDistance, 1e-6));
}

float3 DDGISampleIrradiance(
    float3 worldPosition,
    float3 surfaceNormal,
    float3 viewDirection)
{
    int3 probeCounts = (int3)_DDGI_ProbeCounts.xyz;
    float3 surfaceLocalPosition = mul(_DDGI_WorldToVolume, float4(worldPosition, 1.0)).xyz;
    float3 surfaceCoordinate =
        (surfaceLocalPosition - _DDGI_MinimumLocalProbePosition.xyz) / _DDGI_ProbeSpacing.xyz;
    if (!DDGIIsInsideVolume(surfaceCoordinate, probeCounts))
        return 0.0;
    float3 normalWS = normalize(surfaceNormal);
    float3 visibilityPosition = worldPosition +
        normalWS * _DDGI_SurfaceNormalBias +
        normalize(viewDirection) * _DDGI_SurfaceViewBias;
    float3 localPosition = mul(
        _DDGI_WorldToVolume,
        float4(visibilityPosition, 1.0)).xyz;
    float3 continuousCoordinate =
        (localPosition - _DDGI_MinimumLocalProbePosition.xyz) /
        _DDGI_ProbeSpacing.xyz;

    // Bias affects visibility but must not abruptly exclude points near a volume edge.
    continuousCoordinate = clamp(continuousCoordinate, 0.0, (float3)(probeCounts - 1));

    int3 baseCoordinate = min(
        (int3)floor(continuousCoordinate),
        max(probeCounts - 2, 0));
    float3 alpha = saturate(continuousCoordinate - (float3)baseCoordinate);
    float3 weightedIrradiance = 0.0;
    float totalWeight = 0.0;

    [unroll]
    for (int cornerIndex = 0; cornerIndex < 8; ++cornerIndex)
    {
        int3 probeOffset = DDGI_PROBE_OFFSETS[cornerIndex];
        int3 probeCoordinate = min(baseCoordinate + probeOffset, probeCounts - 1);
        int probeIndex = DDGIFlattenProbeCoordinate(probeCoordinate, probeCounts);
        float3 probeWorldPosition = DDGIGetProbeWorldPosition(probeCoordinate);
        float3 pointToProbe = probeWorldPosition - worldPosition;
        float3 pointToProbeDirection = pointToProbe / max(length(pointToProbe), 1e-5);
        float3 probeToVisibilityPoint = visibilityPosition - probeWorldPosition;
        float distanceToProbe = length(probeToVisibilityPoint);
        float3 probeToVisibilityDirection =
            probeToVisibilityPoint / max(distanceToProbe, 1e-5);

        float trilinearWeight = DDGIGetTrilinearWeight(alpha, probeOffset);
        float directionWeight = DDGIGetDirectionWeight(
            normalWS,
            pointToProbeDirection);
        float2 distanceMoments = DDGISampleProbeDistanceMoments(
            probeIndex,
            probeToVisibilityDirection);
        float chebyshevWeight = DDGIGetChebyshevWeight(
            distanceToProbe,
            distanceMoments);
        float probeWeight = trilinearWeight * directionWeight * chebyshevWeight;
        if (probeWeight < _DDGI_MinimumProbeWeight)
            continue;

        weightedIrradiance +=
            DDGISampleProbeIrradiance(probeIndex, normalWS) * probeWeight;
        totalWeight += probeWeight;
    }

    return weightedIrradiance / max(totalWeight, _DDGI_MinimumProbeWeight);
}

float3 DDGIEvaluateIndirectDiffuse(
    float3 worldPosition,
    float3 surfaceNormal,
    float3 viewDirection,
    float3 albedo)
{
    float3 irradiance = DDGISampleIrradiance(
        worldPosition,
        surfaceNormal,
        viewDirection);
    return irradiance * saturate(albedo) *
        (_DDGI_IndirectDiffuseIntensity / DDGI_PI);
}

#endif
