namespace LectureAssistant.QuestionGeneration.Local;

public sealed record LocalLlmOptions
{
    /// <summary>Full path to a .gguf model file chosen by the user.</summary>
    public required string ModelPath { get; init; }

    /// <summary>Context window in tokens. The transcript is split into sections that fit.</summary>
    public int ContextSize { get; init; } = 8192;

    /// <summary>
    /// Layers offloaded to the GPU (Vulkan). 0 = CPU only; a large number (e.g. 999) offloads everything,
    /// which is fastest when the model fits in video memory.
    /// </summary>
    public int GpuLayerCount { get; init; } = 999;

    /// <summary>CPU threads; null lets llama.cpp choose.</summary>
    public int? Threads { get; init; }

    /// <summary>Output tokens allowed per question; bounds generation if a small model rambles.</summary>
    public int MaxTokensPerQuestion { get; init; } = 450;

    /// <summary>Low temperature keeps small models on-task while leaving some variety between runs.</summary>
    public float Temperature { get; init; } = 0.3f;

    /// <summary>Fixed seed for reproducible output; null for a random seed each run.</summary>
    public uint? Seed { get; init; }
}
