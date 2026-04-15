using UnityEngine;

[DisallowMultipleComponent]
public sealed class EISTester_RandomWander : MonoBehaviour
{
    [SerializeField] private float _moveSpeed = 3.0f;

    private Vector3 _startPosition;
    private Vector3 _targetPosition;

    private void Awake()
    {
        _startPosition = transform.position;
        _targetPosition = GetRandomTarget();
    }

    private void Update()
    {
        Vector3 currentPosition = transform.position;
        Vector3 nextPosition = Vector3.MoveTowards(currentPosition, _targetPosition, _moveSpeed * Time.deltaTime);
        transform.position = nextPosition;

        if (Vector3.Distance(nextPosition, _targetPosition) < 0.3f)
            _targetPosition = GetRandomTarget();
    }

    private Vector3 GetRandomTarget()
    {
        float x = Random.Range(_startPosition.x - 5f, _startPosition.x + 5f);
        float z = Random.Range(_startPosition.z - 5f, _startPosition.z + 5f);
        return new Vector3(x, _startPosition.y, z);
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 center = Application.isPlaying ? _startPosition : transform.position;
        Vector3 size = new Vector3(10f, 0f, 10f);

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(center, size);
    }
}
