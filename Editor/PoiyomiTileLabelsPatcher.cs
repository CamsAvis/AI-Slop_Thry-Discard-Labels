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
        const string SHADER_ROOT = "Packages/com.poiyomi.toon/_PoiyomiShaders/Shaders";

        // Matches both Poi 9.2+ (ThryMultiFloatButtons(u0, u1, u2, u3, _UDIM...)) and Poi 8.0-9.1
        // (ThryMultiFloats(true, _UDIM...)) for UV Tile Discard and Face Discard grids.
        static readonly (Regex pattern, string replacement)[] APPLY_PATCHES = new[]
        {
            (
                new Regex(@"\[ThryMultiFloatButtons\(u0, u1, u2, u3, _UDIM", RegexOptions.Compiled),
                "[ThryNamedTileButtons(u0, u1, u2, u3, _UDIM"
            ),
            (
                new Regex(@"\[ThryMultiFloats\(true, _UDIM", RegexOptions.Compiled),
                "[ThryNamedTileFloats(true, _UDIM"
            ),
        };

        static readonly (Regex pattern, string replacement)[] REVERT_PATCHES = new[]
        {
            (
                new Regex(@"\[ThryNamedTileButtons\(u0, u1, u2, u3, _UDIM", RegexOptions.Compiled),
                "[ThryMultiFloatButtons(u0, u1, u2, u3, _UDIM"
            ),
            (
                new Regex(@"\[ThryNamedTileFloats\(true, _UDIM", RegexOptions.Compiled),
                "[ThryMultiFloats(true, _UDIM"
            ),
        };

        // Used for compatibility detection — count files showing UDIM-grid evidence and their drawer state.
        static readonly Regex UDIM_GRID_MARKER = new Regex(@"_UDIMDiscardRow\d_\d", RegexOptions.Compiled);
        static readonly Regex STOCK_UDIM_DRAWER_MARKER = new Regex(@"\[ThryMultiFloat(?:Buttons|s)\([^\]]*_UDIM", RegexOptions.Compiled);
        static readonly Regex CUSTOM_UDIM_DRAWER_MARKER = new Regex(@"\[ThryNamedTile(?:Buttons|Floats)\([^\]]*_UDIM", RegexOptions.Compiled);

        enum PatchVerb { Apply, Revert }

        static void RunPatch(PatchVerb verb)
        {
            if (!Directory.Exists(SHADER_ROOT))
            {
                EditorUtility.DisplayDialog(
                    DIALOG_TITLE,
                    "Poiyomi Toon package not found at:\n\n" + SHADER_ROOT +
                    "\n\nNothing was modified. Install Poiyomi via VCC and try again.",
                    "OK");
                return;
            }

            var patches = verb == PatchVerb.Apply ? APPLY_PATCHES : REVERT_PATCHES;
            string verbForLog = verb == PatchVerb.Apply ? "applied to" : "reverted";

            int changedCount = 0;
            int udimFileCount = 0;
            int stockMarkerCount = 0;
            int customMarkerCount = 0;

            string[] shaderFiles = Directory.GetFiles(SHADER_ROOT, "*.shader", SearchOption.AllDirectories);
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
                    foreach (var (pattern, replacement) in patches)
                    {
                        if (pattern.IsMatch(patched))
                            patched = pattern.Replace(patched, replacement);
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
            if (udimFileCount == 0)
            {
                EditorUtility.DisplayDialog(
                    DIALOG_TITLE,
                    "No Poiyomi shader files containing UV Tile Discard were found under:\n\n" + SHADER_ROOT +
                    "\n\nIs Poiyomi installed?",
                    "OK");
                return;
            }

            if (stockMarkerCount == 0 && customMarkerCount == 0)
            {
                EditorUtility.DisplayDialog(
                    DIALOG_TITLE,
                    "Found Poiyomi shader files with UV Tile Discard, but the drawer format isn't recognised.\n\n" +
                    "This tool supports Poiyomi Toon 8.0–9.3. If you're on a newer version, the patcher may need updating.\n\n" +
                    "Nothing was modified — your shader files are untouched.",
                    "OK");
                return;
            }

            // Already in target state.
            bool alreadyApplied = verb == PatchVerb.Apply && stockMarkerCount == 0 && customMarkerCount > 0;
            bool alreadyReverted = verb == PatchVerb.Revert && customMarkerCount == 0 && stockMarkerCount > 0;
            if (alreadyApplied || alreadyReverted)
            {
                Debug.Log(LOG_PREFIX + "already " + (verb == PatchVerb.Apply ? "applied" : "reverted") + " — nothing to do.");
                return;
            }

            // Mixed state (some files in each marker form) — partial state. Log it but don't surface a dialog;
            // the per-file logs above already explain.
            Debug.Log(LOG_PREFIX + verb + " complete: 0 file(s) changed (mixed state, see logs above).");
        }

        [MenuItem("Cam/AI Slop/Poiyomi UV Tile Discard Labels/Apply Patch")]
        static void ApplyMenu() { RunPatch(PatchVerb.Apply); }

        [MenuItem("Cam/AI Slop/Poiyomi UV Tile Discard Labels/Revert Patch")]
        static void RevertMenu() { RunPatch(PatchVerb.Revert); }
    }
}
