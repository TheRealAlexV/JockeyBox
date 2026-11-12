namespace JockeyBox.Separation;

/// <summary>
/// What a karaoke render asks the separator to produce. The mode owns both the
/// model file and the stem that model is told to emit, so callers never pick a
/// model independently of the stem it writes.
/// </summary>
public enum KaraokeStemMode
{
    /// <summary>All vocals removed. The plain karaoke instrumental.</summary>
    Instrumental,

    /// <summary>Lead vocal removed, backing vocals/harmonies kept.</summary>
    InstrumentalKeepBacking,

    /// <summary>Vocals alone (reverse karaoke); written as "&lt;Song&gt;.vocals.mp3".</summary>
    VocalsOnly,
}

/// <summary>
/// Model catalogue kept for reference. Nothing reads it today: the Settings
/// dropdown that used to list it was removed, and the context menu picks a stem
/// mode instead of a filename. The live mapping is <see cref="For"/>.
/// </summary>
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

    /// <summary>
    /// The single owner of the mode → (model filename, --single_stem) mapping.
    /// All three models are ONNX and auto-download on first use.
    /// </summary>
    public static (string Filename, string SingleStem) For(KaraokeStemMode mode) => mode switch
    {
        KaraokeStemMode.InstrumentalKeepBacking => ("UVR_MDXNET_KARA_2.onnx", "Instrumental"),
        KaraokeStemMode.VocalsOnly => ("Kim_Vocal_2.onnx", "Vocals"),
        _ => ("UVR-MDX-NET-Inst_HQ_3.onnx", "Instrumental"),
    };
}
