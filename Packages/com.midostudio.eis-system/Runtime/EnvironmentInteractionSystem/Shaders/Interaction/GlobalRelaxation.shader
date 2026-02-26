Shader "Hidden/Interaction/GlobalRelaxation"
{
    Properties
    {
        _PrevRT ("Prev RT", 2D) = "black" {}
        _DecayRate ("Decay Rate", Float) = 3.0
        _DeltaTime ("Delta Time", Float) = 0.0167
        _UVOffset ("UV Offset", Vector) = (0, 0, 0, 0)
        _NeutralColor ("Neutral Color", Color) = (0.5, 0.5, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _PrevRT;
            float _DecayRate;
            float _DeltaTime;
            float4 _UVOffset;
            float4 _NeutralColor;

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

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv + _UVOffset.xy;

                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                    return _NeutralColor;

                float4 prev = tex2D(_PrevRT, uv);
                float decay = exp(-max(0.0, _DecayRate) * max(0.0, _DeltaTime));

                float2 rg = (prev.rg - 0.5) * 2.0;
                rg *= decay;
                float2 packedRG = rg * 0.5 + 0.5;

                float b = saturate(prev.b * decay);
                float a = saturate(prev.a * decay);

                return float4(packedRG, b, a);
            }
            ENDCG
        }
    }
}
