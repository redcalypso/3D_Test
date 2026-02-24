// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

Shader "Hidden/Interaction/BrushRadial"
{
    Properties
    {
        _FalloffPow ("Falloff Pow", Float) = 2
    }

    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One Zero

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // ShaderLab/CG에서 항상 잡히는 기본 행렬들 사용 (include 최소화)
            float _FalloffPow;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 p = i.uv * 2.0 - 1.0;
                float dist = length(p);

                if (dist > 1.0)
                    return fixed4(0.5, 0.5, 0.0, 0.0);

                float2 dir = (dist > 1e-5) ? (p / dist) : float2(0.0, 0.0);
                float2 rg = dir * 0.5 + 0.5;

                float a = saturate(1.0 - dist);
                a = pow(a, max(_FalloffPow, 0.0001));

                float b = 1.0;

                return fixed4(rg.x, rg.y, b, a);
            }
            ENDCG
        }
    }
}
