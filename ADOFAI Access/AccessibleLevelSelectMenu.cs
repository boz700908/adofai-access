using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ADOFAI_Access
{
    internal static class AccessibleLevelSelectMenu
    {
        private const KeyCode ToggleKey = KeyCode.F6;
        private const KeyCode ExecuteKey = KeyCode.Return;
        private const KeyCode ExecuteKeyAlt = KeyCode.Space;

        private static readonly List<MenuEntry> Entries = new List<MenuEntry>();
        private static bool _isOpen;
        private static bool _restoreResponsive;
        private static int _selectedIndex;
        private static bool _openHintSpoken;

        private sealed class MenuEntry
        {
            public string Label;
            public Action Execute;
        }

        public static void Tick()
        {
            bool inLevelSelect = ADOBase.sceneName == GCNS.sceneLevelSelect && ADOBase.levelSelect is scnLevelSelect;
            if (!inLevelSelect)
            {
                _openHintSpoken = false;

                if (_isOpen)
                {
                    Close(speak: false);
                }

                return;
            }

            if (!_openHintSpoken)
            {
                _openHintSpoken = true;
                MenuNarration.Speak("Press F6 to open accessible menu", interrupt: true);
            }

            if (Input.GetKeyDown(ToggleKey))
            {
                if (_isOpen)
                {
                    Close(speak: true);
                }
                else
                {
                    Open();
                }

                return;
            }

            if (!_isOpen)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close(speak: true);
                return;
            }

            if (Entries.Count == 0)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                MoveSelection(-1);
                return;
            }

            if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                MoveSelection(1);
                return;
            }

            if (Input.GetKeyDown(KeyCode.Home))
            {
                _selectedIndex = 0;
                SpeakSelection();
                return;
            }

            if (Input.GetKeyDown(KeyCode.End))
            {
                _selectedIndex = Entries.Count - 1;
                SpeakSelection();
                return;
            }

            if (Input.GetKeyDown(ExecuteKey) || Input.GetKeyDown(ExecuteKeyAlt))
            {
                ExecuteSelection();
            }
        }

        private static void Open()
        {
            BuildEntries();
            if (Entries.Count == 0)
            {
                MenuNarration.Speak("Accessible menu unavailable here", interrupt: true);
                return;
            }

            _isOpen = true;
            _selectedIndex = 0;

            scrController controller = ADOBase.controller;
            if (controller != null)
            {
                _restoreResponsive = controller.responsive;
                controller.responsive = false;
            }
            else
            {
                _restoreResponsive = false;
            }

            MenuNarration.Speak("Accessible menu open. Up and down to navigate. Enter to activate. Escape to close.", interrupt: true);
            SpeakSelection();
        }

        private static void Close(bool speak)
        {
            _isOpen = false;
            Entries.Clear();

            scrController controller = ADOBase.controller;
            if (controller != null)
            {
                controller.responsive = _restoreResponsive;
            }

            _restoreResponsive = false;

            if (speak)
            {
                MenuNarration.Speak("Accessible menu closed", interrupt: true);
            }
        }

        private static void MoveSelection(int delta)
        {
            if (Entries.Count == 0)
            {
                return;
            }

            _selectedIndex += delta;
            if (_selectedIndex < 0)
            {
                _selectedIndex = Entries.Count - 1;
            }
            else if (_selectedIndex >= Entries.Count)
            {
                _selectedIndex = 0;
            }

            SpeakSelection();
        }

        private static void SpeakSelection()
        {
            if (_selectedIndex < 0 || _selectedIndex >= Entries.Count)
            {
                return;
            }

            MenuEntry selected = Entries[_selectedIndex];
            MenuNarration.Speak($"{selected.Label}, {_selectedIndex + 1} of {Entries.Count}", interrupt: true);
        }

        private static void ExecuteSelection()
        {
            if (_selectedIndex < 0 || _selectedIndex >= Entries.Count)
            {
                return;
            }

            MenuEntry selected = Entries[_selectedIndex];
            selected.Execute?.Invoke();
        }

        private static void BuildEntries()
        {
            Entries.Clear();

            AddEntry("Open settings", () =>
            {
                Close(speak: false);
                scrController controller = ADOBase.controller;
                if (controller?.takeScreenshot != null)
                {
                    MenuNarration.Speak("Opening settings", interrupt: true);
                    controller.takeScreenshot.ShowPauseMenu(goToSettings: true);
                }
            });

            AddEntry("Open pause menu", () =>
            {
                Close(speak: false);
                scrController controller = ADOBase.controller;
                if (controller?.takeScreenshot != null)
                {
                    MenuNarration.Speak("Opening pause menu", interrupt: true);
                    controller.takeScreenshot.ShowPauseMenu(goToSettings: false);
                }
            });

            AddEntry("Open calibration", () =>
            {
                Close(speak: false);
                MenuNarration.Speak("Opening calibration", interrupt: true);
                ADOBase.GoToCalibration();
            });

            AddEntry("Open custom levels", () =>
            {
                Close(speak: false);
                MenuNarration.Speak("Opening custom levels", interrupt: true);
                ADOBase.controller?.PortalTravelAction(Portal.CustomLevelsScene);
            });

            AddEntry("Open level editor", () =>
            {
                Close(speak: false);
                MenuNarration.Speak("Opening level editor", interrupt: true);
                ADOBase.controller?.PortalTravelAction(Portal.EditorScene);
            });

            if (NeoCosmosManager.instance != null && NeoCosmosManager.instance.installed)
            {
                AddEntry("Open Neo Cosmos map", () =>
                {
                    Close(speak: false);
                    MenuNarration.Speak("Opening Neo Cosmos map", interrupt: true);
                    ADOBase.controller?.PortalTravelAction(Portal.TaroDLCMap);
                });
            }

            if (VegaDLCManager.instance != null && VegaDLCManager.instance.installed)
            {
                AddEntry("Open Vega map", () =>
                {
                    Close(speak: false);
                    MenuNarration.Speak("Opening Vega map", interrupt: true);
                    ADOBase.controller?.PortalTravelAction(Portal.VegaDLCMap);
                });
            }

            IEnumerable<string> worldIds = scrPortal.portals.Keys
                .Where(k => !string.IsNullOrEmpty(k))
                .OrderBy(WorldSortKey)
                .ThenBy(k => k, StringComparer.Ordinal);

            foreach (string worldId in worldIds)
            {
                string label = $"Enter world {DisplayWorldId(worldId)}";
                string targetWorld = worldId;
                AddEntry(label, () =>
                {
                    Close(speak: false);
                    MenuNarration.Speak($"Entering world {DisplayWorldId(targetWorld)}", interrupt: true);
                    ADOBase.controller?.EnterWorld(targetWorld);
                });
            }
        }

        private static void AddEntry(string label, Action execute)
        {
            Entries.Add(new MenuEntry
            {
                Label = label,
                Execute = execute
            });
        }

        private static int WorldSortKey(string worldId)
        {
            if (int.TryParse(worldId, out int numeric))
            {
                return numeric;
            }

            if (worldId == "B")
            {
                return 100;
            }

            if (worldId.StartsWith("X", StringComparison.OrdinalIgnoreCase))
            {
                return 200;
            }

            if (worldId.StartsWith("M", StringComparison.OrdinalIgnoreCase))
            {
                return 300;
            }

            if (worldId.StartsWith("T", StringComparison.OrdinalIgnoreCase))
            {
                return 400;
            }

            return 500;
        }

        private static string DisplayWorldId(string worldId)
        {
            if (string.IsNullOrEmpty(worldId))
            {
                return worldId;
            }

            if (worldId.EndsWith("J", StringComparison.OrdinalIgnoreCase) && worldId.Length > 1)
            {
                return worldId.Substring(0, worldId.Length - 1);
            }

            return worldId;
        }
    }
}
