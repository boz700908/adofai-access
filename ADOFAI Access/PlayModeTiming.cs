using System;
using System.Collections.Generic;
using UnityEngine;

namespace ADOFAI_Access
{
    internal static class PlayModeTiming
    {
        internal static bool IsGameplayRuntimeAvailable()
        {
            scrController controller = ADOBase.controller;
            if (controller == null || ADOBase.isLevelEditor)
            {
                return false;
            }

            if (ADOBase.sceneName == GCNS.sceneCustomLevelSelect || ADOBase.cls != null)
            {
                return false;
            }

            if (ADOBase.sceneName == GCNS.sceneGame || ADOBase.isScnGame || ADOBase.isPlayingLevel)
            {
                return true;
            }

            return controller.gameworld || controller.isPuzzleRoom;
        }

        internal static bool CanScheduleInCurrentState(scrController controller)
        {
            return controller.state == States.Countdown || controller.state == States.Checkpoint || controller.state == States.PlayerControl;
        }

        internal static bool TryGetCurrentBeat(scrController controller, scrConductor conductor, out double currentBeat)
        {
            currentBeat = 0.0;

            if (controller.state == States.Countdown || controller.state == States.Checkpoint)
            {
                if (conductor.crotchetAtStart > 0f)
                {
                    currentBeat = conductor.songposition_minusi / conductor.crotchetAtStart;
                }
                else
                {
                    currentBeat = conductor.beatNumber - conductor.adjustedCountdownTicks;
                }
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

        internal static bool TryGetPreviewCueDsp(scrConductor conductor, List<scrFloor> floors, scrFloor targetFloor, out double previewDueDsp)
        {
            previewDueDsp = 0d;
            int beatsAhead = ModSettings.Current.patternPreviewBeatsAhead;
            if (beatsAhead <= 0)
            {
                return false;
            }

            double previewBeat = targetFloor.entryBeat - beatsAhead;
            if (previewBeat < 0.0)
            {
                float pitchEarly = conductor.song != null && conductor.song.pitch > 0f ? conductor.song.pitch : 1f;
                double earlyLeadSeconds = conductor.crotchetAtStart * beatsAhead / pitchEarly;
                previewDueDsp = conductor.dspTimeSongPosZero + targetFloor.entryTimePitchAdj - earlyLeadSeconds;
                return true;
            }

            if (TryGetEntryTimePitchAdjustedForBeat(floors, previewBeat, out double previewEntryTime))
            {
                previewDueDsp = conductor.dspTimeSongPosZero + previewEntryTime;
                return true;
            }

            float pitch = conductor.song != null && conductor.song.pitch > 0f ? conductor.song.pitch : 1f;
            double leadSeconds = conductor.crotchetAtStart * beatsAhead / pitch;
            previewDueDsp = conductor.dspTimeSongPosZero + targetFloor.entryTimePitchAdj - leadSeconds;
            return true;
        }

        internal static bool TryGetCueDspForBeat(scrConductor conductor, List<scrFloor> floors, double beat, out double cueDsp)
        {
            cueDsp = 0d;
            if (conductor == null)
            {
                return false;
            }

            if (TryGetEntryTimePitchAdjustedForBeat(floors, beat, out double entryTimePitchAdj))
            {
                cueDsp = conductor.dspTimeSongPosZero + entryTimePitchAdj;
                return true;
            }

            float pitch = conductor.song != null && conductor.song.pitch > 0f ? conductor.song.pitch : 1f;
            cueDsp = conductor.dspTimeSongPosZero + (conductor.crotchetAtStart * beat / pitch);
            return true;
        }

        internal static bool TryGetEntryTimePitchAdjustedForBeat(List<scrFloor> floors, double beat, out double entryTimePitchAdj)
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

        internal static double GetScheduleHorizonSeconds(scrConductor conductor)
        {
            double beatSeconds = GetBeatDurationSeconds(conductor);
            if (beatSeconds > 0.0)
            {
                return Math.Max(beatSeconds, Time.unscaledDeltaTime);
            }

            return Math.Max(Time.unscaledDeltaTime, Time.smoothDeltaTime);
        }

        internal static double GetBeatDurationSeconds(scrConductor conductor)
        {
            if (conductor == null)
            {
                return 0.0;
            }

            float pitch = conductor.song != null && conductor.song.pitch > 0f ? conductor.song.pitch : 1f;
            if (conductor.crotchetAtStart <= 0.0 || pitch <= 0f)
            {
                return 0.0;
            }

            return conductor.crotchetAtStart / pitch;
        }
    }
}
