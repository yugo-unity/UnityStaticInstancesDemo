using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Draws only the objects on the TransparentFX layer with a reduced pixel shading rate (per-draw VRS).
/// - The renderer's Transparent Layer Mask must exclude TransparentFX, otherwise the objects are drawn twice
/// - Falls back to full rate when the device does not support per-draw-call VRS (ShadingRateInfo.supportsPerDrawCall)
/// </summary>
public class VRSTransparentFXFeature : ScriptableRendererFeature
{
    public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
    public LayerMask layerMask = 1 << 1; // TransparentFX
    [Tooltip("FragmentSize2x1 / 1x2 halve the pixel shader invocations, 2x2 quarters them.")]
    public ShadingRateFragmentSize fragmentSize = ShadingRateFragmentSize.FragmentSize2x1;

    VRSTransparentFXPass renderPass;

    public override void Create()
    {
        this.renderPass = new VRSTransparentFXPass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var cameraType = renderingData.cameraData.cameraType;
        if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            return;

        this.renderPass.renderPassEvent = this.renderPassEvent;
        this.renderPass.Setup(this.layerMask, this.fragmentSize);
        renderer.EnqueuePass(this.renderPass);
    }

    class VRSTransparentFXPass : ScriptableRenderPass
    {
        static readonly List<ShaderTagId> shaderTagIds = new()
        {
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
        };

        class PassData
        {
            public RendererListHandle rendererList;
        }

        LayerMask layerMask;
        ShadingRateFragmentSize fragmentSize;

        public VRSTransparentFXPass()
        {
            this.profilingSampler = new ProfilingSampler("VRS TransparentFX");
        }

        public void Setup(LayerMask layerMask, ShadingRateFragmentSize fragmentSize)
        {
            this.layerMask = layerMask;
            this.fragmentSize = fragmentSize;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var lightData = frameData.Get<UniversalLightData>();

            using var builder = renderGraph.AddRasterRenderPass<PassData>(this.passName, out var passData, this.profilingSampler);

            var drawingSettings = RenderingUtils.CreateDrawingSettings(shaderTagIds, renderingData, cameraData, lightData, SortingCriteria.CommonTransparent);
            var filteringSettings = new FilteringSettings(RenderQueueRange.transparent, this.layerMask);
            var param = new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings);
            passData.rendererList = renderGraph.CreateRendererList(param);
            builder.UseRendererList(passData.rendererList);

            builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);

            if (resourceData.mainShadowsTexture.IsValid())
                builder.UseTexture(resourceData.mainShadowsTexture, AccessFlags.Read);
            if (resourceData.additionalShadowsTexture.IsValid())
                builder.UseTexture(resourceData.additionalShadowsTexture, AccessFlags.Read);

            // No-op on devices without per-draw-call VRS: the objects are then drawn at full rate.
            builder.SetShadingRateFragmentSize(this.fragmentSize);

            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
            {
                context.cmd.DrawRendererList(data.rendererList);
            });
        }
    }
}
