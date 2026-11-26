using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassBar
{
    /// <summary>
    /// Sound control through Windows' Core Audio API: the default playback device's master volume, the list of
    /// playback devices (and switching between them), and the volume of each app that is playing sound.
    /// Every call looks up the current default device, so switching speakers/headphones just works.
    /// </summary>
    internal static class AudioVolume
    {
        private static IMMDeviceEnumerator _enumerator;

        private static IMMDevice DefaultDevice()
        {
            try
            {
                _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumerator();
                return _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out IMMDevice device) == 0 ? device : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static T Activate<T>(IMMDevice device) where T : class
        {
            if (device == null)
            {
                return null;
            }

            try
            {
                Guid iid = typeof(T).GUID;
                return device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object result) == 0 ? result as T : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IAudioEndpointVolume Endpoint()
        {
            IMMDevice device = DefaultDevice();
            try
            {
                return Activate<IAudioEndpointVolume>(device);
            }
            finally
            {
                Release(device);
            }
        }

        private static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com))
            {
                Marshal.ReleaseComObject(com);
            }
        }

        #region Master volume

        /// <summary>Volume 0..1 and mute state. False when there is no playback device.</summary>
        public static bool TryGet(out float level, out bool muted)
        {
            level = 0;
            muted = false;
            IAudioEndpointVolume endpoint = Endpoint();
            if (endpoint == null)
            {
                return false;
            }

            try
            {
                return endpoint.GetMasterVolumeLevelScalar(out level) == 0 && endpoint.GetMute(out muted) == 0;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                Release(endpoint);
            }
        }

        public static void Change(float delta)
        {
            if (TryGet(out float level, out _))
            {
                Set(level + delta, delta > 0 ? false : (bool?)null);
            }
        }

        /// <summary>Sets the volume (0..1) and, if given, the mute state.</summary>
        public static void Set(float level, bool? mute = null)
        {
            IAudioEndpointVolume endpoint = Endpoint();
            if (endpoint == null)
            {
                return;
            }

            try
            {
                Guid context = Guid.Empty;
                endpoint.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, level)), ref context);
                if (mute.HasValue)
                {
                    endpoint.SetMute(mute.Value, ref context);
                }
            }
            catch (Exception)
            {
                // Device went away mid-call.
            }
            finally
            {
                Release(endpoint);
            }
        }

        public static void ToggleMute()
        {
            IAudioEndpointVolume endpoint = Endpoint();
            if (endpoint == null)
            {
                return;
            }

            try
            {
                if (endpoint.GetMute(out bool muted) == 0)
                {
                    Guid context = Guid.Empty;
                    endpoint.SetMute(!muted, ref context);
                }
            }
            catch (Exception)
            {
                // Device went away mid-call.
            }
            finally
            {
                Release(endpoint);
            }
        }

        #endregion

        #region Playback devices

        public sealed class Device
        {
            public string Id;
            public string Name;
            public bool IsDefault;
        }

        /// <summary>The active playback devices, by their Windows name ("Speakers (Realtek...)").</summary>
        public static List<Device> GetDevices()
        {
            List<Device> devices = new List<Device>();
            string defaultId = null;

            IMMDevice current = DefaultDevice();
            if (current != null)
            {
                current.GetId(out defaultId);
                Release(current);
            }

            if (_enumerator == null || _enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out IMMDeviceCollection collection) != 0)
            {
                return devices;
            }

            try
            {
                collection.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (collection.Item(i, out IMMDevice device) != 0 || device == null)
                    {
                        continue;
                    }

                    try
                    {
                        device.GetId(out string id);
                        devices.Add(new Device
                        {
                            Id = id,
                            Name = FriendlyName(device) ?? "Unknown device",
                            IsDefault = id == defaultId
                        });
                    }
                    finally
                    {
                        Release(device);
                    }
                }
            }
            catch (Exception)
            {
                // Return what we have.
            }
            finally
            {
                Release(collection);
            }

            return devices;
        }

        private static string FriendlyName(IMMDevice device)
        {
            if (device.OpenPropertyStore(STGM_READ, out IPropertyStore store) != 0 || store == null)
            {
                return null;
            }

            try
            {
                PROPERTYKEY key = PKEY_Device_FriendlyName;
                if (store.GetValue(ref key, out PROPVARIANT value) != 0)
                {
                    return null;
                }

                try
                {
                    return value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.pointer) : null;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Release(store);
            }
        }

        /// <summary>
        /// Makes a device the default for everything (media, games, calls), as the Windows sound settings do.
        /// Uses the same undocumented interface those settings use; there is no public API for this.
        /// </summary>
        public static bool SetDefaultDevice(string id)
        {
            try
            {
                IPolicyConfig policy = (IPolicyConfig)new PolicyConfigClient();
                try
                {
                    bool ok = policy.SetDefaultEndpoint(id, ERole.eConsole) == 0;
                    ok &= policy.SetDefaultEndpoint(id, ERole.eMultimedia) == 0;
                    ok &= policy.SetDefaultEndpoint(id, ERole.eCommunications) == 0;
                    return ok;
                }
                finally
                {
                    Release(policy);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        #endregion

        #region Per-app volume

        /// <summary>One app's sound on the default device. All of an app's sessions are changed together.</summary>
        public sealed class AppSession
        {
            public int ProcessId;
            public string Name;
            public string ExePath;
            public bool IsSystemSounds;
            public float Volume;
            public bool Muted;
            internal List<ISimpleAudioVolume> Controls = new List<ISimpleAudioVolume>();

            public void SetVolume(float level)
            {
                Guid context = Guid.Empty;
                foreach (ISimpleAudioVolume control in Controls)
                {
                    try
                    {
                        control.SetMasterVolume(Math.Max(0, Math.Min(1, level)), ref context);
                    }
                    catch (Exception)
                    {
                        // The app closed.
                    }
                }

                Volume = level;
            }

            public void SetMute(bool mute)
            {
                Guid context = Guid.Empty;
                foreach (ISimpleAudioVolume control in Controls)
                {
                    try
                    {
                        control.SetMute(mute, ref context);
                    }
                    catch (Exception)
                    {
                        // The app closed.
                    }
                }

                Muted = mute;
            }

            /// <summary>Lets go of the audio objects. Call when the list is no longer shown.</summary>
            public void Release()
            {
                foreach (ISimpleAudioVolume control in Controls)
                {
                    AudioVolume.Release(control);
                }

                Controls.Clear();
            }
        }

        /// <summary>Apps with sound sessions on the default device (playing now or recently), grouped by app.</summary>
        public static List<AppSession> GetSessions()
        {
            Dictionary<string, AppSession> byApp = new Dictionary<string, AppSession>(StringComparer.OrdinalIgnoreCase);
            List<AppSession> result = new List<AppSession>();

            IMMDevice device = DefaultDevice();
            IAudioSessionManager2 manager = Activate<IAudioSessionManager2>(device);
            Release(device);
            if (manager == null)
            {
                return result;
            }

            IAudioSessionEnumerator sessions = null;
            try
            {
                if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null)
                {
                    return result;
                }

                sessions.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out IAudioSessionControl control) != 0 || control == null)
                    {
                        continue;
                    }

                    if (!(control is IAudioSessionControl2 control2) || control2.GetState(out int state) != 0 || state == AudioSessionStateExpired)
                    {
                        Release(control);
                        continue;
                    }

                    bool system = control2.IsSystemSoundsSession() == 0; // S_OK means yes
                    control2.GetProcessId(out int pid);

                    string exe = null;
                    string name;
                    if (system)
                    {
                        name = "System sounds";
                    }
                    else
                    {
                        exe = ProcessPath(pid);
                        if (exe == null)
                        {
                            Release(control);
                            continue; // the app already exited
                        }

                        name = AppName(exe);
                    }

                    string key = system ? "::system" : exe;
                    if (!byApp.TryGetValue(key, out AppSession app))
                    {
                        app = new AppSession { ProcessId = pid, Name = name, ExePath = exe, IsSystemSounds = system };
                        byApp[key] = app;
                        result.Add(app);

                        if (control is ISimpleAudioVolume first)
                        {
                            first.GetMasterVolume(out app.Volume);
                            first.GetMute(out app.Muted);
                        }
                    }

                    if (control is ISimpleAudioVolume simple)
                    {
                        app.Controls.Add(simple); // released with the AppSession
                    }
                    else
                    {
                        Release(control);
                    }
                }
            }
            catch (Exception)
            {
                // Return what we have.
            }
            finally
            {
                Release(sessions);
                Release(manager);
            }

            // Apps first (A to Z), system sounds last.
            result.Sort((a, b) => a.IsSystemSounds != b.IsSystemSounds
                ? (a.IsSystemSounds ? 1 : -1)
                : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        /// <summary>The program file of a process. Works for elevated apps too, and is much cheaper than Process.MainModule.</summary>
        private static string ProcessPath(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                System.Text.StringBuilder path = new System.Text.StringBuilder(1024);
                int size = path.Capacity;
                return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string AppName(string exe)
        {
            try
            {
                string description = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description.Trim();
                }
            }
            catch (Exception)
            {
                // Use the file name.
            }

            return System.IO.Path.GetFileNameWithoutExtension(exe);
        }

        #endregion

        #region COM interop

        private const int CLSCTX_ALL = 0x17;
        private const int DEVICE_STATE_ACTIVE = 0x1;
        private const int STGM_READ = 0;
        private const ushort VT_LPWSTR = 31;
        private const int AudioSessionStateExpired = 2;
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }

        private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public int pid;
        }

        private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort reserved1, reserved2, reserved3;
            public IntPtr pointer;
            public IntPtr padding;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT value);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder path, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport]
        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out int count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(int channel, float levelDb, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(int channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [ComImport]
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport]
        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
        }

        [ComImport]
        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            [PreserveSig] int GetState(out int state);
        }

        [ComImport]
        [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr name, ref Guid context);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out int pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [ComImport]
        [Guid("87CE5498-68D6-44E5-9215-6F8E3E6C5D12")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid context);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        // Undocumented, but stable since Windows 7 and used by Windows' own sound settings and many tools.
        [ComImport]
        [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigClient
        {
        }

        [ComImport]
        [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(IntPtr deviceId, IntPtr format);
            [PreserveSig] int GetDeviceFormat(IntPtr deviceId, int defaultFormat, IntPtr format);
            [PreserveSig] int ResetDeviceFormat(IntPtr deviceId);
            [PreserveSig] int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod(IntPtr deviceId, int defaultPeriod, IntPtr period, IntPtr minPeriod);
            [PreserveSig] int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
            [PreserveSig] int GetShareMode(IntPtr deviceId, IntPtr mode);
            [PreserveSig] int SetShareMode(IntPtr deviceId, IntPtr mode);
            [PreserveSig] int GetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
            [PreserveSig] int SetEndpointVisibility(IntPtr deviceId, int visible);
        }

        #endregion
    }
}
