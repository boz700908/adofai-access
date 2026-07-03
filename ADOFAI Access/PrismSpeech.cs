using System;
using System.IO;
using System.Runtime.InteropServices;
using MelonLoader;

namespace ADOFAI_Access
{
    internal static class PrismSpeech
    {
        private const uint SupportedOutputFeatures =
            PrismBackendFeatureSupportsSpeak |
            PrismBackendFeatureSupportsBraille |
            PrismBackendFeatureSupportsOutput;

        private const ulong PrismBackendInvalid = 0;
        private const uint PrismOk = 0;
        private const uint PrismErrorAlreadyInitialized = 15;
        private const uint PrismBackendFeatureSupportsSpeak = 1u << 2;
        private const uint PrismBackendFeatureSupportsBraille = 1u << 4;
        private const uint PrismBackendFeatureSupportsOutput = 1u << 5;
        private const uint PrismBackendFeatureSupportsIsSpeaking = 1u << 6;
        private const uint PrismBackendFeatureSupportsStop = 1u << 7;

        private static readonly object Sync = new object();

        private static IntPtr _context;
        private static IntPtr _backend;
        private static IntPtr _nativeLibrary;
        private static bool _loadAttempted;
        private static string _backendName = string.Empty;

        public static bool IsLoaded
        {
            get
            {
                lock (Sync)
                {
                    return _backend != IntPtr.Zero;
                }
            }
        }

        public static string BackendName
        {
            get
            {
                lock (Sync)
                {
                    return _backendName;
                }
            }
        }

        public static void Load()
        {
            lock (Sync)
            {
                if (_backend != IntPtr.Zero)
                {
                    return;
                }

                if (_loadAttempted)
                {
                    return;
                }

                _loadAttempted = true;
                try
                {
                    TryPreloadNativeLibrary();
                    ConfigureLinuxSpeechDispatcherAddress();
                    PrismConfig config = prism_config_init();
                    _context = prism_init(ref config);
                    if (_context == IntPtr.Zero)
                    {
                        MelonLogger.Warning("[ADOFAI Access] Prism initialization failed.");
                        return;
                    }

                    _backend = prism_registry_acquire_best(_context);
                    if (_backend == IntPtr.Zero)
                    {
                        _backend = TryAcquireBackend("Speech Dispatcher");
                    }

                    if (_backend == IntPtr.Zero)
                    {
                        MelonLogger.Warning("[ADOFAI Access] Prism found no available speech backend.");
                        ShutdownContext();
                        return;
                    }

                    uint initError = prism_backend_initialize(_backend);
                    if (initError != PrismOk && initError != PrismErrorAlreadyInitialized)
                    {
                        MelonLogger.Warning("[ADOFAI Access] Prism backend initialization failed: " + GetErrorString(initError));
                        ShutdownBackend();
                        ShutdownContext();
                        return;
                    }

                    ulong features = prism_backend_get_features(_backend);
                    if ((features & SupportedOutputFeatures) == 0)
                    {
                        MelonLogger.Warning("[ADOFAI Access] Prism backend does not support speech or output.");
                        ShutdownBackend();
                        ShutdownContext();
                        return;
                    }

                    _backendName = PtrToUtf8String(prism_backend_name(_backend)) ?? string.Empty;
                    string backendLabel = string.IsNullOrWhiteSpace(_backendName) ? "unknown" : _backendName;
                    MelonLogger.Msg("[ADOFAI Access] Prism speech initialized with backend: " + backendLabel);
                }
                catch (DllNotFoundException ex)
                {
                    MelonLogger.Warning("[ADOFAI Access] Prism native library not found (expected prism.dll or libprism.so): " + ex.Message);
                    ShutdownBackend();
                    ShutdownContext();
                }
                catch (EntryPointNotFoundException ex)
                {
                    MelonLogger.Warning("[ADOFAI Access] Prism native library is incompatible: " + ex.Message);
                    ShutdownBackend();
                    ShutdownContext();
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[ADOFAI Access] Prism initialization failed: " + ex);
                    ShutdownBackend();
                    ShutdownContext();
                }
            }
        }

        private static void ConfigureLinuxSpeechDispatcherAddress()
        {
            if (IsWindows())
            {
                return;
            }

            string existing = Environment.GetEnvironmentVariable("SPEECHD_ADDRESS");
            if (!string.IsNullOrEmpty(existing))
            {
                return;
            }

            string runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(runtimeDirectory))
            {
                runtimeDirectory = Path.Combine("/run/user", GetUnixUserId());
            }

