using System;
using System.IO;

namespace osu.Game.Beatmaps
{
    public static class XcSupportedModes
    {
        public const string UnsupportedMessage = "osu!mania, osu!taiko and osu!catch are not supported by xc osu yet. Support will come later. Please choose an osu!standard beatmap.";

        // Older imports may have incorrectly stored the mode as standard: check the original file.
        public static bool IsUnsupported(WorkingBeatmap beatmap)
        {
            if (!OperatingSystem.IsBrowser()) return false;
            if (beatmap.BeatmapInfo.Ruleset.OnlineID > 0) return true;
            if (beatmap.BeatmapInfo.Path == null) return false;
            string path = beatmap.BeatmapInfo.BeatmapSet?.GetPathForFile(beatmap.BeatmapInfo.Path)
                          ?? throw new IOException("Beatmap file could not be located.");
            using var stream = beatmap.GetStream(path);
            if (stream == null) throw new IOException("Beatmap file could not be opened.");
            using var reader = new StreamReader(stream);
            bool general = true;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith('[')) general = line == "[General]";
                if (!general) continue;
                var pair = line.Split(':', 2);
                if (pair.Length == 2 && pair[0].Trim() == "Mode")
                {
                    if (!int.TryParse(pair[1].Split("//", 2)[0].Trim(), out int mode))
                        throw new IOException("Invalid beatmap mode.");
                    return mode != 0;
                }
            }
            return false;
        }
    }
}
