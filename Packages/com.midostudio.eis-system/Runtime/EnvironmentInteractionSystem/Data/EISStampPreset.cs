using UnityEngine;

[CreateAssetMenu(menuName = "Trolls/Interaction/EIS Stamp Preset", fileName = "EISStampPreset")]
public sealed class EISStampPreset : ScriptableObject
{
    public enum StampMode
    {
        Additive = 0,
        Max = 1
    }

    public enum DirectionMode
    {
        Local = 0,
        World = 1,
        Radial = 2
    }

    [Header("Base")]
    public Texture2D maskTex;
    public float baseSize = 1.0f;
    public float baseStrength = 1.0f;
    public StampMode blendMode = StampMode.Additive;
    public DirectionMode directionMode = DirectionMode.Local;

    [Header("Channel Multipliers")]
    public float forceMultiplier = 1.0f;   // RG
    public float pressMultiplier = 1.0f;   // B
    public float weightMultiplier = 1.0f;  // A

    [Header("Stability")]
    public float maxForce = 1.0f;

    [Header("Arc Mask")]
    public bool useArcMask = false;
    [Range(0f, 360f)] public float arcAngle = 30f;
    [Min(0f)] public float arcSoftness = 4f;
}
