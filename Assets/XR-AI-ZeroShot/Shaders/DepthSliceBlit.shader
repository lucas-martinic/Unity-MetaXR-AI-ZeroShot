Shader "Hidden/DepthSliceBlit"
{
    Properties
    {
        _MainTex ("Texture", 2DArray) = "" {}
        _Slice ("Slice", Float) = 0
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            UNITY_DECLARE_TEX2DARRAY(_MainTex);
            float _Slice;

            // We only care about a single pixel, but blitting the whole texture
            // is the standard way to handle format conversions.
            float4 frag (v2f i) : SV_Target
            {
                float depth = UNITY_SAMPLE_TEX2DARRAY(_MainTex, float3(i.uv, _Slice)).r;
                return float4(depth, 0, 0, 1);
            }
            ENDCG
        }
    }
}
