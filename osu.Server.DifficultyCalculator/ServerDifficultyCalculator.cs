// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using MySqlConnector;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring.Legacy;
using osu.Server.DifficultyCalculator.Commands;

namespace osu.Server.DifficultyCalculator
{
    public class ServerDifficultyCalculator
    {
        private static readonly List<Ruleset> available_rulesets = getRulesets();

        private readonly bool processConverts;
        private readonly bool dryRun;
        private readonly List<Ruleset> processableRulesets = new List<Ruleset>();

        static Dictionary<int, string> AttributeMapping = new Dictionary<int, string>()
        {
            {1, "diff_aim" },
            {3, "diff_speed" },
            {5, "od" },
            {7,"ar" },
            {9, "max_combo" },
            {11, "diff_strain" },
            {13, "hit300" },
            {15, "score_multiplier" },
            {17, "flashlight_rating" },
            {19, "slider_factor" },
            {21, "speed_note_count" },
            {23, "speed_difficult_strain_count" },
            {25, "aim_difficult_strain_count" },
            {27, "hit100" },
            {29, "mono_stamina_factor" }
        };

        public ServerDifficultyCalculator(int[]? rulesetIds = null, bool processConverts = true, bool dryRun = false)
        {
            this.processConverts = processConverts;
            this.dryRun = dryRun;

            if (rulesetIds != null)
            {
                foreach (int id in rulesetIds)
                    processableRulesets.Add(available_rulesets.Single(r => r.RulesetInfo.OnlineID == id));
            }
            else
            {
                processableRulesets.AddRange(available_rulesets);
            }
        }

        public void Process(WorkingBeatmap beatmap, ProcessingMode mode)
        {
            switch (mode)
            {
                case ProcessingMode.All:
                    ProcessDifficulty(beatmap);
                    ProcessLegacyAttributes(beatmap);
                    break;

                case ProcessingMode.Difficulty:
                    ProcessDifficulty(beatmap);
                    break;

                case ProcessingMode.ScoreAttributes:
                    ProcessLegacyAttributes(beatmap);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported processing mode supplied");
            }
        }

        public void ProcessDifficulty(WorkingBeatmap beatmap) => run(beatmap, processDifficulty);

        public void ProcessLegacyAttributes(WorkingBeatmap beatmap) => run(beatmap, processLegacyAttributes);

        private void run(WorkingBeatmap beatmap, Action<ProcessableBeatmap, MySqlConnection> callback)
        {
            try
            {
                bool ranked;

                using (var conn = Database.GetSlaveConnection())
                {
                    ranked = conn.QuerySingleOrDefault<int>("SELECT `approved` FROM `beatmap` WHERE `beatmap_id` = @BeatmapId", new
                    {
                        BeatmapId = beatmap.BeatmapInfo.OnlineID
                    }) > 0;

                    if (ranked && beatmap.Beatmap.HitObjects.Count == 0)
                        throw new ArgumentException($"Ranked beatmap {beatmap.BeatmapInfo.OnlineInfo} has 0 hitobjects!");
                }

                using (var conn = Database.GetConnection())
                {
                    if (processConverts && beatmap.BeatmapInfo.Ruleset.OnlineID == 0)
                    {
                        foreach (var ruleset in processableRulesets)
                            callback(new ProcessableBeatmap(beatmap, ruleset, ranked), conn);
                    }
                    else if (processableRulesets.Any(r => r.RulesetInfo.OnlineID == beatmap.BeatmapInfo.Ruleset.OnlineID))
                        callback(new ProcessableBeatmap(beatmap, beatmap.BeatmapInfo.Ruleset.CreateInstance(), ranked), conn);
                }
            }
            catch (Exception e)
            {
                throw new Exception($"{beatmap.BeatmapInfo.OnlineID} failed with: {e.Message}");
            }
        }

