#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ScatterField))]
public sealed class ScatterFieldEditor : Editor
{
    private static readonly HashSet<ScatterSurfaceType> s_surfaceSet = new HashSet<ScatterSurfaceType>();

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        SerializedProperty roomDataProp = serializedObject.FindProperty("roomData");
        SerializedProperty surfaceRenderConfigsProp = serializedObject.FindProperty("surfaceRenderConfigs");
        SerializedProperty projectToStaticSurfaceProp = serializedObject.FindProperty("projectToStaticSurface");
        SerializedProperty projectionLayerMaskProp = serializedObject.FindProperty("projectionLayerMask");
        SerializedProperty projectionRayStartHeightProp = serializedObject.FindProperty("projectionRayStartHeight");
        SerializedProperty projectionRayDistanceProp = serializedObject.FindProperty("projectionRayDistance");
        SerializedProperty alignToSurfaceNormalProp = serializedObject.FindProperty("alignToSurfaceNormal");
        SerializedProperty renderInSceneViewWhilePlayingProp = serializedObject.FindProperty("renderInSceneViewWhilePlaying");
        SerializedProperty enableDistanceLodProp = serializedObject.FindProperty("enableDistanceLod");
        SerializedProperty lodMidDistanceProp = serializedObject.FindProperty("lodMidDistance");
        SerializedProperty lodCullDistanceProp = serializedObject.FindProperty("lodCullDistance");
        SerializedProperty lodMidStrideProp = serializedObject.FindProperty("lodMidStride");
        SerializedProperty enableInstanceCullingProp = serializedObject.FindProperty("enableInstanceCulling");
        SerializedProperty instanceCullPaddingProp = serializedObject.FindProperty("instanceCullPadding");

        EditorGUILayout.PropertyField(roomDataProp);

        ScatterField field = (ScatterField)target;
        RoomScatterDataSO roomData = roomDataProp.objectReferenceValue as RoomScatterDataSO;

        if (roomData == null)
        {
            EditorGUILayout.HelpBox("RoomScatterDataSO is required. Create and assign one to use brush/render pipeline.", MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Create Room Scatter Data"))
                    CreateRoomDataAsset(field);
            }
        }
        else
        {
            int chunkCount = 0;
            if (roomData.surfaces != null)
            {
                for (int i = 0; i < roomData.surfaces.Count; i++)
                    chunkCount += roomData.surfaces[i] != null && roomData.surfaces[i].chunks != null ? roomData.surfaces[i].chunks.Count : 0;
            }
            EditorGUILayout.HelpBox($"Room Data assigned. Chunks: {chunkCount}", MessageType.Info);
            DrawRoomDataValidation(roomData);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Ping Room Data"))
                    EditorGUIUtility.PingObject(roomData);
                if (GUILayout.Button("Select Room Data"))
                    Selection.activeObject = roomData;
            }
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Surface Render Configs", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(surfaceRenderConfigsProp, includeChildren: true);
        DrawRenderConfigValidation(field, roomData);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Surface Projection", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(projectToStaticSurfaceProp);
        if (projectToStaticSurfaceProp.boolValue)
        {
            EditorGUILayout.PropertyField(projectionLayerMaskProp);
            EditorGUILayout.PropertyField(projectionRayStartHeightProp);
            EditorGUILayout.PropertyField(projectionRayDistanceProp);
            EditorGUILayout.PropertyField(alignToSurfaceNormalProp);
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Render Options", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(renderInSceneViewWhilePlayingProp);
        EditorGUILayout.PropertyField(enableDistanceLodProp);
        if (enableDistanceLodProp.boolValue)
        {
            EditorGUILayout.PropertyField(lodMidDistanceProp);
            EditorGUILayout.PropertyField(lodCullDistanceProp);
            EditorGUILayout.PropertyField(lodMidStrideProp);
        }

        EditorGUILayout.PropertyField(enableInstanceCullingProp);
        if (enableInstanceCullingProp.boolValue)
            EditorGUILayout.PropertyField(instanceCullPaddingProp);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);
        SerializedProperty debugPebbleMotionProp = serializedObject.FindProperty("debugPebbleMotion");
        if (debugPebbleMotionProp != null)
            EditorGUILayout.PropertyField(debugPebbleMotionProp, new GUIContent("Debug Pebble Motion", "Enable detailed pebble displacement diagnostic logs in the Console."));

