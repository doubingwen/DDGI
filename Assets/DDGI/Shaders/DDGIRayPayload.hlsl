#ifndef DOU_DDGI_RAY_PAYLOAD_INCLUDED
#define DOU_DDGI_RAY_PAYLOAD_INCLUDED

struct DDGIRayPayload
{
    float3 positionWS;
    float hitDistance;
    float3 normalWS;
    uint hit;
    float3 albedo;
};

struct DDGIIntersectionAttributes
{
    float2 barycentrics;
};

#endif
