using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;
using UnityEngine.Networking;

namespace ADOFAI_Access
{
    internal static class TapCueService
    {
        private const int MaxPoolSources = 32;

        private sealed class CueSourceSlot
        {
            public AudioSource Source;
            public double BusyUntilDsp;
        }

        private static AudioSource _cueSource;
        private static readonly List<CueSourceSlot> CuePool = new List<CueSourceSlot>();
        private static AudioClip _cueClip;
        private static AudioClip _fallbackClip;
        private static bool _isLoadingCue;
        private static bool _customCueLoadFailed;
        private static string _loadedCuePath = string.Empty;

        public static string CueFilePath
        {
            get
            {
                string gameRoot = GetGameRoot();
                return Path.Combine(gameRoot, "UserData", "ADOFAI_Access", "Audio", "level_preview_tap.wav");
            }
        }

        public static void PlayCueNow()
        {
            EnsureAudioReady();
            if (_cueSource == null)
            {
                return;
            }

            AudioClip clip = SelectClip();
            if (clip != null)
            {
                _cueSource.PlayOneShot(clip, 1f);
            }
        }

        public static void PlayCueAt(double dspTime)
        {
            EnsureAudioReady();

            AudioClip clip = SelectClip();
            if (clip == null)
            {
                return;
            }

            CueSourceSlot slot = AcquirePoolSlot(dspTime, clip.length);
            if (slot == null || slot.Source == null)
            {
                return;
            }

            slot.Source.clip = clip;
            slot.Source.PlayScheduled(dspTime);
        }

        public static void StopAllCues()
        {
            if (_cueSource != null)
            {
                _cueSource.Stop();
            }

            for (int i = 0; i < CuePool.Count; i++)
            {
                CueSourceSlot slot = CuePool[i];
                if (slot?.Source == null)
                {
                    continue;
                }

                slot.Source.Stop();
                slot.BusyUntilDsp = 0d;
            }
        }

        private static CueSourceSlot AcquirePoolSlot(double dspTime, float clipLengthSeconds)
        {
            EnsureAudioSource();
            if (CuePool.Count == 0)
            {
                return null;
            }

            double nowDsp = AudioSettings.dspTime;
            double startDsp = Math.Max(dspTime, nowDsp);
            double busyUntil = startDsp + Math.Max(clipLengthSeconds, 0.05f);

            for (int i = 0; i < CuePool.Count; i++)
            {
                CueSourceSlot existing = CuePool[i];
                if (existing.BusyUntilDsp <= nowDsp + 0.0001)
                {
                    existing.BusyUntilDsp = busyUntil;
                    return existing;
                }
            }

            if (CuePool.Count >= MaxPoolSources)
            {
                return null;
            }

            CueSourceSlot created = CreateCueSourceSlot();
            created.BusyUntilDsp = busyUntil;
            CuePool.Add(created);
            return created;
        }

        private static void EnsureAudioReady()
        {
            EnsureAudioSource();
            EnsureFallbackClip();

            string cuePath = CueFilePath;
            if (_cueClip != null && string.Equals(_loadedCuePath, cuePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_isLoadingCue)
            {
                return;
            }

            if (!File.Exists(cuePath))
            {
                _cueClip = null;
                _loadedCuePath = string.Empty;
                _customCueLoadFailed = false;
                return;
            }

            _isLoadingCue = true;
            _customCueLoadFailed = false;
            MelonCoroutines.Start(LoadCueClip(cuePath));
        }

        private static void EnsureAudioSource()
        {
            if (_cueSource != null)
            {
                return;
            }

            GameObject go = new GameObject("ADOFAI_Access_TapCueAudio");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _cueSource = go.AddComponent<AudioSource>();
            _cueSource.playOnAwake = false;
            _cueSource.spatialBlend = 0f;
            _cueSource.volume = 1f;

            CuePool.Add(CreateCueSourceSlot());
            CuePool.Add(CreateCueSourceSlot());
            CuePool.Add(CreateCueSourceSlot());
            CuePool.Add(CreateCueSourceSlot());
        }

        private static void EnsureFallbackClip()
        {
            if (_fallbackClip != null)
            {
                return;
            }

            const int sampleRate = 44100;
            const float durationSeconds = 0.045f;
            int sampleCount = Mathf.CeilToInt(sampleRate * durationSeconds);
            float[] samples = new float[sampleCount];
            float frequency = 1760f;
            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)sampleRate;
                float envelope = 1f - i / (float)sampleCount;
                samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * 0.25f;
            }

            _fallbackClip = AudioClip.Create("ADOFAI_Access_DefaultPreviewCue", sampleCount, 1, sampleRate, false);
            _fallbackClip.SetData(samples, 0);
        }

        private static IEnumerator LoadCueClip(string cuePath)
        {
            string uri = new Uri(cuePath).AbsoluteUri;
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
            {
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        _cueClip = clip;
                        _loadedCuePath = cuePath;
                        _customCueLoadFailed = false;
                    }
                    else
                    {
                        _cueClip = null;
                        _loadedCuePath = string.Empty;
                        _customCueLoadFailed = true;
                    }
                }
                else
                {
                    MelonLogger.Warning($"[ADOFAI Access] Failed to load tap cue from {cuePath}: {request.error}");
                    _cueClip = null;
                    _loadedCuePath = string.Empty;
                    _customCueLoadFailed = true;
                }
            }

            _isLoadingCue = false;
        }

        private static AudioClip SelectClip()
        {
            string cuePath = CueFilePath;
            if (_cueClip != null && string.Equals(_loadedCuePath, cuePath, StringComparison.OrdinalIgnoreCase))
            {
                return _cueClip;
            }

            if (File.Exists(cuePath) && !_customCueLoadFailed)
            {
                // Custom cue exists; wait until it is loaded instead of playing fallback first.
                return null;
            }

            return _fallbackClip;
        }

        private static CueSourceSlot CreateCueSourceSlot()
        {
            AudioSource pooled = _cueSource.gameObject.AddComponent<AudioSource>();
            pooled.playOnAwake = false;
            pooled.spatialBlend = 0f;
            pooled.volume = 1f;
            return new CueSourceSlot
            {
                Source = pooled,
                BusyUntilDsp = 0d
            };
        }

        private static string GetGameRoot()
        {
            if (!string.IsNullOrEmpty(Application.dataPath))
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                if (!string.IsNullOrEmpty(root))
                {
                    return root;
                }
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}
