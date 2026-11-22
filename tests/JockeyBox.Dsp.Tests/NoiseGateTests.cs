namespace JockeyBox.Dsp.Tests;

public class NoiseGateTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void Opens_on_signal_and_closes_on_silence()
    {
        var gate = new NoiseGate(SampleRate, thresholdDb: -30, hysteresisDb: 3,
            attackMs: 5, releaseMs: 100, holdMs: 50, floorDb: -60);

        // 1 second of a -6 dBFS 1 kHz tone: well above the -30 dBFS threshold.
        var tone = TestSignals.Sine(SampleRate, 1000, SampleRate, 0.5);
        double inputDb = TestSignals.RmsDb(tone);
        gate.Process(tone);

        // Measure the second half, long after the 5 ms attack ramp.
        double openDb = TestSignals.RmsDb(tone.AsSpan(SampleRate / 2)) - inputDb;
        Assert.True(Math.Abs(openDb) <= 0.1, $"Open gate should pass within 0.1 dB, measured {openDb:F4} dB");
        Assert.True(gate.IsOpen, "Gate should be open while the tone is present");

        // 1 second of a -90 dBFS tone: below threshold minus hysteresis, so it must close.
        var quiet = TestSignals.Sine(SampleRate, 1000, SampleRate, 3.1622776601683795e-5);
        gate.Process(quiet);

        Assert.False(gate.IsOpen, "Gate should be closed after the release and hold periods");
        Assert.True(gate.GainReductionDb <= -60 + 0.5,
            $"Closed gate should sit at the -60 dB floor, readout was {gate.GainReductionDb:F3} dB");
        Assert.True(TestSignals.Rms(quiet.AsSpan(SampleRate / 2)) < 1e-6, "Closed gate should silence the quiet tone");
    }

    [Fact]
    public void Hysteresis_prevents_chatter_at_the_threshold()
    {
        var gate = new NoiseGate(SampleRate, thresholdDb: -30, hysteresisDb: 6,
            attackMs: 1, releaseMs: 20, holdMs: 0, floorDb: -60);

        // Open it with a loud burst.
        var burst = TestSignals.Sine(SampleRate / 10, 1000, SampleRate, 0.5);
        gate.Process(burst);
        Assert.True(gate.IsOpen);

        // A tone at -33 dBFS is inside the reopen band but above the close band: it must stay open.
        var insideHysteresis = TestSignals.Sine(SampleRate / 2, 1000, SampleRate, 0.02236);
        gate.Process(insideHysteresis);
        Assert.True(gate.IsOpen, "Level between close and open thresholds must not close the gate");
    }

    [Fact]
    public void Dip_shorter_than_hold_does_not_close_the_gate()
    {
        // 200 ms hold, 100 ms dip. The 20 ms envelope release has decayed well below the
        // close threshold by the end of the dip, so only the hold can keep the gate open.
        var gate = new NoiseGate(SampleRate, thresholdDb: -30, hysteresisDb: 3,
            attackMs: 5, releaseMs: 100, holdMs: 200, floorDb: -60);

        gate.Process(TestSignals.Sine(SampleRate / 4, 1000, SampleRate, 0.5));
        Assert.True(gate.IsOpen);

        gate.Process(new float[SampleRate / 10]);
        Assert.True(gate.IsOpen, "a 100 ms dip must not close a gate with a 200 ms hold");

        var tone = TestSignals.Sine(SampleRate / 4, 1000, SampleRate, 0.5);
        double inputDb = TestSignals.RmsDb(tone);
        gate.Process(tone);
        double passedDb = TestSignals.RmsDb(tone.AsSpan(SampleRate / 8)) - inputDb;

        Assert.True(Math.Abs(passedDb) <= 0.1,
            $"the tail after a held dip must pass at full level, measured {passedDb:F3} dB");
    }

    [Fact]
    public void Floor_db_sets_the_closed_state_attenuation()
    {
        var gate = new NoiseGate(SampleRate, thresholdDb: -30, hysteresisDb: 3,
            attackMs: 5, releaseMs: 100, holdMs: 50, floorDb: -40);

        // -70 dBFS RMS: far below the threshold, so the gate stays closed at its floor.
        var quiet = TestSignals.Sine(SampleRate, 1000, SampleRate, Math.Pow(10, -70 / 20.0) * Math.Sqrt(2));
        double inputDb = TestSignals.RmsDb(quiet);
        gate.Process(quiet);
        double measuredDb = TestSignals.RmsDb(quiet.AsSpan(SampleRate / 2)) - inputDb;

        Assert.False(gate.IsOpen, "a -70 dBFS tone must not open a -30 dBFS gate");
        Assert.True(Math.Abs(measuredDb + 40.0) <= 0.5,
            $"a closed gate should attenuate by the -40 dB floor, measured {measuredDb:F3} dB");
    }
}
