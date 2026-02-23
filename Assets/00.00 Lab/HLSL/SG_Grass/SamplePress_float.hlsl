StructuredBuffer<float> _InstancePress01;
int _PressCount;

void SamplePress_float(float instanceID, float baseIndex, out float press)
{
    uint inst = (uint)round(instanceID);
    uint base = (uint)round(baseIndex);
    uint index = base + inst;

    if (index >= (uint)_PressCount) { press = 0.0; return; }

    float v = _InstancePress01[index];

    if (v < 0.0 || v > 1.0) v = 0.0;

    press = v;
}