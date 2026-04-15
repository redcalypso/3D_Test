Shader "Hidden/Interaction/Stamp"
{
    Properties
    {
        _PrevRT ("Prev RT", 2D) = "black" {}
        _MaskTex ("Mask", 2D) = "white" {}
        _StampCenter ("Stamp Center", Vector) = (0, 0, 0, 0)
        _StampDir ("Stamp Dir", Vector) = (0, 0, 1, 0)
        _StampSize ("Stamp Size", Float) = 1.0
        _StampStrength ("Stamp Strength", Float) = 1.0
        _ForceMultiplier ("Force Multiplier", Float) = 1.0
        _PressMultiplier ("Press Multiplier", Float) = 1.0
        _WeightMultiplier ("Weight Multiplier", Float) = 1.0
        _MaxForce ("Max Force", Float) = 1.0
        _DirectionMode ("Direction Mode", Float) = 0
        _StampMode ("Stamp Mode", Int) = 0
        _UseArcMask ("Use Arc Mask", Float) = 0
        _ArcAngle ("Arc Angle", Float) = 30
        _ArcSoftness ("Arc Softness", Float) = 4
        _ArcForward ("Arc Forward", Vector) = (0, 0, 1, 0)
        _CurrentCamXZ ("Current Cam XZ", Vector) = (0, 0, 0, 0)
        _InteractionCamPosXZ ("CamPosXZ", Vector) = (0, 0, 0, 0)
        _InteractionCamParams ("CamParams", Vector) = (50, 100, 0.01, 0.39)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend One Zero

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _PrevRT;
            sampler2D _MaskTex;

            float4 _StampCenter;
            float4 _StampDir;
            float4 _CurrentCamXZ;
            float4 _InteractionCamPosXZ;
            float4 _InteractionCamParams;
            float4 _PrevRT_TexelSize;

            float _StampSize;
            float _StampStrength;
            float _ForceMultiplier;
            float _PressMultiplier;
            float _WeightMultiplier;
            float _MaxForce;
            int _DirectionMode;
            int _StampMode;
            float _UseArcMask;
            float _ArcAngle;
            float _ArcSoftness;
            float4 _ArcForward;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float2 SafeNormalize(float2 v)
            {
                float l = length(v);
                return (l > 0.000001) ? (v / l) : float2(0, 0);
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                #if UNITY_UV_STARTS_AT_TOP
                if (_PrevRT_TexelSize.y < 0)
                    uv.y = 1.0 - uv.y;
                #endif

                float4 prev = tex2D(_PrevRT, uv);

                float2 camXZ = _CurrentCamXZ.xy;
                float worldSize = max(0.0001, _InteractionCamParams.y);
                float2 worldXZ = camXZ + (uv - 0.5) * worldSize;

                float stampSize = max(0.0001, _StampSize);
                float2 local = (worldXZ - _StampCenter.xz) / stampSize;
                float2 maskUV = local * 0.5 + 0.5;

                if (maskUV.x < 0.0 || maskUV.x > 1.0 || maskUV.y < 0.0 || maskUV.y > 1.0)
                    return prev;

                float mask = tex2D(_MaskTex, maskUV).r;
                if (mask <= 0.000001)
                    return prev;

                if (_UseArcMask > 0.5)
                {
                    float2 centerToPixel = worldXZ - _StampCenter.xz;
                    float centerLen = length(centerToPixel);
                    if (centerLen > 0.000001)
                    {
                        float2 pixelDir = centerToPixel / centerLen;
                        float2 arcForward = SafeNormalize(_ArcForward.xz);
                        if (length(arcForward) <= 0.000001)
                            arcForward = SafeNormalize(_StampDir.xz);

                        float dotDir = dot(pixelDir, arcForward);
                        float halfAngleRad = radians(clamp(_ArcAngle, 0.0, 360.0) * 0.5);
                        float softRad = radians(max(0.0001, _ArcSoftness));

                        float innerCos = cos(max(0.0, halfAngleRad - softRad));
                        float outerCos = cos(min(UNITY_PI, halfAngleRad + softRad));
                        float arcMask = smoothstep(outerCos, innerCos, dotDir);

                        mask *= arcMask;
                        if (mask <= 0.000001)
                            return prev;
                    }
                }

                float2 dir = SafeNormalize(_StampDir.xz);
                if (_DirectionMode == 2)
                    dir = -SafeNormalize(worldXZ - _StampCenter.xz);

                float2 oldRG = (prev.rg - 0.5) * 2.0;
                float oldMag = length(oldRG);
                float2 oldDir = SafeNormalize(oldRG);

                float strength = max(0.0, _StampStrength);
                float weight = saturate(mask * strength * max(0.0, _WeightMultiplier));
                float targetMag = min(max(0.0, _ForceMultiplier) * strength, max(0.0001, _MaxForce));
                float2 targetVec = dir * targetMag;

                float2 mixedVec;
                if (_StampMode == 1)
                    mixedVec = lerp(oldRG, targetVec, weight);
                else
                    mixedVec = oldRG + (targetVec * weight);

                float mixedLen = length(mixedVec);
                float maxForce = max(0.0001, _MaxForce);
                float softMag = maxForce * (1.0 - exp(-mixedLen / maxForce));
                float2 rg = SafeNormalize(mixedVec) * softMag;
                if (mixedLen <= 0.000001)
                    rg = oldDir * min(oldMag, maxForce);

                float b = prev.b;
                float a = prev.a;
                float targetB = saturate(max(0.0, _PressMultiplier) * strength);
                float targetA = weight;
                if (_StampMode == 1)
                {
                    b = lerp(prev.b, max(prev.b, targetB), weight);
                    a = max(prev.a, targetA);
                }
                else
                {
                    b = saturate(prev.b + targetB * weight);
                    a = saturate(prev.a + targetA);
                }

                float2 packedRG = rg * 0.5 + 0.5;
                b = saturate(b);
                a = saturate(a);
                return float4(packedRG, b, a);
            }
            ENDCG
        }
    }
}
