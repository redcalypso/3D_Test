// GrassInstance.cs
using System;
using UnityEngine;

[Serializable]
public struct GrassInstance
{
    public Vector3 position;   // 블레이드 루트 월드 좌표
    public float scale;      // Uniform scale

    public float press01;    // B * A 값 (0~1)
    public uint variantId;  // 모양/텍스처 구분용 (0~15 정도)
}