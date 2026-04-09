using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyTaskbar.Helpers
{
    public static class AudioHelper
    {
        // ══════════════════════════════════════════════════════════════════
        //  COM — MMDevice
        // ══════════════════════════════════════════════════════════════════

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            int GetCount(out int pcDevices);
            int Item(int nDevice, out IMMDevice ppDevice);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            int GetState(out int pdwState);
        }

        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PROPERTYKEY pkey);
            int GetValue(ref PROPERTYKEY key, out PropVariant pv);
            int SetValue(ref PROPERTYKEY key, ref PropVariant propvar);
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pwszVal;
            public string GetString()
                => (vt == 31 && pwszVal != IntPtr.Zero) ? Marshal.PtrToStringUni(pwszVal) : null;
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(out int pnChannelCount);
            int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
            int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            int GetMasterVolumeLevel(out float pfLevelDB);
            int GetMasterVolumeLevelScalar(out float pfLevel);
            int SetChannelVolumeLevel(int nChannel, float fLevelDB, ref Guid pguidEventContext);
            int SetChannelVolumeLevelScalar(int nChannel, float fLevel, ref Guid pguidEventContext);
            int GetChannelVolumeLevel(int nChannel, out float pfLevelDB);
            int GetChannelVolumeLevelScalar(int nChannel, out float pfLevel);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
            int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
            int VolumeStepUp(ref Guid pguidEventContext);
            int VolumeStepDown(ref Guid pguidEventContext);
            int QueryHardwareSupport(out uint pdwHardwareSupportMask);
            int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
        }

        // ══════════════════════════════════════════════════════════════════
        //  PolicyConfig — создаём через CoCreateInstance вручную
        //  (без [ComImport]-класса, чтобы избежать краша при неверном GUID)
        // ══════════════════════════════════════════════════════════════════

        [Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig]
            int GetMixFormat(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
            [PreserveSig]
            int GetDeviceFormat(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr ppFormat);
            [PreserveSig]
            int ResetDeviceFormat(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
            [PreserveSig]
            int SetDeviceFormat(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
                IntPtr pEndpointFormat, IntPtr MixFormat);
            [PreserveSig]
            int GetProcessingPeriod(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
                bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
            [PreserveSig]
            int SetProcessingPeriod(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
            [PreserveSig]
            int GetShareMode(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
            [PreserveSig]
            int SetShareMode(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
            [PreserveSig]
            int GetPropertyValue(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
                bool bFxStore, IntPtr key, IntPtr pv);
            [PreserveSig]
            int SetPropertyValue(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
                bool bFxStore, IntPtr key, IntPtr pv);
            [PreserveSig]
            int SetDefaultEndpoint(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, uint dwRole);
            [PreserveSig]
            int SetEndpointVisibility(
                [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bVisible);
        }

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
            ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        private const uint CLSCTX_INPROC_SERVER = 1u;
        private const uint CLSCTX_ALL_FLAGS = 0x17u;

        // Все известные CLSID для PolicyConfigClient (Win7 → Win11)
        private static readonly Guid[] s_policyClsids =
        {
            new Guid("77aa99a0-1bd6-484f-8bc7-2c654c9a9b6f"), // Win10 / Win11 (чаще всего)
            new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), // Win7 / Win8 / Win10
            new Guid("f3dbeefb-60b6-4f4e-a28c-dc578b09b6b5"), // редкий альтернативный
        };

        private static readonly Guid s_iidPolicyConfig =
            new Guid("f8679f50-850a-41cf-9c72-430f290290c8");

        // ══════════════════════════════════════════════════════════════════
        //  Константы MMDevice
        // ══════════════════════════════════════════════════════════════════

        private const int EDataFlow_eRender = 0;
        private const int ERole_eMultimedia = 1;
        private const int DEVICE_STATE_ACTIVE = 0x1;
        private const int STGM_READ = 0;

        private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };

        private static readonly Guid IID_IAudioEndpointVolume =
            new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        // ══════════════════════════════════════════════════════════════════
        //  Внутренние хелперы
        // ══════════════════════════════════════════════════════════════════

        private static IMMDeviceEnumerator CreateEnumerator()
            => (IMMDeviceEnumerator)new MMDeviceEnumerator();

        private static IMMDevice GetDefaultDevice()
        {
            CreateEnumerator().GetDefaultAudioEndpoint(
                EDataFlow_eRender, ERole_eMultimedia, out IMMDevice dev);
            return dev;
        }

        private static IAudioEndpointVolume GetEndpointVolume()
        {
            var dev = GetDefaultDevice()
                ?? throw new InvalidOperationException("No default audio endpoint");
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, (int)CLSCTX_ALL_FLAGS, IntPtr.Zero, out object obj);
            return (IAudioEndpointVolume)obj;
        }

        private static string ReadFriendlyName(IMMDevice device)
        {
            try
            {
                device.OpenPropertyStore(STGM_READ, out IPropertyStore store);
                if (store == null) return null;
                var key = PKEY_Device_FriendlyName;
                store.GetValue(ref key, out PropVariant pv);
                return pv.GetString();
            }
            catch { return null; }
        }

        /// <summary>
        /// Создаёт IPolicyConfig, перебирая все известные CLSID.
        /// Возвращает null если ни один не подошёл — без краша.
        /// </summary>
        private static IPolicyConfig TryCreatePolicyConfig()
        {
            var iid = s_iidPolicyConfig;
            foreach (var clsid in s_policyClsids)
            {
                try
                {
                    var c = clsid; // нужна локальная переменная для ref
                    int hr = CoCreateInstance(
                        ref c, IntPtr.Zero, CLSCTX_ALL_FLAGS,
                        ref iid, out object obj);

                    if (hr == 0 && obj is IPolicyConfig pc)
                    {
                        Debug.WriteLine($"[AudioHelper] PolicyConfig CLSID={clsid}");
                        return pc;
                    }
                    Debug.WriteLine($"[AudioHelper] PolicyConfig CLSID={clsid} hr=0x{hr:X8}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioHelper] PolicyConfig CLSID={clsid} ex={ex.Message}");
                }
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Публичный API — громкость / mute
        // ══════════════════════════════════════════════════════════════════

        public static int GetVolume()
        {
            try { GetEndpointVolume().GetMasterVolumeLevelScalar(out float l); return (int)Math.Round(l * 100f); }
            catch { return -1; }
        }

        public static bool IsMuted()
        {
            try { GetEndpointVolume().GetMute(out bool m); return m; }
            catch { return false; }
        }

        public static void SetVolume(int percent)
        {
            try
            {
                float level = Math.Max(0f, Math.Min(1f, percent / 100f));
                Guid empty = Guid.Empty;
                GetEndpointVolume().SetMasterVolumeLevelScalar(level, ref empty);
            }
            catch { }
        }

        public static void SetMute(bool mute)
        {
            try { Guid e = Guid.Empty; GetEndpointVolume().SetMute(mute, ref e); }
            catch { }
        }

        public static void ToggleMute() => SetMute(!IsMuted());

        // ══════════════════════════════════════════════════════════════════
        //  Публичный API — устройства
        // ══════════════════════════════════════════════════════════════════

        public static string GetDefaultDeviceName()
        {
            try { return ReadFriendlyName(GetDefaultDevice()); }
            catch { return null; }
        }

        public static List<AudioDeviceItem> GetPlaybackDevices()
        {
            var result = new List<AudioDeviceItem>();
            try
            {
                var en = CreateEnumerator();
                en.GetDefaultAudioEndpoint(EDataFlow_eRender, ERole_eMultimedia, out IMMDevice defDev);
                string defaultId = null;
                defDev?.GetId(out defaultId);

                en.EnumAudioEndpoints(EDataFlow_eRender, DEVICE_STATE_ACTIVE, out IMMDeviceCollection col);
                if (col == null) return result;

                col.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    col.Item(i, out IMMDevice dev);
                    if (dev == null) continue;
                    dev.GetId(out string id);
                    string name = ReadFriendlyName(dev) ?? id;
                    result.Add(new AudioDeviceItem
                    {
                        Id = id,
                        Name = name,
                        IsDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[AudioHelper] GetPlaybackDevices: {ex.Message}"); }
            return result;
        }

        /// <summary>
        /// Переключает устройство вывода по умолчанию (все три роли).
        /// Возвращает true при успехе.
        /// </summary>
        public static bool SetDefaultDevice(string deviceId)
        {
            try
            {
                var policy = TryCreatePolicyConfig();
                if (policy == null)
                {
                    Debug.WriteLine("[AudioHelper] SetDefaultDevice: no PolicyConfig available");
                    return false;
                }
                policy.SetDefaultEndpoint(deviceId, 0); // eConsole
                policy.SetDefaultEndpoint(deviceId, 1); // eMultimedia
                policy.SetDefaultEndpoint(deviceId, 2); // eCommunications
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioHelper] SetDefaultDevice: {ex.Message}");
                return false;
            }
        }
    }
}
