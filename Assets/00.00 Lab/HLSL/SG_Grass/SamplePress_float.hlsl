void SamplePress_float(float instanceID, out float press)
{
    float3 worldPos = mul(GetObjectToWorldMatrix(), float4(0.0, 0.0, 0.0, 1.0)).xyz;

    float2 camXZ = _InteractionCamPosXZ.xy;
    float invWorldSize = _InteractionCamParams.z;

    float2 uv = (worldPos.xz - camXZ) * invWorldSize + 0.5;

    if (any(uv < 0.0) || any(uv > 1.0))
    {
        press = 0.0;
        return;
    }

    float4 c = SAMPLE_TEXTURE2D_LOD(_InteractionRT, sampler_InteractionRT, uv, 0);
    float v = saturate(c.b * c.a);
    press = (v != v) ? 0.0 : v;
}