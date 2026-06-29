Shader "Custom/UnlitCustom"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ MY_FEATURE
            
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };
            
            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.uv = 0;
                OUT.positionHCS = 0;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                if (MY_FEATURE)
                {
                    return 1;   
                }
                else
                {
                    return 0;
                }
            }
            ENDHLSL
        }
    }
}
