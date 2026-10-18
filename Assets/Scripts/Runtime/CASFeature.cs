using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CAS
{
    /// <summary>
    /// AMD FidelityFX CAS (Contrast Adaptive Sharpening) for URP.
    /// - Sharpen-only at renderScale 1.0, or sharpen + upscale (up to x2.0 per axis, CAS_AREA_LIMIT = 4)
    /// - Runs only at AfterRenderingPostProcessing (needs tonemapped 0..1 input)
    /// - Pure shader, no native plugin: works in the Editor on every platform
    /// </summary>
    public class CASFeature : ScriptableRendererFeature
    {
        const string SHADER_NAME = "Hidden/Upscalers/CAS";

        public Shader shader;
        [Tooltip("Optional. Compute path (CAS.compute). Falls back to the raster pass when unavailable.")]
        public ComputeShader computeShader;
        [Tooltip("Upscale ratio per axis. 1.0 = sharpen only, up to 2.0 (CAS_AREA_LIMIT = 4).")]
        [Range(CASRenderPass.MIN_SCALING, CASRenderPass.MAX_SCALING)] public float scaling = CASRenderPass.MIN_SCALING;
        [Range(0f, 1f)] public float sharpness = 1f;
        [Tooltip("Weight by the green channel only. Faster, nearly identical except on saturated colors.")]
        public bool lowQuality = true;
        [Tooltip("Run CAS as a compute dispatch instead of a fullscreen raster pass.")]
        public bool useCompute = false;
        [Tooltip("Debug: alternate 256px tiles with/without CAS. Sharpen-only raster passes.")]
        public bool debugChecker = false;

        Material material;
        CASRenderPass renderPass;
        float currentScaling, defaultScaling;
        float currentSharpness, defaultSharpness;
        bool currentLowQuality, defaultLowQuality;
        bool currentUseCompute, defaultUseCompute;
        Vector2Int currentTargetExtent;
        bool updatedSettings;

        public override void Create()
        {
            if (this.shader == null)
            {
                this.shader = Shader.Find(SHADER_NAME);
                if (this.shader == null)
                {
                    UexLogger.LogError($"CASFeature: shader '{SHADER_NAME}' not found. Assign it in the inspector.");
                    return;
                }
                UexLogger.LogWarning($"CASFeature: shader was not assigned, resolved '{SHADER_NAME}' with Shader.Find. Assign it in the inspector so it is included in builds.");
            }

            CoreUtils.Destroy(this.material);
            this.material = CoreUtils.CreateEngineMaterial(this.shader);

            this.renderPass = new CASRenderPass();
            this.renderPass.Create(this.material, this.computeShader);

            this.defaultScaling = Mathf.Clamp(this.scaling, CASRenderPass.MIN_SCALING, CASRenderPass.MAX_SCALING);
            this.defaultSharpness = this.sharpness;
            this.defaultLowQuality = this.lowQuality;
            this.defaultUseCompute = this.useCompute;
            this.ResetSettings();
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(this.material);
            this.material = null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (this.material == null || this.renderPass == null)
                return;

            var cameraData = renderingData.frameData.Get<UniversalCameraData>();

#if UNITY_EDITOR
            if (cameraData.cameraType != CameraType.Game)
                return;

            // pick up inspector edits while playing
            if (!Mathf.Approximately(this.defaultScaling, this.scaling))
            {
                this.defaultScaling = Mathf.Clamp(this.scaling, CASRenderPass.MIN_SCALING, CASRenderPass.MAX_SCALING);
                this.ResetSettings();
            }
            if (!Mathf.Approximately(this.defaultSharpness, this.sharpness))
                this.defaultSharpness = this.currentSharpness = this.sharpness;
            if (this.defaultLowQuality != this.lowQuality)
                this.defaultLowQuality = this.currentLowQuality = this.lowQuality;
            if (this.defaultUseCompute != this.useCompute)
                this.defaultUseCompute = this.currentUseCompute = this.useCompute;
#endif
            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset urpAsset)
                return;

            var targetExtent = new Vector2Int(cameraData.pixelWidth, cameraData.pixelHeight);
            var modifiedResolution = this.currentTargetExtent != targetExtent;
            if (this.updatedSettings || modifiedResolution)
            {
                this.currentTargetExtent = targetExtent;
                urpAsset.renderScale = this.renderPass.UpdatePassSettings(targetExtent, this.currentScaling);
                urpAsset.upscalingFilter = UpscalingFilterSelection.Auto;
                this.updatedSettings = false;
                return; // renderScale will be updated from the next frame
            }

            this.renderPass.SetFilterSettings(this.currentSharpness, this.currentLowQuality, this.debugChecker, this.currentUseCompute);

            // support dithering option in Camera Component (same as NDSR). Not at x1.0: it would force an extra FinalPost pass.
            if (!this.renderPass.noScaling)
            {
                cameraData.imageScalingMode = ImageScalingMode.Upscaling;
                if (cameraData.isDitheringEnabled)
                    cameraData.upscalingFilter = ImageUpscalingFilter.Point;
            }

            renderer.EnqueuePass(this.renderPass);
        }


        #region FOR TEST

        public Vector2Int inputResolution => this.renderPass?.inputResolution ?? Vector2Int.zero;
        public Vector2Int outputResolution => this.renderPass?.outputResolution ?? Vector2Int.zero;
        /// <summary>True when the compute path was actually used for the last frame (false = raster, or fallback).</summary>
        public bool computeActive => this.renderPass?.computeActive ?? false;
        /// <summary>True when the last frame wrote straight to the backbuffer (no URP final blit).</summary>
        public bool backbufferActive => this.renderPass?.backbufferActive ?? false;

        public void RequestCompute(bool useCompute)
        {
            this.currentUseCompute = useCompute;
        }

        /// <summary>CAS needs tonemapped input; anything other than AfterRenderingPostProcessing is ignored.</summary>
        public void RequestPassEvent(RenderPassEvent passEvent)
        {
#if DEBUG
            if (passEvent != RenderPassEvent.AfterRenderingPostProcessing)
                UexLogger.LogWarning($"CASFeature: {passEvent} is not supported, CAS always runs at AfterRenderingPostProcessing");
#endif
        }

        public void RequestScaling(float scaling)
        {
            if (scaling > CASRenderPass.MAX_SCALING)
            {
                // CAS_AREA_LIMIT = 4.0 (2.0x per axis)
                UexLogger.LogWarning($"CASFeature: scaling x{scaling} exceeds CAS area limit, clamped to x{CASRenderPass.MAX_SCALING}");
                scaling = CASRenderPass.MAX_SCALING;
            }
            else if (scaling < CASRenderPass.MIN_SCALING)
            {
                scaling = CASRenderPass.MIN_SCALING;
            }

            if (Mathf.Approximately(this.currentScaling, scaling))
                return;
            this.currentScaling = scaling;
            this.updatedSettings = true;
        }

        public void RequestSharpness(float sharpness)
        {
            this.currentSharpness = Mathf.Clamp01(sharpness);
        }

        public void RequestLowQuality(bool lowQuality)
        {
            this.currentLowQuality = lowQuality;
        }

        public void ResetSettings()
        {
            this.currentScaling = this.defaultScaling;
            this.currentSharpness = this.defaultSharpness;
            this.currentLowQuality = this.defaultLowQuality;
            this.currentUseCompute = this.defaultUseCompute;
            this.currentTargetExtent = Vector2Int.zero;
            this.updatedSettings = true;
        }

        #endregion
    }
}
