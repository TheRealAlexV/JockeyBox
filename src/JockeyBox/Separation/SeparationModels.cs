namespace JockeyBox.Separation;

/// <summary>Shared model presets used by both the Settings dropdown and the context-menu submenu.</summary>
public static class SeparationModels
{
    public static readonly (string Filename, string Description)[] Presets =
    {
        ("UVR-MDX-NET-Inst_HQ_3.onnx",
            "Best general-purpose instrumental. Cleanest vocal removal for most music. Fast — ~1-2 min per track on CPU."),
        ("UVR_MDXNET_KARA_2.onnx",
            "Karaoke-friendly. Strips lead vocals but keeps backing vocals/harmonies. Use when original has layered vocals."),
        ("model_bs_roformer_ep_317_sdr_12.9755.ckpt",
            "Highest separation quality. BS-Roformer transformer model. Much slower (5-10 min); use for studio-critical tracks."),
        ("UVR-MDX-NET-Inst_HQ_4.onnx",
            "Updated version of HQ_3. Slightly better on complex arrangements but marginally slower."),
        ("2_HP-UVR.pth",
            "VR architecture alternative. Different approach (phase spectrogram + harmonic masks). Good fallback if MDX models sound thin."),
    };
}
