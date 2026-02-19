Shader "MIDO/Debug/InteractionRT"
{
    Properties{}
        SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_InteractionRT);
            SAMPLER(sampler_InteractionRT);
            float4 _InteractionCamData;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 ws = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionWS = ws;
                OUT.positionHCS = TransformWorldToHClip(ws);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float camX = _InteractionCamData.x;
                float camZ = _InteractionCamData.y;
                float size = _InteractionCamData.z; // OrthoSize

                // float rtSize = _InteractionCam.w; // 지금은 필요없음

                // 월드 XZ -> 0~1 UV
                float2 uv;
                uv.x = (IN.positionWS.x - (camX - size)) / (2.0 * size);
                uv.y = (IN.positionWS.z - (camZ - size)) / (2.0 * size);

                // 디버그: UV 범위 밖이면 빨강
                if (uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1)
                    return half4(1,0,0,1);

                half4 c = SAMPLE_TEXTURE2D(_InteractionRT, sampler_InteractionRT, uv);

                // 지금 RT에 뭐가 들어가든 그냥 보여주기
                return half4(c.rgb, 1);
            }
            ENDHLSL
        }
    }
}