            string userId = GetUnixUserId();
            string[] socketPaths =
            {
                Path.Combine(runtimeDirectory, "speech-dispatcher", "speechd.sock"),
                Path.Combine("/run/user", userId, "speech-dispatcher", "speechd.sock"),
                Path.Combine("/run/host/run/user", userId, "speech-dispatcher", "speechd.sock")
            };

            for (int i = 0; i < socketPaths.Length; i++)
            {
                string socketPath = socketPaths[i];
                if (!File.Exists(socketPath))
                {
                    continue;
                }

                string address = "unix_socket:" + socketPath;
                Environment.SetEnvironmentVariable("SPEECHD_ADDRESS", address);
                return;
            }

            MelonLogger.Warning("[ADOFAI Access] Speech Dispatcher socket was not visible to the game process. If launching from Steam, add PRESSURE_VESSEL_FILESYSTEMS_RW=/run/user/" + userId + "/speech-dispatcher to the launch options.");
        }

        private static string GetUnixUserId()
        {
            try
            {
                return getuid().ToString();
            }
            catch
            {
                return "1000";
            }
        }

        private static IntPtr TryAcquireBackend(string name)
        {
            ulong id = GetBackendId(name);
            if (id == PrismBackendInvalid)
            {
                return IntPtr.Zero;
            }

            IntPtr backend = prism_registry_acquire(_context, id);
            if (backend == IntPtr.Zero)
            {
                MelonLogger.Warning("[ADOFAI Access] Prism backend acquisition failed for " + name + ".");
                return IntPtr.Zero;
            }

            return backend;
        }

        private static ulong GetBackendId(string name)
        {
            byte[] utf8 = Utf8NullTerminated(name);
            unsafe
            {
                fixed (byte* pName = utf8)
                {
                    return prism_registry_id(_context, (IntPtr)pName);
                }
            }
        }

        public static void Unload()
        {
            lock (Sync)
            {
                ShutdownBackend();
                ShutdownContext();
                _loadAttempted = false;
            }
        }

