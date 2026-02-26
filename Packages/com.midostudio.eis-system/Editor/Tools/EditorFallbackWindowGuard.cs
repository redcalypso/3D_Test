#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
internal static class EditorFallbackWindowGuard
{
    private static bool _ran;

    static EditorFallbackWindowGuard()
    {
        EditorApplication.delayCall += CloseFallbackWindows;
    }

    private static void CloseFallbackWindows()
    {
        if (_ran)
            return;
        _ran = true;
        EditorApplication.delayCall -= CloseFallbackWindows;

        var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
        if (windows == null || windows.Length == 0)
            return;

        for (int i = 0; i < windows.Length; i++)
        {
            EditorWindow window = windows[i];
            if (window == null)
                continue;

            if (window.GetType().FullName != "UnityEditor.FallbackEditorWindow")
                continue;

            try
            {
                window.Close();
            }
            catch
            {
                // Broken fallback windows can throw on Close(); force-destroy instead.
                Object.DestroyImmediate(window, true);
            }
        }
    }
}
#endif
