using JockeyBox.Settings;

namespace JockeyBox.Metadata;

/// <summary>
/// One-time bridge from the pre-metadata-store lyric settings
/// (<see cref="AppSettings.LyricsOffsetByTrack"/> /
/// <see cref="AppSettings.LyricsSourceByTrack"/>, both keyed by
/// <c>TrackEntry.RelativeKey</c>) into <see cref="TrackMetadataStore"/>.
///
/// <para>
/// The import is <b>read-only</b> towards <see cref="AppSettings"/>: the
/// dictionaries are left in place (a build rolled back to 1.5.x still finds its
/// per-track offsets), and nothing is written to settings.json. It is
/// <b>idempotent</b>: the store persists a
/// <see cref="TrackMetadataStore.LegacyImportDone"/> flag and refuses a second
/// pass, so clearing a migrated offset does not resurrect it on the next launch.
/// It also never overwrites a value the store already holds.
/// </para>
///
/// <para>
/// Call once, after both objects are loaded and before the first track is
/// played; <see cref="MainViewModel"/> owns that call site (see the wiring
/// report). Safe to call on every launch - the flag short-circuits it.
/// </para>
/// </summary>
public static class TrackMetadataMigration
{
    /// <summary>
    /// Import legacy per-track lyric values from <paramref name="settings"/>.
    /// </summary>
    /// <returns>How many keys were created or filled in; 0 on an already-migrated store.</returns>
    public static int MigrateLegacyLyrics(this TrackMetadataStore store, AppSettings? settings)
    {
        if (store is null || settings is null) return 0;

        var offsets = settings.LyricsOffsetByTrack;
        var sources = settings.LyricsSourceByTrack;

        if ((offsets is null || offsets.Count == 0) && (sources is null || sources.Count == 0))
        {
            // Nothing to move, but still fence the migration so an empty pass
            // does not run again every launch.
            if (!store.LegacyImportDone) store.MarkLegacyImportDone();
            return 0;
        }

        return store.ImportLegacy(offsets, sources);
    }
}
