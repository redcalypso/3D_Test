using UnityEngine;

[CreateAssetMenu(menuName = "Trolls/Scatter/Profiles/Pebble", fileName = "PebbleScatterProfile")]
public sealed class PebbleScatterProfileSO : ScatterLayerProfileSO
{
    [Header("Displacement")]
    [Min(0f)] public float pushStrength = 0.08f;
    [Min(0f)] public float maxDisplacement = 0.20f;
    [Min(0.0001f)] public float pebbleRadius = 0.02f;
    [Min(0f)] public float rollSpeed = 0.4f;
    [Min(0f)] public float rollAngularSpeed = 720f;
    [Min(0f)] [Tooltip("Resistance to further pushing. Higher = pebbles settle faster.")]
    public float friction = 2f;
    [Tooltip("Maps input strength (X: 0~1) to push multiplier (Y: 0~1). Use to control how much pebbles react to weak vs strong stamps.")]
    public AnimationCurve strengthResponse = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [Tooltip("Maps normalized distance from stamp center (X: 0=center, 1=edge) to push multiplier (Y: 0~1).")]
    public AnimationCurve distanceFalloff = new AnimationCurve(
        new Keyframe(0f, 1f, 0f, 0f),
        new Keyframe(0.25f, 0.6f, -1.5f, -1.5f),
        new Keyframe(0.5f, 0.05f, -0.5f, -0.2f),
        new Keyframe(1f, 0f, 0f, 0f));
    [Range(0f, 1f)] [Tooltip("Minimum effective push force to move a pebble. Below this, pebble ignores the stamp.")]
    public float strengthThreshold = 0.05f;

    [Header("Visual")]
    public Color baseColor = Color.gray;
    [Range(0f, 0.3f)] public float colorVariation = 0.15f;
    [Min(0f)] public float bounceHeight = 0.02f;
    [Min(0f)] public float bounceSpeed = 8f;
    [Min(0f)] public float bounceDamping = 4f;
    [Min(0f)] public float shaderPushScale = 0.02f;

    private void Reset()
    {
        surfaceType = ScatterSurfaceType.Pebble;
    }
}
