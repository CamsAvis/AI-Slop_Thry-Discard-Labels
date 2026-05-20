using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Cam.PoiyomiTileLabels
{
    internal static class PoiyomiTileLabelsPatcher
    {
        const string LOG_PREFIX = "[PoiyomiTileLabels] ";
        const string DIALOG_TITLE = "Poiyomi Tile Labels";

        // Known VCC install locations. Free (com.poiyomi.toon) and Pro (com.poiyomi.pro) both
        // ship the same _PoiyomiShaders layout under Packages/. Non-VCC (unitypackage) installs
        // land somewhere under Assets/ and are discovered by scanning at runtime.
        static readonly string[] KNOWN_VCC_ROOTS = new[]
        {
            "Packages/com.poiyomi.toon/_PoiyomiShaders/Shaders",
            "Packages/com.poiyomi.pro/_PoiyomiShaders/Shaders",
        };

        // Three stock attribute forms in the wild:
        //   Poi 8.0–9.1: [ThryMultiFloats(true, _UDIM…)]
        //   Poi 9.2–9.3: [ThryMultiFloatButtons(u0, u1, u2, u3, _UDIM…)]
        //   Poi 10:      [ThryMultiFloatButtons(u0vN, u1vN, u2vN, u3vN, _UDIM…)] (N varies per row)
        // The button form is matched with an optional `vN` capture group so 9.2+ and 10 share one
        // regex; the MatchEvaluator re-emits whatever suffix (or none) the source line had.
        static readonly (Regex pattern, MatchEvaluator evaluator)[] APPLY_PATCHES = new[]
        {
            (
                new Regex(@"\[ThryMultiFloatButtons\((u0(?:v\d)?), (u1(?:v\d)?), (u2(?:v\d)?), (u3(?:v\d)?), _UDIM", RegexOptions.Compiled),
                (MatchEvaluator)(m => $"[ThryNamedTileButtons({m.Groups[1].Value}, {m.Groups[2].Value}, {m.Groups[3].Value}, {m.Groups[4].Value}, _UDIM")
            ),
            (
                new Regex(@"\[ThryMultiFloats\(true, _UDIM", RegexOptions.Compiled),
                (MatchEvaluator)(_ => "[ThryNamedTileFloats(true, _UDIM")
            ),
        };

        static readonly (Regex pattern, MatchEvaluator evaluator)[] REVERT_PATCHES = new[]
        {
            (
                new Regex(@"\[ThryNamedTileButtons\((u0(?:v\d)?), (u1(?:v\d)?), (u2(?:v\d)?), (u3(?:v\d)?), _UDIM", RegexOptions.Compiled),
                (MatchEvaluator)(m => $"[ThryMultiFloatButtons({m.Groups[1].Value}, {m.Groups[2].Value}, {m.Groups[3].Value}, {m.Groups[4].Value}, _UDIM")
            ),
            (
                new Regex(@"\[ThryNamedTileFloats\(true, _UDIM", RegexOptions.Compiled),
                (MatchEvaluator)(_ => "[ThryMultiFloats(true, _UDIM")
            ),
        };

        // Used for compatibility detection — count files showing UDIM-grid evidence and their drawer state.
        static readonly Regex UDIM_GRID_MARKER = new Regex(@"_UDIMDiscardRow\d_\d", RegexOptions.Compiled);
        static readonly Regex STOCK_UDIM_DRAWER_MARKER = new Regex(@"\[ThryMultiFloat(?:Buttons|s)\([^\]]*_UDIM", RegexOptions.Compiled);
        static readonly Regex CUSTOM_UDIM_DRAWER_MARKER = new Regex(@"\[ThryNamedTile(?:Buttons|Floats)\([^\]]*_UDIM", RegexOptions.Compiled);

        enum PatchVerb { Apply, Revert }

        // Discover every Poi shader root: known VCC paths plus any 'Assets/**/_PoiyomiShaders/Shaders'
        // folder. Roots with zero .shader files are skipped (handles orphan dirs left after a VCC uninstall).
        static List<string> DiscoverShaderRoots()
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            void Consider(string candidate)
            {
                if (string.IsNullOrEmpty(candidate) || !Directory.Exists(candidate)) return;
                if (Directory.GetFiles(candidate, "*.shader", SearchOption.AllDirectories).Length == 0) return;
                string norm = candidate.Replace('\\', '/').TrimEnd('/');
                if (seen.Add(norm)) roots.Add(norm);
            }

            foreach (var root in KNOWN_VCC_ROOTS) Consider(root);

            string assetsAbs = Application.dataPath;
            if (Directory.Exists(assetsAbs))
            {
                string[] candidates;
                try { candidates = Directory.GetDirectories(assetsAbs, "_PoiyomiShaders", SearchOption.AllDirectories); }
                catch (System.Exception e) { Debug.LogWarning(LOG_PREFIX + "Assets scan failed: " + e.Message); candidates = new string[0]; }

                foreach (var abs in candidates)
                {
                    string shaders = Path.Combine(abs, "Shaders");
                    if (!Directory.Exists(shaders)) continue;
                    string rel = "Assets" + shaders.Substring(assetsAbs.Length).Replace('\\', '/');
                    Consider(rel);
                }
            }

            return roots;
        }

        static void RunPatch(PatchVerb verb)
        {
            var installedRoots = DiscoverShaderRoots();
            if (installedRoots.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    DIALOG_TITLE,
                    "No Poiyomi shaders found. Looked for:\n\n  " +
                    string.Join("\n  ", KNOWN_VCC_ROOTS) +
                    "\n  Any 'Assets/**/_PoiyomiShaders/Shaders' folder containing .shader files\n\n" +
                    "Nothing was modified. Install Poiyomi (Toon or Pro) and try again.",
                    "OK");
                return;
            }

            var patches = verb == PatchVerb.Apply ? APPLY_PATCHES : REVERT_PATCHES;
            string verbForLog = verb == PatchVerb.Apply ? "applied to" : "reverted";

            int changedCount = 0;
            int udimFileCount = 0;
            int stockMarkerCount = 0;
            int customMarkerCount = 0;

            var shaderFilesList = new List<string>();
            foreach (var root in installedRoots)
                shaderFilesList.AddRange(Directory.GetFiles(root, "*.shader", SearchOption.AllDirectories));
            string[] shaderFiles = shaderFilesList.ToArray();
            foreach (var path in shaderFiles)
            {
                try
                {
                    string text = File.ReadAllText(path);

                    bool hasUdim = UDIM_GRID_MARKER.IsMatch(text);
                    if (hasUdim) udimFileCount++;
                    if (STOCK_UDIM_DRAWER_MARKER.IsMatch(text)) stockMarkerCount++;
                    if (CUSTOM_UDIM_DRAWER_MARKER.IsMatch(text)) customMarkerCount++;

                    string patched = text;
                    foreach (var (pattern, evaluator) in patches)
                    {
                        if (pattern.IsMatch(patched))
                            patched = pattern.Replace(patched, evaluator);
                    }
                    if (patched == text) continue;
                    File.WriteAllText(path, patched);
                    Debug.Log(LOG_PREFIX + verbForLog + " " + path.Replace('\\', '/'));
                    changedCount++;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning(LOG_PREFIX + "failed on " + path + ": " + e.Message);
                }
            }

            if (changedCount > 0)
            {
                AssetDatabase.Refresh();
                Debug.Log(LOG_PREFIX + verb + " complete: " + changedCount + " file(s) changed.");
                return;
            }

            // Zero changes — figure out why and tell the user clearly.
            string diagnostic =
                "Roots searched:\n  " + string.Join("\n  ", installedRoots) + "\n\n" +
                "Files scanned: " + shaderFiles.Length + "\n" +
                "Files with UDIM properties: " + udimFileCount + "\n" +
                "Files with stock drawer attribute: " + stockMarkerCount + "\n" +
                "Files with custom drawer attribute: " + customMarkerCount;

            if (udimFileCount == 0)
            {
                EditorUtility.DisplayDialog(
                    DIALOG_TITLE,
                    "No Poiyomi shader files containing UV Tile Discard were found.\n\n" + diagnostic +
                    "\n\nIs Poiyomi installed at one of these locations?",
                    "OK");
                return;
            }

            if (stockMarkerCount == 0 && customMarkerCount == 0)
            {
                EditorUtility.DisplayDialog(
                    DIALOG_TITLE,
                    "Found Poiyomi shader files with UV Tile Discard, but the drawer attribute format isn't recognised.\n\n" + diagnostic +
                    "\n\nThis tool's regex matches Poiyomi Toon 8.0 through 10.x. If you're on a newer version, open one of the matched shader files, find a line declaring a `_UDIMDiscardRow*_*` property, and share the `[Thry...]` attribute on it so the regex can be extended.\n\n" +
                    "Nothing was modified — your shader files are untouched.",
                    "OK");
                return;
            }

            // Already in target state.
            bool alreadyApplied = verb == PatchVerb.Apply && stockMarkerCount == 0 && customMarkerCount > 0;
            bool alreadyReverted = verb == PatchVerb.Revert && customMarkerCount == 0 && stockMarkerCount > 0;
            if (alreadyApplied || alreadyReverted)
            {
                EditorUtility.DisplayDialog(
                    DIALOG_TITLE,
                    "Already " + (verb == PatchVerb.Apply ? "applied" : "reverted") + " — nothing to do.\n\n" + diagnostic,
                    "OK");
                return;
            }

            // Mixed state (some files in each marker form).
            EditorUtility.DisplayDialog(
                DIALOG_TITLE,
                "0 file(s) changed — shader files are in a mixed state (some stock, some custom).\n\n" + diagnostic +
                "\n\nTry running Revert Patch first, then Apply Patch.",
                "OK");
        }

        [MenuItem("Cam/AI Slop/Poiyomi UV Tile Discard Labels/Apply Patch")]
        static void ApplyMenu() { RunPatch(PatchVerb.Apply); }

        [MenuItem("Cam/AI Slop/Poiyomi UV Tile Discard Labels/Revert Patch")]
        static void RevertMenu() { RunPatch(PatchVerb.Revert); }
    }
}
