using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;

namespace GauntletMod
{
    internal class WaveSlot
    {
        /// <summary>
        /// Either a banked enemy's object name, or a Hunter's Journal record name for
        /// one that hasn't been loaded yet. Both are resolved through
        /// EnemyLibrary.Resolve, so a wave can be built out of enemies you haven't
        /// imported and filled in afterwards.
        /// </summary>
        public string EnemyId;

        /// <summary>What to show in the editor before the enemy is loaded.</summary>
        public string DisplayName;

        public int Count = 1;

        public WaveSlot() { }
        public WaveSlot(string enemyId, string displayName, int count)
        {
            EnemyId = enemyId;
            DisplayName = displayName;
            Count = count;
        }

        public string Label => string.IsNullOrEmpty(DisplayName) ? EnemyId : DisplayName;
    }

    internal class Wave
    {
        public readonly List<WaveSlot> Slots = new List<WaveSlot>();

        /// <summary>Seconds before the wave's enemies appear once it starts.</summary>
        public float StartDelay;

        public int TotalEnemies
        {
            get
            {
                int total = 0;
                foreach (WaveSlot slot in Slots) total += Math.Max(0, slot.Count);
                return total;
            }
        }
    }

    /// <summary>
    /// The gauntlet's wave list, and the little file it lives in.
    ///
    /// Deliberately a plain line-based text format rather than JSON: it's trivial to
    /// hand-edit, it can't throw a parser exception that costs you your whole layout,
    /// and it avoids taking a dependency on the game's bundled Newtonsoft (whose
    /// version we don't control).
    ///
    ///     wave 1.5          <- starts a wave, optional start delay in seconds
    ///     enemy Bell Fly 3  <- enemy id, then how many. Ids may contain spaces;
    ///                          the trailing number is the count.
    /// </summary>
    internal static class WaveConfig
    {
        public static readonly List<Wave> Waves = new List<Wave>();

        private static string FilePath =>
            Path.Combine(Paths.ConfigPath, "GauntletMod.waves.txt");

        public static bool IsEmpty
        {
            get
            {
                foreach (Wave wave in Waves)
                {
                    if (wave.TotalEnemies > 0) return false;
                }
                return true;
            }
        }

        // ------------------------------------------------------------------

        public static void Load()
        {
            Waves.Clear();

            try
            {
                if (!File.Exists(FilePath))
                {
                    Plugin.Log.LogInfo("WaveConfig: no saved waves yet.");
                    return;
                }

                Wave current = null;

                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                    if (line.StartsWith("wave", StringComparison.OrdinalIgnoreCase))
                    {
                        current = new Wave();
                        string rest = line.Substring(4).Trim();
                        if (rest.Length > 0 &&
                            float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out float delay))
                        {
                            current.StartDelay = delay;
                        }
                        Waves.Add(current);
                        continue;
                    }

                    if (line.StartsWith("enemy ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (current == null)
                        {
                            current = new Wave();
                            Waves.Add(current);
                        }

                        string body = line.Substring(6).Trim();

                        // Enemy ids contain spaces, so the count is whatever trails
                        // the last space - if that isn't a number, treat the whole
                        // thing as the id with a count of one.
                        int count = 1;
                        int split = body.LastIndexOf(' ');
                        if (split > 0 && int.TryParse(body.Substring(split + 1), out int parsed))
                        {
                            count = Math.Max(1, parsed);
                            body = body.Substring(0, split).Trim();
                        }

                        // "id | display name" - the label is optional and only there
                        // so an enemy you haven't loaded yet still reads properly.
                        string display = null;
                        int pipe = body.IndexOf('|');
                        if (pipe > 0)
                        {
                            display = body.Substring(pipe + 1).Trim();
                            body = body.Substring(0, pipe).Trim();
                        }

                        if (body.Length > 0) current.Slots.Add(new WaveSlot(body, display, count));
                    }
                }

                Plugin.Log.LogInfo($"WaveConfig: loaded {Waves.Count} wave(s) from {FilePath}.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("WaveConfig: load failed, starting empty: " + e);
                Waves.Clear();
            }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Custom Gauntlet waves. Edited in-game with the wave panel,");
                sb.AppendLine("# but it's a plain text file - hand-editing is fine.");
                sb.AppendLine("#   wave <startDelaySeconds>");
                sb.AppendLine("#   enemy <enemy id> [| display name] <count>");
                sb.AppendLine();

                for (int i = 0; i < Waves.Count; i++)
                {
                    Wave wave = Waves[i];
                    sb.AppendLine("wave " + wave.StartDelay.ToString("0.##", CultureInfo.InvariantCulture));
                    foreach (WaveSlot slot in wave.Slots)
                    {
                        if (string.IsNullOrEmpty(slot.EnemyId)) continue;
                        string label = string.IsNullOrEmpty(slot.DisplayName) || slot.DisplayName == slot.EnemyId
                            ? ""
                            : " | " + slot.DisplayName;
                        sb.AppendLine($"enemy {slot.EnemyId}{label} {Math.Max(1, slot.Count)}");
                    }
                    sb.AppendLine();
                }

                File.WriteAllText(FilePath, sb.ToString());
                Plugin.Log.LogInfo($"WaveConfig: saved {Waves.Count} wave(s) to {FilePath}.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("WaveConfig: save failed: " + e);
            }
        }

        // ------------------------------------------------------------------

        public static Wave AddWave()
        {
            var wave = new Wave();
            Waves.Add(wave);
            return wave;
        }

        public static void RemoveWave(int index)
        {
            if (index >= 0 && index < Waves.Count) Waves.RemoveAt(index);
        }

        public static void MoveWave(int index, int delta)
        {
            int target = index + delta;
            if (index < 0 || index >= Waves.Count) return;
            if (target < 0 || target >= Waves.Count) return;

            Wave wave = Waves[index];
            Waves.RemoveAt(index);
            Waves.Insert(target, wave);
        }
    }
}
