using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace ADOFAI_Access
{
    internal static class PatternPreview
    {
        private const KeyCode ToggleKey = KeyCode.F9;

        private static bool _toggleHintSpoken;
        private static bool _runtimeModeActive;
        private static PlayMode _runtimeMode = PlayMode.Vanilla;
        private static bool _hasStoredPreviousAuto;
        private static bool _previousAuto;
        private static readonly HashSet<int> HandledSeqIds = new HashSet<int>();

        public static bool IsActive => ModSettings.Current.playMode != PlayMode.Vanilla;
        public static PlayMode CurrentMode => ModSettings.Current.playMode;
        public static string ToggleHint => "Press F9 to cycle play mode";

        public static void Tick()
        {
            bool inGameplay = PlayModeTiming.IsGameplayRuntimeAvailable();
            if (!inGameplay)
            {
                _toggleHintSpoken = false;
                StopRuntimeMode();
            }
            else if (!_toggleHintSpoken)
            {
                _toggleHintSpoken = true;
                MenuNarration.Speak(ToggleHint, interrupt: false);
            }

            if (!AccessSettingsMenu.IsOpen && Input.GetKeyDown(ToggleKey))
            {
                if (!inGameplay)
                {
                    MenuNarration.Speak("Play mode unavailable here", interrupt: true);
                }
                else
                {
                    CycleMode(speak: true);
                }
            }

            if (!inGameplay || LevelPreview.IsActive)
            {
                StopRuntimeMode();
                return;
            }

            PlayMode desiredMode = ModSettings.Current.playMode;
            if (desiredMode == PlayMode.Vanilla)
            {
                StopRuntimeMode();
                return;
            }

            EnsureRuntimeModeStarted(desiredMode);

            scrController controller = ADOBase.controller;
            if (controller == null)
            {
                return;
            }

            if (controller.paused || !PlayModeTiming.CanScheduleInCurrentState(controller))
            {
                ResetCueSchedulingState();
                TapCueService.StopAllCues();
                if (_runtimeMode == PlayMode.ListenRepeat)
                {
                    ListenRepeatMode.Stop();
                    RestoreAuto();
                }
                return;
            }

            switch (_runtimeMode)
            {
                case PlayMode.PatternPreview:
                    ListenRepeatMode.Stop();
                    RestoreAuto();
                    TryScheduleNextBarCues();
                    break;
                case PlayMode.ListenRepeat:
                    ListenRepeatMode.Tick(controller);
                    break;
            }
        }

        public static void CycleMode(bool speak)
        {
            SetMode(GetNextMode(CurrentMode), speak);
        }

        public static void StepMode(int delta, bool speak)
        {
            if (delta == 0)
            {
                return;
            }

            PlayMode mode = CurrentMode;
            int steps = Math.Abs(delta);
            for (int i = 0; i < steps; i++)
            {
                mode = delta > 0 ? GetNextMode(mode) : GetPreviousMode(mode);
            }

            SetMode(mode, speak);
        }

        public static void SetMode(PlayMode mode, bool speak)
        {
            if (!Enum.IsDefined(typeof(PlayMode), mode))
            {
                mode = PlayMode.Vanilla;
            }

            ModSettings.Current.playMode = mode;
            ModSettings.Save();

            if (mode != PlayMode.Vanilla && LevelPreview.IsActive)
            {
                LevelPreview.Toggle();
            }

            if (speak)
            {
                MenuNarration.Speak($"Play mode {GetModeLabel(mode)}", interrupt: true);
            }
        }

        public static string GetModeLabel(PlayMode mode)
        {
            switch (mode)
            {
                case PlayMode.PatternPreview:
                    return "pattern preview";
                case PlayMode.ListenRepeat:
                    return "listen-repeat";
                default:
                    return "vanilla";
            }
        }

        internal static void RestoreAuto()
        {
            if (!_hasStoredPreviousAuto)
            {
                return;
            }

            RDC.auto = _previousAuto;
        }

        private static PlayMode GetNextMode(PlayMode mode)
        {
            switch (mode)
            {
                case PlayMode.PatternPreview:
                    return PlayMode.ListenRepeat;
                case PlayMode.ListenRepeat:
                    return PlayMode.Vanilla;
                default:
                    return PlayMode.PatternPreview;
            }
        }

        private static PlayMode GetPreviousMode(PlayMode mode)
        {
            switch (mode)
            {
                case PlayMode.PatternPreview:
                    return PlayMode.Vanilla;
                case PlayMode.Vanilla:
                    return PlayMode.ListenRepeat;
                default:
                    return PlayMode.PatternPreview;
            }
        }

        private static void EnsureRuntimeModeStarted(PlayMode mode)
        {
            if (!_runtimeModeActive)
            {
                _runtimeModeActive = true;
                _runtimeMode = mode;
                _previousAuto = RDC.auto;
                _hasStoredPreviousAuto = true;
                ResetCueSchedulingState();
                ListenRepeatMode.ResetForModeSwitch();
                MelonLogger.Msg($"[ADOFAI Access] Play mode runtime active: {GetModeLabel(mode)}.");
                return;
            }

            if (_runtimeMode == mode)
            {
                return;
            }

            RestoreAuto();
            TapCueService.StopAllCues();
            _runtimeMode = mode;
            ResetCueSchedulingState();
            ListenRepeatMode.ResetForModeSwitch();
            MelonLogger.Msg($"[ADOFAI Access] Play mode runtime switched: {GetModeLabel(mode)}.");
        }

        private static void StopRuntimeMode()
        {
            if (!_runtimeModeActive)
            {
                return;
            }

            _runtimeModeActive = false;
            TapCueService.StopAllCues();
            RestoreAuto();
            _runtimeMode = PlayMode.Vanilla;
            ResetCueSchedulingState();
            ListenRepeatMode.Stop();
        }

        private static void TryScheduleNextBarCues()
        {
            scrConductor conductor = ADOBase.conductor;
            scrLevelMaker levelMaker = ADOBase.lm;
            if (conductor == null || levelMaker == null || levelMaker.listFloors == null)
            {
                return;
            }

            double nowDsp = conductor.dspTime;
            List<scrFloor> floors = levelMaker.listFloors;
            for (int i = 0; i < floors.Count; i++)
            {
                scrFloor floor = floors[i];
                if (floor == null || floor.auto)
                {
                    continue;
                }

                if (HandledSeqIds.Contains(floor.seqID))
                {
                    continue;
                }

                if (!PlayModeTiming.TryGetPreviewCueDsp(conductor, floors, floor, out double previewDueDsp))
                {
                    continue;
                }

                double untilPreview = previewDueDsp - nowDsp;
                if (untilPreview < 0.0)
                {
                    HandledSeqIds.Add(floor.seqID);
                    continue;
                }

                if (untilPreview > PlayModeTiming.GetScheduleHorizonSeconds(conductor))
                {
                    continue;
                }

                HandledSeqIds.Add(floor.seqID);
                if (untilPreview >= 0.0)
                {
                    TapCueService.PlayCueAt(previewDueDsp);
                }
                else
                {
                    TapCueService.PlayCueNow();
                }
            }
        }

        private static void ResetCueSchedulingState()
        {
            HandledSeqIds.Clear();
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(scrController), "PlayerControl_Enter")]
    internal static class PatternPreviewPlayerControlEnterPatch
    {
        private static void Postfix()
        {
            ListenRepeatMode.PrimeStart(ADOBase.controller);
        }
    }
}
