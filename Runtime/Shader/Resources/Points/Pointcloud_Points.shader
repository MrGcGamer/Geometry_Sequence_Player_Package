// Shared URP point shader for pointcloud sequences rendered as MeshTopology.Points.
// Each point is a GPU point primitive whose pixel size is derived from a world-space diameter
// (_PointScale, scaled by the object's world scale), so points scale with distance and with the
// object transform like the billboard renderers do - but without the quad geometry or the per-frame
// compute pass. Used by PointcloudRendererPoints, via the shared default material at
// Resources/Points/Pointcloud_Points.
//
// Points are clipped to circles in the fragment shader for visual parity with the billboard
// renderers (Legacy/Shadergraph "Circle" paths). GPU point primitives rasterize as squares and
// URP exposes no portable point-sprite coord ([[point_coord]]), so instead we reconstruct each
// fragment's offset from the point's screen-space center via SV_POSITION (VPOS) and discard
// fragments outside the point's pixel radius. This is portable across Metal/Vulkan/GL/D3D, and
// foveation-aware - see PointOutsideCircle for why the centre is the side that gets remapped.
//
// The vertex stage and the circle test live in HLSLINCLUDE and are shared by every pass on purpose.
// DepthOnly has to rasterize the exact same fragments the forward pass keeps: under depth priming
// URP fills depth from DepthOnly and then draws opaques with ZTest Equal, so any divergence between
// the two - a different point size, a different discard - makes the forward pass fail the depth test
// and the cloud disappears silently. Duplicating the code here is how that drifts.
Shader "Pointclouds/Pointcloud_Points"
{
    Properties
    {
        _PointScale("Point Size (world units)", Range(0.0001, 1.0)) = 0.02
        _Emission("Emission Strength", Range(0.0, 10.0)) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        LOD 100

        HLSLINCLUDE
        #pragma multi_compile_instancing

        #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRendering.hlsl"

        struct Attributes
        {
            float4 positionOS : POSITION;
            half4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            half4 color : COLOR;
            float size : PSIZE; // point size in pixels (Metal [[point_size]])
            float fogCoord : TEXCOORD0;
            float4 centerSP : TEXCOORD1; // point center, ComputeScreenPos form (constant across the point primitive)
            float pixelRadius : TEXCOORD2; // point radius in pixels (constant across the point primitive)
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        CBUFFER_START(UnityPerMaterial)
            float _PointScale;
            float _Emission;
        CBUFFER_END

        Varyings vert (Attributes IN)
        {
            Varyings OUT = (Varyings)0;

            UNITY_SETUP_INSTANCE_ID(IN);
            UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

            VertexPositionInputs positions = GetVertexPositionInputs(IN.positionOS.xyz);
            OUT.positionCS = positions.positionCS;
            OUT.color = IN.color;

            // Fold in the object's world scale so _PointScale tracks the object transform like the
            // quad-based paths (Legacy/Shadergraph), whose point geometry lives in object space and
            // rides the object->world matrix. Uniform scale assumed (matches those paths); take it
            // from one basis vector of the model matrix.
            float objScale = length(UNITY_MATRIX_M._m00_m10_m20);

            // Convert a world-space diameter into a screen-space pixel size for this point:
            // pixels = worldDiameter * (viewportHeight * 0.5 * P[1][1]) / clipW
            // abs() on _m11: Unity flips clip-space Y (negative _m11) when rendering to the game
            // view / a RenderTexture on Metal & D3D. Without abs(), positive sizes go negative and
            // get clamped to 1px while only negative _PointScale renders large.
            float pixels = _PointScale * objScale * _ScreenParams.y * 0.5 * abs(UNITY_MATRIX_P._m11) / max(OUT.positionCS.w, 1e-5);
            OUT.size = max(pixels, 1.0);
            OUT.pixelRadius = OUT.size * 0.5;

            // Screen-space center of the point, in the same convention as the fragment's VPOS
            // (SV_POSITION). This is Unity's ComputeScreenPos: _ProjectionParams.x carries the
            // render-target Y-flip so it matches VPOS on every platform. For a point primitive
            // these varyings are constant across all of its fragments (= the center).
            float4 sp = OUT.positionCS * 0.5;
            sp.xy = float2(sp.x, sp.y * _ProjectionParams.x) + sp.w;
            sp.zw = OUT.positionCS.zw;
            OUT.centerSP = sp;

            OUT.fogCoord = ComputeFogFactor(OUT.positionCS.z);
            return OUT;
        }

        // True for fragments outside the point's circle. IN.positionCS holds the fragment's pixel
        // position (VPOS); centerSP/centerSP.w is the point center in [0,1] screen UV. The test is
        // skipped for ~1px points where a circle is indistinguishable from a square and the discard
        // would just thin the cloud.
        //
        // The centre is remapped forward into the rasterisation-rate map's space rather than VPOS
        // being remapped back out of it. Both directions were measured on Apple Vision Pro:
        // remapping the per-fragment value collapses the offset to zero and nothing is ever
        // discarded (square points), and comparing across the two spaces untouched makes the offset
        // large enough that everything is discarded (the cloud vanishes as you approach). Only the
        // centre may cross. The radius stays in the rasterizer's own space deliberately: PSIZE fed
        // it the same figure, so the circle inscribes the square footprint exactly whatever the
        // local rate happens to be.
        bool PointOutsideCircle (Varyings IN)
        {
            if (IN.pixelRadius <= 0.75)
                return false;

            float2 centerRaster = FoveatedRemapLinearToNonUniform(IN.centerSP.xy / IN.centerSP.w);
            float2 offsetPix = (IN.positionCS.xy / _ScreenParams.xy - centerRaster) * _ScreenParams.xy;
            return dot(offsetPix, offsetPix) > IN.pixelRadius * IN.pixelRadius;
        }
        ENDHLSL

        Pass
        {
            Name "Unlit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            half4 frag (Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                if (PointOutsideCircle(IN))
                    discard;

                half4 col = IN.color * _Emission;
                col.rgb = MixFog(col.rgb, IN.fogCoord);
                return col;
            }
            ENDHLSL
        }

        // Without this pass the cloud is invisible whenever the active Universal Renderer Data has
        // Depth Priming Mode set to Anything but Disabled, and it never contributes to
        // _CameraDepthTexture, so nothing depth-based can see it. Shader Graph generates the
        // equivalent automatically, which is why only the hand-written point path was affected.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth

            half4 fragDepth (Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                if (PointOutsideCircle(IN))
                    discard;

                return 0;
            }
            ENDHLSL
        }
    }
}
