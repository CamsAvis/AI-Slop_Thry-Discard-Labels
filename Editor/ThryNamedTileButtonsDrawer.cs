using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Thry;
using Thry.ThryEditor;

namespace Cam.PoiyomiTileLabels
{
    public abstract class ThryNamedTileDrawerBase : MaterialPropertyDrawer
    {
        internal const string TAG_PREFIX = "_CamTileLabel_";
        const string TOOLTIP_TEXT = "Right-click any tile button to rename it";
        const float ROW_LABEL_WIDTH = 32f;

        // Poiyomi's lock-in renames animated properties to `<name>_<suffix>`, where suffix defaults to
        // the material name and can be customised via the lock button's "Locked property suffix" field
        // (override tag `thry_rename_suffix`). Normalising back to the unlocked name keeps the tag key
        // stable across lock/unlock cycles regardless of which suffix the user picked.
        static readonly Regex CANONICAL_UDIM_NAME = new Regex(@"^(_UDIM(?:Face)?DiscardRow\d_\d)(?:_.+)?$", RegexOptions.Compiled);

        internal static string CanonicalPropertyName(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return propertyName;
            Match m = CANONICAL_UDIM_NAME.Match(propertyName);
            return m.Success ? m.Groups[1].Value : propertyName;
        }

        internal static string GetTileLabelTag(Material mat, string propertyName)
        {
            if (mat == null || string.IsNullOrEmpty(propertyName)) return string.Empty;
            string canonical = CanonicalPropertyName(propertyName);
            string tag = mat.GetTag(TAG_PREFIX + canonical, false, string.Empty);
            if (!string.IsNullOrEmpty(tag)) return tag;
            // Fallback: any pre-fix tags accidentally saved under the runtime (suffixed) name.
            if (canonical != propertyName)
                return mat.GetTag(TAG_PREFIX + propertyName, false, string.Empty) ?? string.Empty;
            return string.Empty;
        }

        protected string[] _otherProperties = new string[3];
        protected MaterialProperty[] _otherMaterialProps = new MaterialProperty[3];

        static bool _loggedFallbackError;

        public override void OnGUI(Rect position, MaterialProperty prop, GUIContent label, MaterialEditor editor)
        {
            // Safety net: if Thry's editor state isn't available, fall back to plain toggles so the
            // inspector remains usable instead of throwing on ShaderEditor.Active.PropertyDictionary.
            if (ShaderEditor.Active == null)
            {
                DrawFallback(position, prop, label, editor);
                return;
            }

            try
            {
                DrawNormal(position, prop, label, editor);
            }
            catch (System.Exception e)
            {
                if (!_loggedFallbackError)
                {
                    Debug.LogWarning("[PoiyomiTileLabels] drawer error, falling back to plain toggles: " + e.Message);
                    _loggedFallbackError = true;
                }
                DrawFallback(position, prop, label, editor);
            }
        }

        void DrawNormal(Rect position, MaterialProperty prop, GUIContent label, MaterialEditor editor)
        {
            for (int i = 0; i < _otherProperties.Length; i++)
            {
                if (!ShaderEditor.Active.PropertyDictionary.TryGetValue(_otherProperties[i], out var sProp))
                {
                    _otherMaterialProps[i] = null;
                    continue;
                }
                sProp.UpdatedMaterialPropertyReference();
                _otherMaterialProps[i] = sProp.MaterialProperty;
            }

            MaterialProperty[] props = new MaterialProperty[4];
            string[] propNames = new string[4];
            props[0] = prop;
            propNames[0] = prop.name;
            for (int i = 0; i < 3; i++)
            {
                props[i + 1] = _otherMaterialProps[i];
                propNames[i + 1] = _otherProperties[i];
            }

            Material refMat = editor.target as Material;
            string[] displayLabels = new string[4];
            for (int i = 0; i < 4; i++)
            {
                displayLabels[i] = GetTileLabelTag(refMat, propNames[i]);
            }

            var labelContent = new GUIContent(label != null ? label.text : string.Empty, TOOLTIP_TEXT);

            // Narrow the prefix-label reserve so the 4-button grid uses nearly the full inspector row.
            // Zero the indent level around PrefixLabel so nested-section indent doesn't eat the label width.
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            int prevIndent = EditorGUI.indentLevel;
            EditorGUIUtility.labelWidth = ROW_LABEL_WIDTH;
            EditorGUI.indentLevel = 0;
            Rect fieldR = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), labelContent);
            EditorGUI.indentLevel = prevIndent;
            EditorGUIUtility.labelWidth = prevLabelWidth;

            float spacing = 4f;
            float buttonWidth = (fieldR.width - spacing * 3) / 4;

