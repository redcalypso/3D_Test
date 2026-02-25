#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public static class EISRingMaskGenerator
{
    private const int Size = 256;
    // 256x256 기준:
    // - 중심 블랙: 약 76px
    // - 흰 링(플래토): 약 34px
    // - 바깥 페이드: 약 18px
    private const float InnerBlackRadiusPx = 76f;
    private const float InnerRisePx = 4f;
    private const float RingThicknessPx = 34f;
    private const float OuterFadePx = 18f;
    private const string RelativeAssetPath = "Assets/00.00 Tester Scripts/EIS_RingMask_256.png";

    [MenuItem("Tools/Miro/Generate EIS Ring Mask (256)")]
    public static void Generate()
    {
        Texture2D tex = new Texture2D(Size, Size, TextureFormat.R8, false, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "EIS_RingMask_256"
        };

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float cx = (Size - 1) * 0.5f;
                float cy = (Size - 1) * 0.5f;
                float dx = x - cx;
                float dy = y - cy;
                float rPx = Mathf.Sqrt(dx * dx + dy * dy);

                float ringStart = InnerBlackRadiusPx;
                float ringFullStart = ringStart + Mathf.Max(0.0001f, InnerRisePx);
                float ringEnd = ringFullStart + RingThicknessPx;
                float fadeEnd = ringEnd + Mathf.Max(0.0001f, OuterFadePx);

                float value;
                if (rPx <= ringStart)
                {
                    value = 0f;
                }
                else if (rPx < ringFullStart)
                {
                    float t = Mathf.InverseLerp(ringStart, ringFullStart, rPx);
                    value = Mathf.SmoothStep(0f, 1f, t);
                }
                else if (rPx <= ringEnd)
                {
                    value = 1f;
                }
                else if (rPx < fadeEnd)
                {
                    float t = Mathf.InverseLerp(ringEnd, fadeEnd, rPx);
                    value = 1f - Mathf.SmoothStep(0f, 1f, t);
                }
                else
                {
                    value = 0f;
                }

                tex.SetPixel(x, y, new Color(value, value, value, 1f));
            }
        }

        tex.Apply(false, false);

        byte[] png = tex.EncodeToPNG();
        string fullPath = Path.Combine(Directory.GetCurrentDirectory(), RelativeAssetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? string.Empty);
        File.WriteAllBytes(fullPath, png);

        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(RelativeAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

        TextureImporter importer = AssetImporter.GetAtPath(RelativeAssetPath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        Debug.Log($"[EISRingMaskGenerator] Generated: {RelativeAssetPath}");
    }
}
#endif
