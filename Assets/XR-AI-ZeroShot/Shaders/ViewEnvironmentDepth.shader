Shader "Unlit/ViewEnvironmentDepth"
{
    Properties
    {
        _ArraySlice ("Slice", Float) = 0
        _MaxMeters ("Max Display Depth (m)", Float) = 6
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_ARRAY(_EnvironmentDepthTexture);
            SAMPLER(sampler_EnvironmentDepthTexture);
            float _ArraySlice;
            float _MaxMeters;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                int slice = (int)round(_ArraySlice);
                float depthMeters = SAMPLE_TEXTURE2D_ARRAY(_EnvironmentDepthTexture, sampler_EnvironmentDepthTexture, IN.uv, slice).r;
                float grey = saturate(depthMeters / _MaxMeters);
                return half4(grey, grey, grey, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
