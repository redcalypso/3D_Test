using UnityEngine;
using UnityEditor;
using System.IO;

public class RadialMaskGenerator
{
    [MenuItem("Tools/Miro/Generate Radial Mask")]
    public static void GenerateMask()
    {
        int size = 256;
        float falloffPow = 2.0f; // 옛날 BrushRadial.shader의 _FalloffPow 기본값 2.0!
        Texture2D tex = new Texture2D(size, size, TextureFormat.R8, false);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // uv를 -1 ~ 1로 맞추고 거리 계산 (BrushRadial이랑 완벽히 똑같은 수학!)
                float u = x / (float)(size - 1);
                float v = y / (float)(size - 1);
                Vector2 p = new Vector2(u, v) * 2f - Vector2.one;
                float dist = p.magnitude;
                
                // Falloff 계산
                float a = Mathf.Clamp01(1f - dist);
                a = Mathf.Pow(a, falloffPow);

                // 흑백 마스크니까 RGB 다 똑같이 넣기!
                tex.SetPixel(x, y, new Color(a, a, a, 1f));
            }
        }
        tex.Apply();

        byte[] bytes = tex.EncodeToPNG();
        string path = Application.dataPath + "/EIS_RadialMask.png";
        File.WriteAllBytes(path, bytes);
        AssetDatabase.Refresh();
        
        Debug.Log("✨ 미로표 완벽한 Radial Mask PNG 생성 완료! 위치: " + path);
    }
}