        EditorGUILayout.Space(8);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Open Scatter Brush Tool"))
                ScatterBrushToolWindow.Open();
        }

        serializedObject.ApplyModifiedProperties();
    }

    private static void CreateRoomDataAsset(ScatterField field)
    {
        if (field == null)
            return;

        string defaultFolder = ScatterRoomDataMigrationUtility.ResolveSuggestedFolder(field);
        string defaultName = $"{field.gameObject.name}_RoomScatterData";
        string assetPath = EditorUtility.SaveFilePanelInProject(
            "Create Room Scatter Data",
            defaultName,
            "asset",
            "Choose where to create the room scatter data asset.",
            defaultFolder);

        if (string.IsNullOrEmpty(assetPath))
            return;

        Undo.RecordObject(field, "Create Room Scatter Data");
        RoomScatterDataSO created = ScatterRoomDataMigrationUtility.CreateRoomScatterDataForField(field, assetPath);
        if (created != null)
            EditorUtility.SetDirty(field);
    }

    private static void DrawRoomDataValidation(RoomScatterDataSO roomData)
    {
        if (roomData == null)
            return;

        s_surfaceSet.Clear();
        bool hasNullSurface = false;
        bool hasEmptyChunkList = false;
        bool hasNullChunk = false;
        bool hasDuplicateSurface = false;

        if (roomData.surfaces != null)
        {
            for (int i = 0; i < roomData.surfaces.Count; i++)
            {
                RoomScatterDataSO.SurfaceLayerData surface = roomData.surfaces[i];
                if (surface == null)
                {
                    hasNullSurface = true;
                    continue;
                }

                ScatterSurfaceType st = surface.surfaceType;
                if (!s_surfaceSet.Add(st))
                    hasDuplicateSurface = true;

                if (surface.chunks == null || surface.chunks.Count == 0)
                {
                    hasEmptyChunkList = true;
                    continue;
                }

                for (int c = 0; c < surface.chunks.Count; c++)
                {
                    if (surface.chunks[c] == null)
                    {
                        hasNullChunk = true;
                        break;
                    }
                }
            }
        }

        if (roomData.surfaces == null || roomData.surfaces.Count == 0)
            EditorGUILayout.HelpBox("RoomScatterDataSO has no surface entries.", MessageType.Warning);
        if (hasNullSurface)
            EditorGUILayout.HelpBox("RoomScatterDataSO contains null surface entries. Remove null slots.", MessageType.Warning);
        if (hasEmptyChunkList)
            EditorGUILayout.HelpBox("Some surfaces have zero chunks.", MessageType.Warning);
        if (hasNullChunk)
            EditorGUILayout.HelpBox("Some surfaces contain null chunk entries.", MessageType.Warning);
        if (hasDuplicateSurface)
            EditorGUILayout.HelpBox("Duplicate surface types found in RoomScatterDataSO chunks.", MessageType.Warning);
    }

    private static void DrawRenderConfigValidation(ScatterField field, RoomScatterDataSO roomData)
    {
        if (field == null)
            return;

        List<ScatterSurfaceRenderConfig> configs = field.surfaceRenderConfigs;
        if (configs == null || configs.Count == 0)
        {
            EditorGUILayout.HelpBox("Surface render config required. No surface render configs are assigned.", MessageType.Warning);
            return;
        }

        s_surfaceSet.Clear();
        bool hasDuplicateConfigSurface = false;
        bool hasInvalidConfig = false;

        for (int i = 0; i < configs.Count; i++)
        {
            ScatterSurfaceRenderConfig config = configs[i];
            if (config == null)
            {
                hasInvalidConfig = true;
                continue;
            }

            if (!s_surfaceSet.Add(config.surfaceType))
                hasDuplicateConfigSurface = true;

            int requiredVariationCount = 1;
            RoomScatterDataSO.SurfaceLayerData roomSurface = roomData != null ? roomData.FindSurface(config.surfaceType) : null;
            if (roomSurface != null)
                requiredVariationCount = roomSurface.EffectiveVariationCount;
            else if (config.variationMeshes != null && config.variationMeshes.Length > 0)
                requiredVariationCount = config.variationMeshes.Length;

            DrawVariationCountControls(roomData, roomSurface, config);

            if (!ScatterField.ValidateRenderConfig(config, requiredVariationCount, out string reason))
            {
                hasInvalidConfig = true;
                EditorGUILayout.HelpBox(reason, MessageType.Warning);
            }
        }

        if (roomData != null && roomData.surfaces != null)
        {
            for (int i = 0; i < roomData.surfaces.Count; i++)
            {
                RoomScatterDataSO.SurfaceLayerData surface = roomData.surfaces[i];
                if (surface == null)
                    continue;

                if (!field.TryGetRenderConfig(surface.surfaceType, out _))
                    EditorGUILayout.HelpBox($"Surface render config required for {surface.surfaceType}.", MessageType.Warning);
            }
        }

        if (hasDuplicateConfigSurface)
            EditorGUILayout.HelpBox("Duplicate surface types found in Surface Render Configs.", MessageType.Warning);
        else if (!hasInvalidConfig && roomData != null && roomData.surfaces != null && roomData.surfaces.Count > 0)
            EditorGUILayout.HelpBox("Surface render configs are valid.", MessageType.Info);
    }

    private static void DrawVariationCountControls(RoomScatterDataSO roomData, RoomScatterDataSO.SurfaceLayerData roomSurface, ScatterSurfaceRenderConfig config)
    {
        if (config == null)
            return;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField($"{config.surfaceType} Variation Count", EditorStyles.boldLabel);

            int meshCount = config.variationMeshes != null ? config.variationMeshes.Length : 0;
            EditorGUILayout.LabelField("Assigned Mesh Count", meshCount.ToString());

            if (roomSurface == null)
            {
                EditorGUILayout.HelpBox($"RoomScatterDataSO surface is missing for {config.surfaceType}. Add the surface first to control variation count.", MessageType.Info);
                return;
            }

            if (roomSurface.profile != null)
            {
                ScatterLayerProfileSO profile = roomSurface.profile;
                int next = EditorGUILayout.IntSlider("Profile Variation Count", profile.variationCount, 1, 16);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (next != profile.variationCount)
                    {
                        Undo.RecordObject(profile, "Change Scatter Profile Variation Count");
                        profile.variationCount = next;
                        EditorUtility.SetDirty(profile);
                    }

                    if (GUILayout.Button("Match Mesh Count", GUILayout.Width(140f)))
                    {
                        int target = Mathf.Clamp(meshCount, 1, 16);
                        if (profile.variationCount != target)
                        {
                            Undo.RecordObject(profile, "Match Scatter Profile Variation Count");
                            profile.variationCount = target;
                            EditorUtility.SetDirty(profile);
                        }
                    }

                    if (GUILayout.Button("Ping Profile", GUILayout.Width(100f)))
                        EditorGUIUtility.PingObject(profile);
                }

                EditorGUILayout.HelpBox($"Effective variation count is currently driven by profile '{profile.name}'.", MessageType.None);
                return;
            }

            int updated = EditorGUILayout.IntSlider("Surface Variation Count", roomSurface.variationCount, 1, 16);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (updated != roomSurface.variationCount)
                {
                    Undo.RecordObject(roomData, "Change Surface Variation Count");
                    roomSurface.variationCount = updated;
                    roomData.Touch();
                    EditorUtility.SetDirty(roomData);
                }

                if (GUILayout.Button("Match Mesh Count", GUILayout.Width(140f)))
                {
                    int target = Mathf.Clamp(meshCount, 1, 16);
                    if (roomSurface.variationCount != target)
                    {
                        Undo.RecordObject(roomData, "Match Surface Variation Count");
                        roomSurface.variationCount = target;
                        roomData.Touch();
                        EditorUtility.SetDirty(roomData);
                    }
                }
            }
        }
    }
}
#endif