        public static bool Output(string text, bool interrupt = false)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            lock (Sync)
            {
                if (_backend == IntPtr.Zero)
                {
                    Load();
                }

                if (_backend == IntPtr.Zero)
                {
                    return false;
                }

                byte[] utf8 = Utf8NullTerminated(text);
                unsafe
                {
                    fixed (byte* pText = utf8)
                    {
                        uint error = prism_backend_output(_backend, (IntPtr)pText, interrupt);
                        if (error == PrismOk)
                        {
                            return true;
                        }

                        MelonLogger.Warning("[ADOFAI Access] Prism output failed: " + GetErrorString(error));
                        return false;
                    }
                }
            }
        }

        public static bool HasSpeech()
        {
            lock (Sync)
            {
                return _backend != IntPtr.Zero;
            }
        }

        public static bool HasBraille()
        {
            lock (Sync)
            {
                if (_backend == IntPtr.Zero)
                {
                    return false;
                }

                ulong features = prism_backend_get_features(_backend);
                return (features & PrismBackendFeatureSupportsBraille) != 0;
            }
        }

        public static bool IsSpeaking()
        {
            lock (Sync)
            {
                if (_backend == IntPtr.Zero)
                {
                    return false;
                }

                ulong features = prism_backend_get_features(_backend);
                if ((features & PrismBackendFeatureSupportsIsSpeaking) == 0)
                {
                    return false;
                }

                bool speaking;
                uint error = prism_backend_is_speaking(_backend, out speaking);
                return error == PrismOk && speaking;
            }
        }

        public static bool Silence()
        {
            lock (Sync)
            {
                if (_backend == IntPtr.Zero)
                {
                    return false;
                }

                ulong features = prism_backend_get_features(_backend);
                if ((features & PrismBackendFeatureSupportsStop) == 0)
                {
                    return false;
                }

                uint error = prism_backend_stop(_backend);
                return error == PrismOk;
            }
        }

        private static void ShutdownBackend()
        {
            if (_backend != IntPtr.Zero)
            {
                prism_backend_free(_backend);
                _backend = IntPtr.Zero;
            }

            _backendName = string.Empty;
        }

        private static void ShutdownContext()
        {
            if (_context != IntPtr.Zero)
            {
                prism_shutdown(_context);
                _context = IntPtr.Zero;
            }
        }

        private static string GetErrorString(uint error)
        {
            try
            {
                return PtrToUtf8String(prism_error_string(error)) ?? ("error " + error);
            }
            catch
            {
                return "error " + error;
            }
        }

        private static byte[] Utf8NullTerminated(string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            byte[] result = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            result[result.Length - 1] = 0;
            return result;
        }

        private static string PtrToUtf8String(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            int length = 0;
            while (Marshal.ReadByte(ptr, length) != 0)
            {
                length++;
            }

            byte[] buffer = new byte[length];
            Marshal.Copy(ptr, buffer, 0, length);
            return System.Text.Encoding.UTF8.GetString(buffer);
        }

        private static void TryPreloadNativeLibrary()
        {
            if (_nativeLibrary != IntPtr.Zero)
            {
                return;
            }

            string fileName = IsWindows() ? "prism.dll" : "libprism.so";
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string assemblyDirectory = Path.GetDirectoryName(typeof(PrismSpeech).Assembly.Location);

            if (!IsWindows())
            {
                TryPreloadLinuxLibrary("libpcre2-8.so.0", baseDirectory, assemblyDirectory);
                TryPreloadLinuxLibrary("libglib-2.0.so.0", baseDirectory, assemblyDirectory);
                TryPreloadLinuxLibrary("libspeechd.so.2", baseDirectory, assemblyDirectory);
            }

            string[] candidates =
            {
                Path.Combine(baseDirectory, "UserLibs", fileName),
                Path.Combine(baseDirectory, fileName),
                !string.IsNullOrEmpty(assemblyDirectory) ? Path.Combine(assemblyDirectory, fileName) : null,
                !string.IsNullOrEmpty(assemblyDirectory) ? Path.Combine(assemblyDirectory, "UserLibs", fileName) : null
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                ClearNativeLoadError();
                _nativeLibrary = IsWindows()
                    ? LoadLibrary(candidate)
                    : dlopen(candidate, RtldNow | RtldGlobal);

                if (_nativeLibrary != IntPtr.Zero)
                {
                    return;
                }

                MelonLogger.Warning("[ADOFAI Access] Prism native library failed to load from " + candidate + ": " + GetNativeLoadError());
            }
        }

        private static void TryPreloadLinuxLibrary(string fileName, string baseDirectory, string assemblyDirectory)
        {
            string[] candidates =
            {
                Path.Combine(baseDirectory, "UserLibs", fileName),
                Path.Combine(baseDirectory, fileName),
                !string.IsNullOrEmpty(assemblyDirectory) ? Path.Combine(assemblyDirectory, fileName) : null,
                !string.IsNullOrEmpty(assemblyDirectory) ? Path.Combine(assemblyDirectory, "UserLibs", fileName) : null,
                Path.Combine("/run/host/usr/lib", fileName),
                Path.Combine("/run/host/usr/lib64", fileName),
                Path.Combine("/usr/lib", fileName),
                Path.Combine("/usr/lib64", fileName),
                Path.Combine("/lib", fileName),
                Path.Combine("/lib64", fileName),
                Path.Combine("/usr/local/lib", fileName)
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                ClearNativeLoadError();
                IntPtr library = dlopen(candidate, RtldNow | RtldGlobal);
                if (library != IntPtr.Zero)
                {
                    return;
                }
            }
        }

        private static void ClearNativeLoadError()
        {
            if (!IsWindows())
            {
                dlerror();
            }
        }

        private static string GetNativeLoadError()
        {
            if (IsWindows())
            {
                int error = Marshal.GetLastWin32Error();
                return error == 0 ? "unknown error" : "Win32 error " + error;
            }

            IntPtr errorPointer = dlerror();
            string message = PtrToUtf8String(errorPointer);
            return string.IsNullOrEmpty(message) ? "unknown error" : message;
        }

        private static bool IsWindows()
        {
            PlatformID platform = Environment.OSVersion.Platform;
            return platform == PlatformID.Win32NT || platform == PlatformID.Win32S || platform == PlatformID.Win32Windows || platform == PlatformID.WinCE;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PrismConfig
        {
            public byte version;
        }

        private const int RtldNow = 2;
        private const int RtldGlobal = 0x100;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlopen(string fileName, int flags);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlerror();

        [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint getuid();

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern PrismConfig prism_config_init();

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_init(ref PrismConfig config);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern void prism_shutdown(IntPtr context);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong prism_registry_id(IntPtr context, IntPtr name);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_registry_acquire_best(IntPtr context);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_registry_acquire(IntPtr context, ulong id);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern void prism_backend_free(IntPtr backend);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_backend_name(IntPtr backend);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong prism_backend_get_features(IntPtr backend);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint prism_backend_initialize(IntPtr backend);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint prism_backend_output(IntPtr backend, IntPtr text, [MarshalAs(UnmanagedType.I1)] bool interrupt);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint prism_backend_stop(IntPtr backend);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint prism_backend_is_speaking(IntPtr backend, [MarshalAs(UnmanagedType.I1)] out bool speaking);

        [DllImport("prism", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_error_string(uint error);
    }
}
