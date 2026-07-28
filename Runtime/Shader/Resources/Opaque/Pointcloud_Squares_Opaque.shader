// Opaque billboard point shader for PointcloudRendererRT, and its default material.
//
// Same geometry and data flow as the "Circles"/"Quads" Shader Graph materials - a static quad mesh
// whose corners are placed from the position/color render textures written by
// Resources/ShaderGraph/Pointcloud_Shadergraph.compute, indexed by SV_VertexID - but with no
// `clip`/`discard` anywhere. That is the entire point of this shader: on Apple's tile-based
// deferred GPUs a discard in any pass disables hidden surface removal for that draw, so every
// occluded point on the far side of a capture gets fully shaded. Squares instead of circles buys
// back per-fragment occlusion culling in hardware, which for a dense cloud is most of the frame.
// Assign one of the Shader Graph circle materials via GeometrySequenceStream.customMaterial to
// trade that back for round points.
//
// The unused tail of the quad mesh must not be drawn: with no alpha clip, a zero alpha no longer
// hides it. PointcloudRendererRT.SetVisiblePointCount() is what guarantees that, by shrinking the
// submesh to the current frame's point count. The two changes only work as a pair.
//
// No ShadowCaster pass, deliberately: a megapoint cloud re-rendered per shadow cascade is not
// affordable on device, and the points are unlit anyway. Set the MeshRenderer's shadowCastingMode
// if you ever need it back - it would have to be added here first.
//
// Texture2D.Load (rather than a sampler) needs SM4.0+, hence target 4.5. That excludes GLES2/3.0;
// Metal, Vulkan and D3D11 are all fine.
Shader "Pointclouds/Pointcloud_Squares_Opaque"
{
    Properties
    {
        _PointScale("Point Size (object units)", Range(0.0001, 1.0)) = 0.02
        _PointEmission("Emission Strength", Range(0.0, 10.0)) = 1.0
        // Set from PointcloudRendererRT. Declared so Material.HasFloat/SetFloat find them.
        _RTResolution("Point Data RT Resolution", Float) = 1
        [NoScaleOffset] _PositionSourceRT("Point Positions", 2D) = "black" {}
        [NoScaleOffset] _ColorSourceRT("Point Colors", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        LOD 100

        HLSLINCLUDE
        #pragma target 4.5

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        Texture2D<float4> _PositionSourceRT;
        Texture2D<float4> _ColorSourceRT;

        CBUFFER_START(UnityPerMaterial)
            float _PointScale;
            float _PointEmission;
            float _RTResolution;
        CBUFFER_END

        // The point data render textures are square, filled row-major by the compute shader:
        // linear index = x + y * _RTResolution (see Pointcloud_Shadergraph.compute).
        int3 PointTexel(uint vertexID)
        {
            uint pointIndex = vertexID >> 2; // four quad corners per point
            uint stride = max((uint)_RTResolution, 1u);
            return int3(pointIndex % stride, pointIndex / stride, 0);
        }

        // Places one quad corner around its point's center, facing the camera. positionOS carries the
        // corner offset (+-0.5) baked into the mesh by PointcloudRendererRT.MeshCreation(); the center
        // comes from the position RT and is in the stream object's local space.
        float3 BillboardPositionWS(float2 corner, int3 texel)
        {
            float3 centerWS = TransformObjectToWorld(_PositionSourceRT.Load(texel).xyz);

            // Fold in object scale so points track the stream transform, matching the Shader Graph
            // paths whose quad corners ride the object->world matrix. Uniform scale assumed, as there.
            float objScale = length(UNITY_MATRIX_M._m00_m10_m20);

            // Camera right/up in world space. UNITY_MATRIX_I_V is camera-to-world.
            float3 camRight = UNITY_MATRIX_I_V._m00_m10_m20;
            float3 camUp    = UNITY_MATRIX_I_V._m01_m11_m21;

            return centerWS + (camRight * corner.x + camUp * corner.y) * (_PointScale * objScale);
        }
        ENDHLSL

        Pass
        {
            Name "Unlit"
            Tags { "LightMode"="UniversalForward" }

            ZWrite On
            ZTest LEqual
            // Cull Off, not Back: the quads are rebuilt every frame around the camera basis, so their
            // winding flips with a mirrored or negatively scaled stream transform and back-face culling
            // would drop the whole cloud. Each quad is single-sided regardless, so nothing is gained by
            // culling it.
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            struct Attributes
            {
                float4 positionOS : POSITION;
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float fogCoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                int3 texel = PointTexel(IN.vertexID);

                OUT.positionCS = TransformWorldToHClip(BillboardPositionWS(IN.positionOS.xy, texel));
                OUT.color = half4(_ColorSourceRT.Load(texel).rgb, 1.0h);
                OUT.fogCoord = ComputeFogFactor(OUT.positionCS.z);

                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half4 col = IN.color * _PointEmission;
                col.rgb = MixFog(col.rgb, IN.fogCoord);
                col.a = 1.0h;
                return col;
            }
            ENDHLSL
        }

        // Needed for URP's depth texture and depth prepass. The prepass is worth having here: it
        // resolves the cloud's own occlusion before any shading happens.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma multi_compile_instancing

            struct AttributesDepth
            {
                float4 positionOS : POSITION;
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct VaryingsDepth
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            VaryingsDepth vertDepth (AttributesDepth IN)
            {
                VaryingsDepth OUT = (VaryingsDepth)0;

                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionCS = TransformWorldToHClip(BillboardPositionWS(IN.positionOS.xy, PointTexel(IN.vertexID)));

                return OUT;
            }

            half fragDepth (VaryingsDepth IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