        private void processDifficulty(ProcessableBeatmap beatmap, MySqlConnection conn)
        {
            foreach (var attribute in beatmap.Ruleset.CreateDifficultyCalculator(beatmap.Beatmap).CalculateAllLegacyCombinations())
            {
                if (dryRun)
                    continue;

                LegacyMods legacyMods = beatmap.Ruleset.ConvertToLegacyMods(attribute.Mods);

                conn.Execute(
                "INSERT INTO `osu_beatmap_difficulty_data` (`beatmap_id`, `mode`, `mods`, `diff_unified`) "
                + "VALUES (@BeatmapId, @Mode, @Mods, @Diff) "
                + "ON DUPLICATE KEY UPDATE `diff_unified` = @Diff",
                    new
                    {
                        BeatmapId = beatmap.BeatmapID,
                        Mode = beatmap.RulesetID,
                        Mods = (int)legacyMods,
                        Diff = attribute.StarRating
                    });

                //output to console for debug
                //Console.WriteLine($"INSERT INTO `osu_beatmap_difficulty_data` (`beatmap_id`, `mode`, `mods`, `diff_unified`) "
                //    + $"VALUES ({beatmap.BeatmapID}, {beatmap.RulesetID}, {(int)legacyMods}, {attribute.StarRating}) "
                //    + $"ON DUPLICATE KEY UPDATE `diff_unified` = {attribute.StarRating}");

                if (!AppSettings.SKIP_INSERT_ATTRIBUTES)
                {
                    var parameters = new List<object>();

                    foreach (var mapping in attribute.ToDatabaseAttributes())
                    {
                        string attrib_string = AttributeMapping[mapping.attributeId];

                        parameters.Add(new
                        {
                            BeatmapId = beatmap.BeatmapID,
                            Mode = beatmap.RulesetID,
                            Mods = (int)legacyMods,
                            Attribute = attrib_string,
                            Value = mapping.value
                        });
                    }

                    //we want to insert or update the value of the attribute in osu_beatmap_difficulty_data
                    //attribute in parameters is the column name in the database
                    //so if .Attribute = "diff_aim" then the column in the database is diff_aim
                    //mapping.value is the value of the attribute

                    //string query = "INSERT INTO `osu_beatmap_difficulty_data` (`beatmap_id`, `mode`, `mods`, @Attribute) "
                    //    + "VALUES (@BeatmapId, @Mode, @Mods, @Value) "
                    //    + "ON DUPLICATE KEY UPDATE @Attribute = @Value";
                    //conn.Execute(query, parameters);

                    //seperate parameters into groups by Attribute
                    var groupedParameters = parameters.GroupBy(p => p.GetType().GetProperty("Attribute").GetValue(p, null)).ToList();

                    foreach (var group in groupedParameters)
                    {
                        string query = "INSERT INTO `osu_beatmap_difficulty_data` (`beatmap_id`, `mode`, `mods`, " + group.Key + ") "
                            + "VALUES (@BeatmapId, @Mode, @Mods, @Value) "
                            + "ON DUPLICATE KEY UPDATE " + group.Key + " = @Value";
                        conn.Execute(query, group);
                    }
                }

                if (legacyMods == LegacyMods.None && beatmap.Ruleset.RulesetInfo.Equals(beatmap.Beatmap.BeatmapInfo.Ruleset))
                {
                    double beatLength = beatmap.Beatmap.Beatmap.GetMostCommonBeatLength();
                    double bpm = beatLength > 0 ? 60000 / beatLength : 0;

                    object param = new
                    {
                        BeatmapId = beatmap.BeatmapID,
                        Diff = attribute.StarRating,
                        AR = beatmap.Beatmap.BeatmapInfo.Difficulty.ApproachRate,
                        OD = beatmap.Beatmap.BeatmapInfo.Difficulty.OverallDifficulty,
                        HP = beatmap.Beatmap.BeatmapInfo.Difficulty.DrainRate,
                        CS = beatmap.Beatmap.BeatmapInfo.Difficulty.CircleSize,
                        BPM = Math.Round(bpm, 2),
                        MaxCombo = attribute.MaxCombo,
                    };

                    //if (AppSettings.INSERT_BEATMAPS)
                    //{
                    //    conn.Execute(
                    //        "INSERT INTO `osu_beatmaps` (`beatmap_id`, `difficultyrating`, `diff_approach`, `diff_overall`, `diff_drain`, `diff_size`, `bpm`, `max_combo`) "
                    //        + "VALUES (@BeatmapId, @Diff, @AR, @OD, @HP, @CS, @BPM, @MaxCombo) "
                    //        + "ON DUPLICATE KEY UPDATE `difficultyrating` = @Diff, `diff_approach` = @AR, `diff_overall` = @OD, `diff_drain` = @HP, `diff_size` = @CS, `bpm` = @BPM, `max_combo` = @MaxCombo",
                    //        param);
                    //}
                    //else
                    //{
                    //    conn.Execute(
                    //        "UPDATE `osu_beatmaps` SET `difficultyrating` = @Diff, `diff_approach` = @AR, `diff_overall` = @OD, `diff_drain` = @HP, `diff_size` = @CS, `bpm` = @BPM , `max_combo` = @MaxCombo "
                    //        + "WHERE `beatmap_id` = @BeatmapId",
                    //        param);
                    //}
                }
            }
        }

