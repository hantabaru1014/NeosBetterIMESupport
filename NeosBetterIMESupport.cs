using NeosModLoader;
using HarmonyLib;
using FrooxEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using BaseX;
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;

namespace NeosBetterIMESupport
{
    public class NeosBetterIMESupport : NeosMod
    {
        public override string Name => "NeosBetterIMESupport";
        public override string Author => "hantabaru1014";
        public override string Version => "1.0.1";
        public override string Link => "https://github.com/hantabaru1014/NeosBetterIMESupport";

        private static Keyboard? _keyboard;
        private static IText? _editingText;
        private static bool _stringChanged = false;
        private static bool _isTypingUnsettled = false;

        public override void OnEngineInit()
        {
            _keyboard = Keyboard.current;
            _keyboard.onIMECompositionChange += OnIMECompositionChange;

            var harmony = new Harmony("net.hantabaru1014.NeosBetterIMESupport");
            harmony.PatchAll();
            TextEditor_EditCoroutine_Patch.Patch(harmony);
        }

        private void OnIMECompositionChange(IMECompositionString compStr)
        {
            if (_editingText is null) return;
            InsertComposition(compStr.ToString());
        }

        private static bool HasSelection
        {
            get => _editingText is null ? false : _editingText.SelectionStart != -1;
            set
            {
                if (_editingText is null) return;
                _editingText.SelectionStart = value ? CaretPosition : -1;
            }
        }

        private static int CaretPosition
        {
            get => MathX.Clamp(_editingText?.CaretPosition ?? -1, -1, (_editingText?.Text.Length + 1) ?? 0);
            set
            {
                if (_editingText is null) return;
                value = MathX.Clamp(value, 0, _editingText.Text.Length + 1);
                if (value > 0 && value < _editingText.Text.Length && char.GetUnicodeCategory(_editingText.Text, value) == System.Globalization.UnicodeCategory.Surrogate)
                {
                    value += MathX.Sign(value - _editingText.CaretPosition);
                }
                _editingText.CaretPosition = value;
            }
        }

        private static int SelectionStart
        {
            get => MathX.Clamp(_editingText?.SelectionStart ?? -1, -1, _editingText?.Text.Length ?? 0);
            set
            {
                if (_editingText is null) return;
                _editingText.SelectionStart = MathX.Clamp(value, 0, _editingText.Text.Length + 1);
            }
        }

        private static int SelectionLength
        {
            get => !HasSelection ? 0 : MathX.Abs(CaretPosition - SelectionStart);
            set => CaretPosition = SelectionStart + value;
        }

        private static void DeleteSelection()
        {
            if (_editingText is null || SelectionLength == 0) return;
            var start = MathX.Min(SelectionStart, CaretPosition);
            _editingText.Text = _editingText.Text.Remove(start, SelectionLength);
            HasSelection = false;
            CaretPosition = start;
        }

        private static void InsertComposition(string str)
        {
            _editingText?.RunSynchronously(() =>
            {
                if (_editingText is null) return;
                if (HasSelection)
                {
                    DeleteSelection();
                }
                if (str == string.Empty)
                {
                    // 候補から入力確定をすると空文字が入ってくる
                    HasSelection = false;
                    _isTypingUnsettled = false;
                    return;
                }
                SelectionStart = CaretPosition;
                _editingText.Text = _editingText.Text.Substring(0, CaretPosition) + str + _editingText.Text.Substring(CaretPosition, _editingText.Text.Length - CaretPosition);
                CaretPosition += str.Length;
                _isTypingUnsettled = true;
                _stringChanged = true;
            }, true);
        }

        [HarmonyPatch(typeof(InputInterface))]
        class InputInterface_Patch
        {
            [HarmonyPostfix]
            [HarmonyPatch(nameof(InputInterface.ShowKeyboard))]
            static void ShowKeyboard_Postfix(IText targetText)
            {
                //_keyboard?.SetIMECursorPosition(new Vector2(100, 200));
                _editingText = targetText;
            }

            [HarmonyPostfix]
            [HarmonyPatch(nameof(InputInterface.HideKeyboard))]
            static void HideKeyboard_Postfix()
            {
                _editingText = null;
                _isTypingUnsettled = false;
            }
        }

        class TextEditor_EditCoroutine_Patch
        {
            static readonly Type TargetInternalClass = typeof(TextEditor).GetNestedType("<EditCoroutine>d__78", BindingFlags.Instance | BindingFlags.NonPublic);

            public static void Patch(Harmony harmony)
            {
                var targetMethod = AccessTools.Method(TargetInternalClass, "MoveNext");
                var transpiler = AccessTools.Method(typeof(TextEditor_EditCoroutine_Patch), nameof(TextEditor_EditCoroutine_Patch.Transpiler));
                harmony.Patch(targetMethod, transpiler: new HarmonyMethod(transpiler));
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = instructions.ToList();
                var getKeyRepeatMethod = AccessTools.Method(typeof(InputInterface), nameof(InputInterface.GetKeyRepeat));
                int[] arrowKeys = new[]
                {
                    (int)FrooxEngine.Key.UpArrow,
                    (int)FrooxEngine.Key.DownArrow,
                    (int)FrooxEngine.Key.RightArrow,
                    (int)FrooxEngine.Key.LeftArrow,
                };
                for (var i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Ldloc_3 && codes[i+1].opcode == OpCodes.Brfalse_S)
                    {
                        codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, 
                                AccessTools.Method(typeof(TextEditor_EditCoroutine_Patch), nameof(TextEditor_EditCoroutine_Patch.IsStringChanged))));
                        Msg("Patched TextEditor.EditCoroutine IsStringChanged");
                        break; // これが最後のパッチ対象
                    }
                    else if (codes[i].Calls(getKeyRepeatMethod) && codes[i-1].opcode == OpCodes.Ldc_I4 && arrowKeys.Contains((int)codes[i-1].operand))
                    {
                        codes[i] = new CodeInstruction(OpCodes.Call, 
                            AccessTools.Method(typeof(TextEditor_EditCoroutine_Patch), nameof(TextEditor_EditCoroutine_Patch.GetKeyRepeat)));
                        Msg("Patched TextEditor.EditCoroutine GetKeyRepeat");
                    }
                }
                return codes.AsEnumerable();
            }

            static bool IsStringChanged(bool original)
            {
                if (_stringChanged)
                {
                    _stringChanged = false;
                    return true;
                }
                return original;
            }

            static bool GetKeyRepeat(InputInterface inputInterface, FrooxEngine.Key key)
            {
                if (_isTypingUnsettled)
                {
                    // 候補選択等の操作なので無視
                    return false;
                }
                return inputInterface.GetKeyRepeat(key);
            }
        }
    }
}