Shader "Pointclouds/Pointcloud_Circle"
{
    Properties
    {
        _Emission("Emission Strength", Range(0.0,10.0)) = 1.0
        _Alpha("Opacity", Range(0.0, 1.0)) = 1.0

        // Rewritten by PointcloudRenderer.ApplyOpacity to switch between the alpha-tested default and
        // alpha blending, the way URP's own Lit shader does it. Pointcloud_Splat already ships with
        // the blended state hard-coded, which is where these values come from.
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        
        LOD 100

        Lighting Off

        // The vertex stage and the circle test live here, shared by every pass on purpose. URP fills
        // depth from the DepthOnly pass and then draws opaques with ZTest Equal, so a DepthOnly pass
        // that keeps different fragments from the forward one makes the cloud disappear silently.
        // Duplicating the code per pass is how that drifts apart.
        CGINCLUDE
        #include "UnityCG.cginc"

        struct appdata
        {
            float4 vertex : POSITION;
            fixed4 color : COLOR;
            float2 texcoord : TEXCOORD0;

            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct v2f
        {
            UNITY_FOG_COORDS(1)
            float4 vertex : SV_POSITION;
            fixed4 color : COLOR;
            float2 texcoord : TEXCOORD0;

            UNITY_VERTEX_OUTPUT_STEREO
        };

        float _Emission;
        float _Alpha;

        v2f vert (appdata v)
        {
            v2f o;

            UNITY_SETUP_INSTANCE_ID(v); //Insert
            UNITY_INITIALIZE_OUTPUT(v2f, o); //Insert
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o); //Insert

            o.vertex = UnityObjectToClipPos(v.vertex);
            o.color = v.color;
            o.texcoord = v.texcoord;
            UNITY_TRANSFER_FOG(o,o.vertex);
            return o;
        }

        float circle(float2 uv) 
        {
            float2 center = float2(0, 0);
	        float d = length(center - uv) - 0.5;
            float t = step(0.5, 1.0 - d);
	        return t;
        }

        ENDCG

        Pass
        {
            Name "Unlit"

            // Alpha-tested defaults (One/Zero). Alpha's factors stay One/OneMinusSrcAlpha rather than
            // following the colour ones: destination alpha is what visionOS composites the frame
            // against passthrough with, and SrcAlpha/OneMinusSrcAlpha there would square it.
            Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha

            // On even while blending. A cloud is thousands of unsorted quads; without the depth write
            // whichever one is drawn last wins the pixel outright, and a point in front of another
            // reads as having vanished. See PointcloudRendererBase.ApplyMaterialDrivenOpacity.
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // make fog work
            #pragma multi_compile_fog

            fixed4 frag (v2f i) : SV_Target
            {
                // _Emission scales colour only; it used to scale the alpha too, which was
                // harmless only while nothing read the alpha.
                fixed4 col = fixed4(i.color.rgb * _Emission, _Alpha);
                clip(circle(i.texcoord) - 0.5);

                // apply fog
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }

            ENDCG
        }

        // Without this pass the cloud is invisible whenever the active Universal Renderer Data has
        // Depth Priming Mode set to anything but Disabled: URP primes depth from DepthOnly and then
        // draws opaques ZTest Equal, so geometry that never wrote the prepass fails every fragment,
        // with no error to go on. It only shows up in the opaque queue - below full opacity the
        // material moves to the transparent queue, which is not primed, and the cloud reappears.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ColorMask R

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth

            fixed4 fragDepth (v2f i) : SV_Target
            {
                clip(circle(i.texcoord) - 0.5);

                return 0;
            }

            ENDCG
        }
    }
}