            bool anyChanged = false;
            using (new GUILib.IndentOverrideScope(0))
            {
                for (int i = 0; i < 4; i++)
                {
                    if (props[i] == null) continue;

                    Rect buttonRect = new Rect(fieldR.x + i * (buttonWidth + spacing), fieldR.y, buttonWidth, fieldR.height);

                    Event evt = Event.current;
                    if (evt.type == EventType.MouseDown && evt.button == 1 && buttonRect.Contains(evt.mousePosition))
                    {
                        Vector2 screenPos = GUIUtility.GUIToScreenPoint(evt.mousePosition);
                        ShowContextMenu(editor.targets, propNames[i], string.Empty, screenPos);
                        evt.Use();
                    }
                    else if (evt.type == EventType.ContextClick && buttonRect.Contains(evt.mousePosition))
                    {
                        evt.Use();
                    }

                    using (new GUILib.AnimationScope(editor, props[i]))
                    {
                        bool on = props[i].floatValue > 0.5f;
                        EditorGUI.showMixedValue = props[i].hasMixedValue;
                        bool newOn = GUI.Toggle(buttonRect, on, displayLabels[i], "Button");
                        EditorGUI.showMixedValue = false;

                        if (newOn != on)
                        {
                            props[i].floatValue = newOn ? 1f : 0f;
                            anyChanged = true;

                            if (i > 0 && ShaderEditor.Active.PropertyDictionary.TryGetValue(_otherProperties[i - 1], out var changedProp))
                            {
                                changedProp.CheckForValueChange();
                            }
                        }
                    }
                }
            }

            if (anyChanged && ShaderEditor.Active.IsInAnimationMode && !ShaderEditor.Active.CurrentProperty.IsAnimated)
                ShaderEditor.Active.CurrentProperty.SetAnimated(true, false);

