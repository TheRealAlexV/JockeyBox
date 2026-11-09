using System.Runtime.CompilerServices;

// The audio graph is deliberately internal: the DSP stages (ring buffers, channel
// mapping, deck clock) are implementation detail, not API. The unit-test project is
// the one consumer that must reach them directly, because the behaviour under test
// happens inside a single block of samples rather than behind a public surface.
[assembly: InternalsVisibleTo("JockeyBox.Tests")]
