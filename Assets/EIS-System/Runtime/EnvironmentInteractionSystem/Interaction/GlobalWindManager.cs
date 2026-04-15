using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class GlobalWindManager : MonoBehaviour
{
    [Header("Wind")]
    [SerializeField] private Vector3 _windDirection = new Vector3(1f, 0f, 0f);
    [SerializeField] private float _windSpeed = 1.0f;
    [SerializeField] private float _windNoiseScale = 0.2f;

    private static readonly int IdGlobalWindDirection = Shader.PropertyToID("_GlobalWindDirection");
    private static readonly int IdGlobalWindSpeed = Shader.PropertyToID("_GlobalWindSpeed");
    private static readonly int IdGlobalWindNoiseScale = Shader.PropertyToID("_GlobalWindNoiseScale");

    private Vector4 _lastDir = new Vector4(float.NaN, 0f, 0f, 0f);
    private float _lastSpeed = float.NaN;
    private float _lastNoiseScale = float.NaN;

    private void OnEnable()
    {
        PushGlobals(force: true);
    }

    private void OnValidate()
    {
        PushGlobals(force: true);
    }

    private void Update()
    {
        PushGlobals(force: false);
    }

    private void PushGlobals(bool force)
    {
        Vector3 dir3 = _windDirection;
        dir3.y = 0f;
        if (dir3.sqrMagnitude < 0.000001f)
        {
            dir3 = Vector3.right;
        }
        dir3.Normalize();

        Vector4 dir = new Vector4(dir3.x, 0f, dir3.z, 0f);
        float speed = Mathf.Max(0f, _windSpeed);
        float noiseScale = Mathf.Max(0.0001f, _windNoiseScale);

        if (force || dir != _lastDir)
        {
            Shader.SetGlobalVector(IdGlobalWindDirection, dir);
            _lastDir = dir;
        }

        if (force || !Mathf.Approximately(speed, _lastSpeed))
        {
            Shader.SetGlobalFloat(IdGlobalWindSpeed, speed);
            _lastSpeed = speed;
        }

        if (force || !Mathf.Approximately(noiseScale, _lastNoiseScale))
        {
            Shader.SetGlobalFloat(IdGlobalWindNoiseScale, noiseScale);
            _lastNoiseScale = noiseScale;
        }
    }
}