            bool animated = ShaderEditor.Active.CurrentProperty.IsAnimated;
            bool renamed = ShaderEditor.Active.CurrentProperty.IsRenaming;
            for (int i = 0; i < _otherProperties.Length; i++)
            {
                if (ShaderEditor.Active.PropertyDictionary.TryGetValue(_otherProperties[i], out var sProp))
                    sProp.SetAnimated(animated, renamed);
            }
        }

        // Fallback when Thry's editor state isn't usable. Draws 4 plain toggle buttons bound directly to
        // the underlying float properties on the active material. No custom labels, no animation hookup,
        // but the inspector remains functional.
        void DrawFallback(Rect position, MaterialProperty prop, GUIContent label, MaterialEditor editor)
        {
            Material mat = editor.target as Material;
            if (mat == null)
            {
                EditorGUI.LabelField(position, label != null ? label.text : string.Empty, "(no material)");
                return;
            }

            float prevLabelWidth = EditorGUIUtility.labelWidth;
            int prevIndent = EditorGUI.indentLevel;
            EditorGUIUtility.labelWidth = ROW_LABEL_WIDTH;
            EditorGUI.indentLevel = 0;
            Rect fieldR = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);
            EditorGUI.indentLevel = prevIndent;
            EditorGUIUtility.labelWidth = prevLabelWidth;
            float spacing = 4f;
            float buttonWidth = (fieldR.width - spacing * 3) / 4;

            string[] names = new[] { prop.name, _otherProperties[0], _otherProperties[1], _otherProperties[2] };
            for (int i = 0; i < 4; i++)
            {
                if (string.IsNullOrEmpty(names[i]) || !mat.HasProperty(names[i])) continue;
                Rect r = new Rect(fieldR.x + i * (buttonWidth + spacing), fieldR.y, buttonWidth, fieldR.height);
                bool on = mat.GetFloat(names[i]) > 0.5f;
                bool newOn = GUI.Toggle(r, on, "", "Button");
                if (newOn != on)
                {
                    Undo.RecordObject(mat, "Toggle UV Tile");
                    mat.SetFloat(names[i], newOn ? 1f : 0f);
                    EditorUtility.SetDirty(mat);
                }
            }
        }

        public override float GetPropertyHeight(MaterialProperty prop, string label, MaterialEditor editor)
        {
            try
            {
                if (ShaderEditor.Active != null)
                {
                    ShaderProperty.RegisterDrawer(this);
                    if (ShaderEditor.Active.PropertyDictionary != null
                        && ShaderEditor.Active.PropertyDictionary.TryGetValue(prop.name, out var mainShaderProp))
                    {
                        mainShaderProp.AdditionalDefaultCheckProperties = _otherProperties;
                    }
                }
            }
            catch { /* never block height calc */ }

            return base.GetPropertyHeight(prop, label, editor);
        }

        static void ShowContextMenu(Object[] targets, string propertyName, string defaultLabel, Vector2 screenPos)
        {
            Object[] capturedTargets = new Object[targets != null ? targets.Length : 0];
            if (targets != null) System.Array.Copy(targets, capturedTargets, targets.Length);
            // Always write under the canonical (unlocked) property name so the tag survives lock/unlock,
            // material rename, and custom rename-suffix changes.
            string canonicalName = CanonicalPropertyName(propertyName);
            string tagKey = TAG_PREFIX + canonicalName;
            string runtimeTagKey = canonicalName != propertyName ? TAG_PREFIX + propertyName : null;

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Rename..."), false, () =>
            {
                TileLabelRenamePopup.Show(capturedTargets, tagKey, defaultLabel, screenPos);
            });
            menu.AddItem(new GUIContent("Reset to default"), false, () =>
            {
                ApplyTagToTargets(capturedTargets, tagKey, string.Empty);
                // Also clear any stale tag saved under the runtime (suffixed) name from before normalisation.
                if (runtimeTagKey != null)
                    ApplyTagToTargets(capturedTargets, runtimeTagKey, string.Empty);
            });
            menu.ShowAsContext();
        }

        internal static void ApplyTagToTargets(Object[] targets, string tagKey, string value)
        {
            if (targets == null || targets.Length == 0) return;
            Undo.RegisterCompleteObjectUndo(targets, "Rename UV Tile Label");
            foreach (var t in targets)
            {
                if (t is Material mat)
                {
                    mat.SetOverrideTag(tagKey, value ?? string.Empty);
                    EditorUtility.SetDirty(mat);
                }
            }
        }
    }

    // Matches Poi 9.2+ shader attribute: [ThryMultiFloatButtons(u0, u1, u2, u3, prop1, prop2, prop3)]
    // The four label args are accepted to satisfy the attribute signature but are ignored —
    // empty defaults are intentional so the user names every tile explicitly.
    public class ThryNamedTileButtonsDrawer : ThryNamedTileDrawerBase
    {
        public ThryNamedTileButtonsDrawer(string label0, string label1, string label2, string label3, string prop1, string prop2, string prop3)
        {
            _otherProperties[0] = prop1;
            _otherProperties[1] = prop2;
            _otherProperties[2] = prop3;
        }
    }

    // Matches Poi 8.0-9.1 shader attribute: [ThryMultiFloats(true, prop1, prop2, prop3)]
    public class ThryNamedTileFloatsDrawer : ThryNamedTileDrawerBase
    {
        public ThryNamedTileFloatsDrawer(string displayAsToggles, string prop1, string prop2, string prop3)
        {
            _otherProperties[0] = prop1;
            _otherProperties[1] = prop2;
            _otherProperties[2] = prop3;
        }
    }

    internal class TileLabelRenamePopup : EditorWindow
    {
        Object[] _targets;
        string _tagKey;
        string _value;
        bool _focusGrabbed;

        public static void Show(Object[] targets, string tagKey, string defaultLabel, Vector2 screenPos)
        {
            var win = CreateInstance<TileLabelRenamePopup>();
            win._targets = targets;
            win._tagKey = tagKey;
            Material firstMat = (targets != null && targets.Length > 0) ? targets[0] as Material : null;
            string current = firstMat != null ? firstMat.GetTag(tagKey, false, string.Empty) : string.Empty;
            win._value = string.IsNullOrEmpty(current) ? defaultLabel : current;
            win.titleContent = new GUIContent("Rename tile label");
            win.position = new Rect(screenPos.x, screenPos.y, 260f, 80f);
            win.ShowPopup();
            win.Focus();
        }

        void OnGUI()
        {
            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                Close();
                e.Use();
                return;
            }
            bool submitOnEnter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);

            GUILayout.Space(6);
            GUI.SetNextControlName("LabelField");
            _value = EditorGUILayout.TextField("Label", _value);
            if (!_focusGrabbed)
            {
                EditorGUI.FocusTextInControl("LabelField");
                _focusGrabbed = true;
            }

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            bool cancel = GUILayout.Button("Cancel", GUILayout.Width(80));
            bool ok = GUILayout.Button("OK", GUILayout.Width(80));
            GUILayout.EndHorizontal();

            if (cancel)
            {
                Close();
                return;
            }
            if (ok || submitOnEnter)
            {
                ThryNamedTileDrawerBase.ApplyTagToTargets(_targets, _tagKey, _value);
                Close();
            }
        }

        void OnLostFocus()
        {
            Close();
        }
    }
}
