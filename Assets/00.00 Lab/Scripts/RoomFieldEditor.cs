#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(RoomField))]
public sealed class RoomFieldEditor : Editor
{
    private enum Mode { Paint, Erase, Scale }

    private Mode _mode = Mode.Paint;
    private float _radius = 2.0f;
    private float _density = 1.0f;   // paint spawn chance
    private float _strength = 1.0f;  // scale lerp strength
    private float _targetScale01 = 0.5f; // 0..1 mapped to [min..max]
    private bool _rerollOnPaint = false;

    // Cache for faster key lookups (rebuilt each OnSceneGUI)
    private readonly Dictionary<int, int> _keyToIndex = new();

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Grass Brush (MVP)", EditorStyles.boldLabel);

        _radius = EditorGUILayout.Slider("Radius", _radius, 0.25f, 10f);
        _density = EditorGUILayout.Slider("Density", _density, 0f, 1f);
        _strength = EditorGUILayout.Slider("Strength", _strength, 0f, 1f);
        _targetScale01 = EditorGUILayout.Slider("Target Scale (01)", _targetScale01, 0f, 1f);
        _rerollOnPaint = EditorGUILayout.Toggle("Re-roll on Paint", _rerollOnPaint);

        EditorGUILayout.HelpBox("LMB drag: Paint\nShift+LMB: Erase\nCtrl+LMB: Scale", MessageType.Info);
    }

    private void OnSceneGUI()
    {
        var field = (RoomField)target;
        if (field.grassChunk == null) return;

        Event e = Event.current;
        UpdateModeFromModifiers(e);

        // Raycast to XZ plane of RoomField (no rotation assumption)
        var plane = new Plane(Vector3.up, field.transform.position);
        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        if (!plane.Raycast(ray, out float hitDist)) return;
        Vector3 hitWorld = ray.GetPoint(hitDist);

        // Convert to RoomField local XZ
        Vector3 local = field.transform.InverseTransformPoint(hitWorld);

        DrawBrushGizmo(field, hitWorld);

        if (e.type == EventType.MouseDrag && e.button == 0 && !e.alt)
        {
            ApplyBrush(field, local);
            e.Use();
        }
        else if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
        {
            ApplyBrush(field, local);
            e.Use();
        }
    }

    private void UpdateModeFromModifiers(Event e)
    {
        if (e.control) _mode = Mode.Scale;
        else if (e.shift) _mode = Mode.Erase;
        else _mode = Mode.Paint;
    }

    private void DrawBrushGizmo(RoomField field, Vector3 hitWorld)
    {
        Color c = _mode switch
        {
            Mode.Paint => new Color(0.3f, 0.9f, 0.3f, 0.9f),
            Mode.Erase => new Color(0.9f, 0.3f, 0.3f, 0.9f),
            _ => new Color(0.3f, 0.6f, 0.9f, 0.9f)
        };
        Handles.color = c;
        Handles.DrawWireDisc(hitWorld, Vector3.up, _radius);
    }

    private void ApplyBrush(RoomField field, Vector3 brushCenterLocal)
    {
        var grass = field.grassChunk;
        int cellsPerAxis = grass.CellsPerAxis;

        // Build key->index map
        _keyToIndex.Clear();
        for (int i = 0; i < grass.cells.Count; i++)
        {
            var rec = grass.cells[i];
            _keyToIndex[rec.Key(cellsPerAxis)] = i;
        }

        float cellSize = grass.cellSize;
        float jitterRadius = cellSize * 0.35f;

        // Brush bounds in cell indices (local 0..chunk)
        float half = grass.chunkSize * 0.5f;

        // We assume RoomField local origin is the center of the 32m chunk.
        // If your RoomField origin is corner-based, tell me and we¡¯ll adjust.
        float bx = brushCenterLocal.x + half;
        float bz = brushCenterLocal.z + half;

        int minCx = Mathf.FloorToInt((bx - _radius) / cellSize);
        int maxCx = Mathf.FloorToInt((bx + _radius) / cellSize);
        int minCy = Mathf.FloorToInt((bz - _radius) / cellSize);
        int maxCy = Mathf.FloorToInt((bz + _radius) / cellSize);

        minCx = Mathf.Clamp(minCx, 0, cellsPerAxis - 1);
        maxCx = Mathf.Clamp(maxCx, 0, cellsPerAxis - 1);
        minCy = Mathf.Clamp(minCy, 0, cellsPerAxis - 1);
        maxCy = Mathf.Clamp(maxCy, 0, cellsPerAxis - 1);

        bool changed = false;

        for (int cy = minCy; cy <= maxCy; cy++)
            for (int cx = minCx; cx <= maxCx; cx++)
            {
                uint seed = GrassHash.MakeSeed(grass.globalSeed, cx, cy);
                Vector2 jitter = GrassHash.Jitter(seed, jitterRadius);

                // cell center in local space
                float centerX = (cx + 0.5f) * cellSize - half;
                float centerZ = (cy + 0.5f) * cellSize - half;

                Vector2 p = new Vector2(centerX + jitter.x, centerZ + jitter.y);
                float dist = Vector2.Distance(p, new Vector2(brushCenterLocal.x, brushCenterLocal.z));
                if (dist > _radius) continue;

                float t = dist / Mathf.Max(0.0001f, _radius);
                float mask = 1f - (t * t); // simple quadratic falloff

                int key = cy * cellsPerAxis + cx;

                switch (_mode)
                {
                    case Mode.Paint:
                        {
                            float spawnChance = _density * mask;
                            float r = GrassHash.To01(GrassHash.Hash(seed ^ 0x11111111u));
                            if (r > spawnChance) break;

                            if (_keyToIndex.TryGetValue(key, out int idx))
                            {
                                if (_rerollOnPaint)
                                {
                                    var rec = grass.cells[idx];
                                    rec.variant = GrassHash.Variant(seed, grass.variationCount);
                                    grass.cells[idx] = rec;
                                    changed = true;
                                }
                            }
                            else
                            {
                                var rec = new CellRecord
                                {
                                    cx = (ushort)cx,
                                    cy = (ushort)cy,
                                    variant = GrassHash.Variant(seed, grass.variationCount),
                                    scaleByte = CellRecord.Encode01(_targetScale01) // start scale from target
                                };
                                grass.cells.Add(rec);
                                _keyToIndex[key] = grass.cells.Count - 1;
                                changed = true;
                            }
                            break;
                        }

                    case Mode.Erase:
                        {
                            if (_keyToIndex.TryGetValue(key, out int idx))
                            {
                                grass.cells.RemoveAt(idx);
                                changed = true;
                                // Rebuild map after deletes (cheap enough for MVP)
                                _keyToIndex.Clear();
                                for (int i = 0; i < grass.cells.Count; i++)
                                {
                                    var rec = grass.cells[i];
                                    _keyToIndex[rec.Key(cellsPerAxis)] = i;
                                }
                            }
                            break;
                        }

                    case Mode.Scale:
                        {
                            if (_keyToIndex.TryGetValue(key, out int idx))
                            {
                                var rec = grass.cells[idx];
                                float cur01 = rec.scaleByte / 255f;
                                float tgt01 = _targetScale01;
                                float next01 = Mathf.Lerp(cur01, tgt01, _strength * mask);
                                rec.scaleByte = CellRecord.Encode01(next01);
                                grass.cells[idx] = rec;
                                changed = true;
                            }
                            break;
                        }
                }
            }

        if (changed)
        {
            EditorUtility.SetDirty(grass);
            // If you want autosave:
            // AssetDatabase.SaveAssets();
        }
    }
}
#endif