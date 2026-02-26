#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public sealed class ScatterBrushToolWindow : EditorWindow
{
    private enum Mode { Paint, Erase, Scale }
    private enum ScaleMode { TowardTarget, Additive }

    [Serializable]
    private sealed class ScatterChunkClipboard
    {
        public int surfaceType;
        public float chunkSize;
        public float cellSize;
        public int variationCount;
        public uint globalSeed;
        public float scaleMin;
        public float scaleMax;
        public List<CellRecord> cells;
    }

    [FilePath("ProjectSettings/ScatterBrushToolWindowState.asset", FilePathAttribute.Location.ProjectFolder)]
    private sealed class BrushState : ScriptableSingleton<BrushState>
    {
        public Component targetField;
        public ScatterSurfaceType surfaceType = ScatterSurfaceType.Grass;

        public bool toolEnabled = true;
        public bool useModifierShortcuts = true;

        public Mode mode = Mode.Paint;
        public float radius = 2.0f;
        public float density = 1.0f;

        public float strength = 1.0f;
        public ScaleMode scaleMode = ScaleMode.TowardTarget;
        public float targetScale01 = 0.5f;
        public float scaleDelta01 = 0.15f;

        public bool rerollOnPaint;

        public float dragStepMeters = 0.25f;
        public bool previewCells = true;
        public int previewMax = 600;

        public void SaveState()
        {
            Save(true);
        }
    }

    private readonly Dictionary<int, int> _keyToIndex = new Dictionary<int, int>(4096);
    private static readonly ScatterSurfaceType[] s_surfaceValues = (ScatterSurfaceType[])Enum.GetValues(typeof(ScatterSurfaceType));
    private static readonly string[] s_surfaceLabels = Array.ConvertAll(s_surfaceValues, v => v.ToString());

    private bool _strokeActive;
    private bool _hasLastApplied;
    private Vector3 _lastAppliedLocal;

    [MenuItem("Tools/MIDO/Scatter Brush Tool")]
    public static void Open()
    {
        GetWindow<ScatterBrushToolWindow>("Scatter Brush Tool");
    }

    public static void OpenFromGrassMenu()
    {
        GetWindow<ScatterBrushToolWindow>("Scatter Brush Tool");
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        SceneView.duringSceneGui += OnSceneGUI;
        TryAutoAssignFromSelection();
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        EndStroke();
    }

    private void OnSelectionChange()
    {
        TryAutoAssignFromSelection();
        Repaint();
    }

    private void TryAutoAssignFromSelection()
    {
        if (BrushState.instance.targetField != null)
            return;

        if (Selection.activeGameObject == null)
            return;

        BrushState.instance.targetField = ResolveFieldComponent(Selection.activeGameObject);
        BrushState.instance.SaveState();
    }

    private void OnGUI()
    {
        var s = BrushState.instance;
        bool changed = false;

        using (var cc = new EditorGUI.ChangeCheckScope())
        {
            s.targetField = (Component)EditorGUILayout.ObjectField("Target Field", s.targetField, typeof(Component), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Use Selection", GUILayout.Width(120f)) && Selection.activeGameObject != null)
                {
                    s.targetField = ResolveFieldComponent(Selection.activeGameObject);
                    changed = true;
                }

                if (GUILayout.Button("Ping", GUILayout.Width(70f)) && s.targetField != null)
                {
                    EditorGUIUtility.PingObject(s.targetField);
                }
            }

            int selectedSurfaceIndex = Array.IndexOf(s_surfaceValues, s.surfaceType);
            if (selectedSurfaceIndex < 0) selectedSurfaceIndex = 0;
            int nextSurfaceIndex = GUILayout.Toolbar(selectedSurfaceIndex, s_surfaceLabels);
            s.surfaceType = s_surfaceValues[Mathf.Clamp(nextSurfaceIndex, 0, s_surfaceValues.Length - 1)];

            s.toolEnabled = EditorGUILayout.Toggle("Enable Tool", s.toolEnabled);
            s.useModifierShortcuts = EditorGUILayout.Toggle("Use Shift/Ctrl", s.useModifierShortcuts);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Brush", EditorStyles.boldLabel);

            s.mode = (Mode)EditorGUILayout.EnumPopup("Mode", s.mode);
            s.radius = EditorGUILayout.Slider("Radius", s.radius, 0.25f, 10f);
            s.density = EditorGUILayout.Slider("Density", s.density, 0f, 1f);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Scale", EditorStyles.boldLabel);
            s.scaleMode = (ScaleMode)EditorGUILayout.EnumPopup("Scale Mode", s.scaleMode);
            s.strength = EditorGUILayout.Slider("Strength", s.strength, 0f, 1f);

            if (s.scaleMode == ScaleMode.TowardTarget)
                s.targetScale01 = EditorGUILayout.Slider("Target Scale (01)", s.targetScale01, 0f, 1f);
            else
                s.scaleDelta01 = EditorGUILayout.Slider("Delta (01)", s.scaleDelta01, -1f, 1f);

            EditorGUILayout.Space(4);
            s.rerollOnPaint = EditorGUILayout.Toggle("Re-roll on Paint", s.rerollOnPaint);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Editor QoL", EditorStyles.boldLabel);
            s.dragStepMeters = EditorGUILayout.Slider("Drag Step (m)", s.dragStepMeters, 0.01f, 2f);
            s.previewCells = EditorGUILayout.Toggle("Preview Cells", s.previewCells);
            using (new EditorGUI.DisabledScope(!s.previewCells))
                s.previewMax = EditorGUILayout.IntSlider("Preview Max", s.previewMax, 50, 3000);

            EditorGUILayout.Space(6);
            DrawCopyPasteUI(s);

            EditorGUILayout.HelpBox(
                "SceneView Input\n" +
                " - LMB Drag: Apply Brush\n" +
                " - Shift + LMB: Erase (when shortcut enabled)\n" +
                " - Ctrl + LMB: Scale (when shortcut enabled)",
                MessageType.Info
            );

            changed |= cc.changed;
        }

        if (changed)
        {
            s.SaveState();
            SceneView.RepaintAll();
        }

        DrawStateInfo(s);
    }

    private void DrawStateInfo(BrushState s)
    {
        if (s.targetField == null)
        {
            EditorGUILayout.HelpBox("Target Field(ScatterField)�� �����ϴ�.", MessageType.Warning);
            return;
        }

        var chunk = ResolveChunk(s.targetField, s.surfaceType);
        if (chunk == null)
        {
            EditorGUILayout.HelpBox($"Ÿ�� �ʵ忡�� {s.surfaceType} Chunk�� ã�� ���߽��ϴ�.", MessageType.Warning);
            return;
        }

        EditorGUILayout.LabelField("Chunk Asset", chunk.name);
        EditorGUILayout.LabelField("Surface", chunk.EffectiveSurfaceType.ToString());
        EditorGUILayout.LabelField("Cell Count", (chunk.cells != null ? chunk.cells.Count : 0).ToString());
    }

    private void DrawCopyPasteUI(BrushState s)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!HasValidChunk(s)))
            {
                if (GUILayout.Button("Copy Chunk", GUILayout.Height(24f)))
                    CopyChunkToClipboard(ResolveChunk(s.targetField, s.surfaceType));
            }

            using (new EditorGUI.DisabledScope(!HasValidChunk(s)))
            {
                if (GUILayout.Button("Paste Chunk", GUILayout.Height(24f)))
                    PasteChunkFromClipboard(ResolveChunk(s.targetField, s.surfaceType));
            }

            using (new EditorGUI.DisabledScope(!HasValidChunk(s)))
            {
                if (GUILayout.Button("Clear Cells", GUILayout.Height(24f)))
                    ClearCells(ResolveChunk(s.targetField, s.surfaceType));
            }
        }
    }

    private static bool HasValidChunk(BrushState s)
    {
        return s.targetField != null && ResolveChunk(s.targetField, s.surfaceType) != null;
    }

    private static void CopyChunkToClipboard(ScatterChunkSO chunk)
    {
        if (chunk == null)
            return;

        var data = new ScatterChunkClipboard
        {
            surfaceType = (int)chunk.EffectiveSurfaceType,
            chunkSize = chunk.chunkSize,
            cellSize = chunk.cellSize,
            variationCount = chunk.EffectiveVariationCount,
            globalSeed = chunk.EffectiveGlobalSeed,
            scaleMin = chunk.EffectiveScaleMin,
            scaleMax = chunk.EffectiveScaleMax,
            cells = chunk.cells != null ? new List<CellRecord>(chunk.cells) : new List<CellRecord>()
        };

        EditorGUIUtility.systemCopyBuffer = JsonUtility.ToJson(data);
    }

    private static void PasteChunkFromClipboard(ScatterChunkSO chunk)
    {
        if (chunk == null)
            return;

        var raw = EditorGUIUtility.systemCopyBuffer;
        if (string.IsNullOrEmpty(raw))
        {
            EditorUtility.DisplayDialog("Scatter Brush Tool", "Ŭ�����尡 ��� �ֽ��ϴ�.", "OK");
            return;
        }

        ScatterChunkClipboard data;
        try
        {
            data = JsonUtility.FromJson<ScatterChunkClipboard>(raw);
        }
        catch
        {
            data = null;
        }

        if (data == null)
        {
            EditorUtility.DisplayDialog("Scatter Brush Tool", "Ŭ������ ������ ������ �ùٸ��� �ʽ��ϴ�.", "OK");
            return;
        }

        Undo.RecordObject(chunk, "Paste Scatter Chunk");

        chunk.surfaceType = (ScatterSurfaceType)Mathf.Clamp(data.surfaceType, 0, s_surfaceValues.Length - 1);
        chunk.chunkSize = Mathf.Max(0.01f, data.chunkSize);
        chunk.cellSize = Mathf.Max(0.01f, data.cellSize);
        chunk.variationCount = Mathf.Clamp(data.variationCount, 1, 16);
        chunk.globalSeed = data.globalSeed;
        chunk.scaleMin = data.scaleMin;
        chunk.scaleMax = data.scaleMax;
        chunk.cells = data.cells != null ? new List<CellRecord>(data.cells) : new List<CellRecord>();
        chunk.Touch();
        EditorUtility.SetDirty(chunk);
        AssetDatabase.SaveAssets();
        SceneView.RepaintAll();
    }

    private static void ClearCells(ScatterChunkSO chunk)
    {
        if (chunk == null)
            return;

        Undo.RecordObject(chunk, "Clear Scatter Cells");
        chunk.cells.Clear();
        chunk.Touch();
        EditorUtility.SetDirty(chunk);
        SceneView.RepaintAll();
    }

    private void OnSceneGUI(SceneView view)
    {
        var s = BrushState.instance;
        var field = s.targetField;
        var grassChunk = ResolveChunk(field, s.surfaceType);

        if (!s.toolEnabled || field == null || grassChunk == null)
        {
            EndStroke();
            return;
        }

        if (!TryGetGroundHit(field.transform, out var hitWorld))
            return;

        var e = Event.current;

        if (s.useModifierShortcuts)
            UpdateModeFromModifiers(s, e);

        var brushCenterLocal = field.transform.InverseTransformPoint(hitWorld);

        RebuildKeyMap(grassChunk);

        DrawBrushGizmo(s.mode, hitWorld, s.radius);

        if (s.previewCells && e.type == EventType.Repaint)
            DrawCellPreview(field.transform, grassChunk, s, brushCenterLocal);

        if (e.alt)
            return;

        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            BeginStroke(grassChunk);
            ApplyBrushWithStep(field.transform, grassChunk, s, brushCenterLocal, force: true);
            e.Use();
        }
        else if (e.type == EventType.MouseDrag && e.button == 0)
        {
            ApplyBrushWithStep(field.transform, grassChunk, s, brushCenterLocal, force: false);
            e.Use();
        }
        else if (e.type == EventType.MouseUp && e.button == 0)
        {
            EndStroke();
        }
    }

    private static void UpdateModeFromModifiers(BrushState s, Event e)
    {
        if (e.control) s.mode = Mode.Scale;
        else if (e.shift) s.mode = Mode.Erase;
        else s.mode = Mode.Paint;
    }

    private void BeginStroke(ScatterChunkSO grass)
    {
        if (_strokeActive)
            return;

        _strokeActive = true;
        _hasLastApplied = false;

        Undo.RecordObject(grass, "Scatter Brush Stroke");
    }

    private void EndStroke()
    {
        _strokeActive = false;
        _hasLastApplied = false;
    }

    private void ApplyBrushWithStep(Transform fieldTransform, ScatterChunkSO grass, BrushState s, Vector3 brushCenterLocal, bool force)
    {
        if (!force && _hasLastApplied)
        {
            float step = Mathf.Max(0.0001f, s.dragStepMeters);
            if ((brushCenterLocal - _lastAppliedLocal).sqrMagnitude < step * step)
                return;
        }

        ApplyBrush(fieldTransform, grass, s, brushCenterLocal);

        _lastAppliedLocal = brushCenterLocal;
        _hasLastApplied = true;
    }

    private void ApplyBrush(Transform fieldTransform, ScatterChunkSO grass, BrushState s, Vector3 brushCenterLocal)
    {
        var cells = grass.cells;
        int cellsPerAxis = grass.CellsPerAxis;

        float half = grass.chunkSize * 0.5f;
        float cellSize = grass.cellSize;

        float bx = brushCenterLocal.x + half;
        float bz = brushCenterLocal.z + half;

        int minCx = Mathf.FloorToInt((bx - s.radius) / cellSize);
        int maxCx = Mathf.FloorToInt((bx + s.radius) / cellSize);
        int minCy = Mathf.FloorToInt((bz - s.radius) / cellSize);
        int maxCy = Mathf.FloorToInt((bz + s.radius) / cellSize);

        minCx = Mathf.Clamp(minCx, 0, cellsPerAxis - 1);
        maxCx = Mathf.Clamp(maxCx, 0, cellsPerAxis - 1);
        minCy = Mathf.Clamp(minCy, 0, cellsPerAxis - 1);
        maxCy = Mathf.Clamp(maxCy, 0, cellsPerAxis - 1);

        bool changed = false;

        List<int> removeIndices = null;
        List<CellRecord> addRecords = null;

        float jitterRadius = cellSize * 0.35f;

        for (int cy = minCy; cy <= maxCy; cy++)
        for (int cx = minCx; cx <= maxCx; cx++)
        {
            uint seed = ScatterHash.MakeSeed(grass.EffectiveGlobalSeed, cx, cy);
            Vector2 jitter = ScatterHash.Jitter(seed, jitterRadius);

            float centerX = (cx + 0.5f) * cellSize - half;
            float centerZ = (cy + 0.5f) * cellSize - half;
            Vector3 local = new Vector3(centerX + jitter.x, 0f, centerZ + jitter.y);

            float dist = Vector2.Distance(new Vector2(local.x, local.z), new Vector2(brushCenterLocal.x, brushCenterLocal.z));
            if (dist > s.radius)
                continue;

            float mask = 1f - Mathf.Clamp01(dist / Mathf.Max(0.0001f, s.radius));

            int key = CellRecord.Key(cx, cy, cellsPerAxis);

            if (s.mode == Mode.Paint)
            {
                float p = s.density * mask;
                if (p <= 0f)
                    continue;
                if (UnityEngine.Random.value > p)
                    continue;

                if (_keyToIndex.ContainsKey(key))
                {
                    if (s.rerollOnPaint)
                    {
                        int idx = _keyToIndex[key];
                        var rec = cells[idx];
                        rec.variant = ScatterHash.Variant(seed, grass.EffectiveVariationCount);
                        cells[idx] = rec;
                        changed = true;
                    }
                }
                else
                {
                    var rec = new CellRecord
                    {
                        cx = (ushort)cx,
                        cy = (ushort)cy,
                        variant = ScatterHash.Variant(seed, grass.EffectiveVariationCount),
                        scaleByte = CellRecord.Encode01(s.targetScale01)
                    };
                    (addRecords ??= new List<CellRecord>(64)).Add(rec);
                    changed = true;
                }
            }
            else if (s.mode == Mode.Erase)
            {
                if (_keyToIndex.TryGetValue(key, out int idx))
                {
                    (removeIndices ??= new List<int>(64)).Add(idx);
                    changed = true;
                }
            }
            else
            {
                if (_keyToIndex.TryGetValue(key, out int idx))
                {
                    var rec = cells[idx];
                    float cur01 = rec.scaleByte / 255f;

                    float next01;
                    if (s.scaleMode == ScaleMode.TowardTarget)
                        next01 = Mathf.Lerp(cur01, s.targetScale01, s.strength * mask);
                    else
                        next01 = cur01 + (s.scaleDelta01 * s.strength * mask);

                    rec.scaleByte = CellRecord.Encode01(Mathf.Clamp01(next01));
                    cells[idx] = rec;
                    changed = true;
                }
            }
        }

        if (removeIndices != null && removeIndices.Count > 0)
        {
            removeIndices.Sort();
            for (int i = removeIndices.Count - 1; i >= 0; i--)
            {
                int idx = removeIndices[i];
                if ((uint)idx < (uint)cells.Count)
                    cells.RemoveAt(idx);
            }
        }

        if ((removeIndices != null && removeIndices.Count > 0) || (addRecords != null && addRecords.Count > 0))
        {
            _keyToIndex.Clear();
            for (int i = 0; i < cells.Count; i++)
            {
                var r = cells[i];
                _keyToIndex[CellRecord.Key(r.cx, r.cy, cellsPerAxis)] = i;
            }
        }

        if (addRecords != null && addRecords.Count > 0)
        {
            for (int i = 0; i < addRecords.Count; i++)
            {
                var rec = addRecords[i];
                int k = CellRecord.Key(rec.cx, rec.cy, cellsPerAxis);
                if (_keyToIndex.ContainsKey(k))
                    continue;

                cells.Add(rec);
                _keyToIndex[k] = cells.Count - 1;
            }
        }

        if (changed)
        {
            grass.Touch();
            EditorUtility.SetDirty(grass);
            SceneView.RepaintAll();
        }
    }

    private void RebuildKeyMap(ScatterChunkSO grass)
    {
        _keyToIndex.Clear();

        var cells = grass.cells;
        int cellsPerAxis = grass.CellsPerAxis;

        for (int i = 0; i < cells.Count; i++)
        {
            var r = cells[i];
            _keyToIndex[CellRecord.Key(r.cx, r.cy, cellsPerAxis)] = i;
        }
    }

    private static bool TryGetGroundHit(Transform fieldTransform, out Vector3 hitWorld)
    {
        Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        Plane plane = new Plane(Vector3.up, new Vector3(0f, fieldTransform.position.y, 0f));
        if (plane.Raycast(ray, out float enter))
        {
            hitWorld = ray.GetPoint(enter);
            return true;
        }

        hitWorld = default;
        return false;
    }

    private static void DrawBrushGizmo(Mode mode, Vector3 hitWorld, float radius)
    {
        Color c = mode switch
        {
            Mode.Paint => new Color(0.2f, 1f, 0.2f, 0.6f),
            Mode.Erase => new Color(1f, 0.2f, 0.2f, 0.6f),
            _ => new Color(0.2f, 0.6f, 1f, 0.6f)
        };

        Handles.color = c;
        Handles.DrawWireDisc(hitWorld, Vector3.up, radius);
    }

    private static void DrawCellPreview(Transform fieldTransform, ScatterChunkSO grass, BrushState s, Vector3 brushCenterLocal)
    {
        int cellsPerAxis = grass.CellsPerAxis;
        float cellSize = grass.cellSize;
        float half = grass.chunkSize * 0.5f;

        float bx = brushCenterLocal.x + half;
        float bz = brushCenterLocal.z + half;

        int minCx = Mathf.FloorToInt((bx - s.radius) / cellSize);
        int maxCx = Mathf.FloorToInt((bx + s.radius) / cellSize);
        int minCy = Mathf.FloorToInt((bz - s.radius) / cellSize);
        int maxCy = Mathf.FloorToInt((bz + s.radius) / cellSize);

        minCx = Mathf.Clamp(minCx, 0, cellsPerAxis - 1);
        maxCx = Mathf.Clamp(maxCx, 0, cellsPerAxis - 1);
        minCy = Mathf.Clamp(minCy, 0, cellsPerAxis - 1);
        maxCy = Mathf.Clamp(maxCy, 0, cellsPerAxis - 1);

        float jitterRadius = cellSize * 0.35f;

        Handles.color = s.mode switch
        {
            Mode.Paint => new Color(0.2f, 1f, 0.2f, 0.75f),
            Mode.Erase => new Color(1f, 0.2f, 0.2f, 0.75f),
            _ => new Color(0.2f, 0.6f, 1f, 0.75f)
        };

        int drawn = 0;

        for (int cy = minCy; cy <= maxCy; cy++)
        for (int cx = minCx; cx <= maxCx; cx++)
        {
            if (drawn >= s.previewMax)
                return;

            uint seed = ScatterHash.MakeSeed(grass.EffectiveGlobalSeed, cx, cy);
            Vector2 jitter = ScatterHash.Jitter(seed, jitterRadius);

            float centerX = (cx + 0.5f) * cellSize - half;
            float centerZ = (cy + 0.5f) * cellSize - half;

            Vector3 local = new Vector3(centerX + jitter.x, 0f, centerZ + jitter.y);

            float dist = Vector2.Distance(new Vector2(local.x, local.z), new Vector2(brushCenterLocal.x, brushCenterLocal.z));
            if (dist > s.radius)
                continue;

            Vector3 world = fieldTransform.TransformPoint(local);

            float dotRadius = Mathf.Max(0.03f, cellSize * 0.05f);
            Handles.DrawSolidDisc(world, Vector3.up, dotRadius);
            drawn++;
        }
    }

    private static Component ResolveFieldComponent(GameObject go)
    {
        if (go == null)
            return null;

        var scatterField = go.GetComponent<ScatterField>();
        if (scatterField != null)
            return scatterField;

        return null;
    }

    private static ScatterChunkSO ResolveChunk(Component field, ScatterSurfaceType surfaceType)
    {
        if (field == null)
            return null;
        if (field is ScatterField scatterField)
        {
            if (scatterField.primaryChunk != null && scatterField.primaryChunk.EffectiveSurfaceType == surfaceType)
                return scatterField.primaryChunk;

            if (scatterField.layers != null)
            {
                for (int i = 0; i < scatterField.layers.Count; i++)
                {
                    var layer = scatterField.layers[i];
                    if (layer != null && layer.EffectiveSurfaceType == surfaceType)
                        return layer;
                }
            }
        }

        return null;
    }
}
#endif


