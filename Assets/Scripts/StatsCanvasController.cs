using System.Collections.Generic;
using StaticInstances;
using TMPro;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;


public class StatsCanvasController : MonoBehaviour
{
    public TextMeshProUGUI CPU, GPU;
    [Tooltip("Touch / click area that toggles every StaticInstanceScatter in the scene")]
    public RectTransform sisToggle;
    [Tooltip("Label showing the SIS On/Off state")]
    public TextMeshProUGUI sisLabel;

    const int sampleCount = 30;
    ProfilerRecorder cpuMainThreaTimeRecorder;
    ProfilerRecorder gpuFrameTimeRecorder;

    Canvas sisCanvas;
    bool sisEnabled = true;

    void Start()
    {
        cpuMainThreaTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Main Thread Frame Time", sampleCount);
        gpuFrameTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time", sampleCount);

        if (this.sisToggle != null)
            this.sisCanvas = this.sisToggle.GetComponentInParent<Canvas>();

        // start from the scene's current state
        var scatters = FindScatters();
        this.sisEnabled = scatters.Length == 0 || System.Array.Exists(scatters, s => s.enabled);
        this.UpdateSisLabel();
    }

    void OnDestroy()
    {
        this.cpuMainThreaTimeRecorder.Dispose();
        this.gpuFrameTimeRecorder.Dispose();
    }

    void Update()
    {
        CPU.text = $"{GetRecorderFrameAverage(this.cpuMainThreaTimeRecorder) * (1e-6f):F2} ms";
        GPU.text = $"{GetRecorderFrameAverage(this.gpuFrameTimeRecorder) * (1e-6f):F2} ms";

        // Pointer covers touch, mouse and pen
        var pointer = Pointer.current;
        if (this.sisToggle != null && pointer != null && pointer.press.wasPressedThisFrame)
        {
            var cam = this.sisCanvas == null || this.sisCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : this.sisCanvas.worldCamera;
            if (RectTransformUtility.RectangleContainsScreenPoint(this.sisToggle, pointer.position.ReadValue(), cam))
                this.ToggleSis();
        }
    }

    static double GetRecorderFrameAverage(ProfilerRecorder recorder)
    {
        var samplesCount = recorder.Capacity;
        if (samplesCount == 0)
            return 0;

        var samples = new List<ProfilerRecorderSample>(samplesCount);
        recorder.CopyTo(samples);
        var r = 0d;
        for (var i = 0; i < samples.Count; ++i)
            r += samples[i].Value;
        r /= samplesCount;
        return r;
    }

    static StaticInstanceScatter[] FindScatters() =>
        FindObjectsByType<StaticInstanceScatter>(FindObjectsInactive.Include, FindObjectsSortMode.None);

    public void ToggleSis()
    {
        this.sisEnabled = !this.sisEnabled;
        foreach (var scatter in FindScatters())
            scatter.enabled = this.sisEnabled;
        this.UpdateSisLabel();
    }

    void UpdateSisLabel()
    {
        if (this.sisLabel != null)
            this.sisLabel.text = $"<b>SIS</b> : {(this.sisEnabled ? "On" : "Off")}";
    }
}
