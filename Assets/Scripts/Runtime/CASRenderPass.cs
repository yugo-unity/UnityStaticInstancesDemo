using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace CAS
{
    /// <summary>
    /// Fullscreen raster pass running AMD FidelityFX CAS (ffx_cas.hlsl) on the tonemapped camera color.
    /// Sharpen-only at renderScale 1.0, or sharpen + upscale to the output resolution otherwise.
    /// When CAS is the last write of the frame it renders straight to the backbuffer and URP skips its final blit.
    /// Otherwise the result replaces resourceData.cameraColor and URP's final blit copies it 1:1.
    /// </summary>
    class CASRenderPass : ScriptableRenderPass
    {
        static class ShaderConstants
        {
            public static readonly int _CasConst0 = Shader.PropertyToID("_CasConst0");
            public static readonly int _CasConst1 = Shader.PropertyToID("_CasConst1");
            public static readonly int _CasInputSize = Shader.PropertyToID("_CasInputSize");
            public static readonly int _CasOutputSize = Shader.PropertyToID("_CasOutputSize");
            public static readonly int _CasInput = Shader.PropertyToID("_CasInput");
            public static readonly int _CasOutput = Shader.PropertyToID("_CasOutput");
            public static readonly int _CasFlipY = Shader.PropertyToID("_CasFlipY");
        }

        // kernel order in CAS.compute: (noScaling ? 0 : 2) + (lowQuality ? 1 : 0), same as the raster pass index
        static readonly string[] COMPUTE_KERNELS = { "CasSharpen", "CasSharpenLQ", "CasUpscale", "CasUpscaleLQ" };

        public const string DEBUG_CHECKER_KEYWORD = "CAS_DEBUG_CHECKER";

        /// <summary>Upscale ratio per axis. 1.0 = sharpen only, up to 2.0 (CAS_AREA_LIMIT = 4.0).</summary>
        public const float MIN_SCALING = 1f;
        public const float MAX_SCALING = 2f;

        static readonly double INTEGER_CONVERSION = Math.Pow(10, 7);

        Material material;
        ComputeShader computeShader;
        int[] kernels;
        bool useCompute;
        bool computeFallbackWarned;
        float scaling = MIN_SCALING;
        float sharpness = 0.5f;
        bool lowQuality;
        Vector2Int inputExtent, outputExtent;

        public Vector2Int inputResolution => this.inputExtent;
        public Vector2Int outputResolution => this.outputExtent;
        public bool noScaling => this.scaling <= MIN_SCALING;
        /// <summary>True when the last recorded frame actually dispatched the compute path.</summary>
        public bool computeActive { get; private set; }
        /// <summary>True when the last recorded frame wrote straight to the backbuffer (no URP final blit).</summary>
        public bool backbufferActive { get; private set; }

        public void Create(Material material, ComputeShader computeShader = null)
        {
            this.material = material;
            this.computeShader = computeShader;
            this.kernels = null;
            if (computeShader != null)
            {
                this.kernels = new int[COMPUTE_KERNELS.Length];
                for (var i = 0; i < COMPUTE_KERNELS.Length; i++)
                    this.kernels[i] = computeShader.HasKernel(COMPUTE_KERNELS[i]) ? computeShader.FindKernel(COMPUTE_KERNELS[i]) : -1;
            }
            this.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            this.profilingSampler = new ProfilingSampler("CAS");
        }

        /// <summary>
        /// Returns the renderScale to apply to the URP asset. inputExtent is a prediction for display;
        /// the real size is taken from the source texture every frame in RecordRenderGraph.
        /// </summary>
        public float UpdatePassSettings(Vector2Int targetExtent, float scaling)
        {
            this.scaling = Mathf.Clamp(scaling, MIN_SCALING, MAX_SCALING);
            this.outputExtent = targetExtent;

            var renderScale = 1f;
            if (this.noScaling)
            {
                this.inputExtent = targetExtent;
            }
            else
            {
                // same rounding as StatsCanvasController.CalcRenderScale / NDSR editor path
                renderScale = (float)(Math.Ceiling(INTEGER_CONVERSION / this.scaling) / INTEGER_CONVERSION);
                this.inputExtent = new Vector2Int((int)(targetExtent.x * renderScale), (int)(targetExtent.y * renderScale));
            }

            UexLogger.Log(
                $"----------------------------- UPDATE CAS -----------------------------\n" +
                $"-- Scaling : x{this.scaling}\n" +
                $"-- Render Scale : {renderScale}\n" +
                $"-- Source : {this.inputExtent}, Destination : {this.outputExtent}\n" +
                $"----------------------------------------------------------------------");

            return renderScale;
        }

        public void SetFilterSettings(float sharpness, bool lowQuality, bool debugChecker, bool useCompute = false)
        {
            this.sharpness = Mathf.Clamp01(sharpness);
            this.lowQuality = lowQuality;
            this.useCompute = useCompute;
            if (this.material != null)
                CoreUtils.SetKeyword(this.material, DEBUG_CHECKER_KEYWORD, debugChecker);
        }

        /// <summary>
        /// Compute needs a UAV-capable, non-sRGB destination. Returns the format to use, or None if the compute path
        /// cannot run for this frame (caller falls back to the raster pass).
        /// </summary>
        GraphicsFormat ResolveComputeFormat(GraphicsFormat preferred)
        {
            if (this.computeShader == null || this.kernels == null)
                return GraphicsFormat.None;
            var kernel = this.kernels[(this.noScaling ? 0 : 2) + (this.lowQuality ? 1 : 0)];
            if (kernel < 0)
                return GraphicsFormat.None;

            if (!GraphicsFormatUtility.IsSRGBFormat(preferred) && SystemInfo.IsFormatSupported(preferred, GraphicsFormatUsage.LoadStore))
                return preferred;
            if (SystemInfo.IsFormatSupported(GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormatUsage.LoadStore))
                return GraphicsFormat.R8G8B8A8_UNorm;
            return GraphicsFormat.None;
        }

        /// <summary>
        /// C# port of CasSetup() in ffx_cas.hlsl. const1.y carries a packed half2 bit pattern (only used by CasFilterH).
        /// </summary>
        static void CasSetup(float sharpness, Vector2 inputSize, Vector2 outputSize, out Vector4 const0, out Vector4 const1)
        {
            var scaleX = inputSize.x / outputSize.x;
            var scaleY = inputSize.y / outputSize.y;
            const0 = new Vector4(scaleX, scaleY, 0.5f * scaleX - 0.5f, 0.5f * scaleY - 0.5f);

            var sharp = -1f / Mathf.Lerp(8f, 5f, Mathf.Clamp01(sharpness));
            var sharpAsHalf = Mathf.FloatToHalf(sharp);
            var packed = (int)(uint)sharpAsHalf; // AU1_AH2_AF2(sharp, 0.0): low 16 bits = sharp, high 16 bits = 0
            const1 = new Vector4(sharp, BitConverter.Int32BitsToSingle(packed), 8f * scaleX, 0f);
        }

        class PassData
        {
            public TextureHandle source;
            public TextureHandle destination;
            public Material material;
            public int passIndex;
            public Vector4 const0;
            public Vector4 const1;
            public Vector4 inputSize;
            public int outputHeight;
        }

        static void ExecutePass(PassData data, RasterGraphContext context)
        {
            // CAS addresses pixels by SV_Position, so flip rows when the target's UV origin differs (same test as URP's final blit)
            var flipY = context.GetTextureUVOrigin(data.source) != context.GetTextureUVOrigin(data.destination);
            data.material.SetVector(ShaderConstants._CasConst0, data.const0);
            data.material.SetVector(ShaderConstants._CasConst1, data.const1);
            data.material.SetVector(ShaderConstants._CasInputSize, data.inputSize);
            data.material.SetVector(ShaderConstants._CasFlipY, new Vector4(flipY ? 1f : 0f, data.outputHeight - 1, 0f, 0f));
            Blitter.BlitTexture(context.cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, data.passIndex);
        }

        /// <summary>
        /// True when the CAS output can go straight to the backbuffer: this camera resolves to the screen, the output
        /// covers the whole backbuffer, and nothing URP records after this pass reads the intermediate color
        /// (FinalPost, capture, Rendering Debugger, UI Toolkit backdrop composite).
        /// </summary>
        static bool CanWriteToBackbuffer(RenderGraph renderGraph, UniversalResourceData resourceData, UniversalCameraData cameraData, Vector2Int outSize)
        {
            if (!cameraData.resolveFinalTarget || !cameraData.isDefaultViewport || cameraData.isHDROutputActive || cameraData.xr.enabled)
                return false;
            if (cameraData.captureActions != null)
                return false;
            // a non-sRGB backbuffer needs the encoding URP's final blit applies
            if (cameraData.requireSrgbConversion)
                return false;

            // URP's FinalPost runs after every user pass and reads the intermediate color
            if (cameraData.antialiasing == AntialiasingMode.FastApproximateAntialiasing)
                return false;
            if (cameraData.IsTemporalAAEnabled() && cameraData.taaSettings.contrastAdaptiveSharpening > 0f)
                return false;
#if UNITY_6000_5_OR_NEWER
            if (cameraData.imageScalingMode == ImageScalingMode.Upscaling && cameraData.upscalingFilter == ImageUpscalingFilter.FSR)
                return false;
#else
            // older URP also runs FinalPost for point upscaling
            if (cameraData.imageScalingMode == ImageScalingMode.Upscaling && cameraData.upscalingFilter != ImageUpscalingFilter.Linear)
                return false;
#endif

            // Rendering Debugger overrides and validation are applied by URP's final blit
            if (GetActiveDebugHandler(cameraData) != null)
                return false;

#if UNITY_6000_6_OR_NEWER && ENABLE_UIELEMENTS_MODULE
            // UI Toolkit backdrop filters composite the overlay UI against a copy of the intermediate color
            if (cameraData.rendersOverlayUI && UnityEngine.UIElements.UIElementsRuntimeUtility.AnyOverlayPanelHasBackdropFilter())
                return false;
#endif

            var backbuffer = renderGraph.GetRenderTargetInfo(resourceData.backBufferColor);
            return backbuffer.width == outSize.x && backbuffer.height == outSize.y;
        }

        class ComputePassData
        {
            public TextureHandle source;
            public TextureHandle destination;
            public ComputeShader computeShader;
            public int kernel;
            public Vector4 const0;
            public Vector4 const1;
            public Vector4 inputSize;
            public Vector4 outputSize;
            public int groupsX, groupsY;
        }

        static void ExecuteComputePass(ComputePassData data, ComputeGraphContext context)
        {
            var cmd = context.cmd;
            cmd.SetComputeVectorParam(data.computeShader, ShaderConstants._CasConst0, data.const0);
            cmd.SetComputeVectorParam(data.computeShader, ShaderConstants._CasConst1, data.const1);
            cmd.SetComputeVectorParam(data.computeShader, ShaderConstants._CasInputSize, data.inputSize);
            cmd.SetComputeVectorParam(data.computeShader, ShaderConstants._CasOutputSize, data.outputSize);
            cmd.SetComputeTextureParam(data.computeShader, data.kernel, ShaderConstants._CasInput, data.source);
            cmd.SetComputeTextureParam(data.computeShader, data.kernel, ShaderConstants._CasOutput, data.destination);
            cmd.DispatchCompute(data.computeShader, data.kernel, data.groupsX, data.groupsY, 1);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (this.material == null)
                return;

            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
#if UNITY_EDITOR
            if (cameraData.isSceneViewCamera)
                return;
#endif
            var source = resourceData.activeColorTexture;
            var srcDesc = renderGraph.GetTextureDesc(source);

            // use the real size, not the predicted one (URP truncates pixelWidth * renderScale)
            var inSize = new Vector2Int(srcDesc.width, srcDesc.height);
            this.inputExtent = inSize;
            var outSize = this.noScaling ? inSize : this.outputExtent;

            var destDesc = srcDesc;
#if UNITY_6000_4_OR_NEWER
            PostProcessUtils.MakeCompatible(ref destDesc);
#else
            PostProcessPassRenderGraph.MakeCompatible(ref destDesc);
#endif
            // Values are already tonemapped to 0..1; a UNORM target avoids re-quantizing into a float format (same as URP FinalPost)
            if (!cameraData.isHDROutputActive)
                destDesc.format = UniversalRenderPipeline.MakeUnormRenderTextureGraphicsFormat();
            destDesc.name = "CAS Output";
            destDesc.width = outSize.x;
            destDesc.height = outSize.y;
            destDesc.clearBuffer = false;

            // Compute path (AMD reference layout). Falls back to raster when the target cannot be a UAV.
            var computeFormat = this.useCompute ? this.ResolveComputeFormat(destDesc.format) : GraphicsFormat.None;
            this.computeActive = computeFormat != GraphicsFormat.None;
            if (this.useCompute && !this.computeActive && !this.computeFallbackWarned)
            {
                this.computeFallbackWarned = true;
                UexLogger.LogWarning($"CAS: compute path unavailable (shader={(this.computeShader != null)}, format={destDesc.format}), using raster pass");
            }

            if (this.computeActive)
            {
                // a UAV cannot target the backbuffer, so the compute path always goes through URP's final blit
                this.backbufferActive = false;
                destDesc.format = computeFormat;
                destDesc.enableRandomWrite = true;
                var computeDestination = renderGraph.CreateTexture(destDesc);

                using (var builder = renderGraph.AddComputePass<ComputePassData>(this.passName, out var computeData, this.profilingSampler))
                {
                    computeData.source = source;
                    computeData.destination = computeDestination;
                    computeData.computeShader = this.computeShader;
                    computeData.kernel = this.kernels[(this.noScaling ? 0 : 2) + (this.lowQuality ? 1 : 0)];
                    CasSetup(this.sharpness, inSize, outSize, out computeData.const0, out computeData.const1);
                    computeData.inputSize = new Vector4(inSize.x, inSize.y, 1f / inSize.x, 1f / inSize.y);
                    computeData.outputSize = new Vector4(outSize.x, outSize.y, 1f / outSize.x, 1f / outSize.y);
                    // one {64,1,1} group covers 16x16 output pixels
                    computeData.groupsX = (outSize.x + 15) >> 4;
                    computeData.groupsY = (outSize.y + 15) >> 4;

                    builder.UseTexture(source, AccessFlags.Read);
                    builder.UseTexture(computeDestination, AccessFlags.WriteAll);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc<ComputePassData>(ExecuteComputePass);
                }

                resourceData.cameraColor = computeDestination;
                return;
            }

            // Rendering straight to the screen saves URP's 1:1 final blit; the backbuffer keeps its own format (destDesc is unused).
            var toBackbuffer = CanWriteToBackbuffer(renderGraph, resourceData, cameraData, outSize);
            if (toBackbuffer != this.backbufferActive)
                UexLogger.Log(toBackbuffer ? "CAS: output to the backbuffer" : "CAS: output to an intermediate texture (URP final blit)");
            this.backbufferActive = toBackbuffer;
            var destination = this.backbufferActive ? resourceData.backBufferColor : renderGraph.CreateTexture(destDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(this.passName, out var passData, this.profilingSampler))
            {
                passData.source = source;
                passData.destination = destination;
                passData.material = this.material;
                passData.passIndex = (this.noScaling ? 0 : 2) + (this.lowQuality ? 1 : 0);
                CasSetup(this.sharpness, inSize, outSize, out passData.const0, out passData.const1);
                passData.inputSize = new Vector4(inSize.x, inSize.y, 1f / inSize.x, 1f / inSize.y);
                passData.outputHeight = outSize.y;

                builder.UseTexture(source, AccessFlags.Read);
                // same load action as URP's final blit: keep the backbuffer contents when alpha is output
                builder.SetRenderAttachment(destination, 0, this.backbufferActive && cameraData.isAlphaOutputEnabled ? AccessFlags.Write : AccessFlags.WriteAll);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc<PassData>(ExecutePass);
            }

            if (this.backbufferActive)
            {
                // URP adds its final blit only while the active target is not the backbuffer
                resourceData.SwitchActiveTexturesToBackbuffer();
            }
            else
            {
                // swap buffer
                resourceData.cameraColor = destination;
            }
        }
    }
}
