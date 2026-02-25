using UnityEngine;

public interface IScatterPressProvider
{
    ComputeBuffer PressBuffer { get; }
    void SetInstances(Vector4[] posScale);
}
