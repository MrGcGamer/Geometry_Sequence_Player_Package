// Shared URP point shader for pointcloud sequences rendered as MeshTopology.Points.
// Each point is a GPU point primitive whose pixel size is derived from a world-space diameter
// (_PointSize, scaled by the object's world scale), so points scale with distance and with the
// object transform like the billboard renderers do - but without the quad geometry or the per-frame
// compute pass. Used by both PointcloudRendererPoints and DracoPointcloudRenderer, via the shared
// default material at Resources/Points/Pointcloud_Points.
//
// Points are clipped to circles in the fragment shader for visual parity with the billboard
// renderers (Legacy/Shadergraph "Circle" paths). GPU point primitives rasterize as squares and
// URP exposes no portable point-sprite coord ([[point_coord]]), so instead we reconstruct each
// fragment's offset from the point's screen-space center via SV_POSITION (VPOS) and discard
// fragments outside the point's pixel radius. This is portable across Metal/Vulkan/GL/D3D.
Shader "Pointclouds/Pointcloud_Points"
{
    Properties
    {
        _PointSize("Point Size (world units)", Range(0.0001, 1.0)) = 0.02
        _Emission("Emission Strength", Range(0.0, 10.0)) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Name "Unlit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
                float _PointSize;
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

                // Fold in the object's world scale so _PointSize tracks the object transform like the
                // quad-based paths (Legacy/Shadergraph), whose point geometry lives in object space and
                // rides the object->world matrix. Uniform scale assumed (matches those paths); take it
                // from one basis vector of the model matrix.
                float objScale = length(UNITY_MATRIX_M._m00_m10_m20);

                // Convert a world-space diameter into a screen-space pixel size for this point:
                // pixels = worldDiameter * (viewportHeight * 0.5 * P[1][1]) / clipW
                // abs() on _m11: Unity flips clip-space Y (negative _m11) when rendering to the game
                // view / a RenderTexture on Metal & D3D. Without abs(), positive sizes go negative and
                // get clamped to 1px while only negative _PointSize renders large.
                float pixels = _PointSize * objScale * _ScreenParams.y * 0.5 * abs(UNITY_MATRIX_P._m11) / max(OUT.positionCS.w, 1e-5);
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

            half4 frag (Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // Discard fragments outside the point's circle. IN.positionCS holds the fragment's
                // pixel position (VPOS); centerSP/centerSP.w is the point center in [0,1] screen UV.
                // Skip the test for ~1px points where a circle is indistinguishable from a square
                // and the discard would just thin the cloud.
                if (IN.pixelRadius > 0.75)
                {
                    float2 centerUV = IN.centerSP.xy / IN.centerSP.w;
                    float2 offsetPix = (IN.positionCS.xy / _ScreenParams.xy - centerUV) * _ScreenParams.xy;
                    if (dot(offsetPix, offsetPix) > IN.pixelRadius * IN.pixelRadius)
                        discard;
                }

                half4 col = IN.color * _Emission;
                col.rgb = MixFog(col.rgb, IN.fogCoord);
                return col;
            }
            ENDHLSL
        }
    }
}
