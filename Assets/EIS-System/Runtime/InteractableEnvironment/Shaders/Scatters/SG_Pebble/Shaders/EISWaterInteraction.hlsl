#ifndef EIS_WATER_INTERACTION_INCLUDED
#define EIS_WATER_INTERACTION_INCLUDED

// ──────────────────────────────────────────────────
// EIS Water Interaction for ShaderGraph
// All params passed via ShaderGraph Blackboard properties.
// ──────────────────────────────────────────────────

void EISWaterInteraction_half(
    UnityTexture2D InteractionRT,
    float4 CamPosXZ,
    float4 CamParams,
    float4 UVOffset,
    float3 WorldPos,
    half NormalStrength,
    half FoamStrength,
    out half3 NormalOffset,
    out half FoamBoost)
{
#ifdef SHADERGRAPH_PREVIEW
    NormalOffset = half3(0, 0, 1);
    FoamBoost = 0;
#else
    float worldSize = max(0.0001, CamParams.y);
    float2 uv = (WorldPos.xz - CamPosXZ.xy) / worldSize + 0.5;
    uv += UVOffset.xy;
    
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
    {
        NormalOffset = half3(0, 0, 1);
        FoamBoost = 0;
        return;
    }
    
    float4 data = tex2D(InteractionRT, uv);
    
    float2 dir = (data.rg - 0.5) * 2.0;
    float magnitude = length(dir);
    float press = data.b;
    
    NormalOffset = half3(
        dir.x * NormalStrength,
        dir.y * NormalStrength,
        1.0
    );
    NormalOffset = normalize(NormalOffset);
    
    FoamBoost = half(saturate(magnitude + press * 0.5) * FoamStrength);
#endif
}

void EISWaterInteraction_float(
    UnityTexture2D InteractionRT,
    float4 CamPosXZ,
    float4 CamParams,
    float4 UVOffset,
    float3 WorldPos,
    float NormalStrength,
    float FoamStrength,
    out float3 NormalOffset,
    out float FoamBoost)
{
    half3 no;
    half fb;
    EISWaterInteraction_half(
        InteractionRT, CamPosXZ, CamParams, UVOffset,
        WorldPos, (half)NormalStrength, (half)FoamStrength,
        no, fb);
    NormalOffset = float3(no);
    FoamBoost = float(fb);
}

#endif
