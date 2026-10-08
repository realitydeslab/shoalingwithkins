// Bright unlit fish with a travelling tail wiggle, GPU-instanced (per-instance colour and phase).
// HoloKit is optical see-through: dark reads as clear, so the colours stay light.
// Also used flat (_WiggleAmp 0, _Shade 0) for the stereo HUD bars.
Shader "ShoalingWithKin/Fish"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0.8, 0.4, 1)
        _WiggleAmp ("Wiggle Amplitude", Float) = 0.12
        _WiggleFreq ("Wiggle Frequency", Float) = 10
        _Shade ("Shading", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float, _Phase)
            UNITY_INSTANCING_BUFFER_END(Props)

            float _WiggleAmp;
            float _WiggleFreq;
            float _Shade;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float light : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float phase = UNITY_ACCESS_INSTANCED_PROP(Props, _Phase);
                float tail = saturate(v.uv.x);          // 0 at the nose, 1 at the tail fin
                v.vertex.x += sin(_Time.y * _WiggleFreq + phase - v.vertex.z * 6.0) * _WiggleAmp * tail * tail;

                o.pos = UnityObjectToClipPos(v.vertex);
                float3 n = UnityObjectToWorldNormal(v.normal);
                float shaded = 0.6 + 0.4 * saturate(n.y * 0.5 + 0.5) + 0.15 * saturate(v.uv.y - 0.5);
                o.light = lerp(1.0, shaded, _Shade);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float4 c = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                return fixed4(saturate(c.rgb * i.light), 1);
            }
            ENDCG
        }
    }
}
