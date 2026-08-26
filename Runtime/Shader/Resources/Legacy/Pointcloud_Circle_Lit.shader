Shader "Pointclouds/Lit"
{
    Properties
    {
       _MainTex ("UV (Dont set a texture here)", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Lambert
        
        sampler2D _MainTex;

        struct Input
        {
            float4 color: Color;
            float2 uv_MainTex : TEXCOORD0;
        };

        float circle(float2 uv) 
        {
            float2 center = float2(0, 0);
	        float d = length(center - uv) - 0.5;
            float t = step(0.5, 1.0 - d);
	        return t;
        }

        void surf (Input IN, inout SurfaceOutput o)
        {
            o.Albedo.rgb = IN.color.rgb;
            float a = circle(IN.uv_MainTex);
            clip(a - 0.5);
        }
        ENDCG

        // Without this the cloud renders nothing at all whenever the active Universal Renderer Data
        // has Depth Priming Mode set to anything but Disabled: URP primes depth from DepthOnly and
        // then draws opaques ZTest Equal, so geometry that never wrote the prepass fails every
        // fragment, silently. A surface shader generates a ShadowCaster but no DepthOnly, so it has
        // to be written out.
        //
        // The circle test is duplicated from surf above rather than shared - a surface shader's body
        // cannot be called from a hand-written pass. Any change to one must be made to the other, or
        // the prepass and the forward pass keep different fragments and the cloud disappears.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ColorMask R

            CGPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #include "UnityCG.cginc"

            struct appdataDepth
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2fDepth
            {
                float4 vertex : SV_POSITION;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _MainTex_ST;

            v2fDepth vertDepth (appdataDepth v)
            {
                v2fDepth o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2fDepth, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            fixed4 fragDepth (v2fDepth i) : SV_Target
            {
                float d = length(float2(0, 0) - i.texcoord) - 0.5;
                clip(step(0.5, 1.0 - d) - 0.5);
                return 0;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