        private void processLegacyAttributes(ProcessableBeatmap beatmap, MySqlConnection conn)
        {
            Mod? classicMod = beatmap.Ruleset.CreateMod<ModClassic>();
            Mod[] mods = classicMod != null ? new[] { classicMod } : Array.Empty<Mod>();

            ILegacyScoreSimulator simulator = ((ILegacyRuleset)beatmap.Ruleset).CreateLegacyScoreSimulator();
            LegacyScoreAttributes attributes = simulator.Simulate(beatmap.Beatmap, beatmap.Beatmap.GetPlayableBeatmap(beatmap.Ruleset.RulesetInfo, mods));

            if (dryRun)
                return;

            conn.Execute(
                "INSERT INTO `osu_beatmap_scoring_attribs` (`beatmap_id`, `mode`, `legacy_accuracy_score`, `legacy_combo_score`, `legacy_bonus_score_ratio`, `legacy_bonus_score`, `max_combo`) "
                + "VALUES (@BeatmapId, @Mode, @AccuracyScore, @ComboScore, @BonusScoreRatio, @BonusScore, @MaxCombo) "
                + "ON DUPLICATE KEY UPDATE `legacy_accuracy_score` = @AccuracyScore, `legacy_combo_score` = @ComboScore, `legacy_bonus_score_ratio` = @BonusScoreRatio, `legacy_bonus_score` = @BonusScore, `max_combo` = @MaxCombo",
                new
                {
                    BeatmapId = beatmap.BeatmapID,
                    Mode = beatmap.RulesetID,
                    AccuracyScore = attributes.AccuracyScore,
                    ComboScore = attributes.ComboScore,
                    BonusScoreRatio = attributes.BonusScoreRatio,
                    BonusScore = attributes.BonusScore,
                    MaxCombo = attributes.MaxCombo
                });
        }

        private static List<Ruleset> getRulesets()
        {
            const string ruleset_library_prefix = "osu.Game.Rulesets";

            var rulesetsToProcess = new List<Ruleset>();

            foreach (string file in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, $"{ruleset_library_prefix}.*.dll"))
            {
                try
                {
                    var assembly = Assembly.LoadFrom(file);
                    Type type = assembly.GetTypes().First(t => t.IsPublic && t.IsSubclassOf(typeof(Ruleset)));
                    rulesetsToProcess.Add((Ruleset)Activator.CreateInstance(type)!);
                }
                catch
                {
                    throw new Exception($"Failed to load ruleset ({file})");
                }
            }

            return rulesetsToProcess;
        }

        private readonly record struct ProcessableBeatmap(WorkingBeatmap Beatmap, Ruleset Ruleset, bool Ranked)
        {
            public int BeatmapID => Beatmap.BeatmapInfo.OnlineID;
            public int RulesetID => Ruleset.RulesetInfo.OnlineID;
        }
    }
}
