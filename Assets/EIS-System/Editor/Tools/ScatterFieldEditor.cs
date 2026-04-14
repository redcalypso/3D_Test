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
        SerializedProperty variationMeshesProp = serializedObject.FindProperty("variationMeshes");
        SerializedProperty sharedMaterialProp = serializedObject.FindProperty("sharedMaterial");
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
        SerializedProperty overrideInteractionColorTuningProp = serializedObject.FindProperty("overrideInteractionColorTuning");
        SerializedProperty pressColorWeightProp = serializedObject.FindProperty("pressColorWeight");
        SerializedProperty bendColorWeightProp = serializedObject.FindProperty("bendColorWeight");

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
        EditorGUILayout.LabelField("Render Source", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(variationMeshesProp);
        EditorGUILayout.PropertyField(sharedMaterialProp);

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
        EditorGUILayout.LabelField("Interaction Color Tuning", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(overrideInteractionColorTuningProp);
        if (overrideInteractionColorTuningProp.boolValue)
        {
            EditorGUILayout.PropertyField(pressColorWeightProp);
            EditorGUILayout.PropertyField(bendColorWeightProp);
        }

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
}
#endif
