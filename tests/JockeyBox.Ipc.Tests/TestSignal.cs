namespace JockeyBox.Ipc.Tests;

internal static class TestSignal
{
    /// <summary>
    /// Deterministic, exactly-representable ramp per sample. Values are multiples of 1/256
    /// in [-16, 16), so any difference between written and read data is a real copy error.
    /// </summary>
    public static void Fill(Span<float> block, long blockIndex)
    {
        for (int i = 0; i < block.Length; i++)
            block[i] = (((blockIndex * 17) + (i * 3)) % 8192) / 256f - 16f;
    }

    /// <summary>Every sample of the block carries the block index; used by the child process
    /// so a block identifies itself across the process boundary.</summary>
    public static void FillIdentified(Span<float> block, long blockIndex)
        => block.Fill(blockIndex);

    public static bool BitEqual(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual)
    {
        if (expected.Length != actual.Length)
            return false;

        for (int i = 0; i < expected.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
                return false;
        }

        return true;
    }

    public static string FirstDifference(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual)
    {
        int n = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < n; i++)
        {
            if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
                return $"index {i}: expected {expected[i]:R} (0x{BitConverter.SingleToInt32Bits(expected[i]):X8}), actual {actual[i]:R} (0x{BitConverter.SingleToInt32Bits(actual[i]):X8})";
        }

        return expected.Length == actual.Length ? "identical" : $"length {expected.Length} vs {actual.Length}";
    }
}
