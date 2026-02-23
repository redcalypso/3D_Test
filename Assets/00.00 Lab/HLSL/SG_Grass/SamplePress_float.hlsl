StructuredBuffer<float> _InstancePress01;

// ShaderGraph에서 Vector1(=float)로 들어오는 걸로 통일
float _InstancePressCount;
float _BaseInstanceIndex;

void SamplePress_float(float instanceID, out float press)
{
    // instanceID는 보통 정수로 들어오지만 float이니 안전하게 처리
    uint inst = (uint)round(instanceID);
    uint baseI = (uint)round(_BaseInstanceIndex);
    uint count = (uint)round(_InstancePressCount);

    uint index = baseI + inst;

    if (index >= count)
    {
        press = 0.0;
        return;
    }

    float v = _InstancePress01[index];
    press = (v < 0.0 || v > 1.0 || v != v) ? 0.0 : v;
}