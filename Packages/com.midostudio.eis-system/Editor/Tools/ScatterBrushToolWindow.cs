#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public sealed class ScatterBrushToolWindow : EditorWindow
{
    private enum Mode { Paint, Erase, Scale }
    private enum ScaleMode { TowardTarget, Additive }
    private enum PreviewFrameRate { Off = 0, Fps30 = 30, Fps60 = 60 }

    [Serializable]
    private sealed class ScatterChunkClipboard
    {
        public int surfaceType;
        public int chunkX;
        public int chunkY;
        public float chunkSize;
        public float cellSize;
        public int variationCount;
        public uint globalSeed;
        public float scaleMin;
        public float scaleMax;
        public List<CellRecord> cells;
    }

    private readonly struct ActiveChunk
    {
        public readonly RoomScatterDataSO roomData;
        public readonly RoomScatterDataSO.SurfaceLayerData surface;
        public readonly RoomScatterDataSO.ChunkData chunk;

        public ActiveChunk(RoomScatterDataSO roomData, RoomScatterDataSO.SurfaceLayerData surface, RoomScatterDataSO.ChunkData chunk)
        {
            this.roomData = roomData;
            this.surface = surface;
            this.chunk = chunk;
        }

        public bool IsValid => roomData != null && surface != null && chunk != null;
    }

    [FilePath("ProjectSettings/ScatterBrushToolWindowState.asset", FilePathAttribute.Location.ProjectFolder)]
    private sealed class BrushState : ScriptableSingleton<BrushState>
    {
        public Component targetField;
        public ScatterSurfaceType surfaceType = ScatterSurfaceType.Grass;

        public bool toolEnabled = true;
        public bool useModifierShortcuts = true;
        public bool paintModeEnabled = false;
        public bool capsScaleMode = false;
        public bool autoCreateChunkOnPaint = true;
        public bool showEditorPreview = true;
        public bool useMeshSurfaceHit = true;
        public LayerMask meshSurfaceLayerMask = ~0;
        [Range(0f, 89f)] public float maxSurfaceSlopeDeg = 60f;
        [Min(1f)] public float meshHitDistance = 500f;
        public bool fallbackToFieldPlane = true;

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
        public PreviewFrameRate forcePreviewFrameRate = PreviewFrameRate.Off;

        public void SaveState()
        {
            Save(true);
        }
    }

    private readonly Dictionary<int, int> _keyToIndex = new Dictionary<int, int>(4096);
    private static readonly ScatterSurfaceType[] s_surfaceValues = (ScatterSurfaceType[])Enum.GetValues(typeof(ScatterSurfaceType));
    private static readonly string[] s_surfaceLabels = Array.ConvertAll(s_surfaceValues, v => v.ToString());
    private static readonly List<RoomScatterDataSO.SurfaceLayerData> s_surfaceScratch = new List<RoomScatterDataSO.SurfaceLayerData>(16);
    private static readonly List<RoomScatterDataSO.ChunkRef> s_previewChunkScratch = new List<RoomScatterDataSO.ChunkRef>(64);
    private static readonly Matrix4x4[] s_previewMatrices = new Matrix4x4[1023];
    private static readonly int IdPressColorWeight = Shader.PropertyToID("_PressColorWeight");
    private static readonly int IdBendColorWeight = Shader.PropertyToID("_BendColorWeight");
    private static readonly int IdInstancePressCount = Shader.PropertyToID("_InstancePressCount");
    private static readonly int IdPressCount = Shader.PropertyToID("_PressCount");
    private static readonly int IdBaseInstanceIndex = Shader.PropertyToID("_BaseInstanceIndex");

    private bool _strokeActive;
    private bool _hasLastApplied;
    private Vector3 _lastAppliedLocal;
    private static double s_nextSceneViewRepaintTime;
    private static GameObject s_prefabPreviewManagerGO;
    private static ScatterRenderManager s_prefabPreviewManager;
    private static ScatterField s_prefabPreviewTargetField;
    private static int s_prefabPreviewStageHandle = -1;

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
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.update += OnEditorUpdate;
        TryAutoAssignFromSelection();
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        EditorApplication.update -= OnEditorUpdate;
        CleanupPrefabStagePreviewManager();
        EndStroke();
    }

    private void OnDestroy()
    {
        EditorApplication.update -= OnEditorUpdate;
        CleanupPrefabStagePreviewManager();
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
            s.paintModeEnabled = EditorGUILayout.Toggle("Paint Mode", s.paintModeEnabled);
            s.useModifierShortcuts = EditorGUILayout.Toggle("Use Shortcut Keys", s.useModifierShortcuts);
            if (s.useModifierShortcuts)
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.Toggle("CapsLock Scale Mode", s.capsScaleMode);
            }

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
            s.forcePreviewFrameRate = (PreviewFrameRate)EditorGUILayout.EnumPopup("Force Preview FPS", s.forcePreviewFrameRate);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Auto Create", EditorStyles.boldLabel);
            s.autoCreateChunkOnPaint = EditorGUILayout.Toggle("Create Chunk On First Paint", s.autoCreateChunkOnPaint);
            s.showEditorPreview = EditorGUILayout.Toggle("Show Editor Preview", s.showEditorPreview);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Surface Hit", EditorStyles.boldLabel);
            s.useMeshSurfaceHit = EditorGUILayout.Toggle("Use Collider Surface Hit", s.useMeshSurfaceHit);
            if (s.useMeshSurfaceHit)
            {
                s.meshSurfaceLayerMask = LayerMaskField("Collider Layer Mask", s.meshSurfaceLayerMask);
                s.maxSurfaceSlopeDeg = EditorGUILayout.Slider("Max Slope Deg", s.maxSurfaceSlopeDeg, 0f, 89f);
                s.meshHitDistance = EditorGUILayout.FloatField("Hit Distance", s.meshHitDistance);
                s.fallbackToFieldPlane = EditorGUILayout.Toggle("Fallback To Field Plane", s.fallbackToFieldPlane);
            }

            if (s.targetField is ScatterField sf && sf.roomData == null)
            {
                EditorGUILayout.HelpBox("Room Data is not assigned on ScatterField. Assign/Create RoomScatterDataSO first.", MessageType.Warning);
            }

            EditorGUILayout.Space(6);
            DrawCopyPasteUI(s);

            EditorGUILayout.HelpBox(
                "SceneView Input\n" +
                " - Paint Mode OFF: selection/move tools work normally\n" +
                " - Paint Mode ON + Shift + LMB: Paint\n" +
                " - Paint Mode ON + Ctrl + LMB: Erase\n" +
                " - Paint Mode ON + CapsLock + LMB: Scale",
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
            EditorGUILayout.HelpBox("Target Field (ScatterField) is not assigned.", MessageType.Warning);
            return;
        }

        if (!(s.targetField is ScatterField sf) || sf.roomData == null)
        {
            EditorGUILayout.HelpBox("RoomScatterDataSO is not assigned.", MessageType.Warning);
            return;
        }

        RoomScatterDataSO.SurfaceLayerData surface = sf.roomData.FindSurface(s.surfaceType);
        if (surface == null || surface.chunks == null || surface.chunks.Count == 0)
        {
            if (s.autoCreateChunkOnPaint)
                EditorGUILayout.HelpBox($"{s.surfaceType} chunk is missing. Shift+LMB (Paint Mode ON) will auto-create and bind one.", MessageType.Info);
            else
                EditorGUILayout.HelpBox($"{s.surfaceType} chunk is missing on the target field.", MessageType.Warning);
            return;
        }

        int totalCells = 0;
        for (int i = 0; i < surface.chunks.Count; i++)
            totalCells += surface.chunks[i] != null && surface.chunks[i].cells != null ? surface.chunks[i].cells.Count : 0;

        EditorGUILayout.LabelField("Surface", surface.surfaceType.ToString());
        EditorGUILayout.LabelField("Chunk Count", surface.chunks.Count.ToString());
        EditorGUILayout.LabelField("Total Cell Count", totalCells.ToString());
    }

    private void DrawCopyPasteUI(BrushState s)
    {
        ScatterField scatterField = s.targetField as ScatterField;

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!HasValidChunk(s)))
            {
                if (GUILayout.Button("Copy Chunk", GUILayout.Height(24f)))
                    CopyChunkToClipboard(ResolveFirstChunk(s.targetField, s.surfaceType));
            }

            using (new EditorGUI.DisabledScope(!HasValidChunk(s)))
            {
                if (GUILayout.Button("Paste Chunk", GUILayout.Height(24f)))
                    PasteChunkFromClipboard(ResolveFirstChunk(s.targetField, s.surfaceType));
            }

            using (new EditorGUI.DisabledScope(!HasValidChunk(s)))
            {
                if (GUILayout.Button("Clear Cells", GUILayout.Height(24f)))
                    ClearCells(ResolveFirstChunk(s.targetField, s.surfaceType));
            }
        }

        using (new EditorGUI.DisabledScope(scatterField == null || !HasValidChunk(s)))
        {
            if (GUILayout.Button("Rebake Chunk Projection", GUILayout.Height(22f)))
                RebakeChunkProjection(scatterField, ResolveFirstChunk(s.targetField, s.surfaceType));
        }
    }

    private static bool HasValidChunk(BrushState s)
    {
        ActiveChunk chunk = ResolveFirstChunk(s.targetField, s.surfaceType);
        return chunk.IsValid;
    }

    private static void CopyChunkToClipboard(ActiveChunk active)
    {
        if (!active.IsValid)
            return;

        var surface = active.surface;
        var chunk = active.chunk;
        var data = new ScatterChunkClipboard
        {
            surfaceType = (int)surface.surfaceType,
            chunkX = chunk.chunkX,
            chunkY = chunk.chunkY,
            chunkSize = surface.chunkSize,
            cellSize = surface.cellSize,
            variationCount = surface.EffectiveVariationCount,
            globalSeed = surface.EffectiveGlobalSeed,
            scaleMin = surface.EffectiveScaleMin,
            scaleMax = surface.EffectiveScaleMax,
            cells = chunk.cells != null ? new List<CellRecord>(chunk.cells) : new List<CellRecord>()
        };

        EditorGUIUtility.systemCopyBuffer = JsonUtility.ToJson(data);
    }

    private static void PasteChunkFromClipboard(ActiveChunk active)
    {
        if (!active.IsValid)
            return;

        var raw = EditorGUIUtility.systemCopyBuffer;
        if (string.IsNullOrEmpty(raw))
        {
            EditorUtility.DisplayDialog("Scatter Brush Tool", "클占쏙옙占쏙옙占썲가 占쏙옙占?占쌍쏙옙占싹댐옙.", "OK");
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
            EditorUtility.DisplayDialog("Scatter Brush Tool", "클占쏙옙占쏙옙占쏙옙 占쏙옙占쏙옙占쏙옙 占쏙옙占쏙옙占쏙옙 占시바몌옙占쏙옙 占십쏙옙占싹댐옙.", "OK");
            return;
        }

        Undo.RecordObject(active.roomData, "Paste Scatter Chunk");

        active.surface.surfaceType = (ScatterSurfaceType)Mathf.Clamp(data.surfaceType, 0, s_surfaceValues.Length - 1);
        active.surface.chunkSize = Mathf.Max(0.01f, data.chunkSize);
        active.surface.cellSize = Mathf.Max(0.01f, data.cellSize);
        active.surface.variationCount = Mathf.Clamp(data.variationCount, 1, 16);
        active.surface.globalSeed = data.globalSeed;
        active.surface.scaleMin = data.scaleMin;
        active.surface.scaleMax = data.scaleMax;
        active.chunk.cells = data.cells != null ? new List<CellRecord>(data.cells) : new List<CellRecord>();
        active.roomData.Touch();
        EditorUtility.SetDirty(active.roomData);
        AssetDatabase.SaveAssets();
        SceneView.RepaintAll();
    }

    private static void ClearCells(ActiveChunk active)
    {
        if (!active.IsValid)
            return;

        Undo.RecordObject(active.roomData, "Clear Scatter Cells");
        active.chunk.cells.Clear();
        active.roomData.Touch();
        EditorUtility.SetDirty(active.roomData);
        SceneView.RepaintAll();
    }

    private static void RebakeChunkProjection(ScatterField field, ActiveChunk active)
    {
        if (field == null || !active.IsValid || active.chunk.cells == null)
            return;

        Undo.RecordObject(active.roomData, "Rebake Scatter Chunk Projection");
        RoomScatterDataSO.SurfaceLayerData surface = active.surface;
        RoomScatterDataSO.ChunkData chunk = active.chunk;
        float chunkSize = Mathf.Max(0.0001f, surface.chunkSize);
        float half = chunkSize * 0.5f;
        float cellSize = Mathf.Max(0.0001f, surface.cellSize);
        uint globalSeed = surface.EffectiveGlobalSeed;
        float chunkBaseX = chunk.chunkX * chunkSize;
        float chunkBaseZ = chunk.chunkY * chunkSize;
        float jitterRadius = cellSize * 0.35f;
        Transform root = field.transform;

        List<CellRecord> cells = chunk.cells;
        for (int i = 0; i < cells.Count; i++)
        {
            CellRecord rec = cells[i];
            uint seed = ScatterHash.MakeSeed(globalSeed, rec.cx, rec.cy);
            Vector2 jitter = ScatterHash.Jitter(seed, jitterRadius);
            float x = chunkBaseX + ((int)rec.cx + 0.5f) * cellSize - half + jitter.x;
            float z = chunkBaseZ + ((int)rec.cy + 0.5f) * cellSize - half + jitter.y;
            Vector3 local = new Vector3(x, 0f, z);
            SampleProjectionData(field, root, local, out rec.localY, out rec.localNormal);
            cells[i] = rec;
        }

        active.roomData.Touch();
        EditorUtility.SetDirty(active.roomData);
        AssetDatabase.SaveAssets();
        SceneView.RepaintAll();
    }

    private void OnSceneGUI(SceneView view)
    {
        var s = BrushState.instance;
        if (!s.toolEnabled || s.targetField == null)
        {
            CleanupPrefabStagePreviewManager();
            EndStroke();
            return;
        }

        if (!(s.targetField is ScatterField scatterField))
        {
            CleanupPrefabStagePreviewManager();
            EndStroke();
            return;
        }

        var e = Event.current;
        bool useToolPreviewManager = EnsureToolPreviewManager(scatterField, s);
        if (!useToolPreviewManager)
            DrawEditorPreviewIfNeeded(scatterField, view, s);

        if (!TryGetBrushHit(scatterField.transform, s, out var hitWorld, out _))
            return;

        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.CapsLock && s.useModifierShortcuts)
        {
            s.capsScaleMode = !s.capsScaleMode;
            s.mode = s.capsScaleMode ? Mode.Scale : Mode.Paint;
            s.SaveState();
            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return;
        }

        if (s.useModifierShortcuts)
            UpdateModeFromModifiers(s, e);

        var brushCenterLocal = scatterField.transform.InverseTransformPoint(hitWorld);
        ActiveChunk activeChunk = ResolveChunkAtLocalPosition(scatterField, s.surfaceType, brushCenterLocal);

        if (s.paintModeEnabled)
            DrawBrushGizmo(s.mode, hitWorld, s.radius);

        if (activeChunk.IsValid && s.paintModeEnabled && s.previewCells && e.type == EventType.Repaint)
            DrawCellPreview(scatterField.transform, activeChunk, s, brushCenterLocal);

        if (e.alt)
            return;

        if (!s.paintModeEnabled)
        {
            EndStroke();
            return;
        }

        bool canApplyByShortcut = !s.useModifierShortcuts || IsPaintInputArmed(s, e);
        if (!canApplyByShortcut)
        {
            EndStroke();
            return;
        }

        if (!activeChunk.IsValid && s.mode == Mode.Paint && e.type == EventType.MouseDown && e.button == 0)
        {
            activeChunk = TryCreateAndBindChunk(scatterField, s.surfaceType, brushCenterLocal, s);
            if (!activeChunk.IsValid)
            {
                EndStroke();
                return;
            }
        }

        if (!activeChunk.IsValid)
        {
            EndStroke();
            return;
        }

        RebuildKeyMap(activeChunk);
        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            BeginStroke(activeChunk);
            ApplyBrushWithStep(scatterField, activeChunk, s, brushCenterLocal, force: true);
            e.Use();
        }
        else if (e.type == EventType.MouseDrag && e.button == 0)
        {
            ApplyBrushWithStep(scatterField, activeChunk, s, brushCenterLocal, force: false);
            e.Use();
        }
        else if (e.type == EventType.MouseUp && e.button == 0)
        {
            EndStroke();
        }
    }

    private static void UpdateModeFromModifiers(BrushState s, Event e)
    {
        if (s.capsScaleMode) s.mode = Mode.Scale;
        else if (e.control) s.mode = Mode.Erase;
        else if (e.shift) s.mode = Mode.Paint;
    }

    private static bool IsPaintInputArmed(BrushState s, Event e)
    {
        if (s == null)
            return false;
        if (!s.useModifierShortcuts)
            return true;
        if (s.capsScaleMode)
            return true;
        return e.shift || e.control;
    }

    private void BeginStroke(ActiveChunk chunk)
    {
        if (_strokeActive)
            return;

        _strokeActive = true;
        _hasLastApplied = false;

        if (chunk.IsValid)
            Undo.RecordObject(chunk.roomData, "Scatter Brush Stroke");
    }

    private void EndStroke()
    {
        _strokeActive = false;
        _hasLastApplied = false;
    }

    private void ApplyBrushWithStep(ScatterField scatterField, ActiveChunk chunk, BrushState s, Vector3 brushCenterLocal, bool force)
    {
        if (scatterField == null)
            return;

        if (!force && _hasLastApplied)
        {
            float step = Mathf.Max(0.0001f, s.dragStepMeters);
            if ((brushCenterLocal - _lastAppliedLocal).sqrMagnitude < step * step)
                return;
        }

        ApplyBrush(scatterField, chunk, s, brushCenterLocal);

        _lastAppliedLocal = brushCenterLocal;
        _hasLastApplied = true;
    }

    private void ApplyBrush(ScatterField scatterField, ActiveChunk activeChunk, BrushState s, Vector3 brushCenterLocal)
    {
        if (!activeChunk.IsValid)
            return;
        Transform fieldTransform = scatterField.transform;

        var surface = activeChunk.surface;
        var chunk = activeChunk.chunk;
        var cells = chunk.cells;
        if (cells == null)
        {
            cells = new List<CellRecord>();
            chunk.cells = cells;
        }
        int cellsPerAxis = surface.CellsPerAxis;

        float chunkSize = Mathf.Max(0.0001f, surface.chunkSize);
        float half = chunkSize * 0.5f;
        float cellSize = Mathf.Max(0.0001f, surface.cellSize);
        float chunkBaseX = chunk.chunkX * chunkSize;
        float chunkBaseZ = chunk.chunkY * chunkSize;

        float localInChunkX = brushCenterLocal.x - chunkBaseX;
        float localInChunkZ = brushCenterLocal.z - chunkBaseZ;
        float bx = localInChunkX + half;
        float bz = localInChunkZ + half;

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
            uint seed = ScatterHash.MakeSeed(surface.EffectiveGlobalSeed, cx, cy);
            Vector2 jitter = ScatterHash.Jitter(seed, jitterRadius);

            float centerX = chunkBaseX + (cx + 0.5f) * cellSize - half;
            float centerZ = chunkBaseZ + (cy + 0.5f) * cellSize - half;
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
                        rec.variant = ScatterHash.Variant(seed, surface.EffectiveVariationCount);
                        SampleProjectionData(scatterField, fieldTransform, local, out rec.localY, out rec.localNormal);
                        cells[idx] = rec;
                        changed = true;
                    }
                }
                else
                {
                    SampleProjectionData(scatterField, fieldTransform, local, out float localY, out Vector3 localNormal);
                    var rec = new CellRecord
                    {
                        cx = (ushort)cx,
                        cy = (ushort)cy,
                        variant = ScatterHash.Variant(seed, surface.EffectiveVariationCount),
                        scaleByte = CellRecord.Encode01(s.targetScale01),
                        localY = localY,
                        localNormal = localNormal
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
            activeChunk.roomData.Touch();
            EditorUtility.SetDirty(activeChunk.roomData);
            SceneView.RepaintAll();
        }
    }

    private void RebuildKeyMap(ActiveChunk chunk)
    {
        _keyToIndex.Clear();

        if (!chunk.IsValid || chunk.chunk.cells == null)
            return;

        var cells = chunk.chunk.cells;
        int cellsPerAxis = chunk.surface.CellsPerAxis;

        for (int i = 0; i < cells.Count; i++)
        {
            var r = cells[i];
            _keyToIndex[CellRecord.Key(r.cx, r.cy, cellsPerAxis)] = i;
        }
    }

    private static bool TryGetBrushHit(Transform fieldTransform, BrushState state, out Vector3 hitWorld, out Vector3 hitNormal)
    {
        Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);

        if (state != null && state.useMeshSurfaceHit)
        {
            float maxSlopeDeg = Mathf.Clamp(state.maxSurfaceSlopeDeg, 0f, 89f);
            float minUpDot = Mathf.Cos(maxSlopeDeg * Mathf.Deg2Rad);
            float hitDistance = Mathf.Max(1f, state.meshHitDistance);

            if (Physics.Raycast(ray, out RaycastHit hit, hitDistance, state.meshSurfaceLayerMask, QueryTriggerInteraction.Ignore))
            {
                float upDot = Vector3.Dot(hit.normal.normalized, Vector3.up);
                if (upDot >= minUpDot)
                {
                    hitWorld = hit.point;
                    hitNormal = hit.normal;
                    return true;
                }
            }

            if (!state.fallbackToFieldPlane)
            {
                hitWorld = default;
                hitNormal = Vector3.up;
                return false;
            }
        }

        Plane plane = new Plane(Vector3.up, new Vector3(0f, fieldTransform.position.y, 0f));
        if (plane.Raycast(ray, out float enter))
        {
            hitWorld = ray.GetPoint(enter);
            hitNormal = Vector3.up;
            return true;
        }

        hitWorld = default;
        hitNormal = Vector3.up;
        return false;
    }

    private static LayerMask LayerMaskField(string label, LayerMask selected)
    {
        var layers = InternalEditorUtility.layers;
        int selectedMask = 0;
        for (int i = 0; i < layers.Length; i++)
        {
            int layer = LayerMask.NameToLayer(layers[i]);
            if (layer >= 0 && ((selected.value & (1 << layer)) != 0))
                selectedMask |= 1 << i;
        }

        selectedMask = EditorGUILayout.MaskField(label, selectedMask, layers);

        int mask = 0;
        for (int i = 0; i < layers.Length; i++)
        {
            if ((selectedMask & (1 << i)) == 0)
                continue;
            int layer = LayerMask.NameToLayer(layers[i]);
            if (layer >= 0)
                mask |= 1 << layer;
        }

        selected.value = mask;
        return selected;
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

    private static void DrawCellPreview(Transform fieldTransform, ActiveChunk activeChunk, BrushState s, Vector3 brushCenterLocal)
    {
        if (!activeChunk.IsValid)
            return;

        var surface = activeChunk.surface;
        var chunk = activeChunk.chunk;
        int cellsPerAxis = surface.CellsPerAxis;
        float cellSize = Mathf.Max(0.0001f, surface.cellSize);
        float chunkSize = Mathf.Max(0.0001f, surface.chunkSize);
        float half = chunkSize * 0.5f;
        float chunkBaseX = chunk.chunkX * chunkSize;
        float chunkBaseZ = chunk.chunkY * chunkSize;

        float localInChunkX = brushCenterLocal.x - chunkBaseX;
        float localInChunkZ = brushCenterLocal.z - chunkBaseZ;
        float bx = localInChunkX + half;
        float bz = localInChunkZ + half;

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

            uint seed = ScatterHash.MakeSeed(surface.EffectiveGlobalSeed, cx, cy);
            Vector2 jitter = ScatterHash.Jitter(seed, jitterRadius);

            float centerX = chunkBaseX + (cx + 0.5f) * cellSize - half;
            float centerZ = chunkBaseZ + (cy + 0.5f) * cellSize - half;

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

    private static ActiveChunk TryCreateAndBindChunk(ScatterField field, ScatterSurfaceType surfaceType, Vector3 localPos, BrushState state)
    {
        if (field == null || state == null || !state.autoCreateChunkOnPaint || field.roomData == null)
            return default;

        Undo.RecordObject(field.roomData, "Auto Create Scatter Chunk");
        var surface = field.roomData.GetOrCreateSurface(surfaceType);
        var chunk = field.roomData.GetOrCreateChunkAtLocalPosition(surface, localPos);
        field.roomData.Touch();
        EditorUtility.SetDirty(field.roomData);
        AssetDatabase.SaveAssets();
        return new ActiveChunk(field.roomData, surface, chunk);
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

    private static ActiveChunk ResolveFirstChunk(Component field, ScatterSurfaceType surfaceType)
    {
        if (field == null)
            return default;

        if (field is ScatterField scatterField)
        {
            RoomScatterDataSO.SurfaceLayerData surface = scatterField.ResolveSurface(surfaceType);
            if (scatterField.roomData != null && surface != null && surface.chunks != null && surface.chunks.Count > 0 && surface.chunks[0] != null)
                return new ActiveChunk(scatterField.roomData, surface, surface.chunks[0]);
        }

        return default;
    }

    private static ActiveChunk ResolveChunkAtLocalPosition(ScatterField field, ScatterSurfaceType surfaceType, Vector3 localPos)
    {
        if (field == null || field.roomData == null)
            return default;

        RoomScatterDataSO.SurfaceLayerData surface = field.ResolveSurface(surfaceType);
        if (surface == null)
            return default;

        RoomScatterDataSO.ChunkData chunk = field.roomData.FindChunkAtLocalPosition(surface, localPos);
        if (chunk == null)
            return default;

        return new ActiveChunk(field.roomData, surface, chunk);
    }

    private static void DrawEditorPreviewIfNeeded(ScatterField field, SceneView view, BrushState state)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint)
            return;
        if (Application.isPlaying)
            return;
        if (field == null || view == null || !state.toolEnabled || !state.showEditorPreview)
            return;

        if (!field.HasRenderConfig || field.sharedMaterial == null || field.variationMeshes == null || field.variationMeshes.Length == 0)
            return;

        s_previewChunkScratch.Clear();
        field.CollectChunkRefs(s_previewChunkScratch);
        if (s_previewChunkScratch.Count == 0)
            return;

        var mpb = new MaterialPropertyBlock();
        if (field.overrideInteractionColorTuning)
        {
            mpb.SetFloat(IdPressColorWeight, field.pressColorWeight);
            mpb.SetFloat(IdBendColorWeight, field.bendColorWeight);
        }
        // Editor preview must not read runtime instance-press buffers.
        mpb.SetFloat(IdInstancePressCount, 0f);
        mpb.SetFloat(IdPressCount, 0f);
        mpb.SetFloat(IdBaseInstanceIndex, 0f);

        Material previewMaterial = field.sharedMaterial;

        for (int c = 0; c < s_previewChunkScratch.Count; c++)
        {
            RoomScatterDataSO.ChunkRef chunkRef = s_previewChunkScratch[c];
            if (chunkRef.surface == null || chunkRef.chunk == null || chunkRef.chunk.cells == null || chunkRef.chunk.cells.Count == 0)
                continue;

            DrawChunkPreview(field, chunkRef, view.camera, previewMaterial, mpb);
        }
    }

    private static bool EnsureToolPreviewManager(ScatterField field, BrushState state)
    {
        if (Application.isPlaying || field == null || state == null || !state.toolEnabled || !state.showEditorPreview)
        {
            CleanupPrefabStagePreviewManager();
            return false;
        }

        Scene scene = field.gameObject.scene;
        if (!scene.IsValid())
        {
            CleanupPrefabStagePreviewManager();
            return false;
        }

        if (HasExternalRenderManagerForScene(scene))
        {
            CleanupPrefabStagePreviewManager();
            return true;
        }

        int sceneHandle = scene.handle;
        bool needsCreate =
            s_prefabPreviewManagerGO == null ||
            s_prefabPreviewManager == null ||
            s_prefabPreviewTargetField != field ||
            s_prefabPreviewStageHandle != sceneHandle;

        if (needsCreate)
        {
            CleanupPrefabStagePreviewManager();
            CreateToolPreviewManager(field, sceneHandle);
        }

        return s_prefabPreviewManager != null;
    }

    private static void CreateToolPreviewManager(ScatterField targetField, int sceneHandle)
    {
        if (targetField == null)
            return;

        s_prefabPreviewManagerGO = new GameObject("__ScatterPrefabPreviewManager");
        s_prefabPreviewManagerGO.hideFlags = HideFlags.HideAndDontSave | HideFlags.NotEditable;
        s_prefabPreviewManagerGO.transform.SetParent(targetField.transform, false);
        s_prefabPreviewManager = s_prefabPreviewManagerGO.AddComponent<ScatterRenderManager>();
        s_prefabPreviewManager.hideFlags = HideFlags.HideAndDontSave | HideFlags.NotEditable;
        s_prefabPreviewTargetField = targetField;
        s_prefabPreviewStageHandle = sceneHandle;

        var so = new SerializedObject(s_prefabPreviewManager);
        SerializedProperty autoDiscoverProp = so.FindProperty("autoDiscoverFields");
        SerializedProperty autoDiscoverPlayProp = so.FindProperty("autoDiscoverDuringPlay");
        SerializedProperty refreshIntervalProp = so.FindProperty("refreshInterval");
        SerializedProperty fieldsProp = so.FindProperty("fields");

        if (autoDiscoverProp != null) autoDiscoverProp.boolValue = false;
        if (autoDiscoverPlayProp != null) autoDiscoverPlayProp.boolValue = false;
        if (refreshIntervalProp != null) refreshIntervalProp.floatValue = 1f;
        if (fieldsProp != null)
        {
            fieldsProp.ClearArray();
            fieldsProp.InsertArrayElementAtIndex(0);
            fieldsProp.GetArrayElementAtIndex(0).objectReferenceValue = targetField;
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        s_prefabPreviewManager.RefreshFieldsNow();
    }

    private static void CleanupPrefabStagePreviewManager()
    {
        if (s_prefabPreviewManagerGO != null)
            UnityEngine.Object.DestroyImmediate(s_prefabPreviewManagerGO);

        s_prefabPreviewManagerGO = null;
        s_prefabPreviewManager = null;
        s_prefabPreviewTargetField = null;
        s_prefabPreviewStageHandle = -1;
    }

    private static bool HasExternalRenderManagerForScene(Scene scene)
    {
#if UNITY_2023_1_OR_NEWER
        var managers = UnityEngine.Object.FindObjectsByType<ScatterRenderManager>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
        var managers = UnityEngine.Object.FindObjectsOfType<ScatterRenderManager>();
#endif
        if (managers == null || managers.Length == 0)
            return false;

        for (int i = 0; i < managers.Length; i++)
        {
            ScatterRenderManager manager = managers[i];
            if (manager == null)
                continue;
            if (manager == s_prefabPreviewManager)
                continue;
            if (manager.gameObject.scene == scene)
                return true;
        }

        return false;
    }

    private static void OnEditorUpdate()
    {
        BrushState state = BrushState.instance;
        if (state == null || !state.toolEnabled || !state.showEditorPreview)
            return;

        int targetFps = (int)state.forcePreviewFrameRate;
        if (targetFps <= 0)
            return;

        double now = EditorApplication.timeSinceStartup;
        double interval = 1.0 / Math.Max(1, targetFps);
        if (now < s_nextSceneViewRepaintTime)
            return;

        s_nextSceneViewRepaintTime = now + interval;
        SceneView.RepaintAll();
    }

    private static void DrawChunkPreview(ScatterField field, RoomScatterDataSO.ChunkRef chunkRef, Camera cam, Material previewMaterial, MaterialPropertyBlock mpb)
    {
        if (field == null || chunkRef.surface == null || chunkRef.chunk == null || cam == null)
            return;

        RoomScatterDataSO.SurfaceLayerData surface = chunkRef.surface;
        RoomScatterDataSO.ChunkData chunk = chunkRef.chunk;
        int variationCount = Mathf.Clamp(surface.EffectiveVariationCount, 1, Mathf.Min(16, field.variationMeshes.Length));
        if (variationCount <= 0)
            return;

        float chunkSize = Mathf.Max(0.0001f, surface.chunkSize);
        float half = chunkSize * 0.5f;
        float cellSize = Mathf.Max(0.0001f, surface.cellSize);
        uint globalSeed = surface.EffectiveGlobalSeed;
        float scaleMin = surface.EffectiveScaleMin;
        float scaleMax = surface.EffectiveScaleMax;
        float chunkBaseX = chunk.chunkX * chunkSize;
        float chunkBaseZ = chunk.chunkY * chunkSize;
        float jitterRadius = cellSize * 0.35f;

        for (int variant = 0; variant < variationCount; variant++)
        {
            Mesh mesh = field.variationMeshes[Mathf.Min(variant, field.variationMeshes.Length - 1)];
            if (mesh == null)
                continue;

            int count = 0;
            List<CellRecord> cells = chunk.cells;
            for (int i = 0; i < cells.Count; i++)
            {
                CellRecord rec = cells[i];
                int recVariant = Mathf.Clamp(rec.variant, 0, variationCount - 1);
                if (recVariant != variant)
                    continue;

                uint seed = ScatterHash.MakeSeed(globalSeed, rec.cx, rec.cy);
                Vector2 jitter = ScatterHash.Jitter(seed, jitterRadius);
                float x = chunkBaseX + ((int)rec.cx + 0.5f) * cellSize - half + jitter.x;
                float z = chunkBaseZ + ((int)rec.cy + 0.5f) * cellSize - half + jitter.y;
                float scale = rec.Scale(scaleMin, scaleMax);

                Vector3 localPos = new Vector3(x, rec.localY, z);
                Vector3 worldPos = field.transform.TransformPoint(localPos);
                Quaternion worldRot = Quaternion.identity;
                if (field.projectToStaticSurface && field.alignToSurfaceNormal)
                {
                    Vector3 localNormal = rec.localNormal.sqrMagnitude > 1e-6f ? rec.localNormal.normalized : Vector3.up;
                    Vector3 normalWorld = field.transform.TransformDirection(localNormal).normalized;
                    Vector3 tangent = Vector3.ProjectOnPlane(field.transform.forward, normalWorld);
                    if (tangent.sqrMagnitude < 1e-6f)
                        tangent = Vector3.ProjectOnPlane(Vector3.forward, normalWorld);
                    if (tangent.sqrMagnitude < 1e-6f)
                        tangent = Vector3.right;
                    worldRot = Quaternion.LookRotation(tangent.normalized, normalWorld);
                }

                s_previewMatrices[count++] = Matrix4x4.TRS(worldPos, worldRot, Vector3.one * scale);

                if (count >= s_previewMatrices.Length)
                {
                    Graphics.DrawMeshInstanced(
                        mesh, 0, previewMaterial, s_previewMatrices, count, mpb,
                        ShadowCastingMode.Off, false, field.gameObject.layer, cam);
                    count = 0;
                }
            }

            if (count > 0)
            {
                Graphics.DrawMeshInstanced(
                    mesh, 0, previewMaterial, s_previewMatrices, count, mpb,
                    ShadowCastingMode.Off, false, field.gameObject.layer, cam);
            }
        }
    }


    private static void SampleProjectionData(ScatterField field, Transform fieldTransform, Vector3 localPos, out float localY, out Vector3 localNormal)
    {
        localY = 0f;
        localNormal = Vector3.up;
        if (field == null || !field.projectToStaticSurface)
            return;

        Vector3 baseWorld = fieldTransform.TransformPoint(new Vector3(localPos.x, 0f, localPos.z));
        float rayStartHeight = Mathf.Max(0.1f, field.projectionRayStartHeight);
        float rayDistance = Mathf.Max(0.1f, field.projectionRayDistance);
        Vector3 rayOrigin = baseWorld + Vector3.up * rayStartHeight;

        if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, rayDistance, field.projectionLayerMask, QueryTriggerInteraction.Ignore))
            return;

        Vector3 projectedLocal = fieldTransform.InverseTransformPoint(hit.point);
        localY = projectedLocal.y;
        localNormal = fieldTransform.InverseTransformDirection(hit.normal).normalized;
    }
}
#endif





