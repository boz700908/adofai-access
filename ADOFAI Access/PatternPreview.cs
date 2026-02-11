using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace ADOFAI_Access
{
    internal static class PatternPreview
    {
        private const KeyCode ToggleKey = KeyCode.F9;
        private const double CueScheduleHorizonSeconds = 0.25;
        private const double CueLateGraceSeconds = 0.04;
        private const int BeatsPerBar = 4;

        private static bool _active;
        private static bool _toggleHintSpoken;
        private static readonly HashSet<int> HandledSeqIds = new HashSet<int>();

        public static bool IsActive => _active;
        public static string ToggleHint => "Press F9 to toggle pattern preview";

        public static void Tick()
        {
            bool inGameplay = IsGameplayRuntimeAvailable();
            if (!inGameplay)
            {
                _toggleHintSpoken = false;
            }
            else if (!_toggleHintSpoken)
            {
                _toggleHintSpoken = true;
                MenuNarration.Speak(ToggleHint, interrupt: false);
            }

            if (Input.GetKeyDown(ToggleKey))
            {
                if (!Toggle())
                {
                    MenuNarration.Speak("Pattern preview unavailable here", interrupt: true);
                }
            }

            if (!_active)
            {
                return;
            }

            if (!IsGameplayRuntimeAvailable())
            {
                StopInternal(speak: false);
                return;
            }

            scrController controller = ADOBase.controller;
            if (controller == null)
            {
                return;
            }

            if (controller.paused || !CanScheduleInCurrentState(controller))
            {
                ResetSchedulingState();
                TapCueService.StopAllCues();
                return;
            }

            TryScheduleNextBarCues();
        }

        public static bool Toggle()
        {
            if (_active)
            {
                StopInternal(speak: true);
                return true;
            }

            return StartInternal();
        }

        private static bool StartInternal()
        {
            if (_active)
            {
                return true;
            }

            if (!IsGameplayRuntimeAvailable())
            {
                MelonLogger.Msg("[ADOFAI Access] Pattern preview start rejected (not in gameplay).");
                return false;
            }

            if (LevelPreview.IsActive)
            {
                LevelPreview.Toggle();
            }

            _active = true;
            HandledSeqIds.Clear();
            MelonLogger.Msg("[ADOFAI Access] Pattern preview enabled.");
            MenuNarration.Speak("Pattern preview on", interrupt: true);
            return true;
        }

        private static void StopInternal(bool speak)
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            ResetSchedulingState();
            TapCueService.StopAllCues();

            if (speak)
            {
                MelonLogger.Msg("[ADOFAI Access] Pattern preview disabled.");
                MenuNarration.Speak("Pattern preview off", interrupt: true);
            }
        }

        private static bool IsGameplayRuntimeAvailable()
        {
            scrController controller = ADOBase.controller;
            if (controller == null || ADOBase.isLevelEditor)
            {
                return false;
            }

            if (ADOBase.sceneName == GCNS.sceneGame || ADOBase.isScnGame || ADOBase.isPlayingLevel)
            {
                return true;
            }

            return controller.gameworld || controller.isPuzzleRoom;
        }

        private static void TryScheduleNextBarCues()
        {
            scrController controller = ADOBase.controller;
            scrConductor conductor = ADOBase.conductor;
            scrLevelMaker levelMaker = ADOBase.lm;
            if (controller == null || conductor == null || levelMaker == null || levelMaker.listFloors == null)
            {
                return;
            }

            if (!TryGetCurrentBeat(controller, conductor, out double currentBeat))
            {
                return;
            }

            double nowDsp = conductor.dspTime;
            double lookaheadLimitBeat = currentBeat + BeatsPerBar + 0.0001;

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

                // Only preview notes in the next bar window.
                if (floor.entryBeat <= currentBeat || floor.entryBeat > lookaheadLimitBeat)
                {
                    continue;
                }

                if (!TryGetPreviewCueDsp(conductor, floors, floor, out double previewDueDsp))
                {
                    continue;
                }

                double untilPreview = previewDueDsp - nowDsp;
                if (untilPreview < -CueLateGraceSeconds)
                {
                    HandledSeqIds.Add(floor.seqID);
                    continue;
                }

                if (untilPreview > CueScheduleHorizonSeconds)
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

        private static bool TryGetCurrentBeat(scrController controller, scrConductor conductor, out double currentBeat)
        {
            currentBeat = 0.0;

            if (controller.state == States.Countdown || controller.state == States.Checkpoint || controller.state == States.Start)
            {
                currentBeat = conductor.beatNumber - conductor.adjustedCountdownTicks;
                return true;
            }

            scrFloor current = controller.currFloor;
            if (current == null)
            {
                return false;
            }

            scrFloor next = current.nextfloor;
            if (next == null)
            {
                currentBeat = current.entryBeat;
                return true;
            }

            double t0 = current.entryTimePitchAdj;
            double t1 = next.entryTimePitchAdj;
            double b0 = current.entryBeat;
            double b1 = next.entryBeat;
            if (t1 <= t0 || b1 < b0)
            {
                currentBeat = b0;
                return true;
            }

            double nowT = conductor.dspTime - conductor.dspTimeSongPosZero;
            double alpha = (nowT - t0) / (t1 - t0);
            if (alpha < 0.0)
            {
                alpha = 0.0;
            }
            else if (alpha > 1.0)
            {
                alpha = 1.0;
            }

            currentBeat = b0 + (b1 - b0) * alpha;
            return true;
        }

        private static bool TryGetPreviewCueDsp(scrConductor conductor, List<scrFloor> floors, scrFloor targetFloor, out double previewDueDsp)
        {
            previewDueDsp = 0d;

            double previewBeat = targetFloor.entryBeat - BeatsPerBar;
            if (TryGetEntryTimePitchAdjustedForBeat(floors, previewBeat, out double previewEntryTime))
            {
                previewDueDsp = conductor.dspTimeSongPosZero + previewEntryTime;
                return true;
            }

            // Fallback when beat->time interpolation is unavailable.
            float pitch = conductor.song != null && conductor.song.pitch > 0f ? conductor.song.pitch : 1f;
            double leadSeconds = conductor.crotchetAtStart * BeatsPerBar / pitch;
            previewDueDsp = conductor.dspTimeSongPosZero + targetFloor.entryTimePitchAdj - leadSeconds;
            return true;
        }

        private static bool TryGetEntryTimePitchAdjustedForBeat(List<scrFloor> floors, double beat, out double entryTimePitchAdj)
        {
            entryTimePitchAdj = 0d;
            if (floors == null || floors.Count == 0)
            {
                return false;
            }

            scrFloor firstA = null;
            scrFloor firstB = null;
            scrFloor lastA = null;
            scrFloor lastB = null;

            for (int i = 1; i < floors.Count; i++)
            {
                scrFloor a = floors[i - 1];
                scrFloor b = floors[i];
                if (a == null || b == null)
                {
                    continue;
                }

                if (b.entryBeat <= a.entryBeat)
                {
                    continue;
                }

                if (firstA == null)
                {
                    firstA = a;
                    firstB = b;
                }

                lastA = a;
                lastB = b;

                if (beat < a.entryBeat || beat > b.entryBeat)
                {
                    continue;
                }

                double alpha = (beat - a.entryBeat) / (b.entryBeat - a.entryBeat);
                entryTimePitchAdj = a.entryTimePitchAdj + (b.entryTimePitchAdj - a.entryTimePitchAdj) * alpha;
                return true;
            }

            if (firstA == null || firstB == null || lastA == null || lastB == null)
            {
                return false;
            }

            if (beat < firstA.entryBeat)
            {
                double slope = (firstB.entryTimePitchAdj - firstA.entryTimePitchAdj) / (firstB.entryBeat - firstA.entryBeat);
                entryTimePitchAdj = firstA.entryTimePitchAdj + (beat - firstA.entryBeat) * slope;
                return true;
            }

            if (beat > lastB.entryBeat)
            {
                double slope = (lastB.entryTimePitchAdj - lastA.entryTimePitchAdj) / (lastB.entryBeat - lastA.entryBeat);
                entryTimePitchAdj = lastB.entryTimePitchAdj + (beat - lastB.entryBeat) * slope;
                return true;
            }

            return false;
        }

        private static bool CanScheduleInCurrentState(scrController controller)
        {
            return controller.state == States.Countdown || controller.state == States.Checkpoint || controller.state == States.PlayerControl;
        }

        private static void ResetSchedulingState()
        {
            HandledSeqIds.Clear();
        }
    }
}
