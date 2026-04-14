#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public static class ScatterRoomDataMigrationUtility
{
    public static RoomScatterDataSO CreateRoomScatterDataForField(ScatterField field, string assetPath)
    {
        return CreateAndAssignRoomData(field, assetPath);
    }

    public static string ResolveSuggestedFolder(ScatterField field)
    {
        return ResolveDefaultFolder(field);
    }

    [MenuItem("CONTEXT/ScatterField/Create Room Scatter Data (Embed Chunks)")]
    private static void CreateRoomScatterDataFromContext(MenuCommand command)
    {
        ScatterField field = command.context as ScatterField;
        if (field == null)
            return;

        string defaultFolder = ResolveDefaultFolder(field);
        string defaultName = $"{field.gameObject.name}_RoomScatterData";

        string assetPath = EditorUtility.SaveFilePanelInProject(
            "Create Room Scatter Data",
            defaultName,
            "asset",
            "Choose where to create the room scatter data asset.",
            defaultFolder);

        if (string.IsNullOrEmpty(assetPath))
            return;

        CreateAndAssignRoomData(field, assetPath);
    }

    private static RoomScatterDataSO CreateAndAssignRoomData(ScatterField field, string assetPath)
    {
        if (field == null || string.IsNullOrEmpty(assetPath))
            return null;

        RoomScatterDataSO roomData = ScriptableObject.CreateInstance<RoomScatterDataSO>();
        roomData.name = Path.GetFileNameWithoutExtension(assetPath);
        AssetDatabase.CreateAsset(roomData, assetPath);

        Undo.RecordObject(field, "Assign Room Scatter Data");
        field.roomData = roomData;

        EditorUtility.SetDirty(roomData);
        EditorUtility.SetDirty(field);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Selection.activeObject = roomData;
        EditorGUIUtility.PingObject(roomData);
        Debug.Log($"[Scatter] Created RoomScatterData '{assetPath}'.");
        return roomData;
    }

    private static string ResolveDefaultFolder(ScatterField field)
    {
        if (field == null)
            return "Assets";

        string path = field.roomData != null ? AssetDatabase.GetAssetPath(field.roomData) : null;
        if (string.IsNullOrEmpty(path))
            path = field.gameObject.scene.path;

        if (string.IsNullOrEmpty(path))
            return "Assets";

        if (File.Exists(path))
            return Path.GetDirectoryName(path).Replace("\\", "/");

        if (Directory.Exists(path))
            return path.Replace("\\", "/");

        return "Assets";
    }
}
#endif
