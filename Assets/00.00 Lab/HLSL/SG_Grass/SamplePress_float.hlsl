void SamplePress_float(float instanceID, out float press)
{
    // Keep signature for Shader Graph, but use world-space RT sampling for stable color press.

    float4x4 objectToWorld = GetObjectToWorldMatrix();
    float3 worldPos = mul(objectToWorld, float4(0.0, 0.0, 0.0, 1.0)).xyz;

    float worldSize = max(_InteractionCamData.w, 1e-5);
    float2 minXZ = _InteractionCamData.xy - worldSize * 0.5;
    float2 uv = (worldPos.xz - minXZ) / worldSize;

    if (any(uv < 0.0) || any(uv > 1.0))
    {
        press = 0.0;
        return;
    }

    float4 c = SAMPLE_TEXTURE2D_LOD(_InteractionRT, sampler_InteractionRT, uv, 0);
    float v = saturate(c.b * c.a);
    press = (v != v) ? 0.0 : v;
}
