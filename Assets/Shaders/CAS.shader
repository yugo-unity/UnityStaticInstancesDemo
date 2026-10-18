// AMD FidelityFX Contrast Adaptive Sharpening (CAS) as a URP fullscreen pass.
// Algorithm: Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/ffx/ffx_cas.hlsl (MIT, AMD)
//
// Pass 0 : Sharpen    (no scaling, high quality)
// Pass 1 : SharpenLQ  (no scaling, weight by green only)
// Pass 2 : Upscale    (scaling <= 4x area, high quality)
// Pass 3 : UpscaleLQ  (scaling <= 4x area, weight by green only)
Shader "Hidden/Upscalers/CAS"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Blend Off
        Cull Off

        HLSLINCLUDE
        #pragma target 4.5

        // URP Core.hlsl brings in TextureXR.hlsl (TEXTURE2D_X / LOAD_TEXTURE2D_X) that Blit.hlsl relies on
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        #define A_GPU 1
        #define A_HLSL 1
        #include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/ffx/ffx_a.hlsl"

        // Constants produced by CASRenderPass.CasSetup (C# port of ffx_cas.hlsl CasSetup).
        // Unity cannot bind uint4, so they travel as float4 and are reinterpreted with asuint().
        float4 _CasConst0;
        float4 _CasConst1;
        // xy = input size in pixels, zw = 1 / input size
        float4 _CasInputSize;
        // x = 1 when the target's UV origin differs from the input (the backbuffer on top-left APIs), y = output height - 1
        float4 _CasFlipY;

        AF3 CasLoad(ASU2 p)
        {
            // CasFilter reads up to (-1,-1)..(+2,+2) around the sample point; keep it inside the input.
            p = clamp(p, ASU2(0, 0), ASU2(_CasInputSize.xy) - ASU2(1, 1));
            return LOAD_TEXTURE2D_X(_BlitTexture, p).rgb;
        }

        // Input is tonemapped linear color in 0..1 (AfterRenderingPostProcessing). No transform needed.
        void CasInput(inout AF1 r, inout AF1 g, inout AF1 b) {}

        #include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/ffx/ffx_cas.hlsl"

        float4 FragCAS(Varyings input, const bool noScaling, const bool lowQuality)
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            // Integer pixel position in the output, in the input's row order.
            AU2 ip = AU2(input.positionCS.xy);
            if (_CasFlipY.x > 0.5)
                ip.y = AU1(_CasFlipY.y) - ip.y;

            AF4 c;
            CasFilter(c.r, c.g, c.b, ip, asuint(_CasConst0), asuint(_CasConst1), noScaling, lowQuality);

            // Pass alpha through from the nearest input pixel.
            ASU2 center = noScaling ? ASU2(ip) : ASU2(AF2(ip) * _CasConst0.xy + _CasConst0.zw + AF2(0.5, 0.5));
            center = clamp(center, ASU2(0, 0), ASU2(_CasInputSize.xy) - ASU2(1, 1));
            c.a = LOAD_TEXTURE2D_X(_BlitTexture, center).a;
            return c;
        }
        ENDHLSL

        Pass
        {
            Name "Sharpen"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local_fragment _ CAS_DEBUG_CHECKER
            float4 Frag(Varyings input) : SV_Target { return FragCAS(input, true, false); }
            ENDHLSL
        }

        Pass
        {
            Name "SharpenLQ"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local_fragment _ CAS_DEBUG_CHECKER
            float4 Frag(Varyings input) : SV_Target { return FragCAS(input, true, true); }
            ENDHLSL
        }

        Pass
        {
            Name "Upscale"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(Varyings input) : SV_Target { return FragCAS(input, false, false); }
            ENDHLSL
        }

        Pass
        {
            Name "UpscaleLQ"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(Varyings input) : SV_Target { return FragCAS(input, false, true); }
            ENDHLSL
        }
    }
    Fallback Off
}
