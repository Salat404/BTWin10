using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyTaskbar.Helpers
{
    public static class AppVolumeHelper
    {
        // ══════════════════════════════════════════════════════════════════
        //  COM — Audio Session Manager
        // ══════════════════════════════════════════════════════════════════

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
            [PreserveSig]
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

        // IAudioSessionManager2 — для доступу к audio sessions
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionEnumerator(out IAudioSessionEnumerator ppSessionEnum);
            int RegisterSessionNotification(IntPtr SessionNotification);
            int UnregisterSessionNotification(IntPtr SessionNotification);
            int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);
            int UnregisterDuckNotification(IntPtr duckNotification);
        }

        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int SessionCount);
            int GetSession(int SessionCount, out IAudioSessionControl2 Session);
        }

        [Guid("87CE5498-68D6-44E0-A643-31807F48A998"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            int SetMasterVolume(float fLevel, [MarshalAs(UnmanagedType.LPWStr)] string EventContext);
            int GetMasterVolume(out float pfLevel);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, [MarshalAs(UnmanagedType.LPWStr)] string EventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }

        [Guid("55391142-255D-4FEE-8399-C37DE95F72FC"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            int NotifyWinEventSubscribed(IntPtr hns);
            int NotifyWinEventUnsubscribed();
            int GetSessionState(out int pRetVal);
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, [MarshalAs(UnmanagedType.LPWStr)] string EventContext);
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, [MarshalAs(UnmanagedType.LPWStr)] string EventContext);
            int GetGroupingParam(out Guid pRetVal);
            int SetGroupingParam(Guid Override, [MarshalAs(UnmanagedType.LPWStr)] string EventContext);
            int RegisterAudioSessionNotification(IntPtr NewNotifications);
            int UnregisterAudioSessionNotification(IntPtr NewNotifications);
            int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        }

        [Guid("BC94D4A0-55D1-41C6-BA47-02541CB1991B"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl methods
            int NotifyWinEventSubscribed(IntPtr hns);
            int NotifyWinEventUnsubscribed();
            int GetSessionState(out int pRetVal);
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, [MarshalAs(UnmanagedType.LPWStr)] string EventContext);
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, [MarshalAs(UnmanagedType.LPWStr)] string EventContext);
            int GetGroupingParam(out Guid pRetVal);
            int SetGroupingParam(Guid Override, [MarshalAs(UnmanagedType.LPWStr)] string EventContext);
            int RegisterAudioSessionNotification(IntPtr NewNotifications);
            int UnregisterAudioSessionNotification(IntPtr NewNotifications);
            int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);

            // IAudioSessionControl2 methods
            [PreserveSig]
            int GetProcessId(out uint pRetVal);
            int IsSystemSoundsSession();
            int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        private const int EDataFlow_eRender = 0;
        private const int ERole_eMultimedia = 1;
        private const int DEVICE_STATE_ACTIVE = 0x1;
        private const int STGM_READ = 0;

        private static readonly Guid IID_IAudioSessionManager2 = 
            new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        private static readonly Guid IID_ISimpleAudioVolume = 
            new Guid("87CE5498-68D6-44E0-A643-31807F48A998");

        private static readonly Guid IID_IAudioSessionControl2 = 
            new Guid("BC94D4A0-55D1-41C6-BA47-02541CB1991B");

        // ══════════════════════════════════════════════════════════════════
        //  Публичный API
        // ══════════════════════════════════════════════════════════════════

        public static int GetAppVolume(uint pid)
        {
            try
            {
                var vol = FindAudioSessionByPid(pid);
                if (vol == null) return -1;

                vol.GetMasterVolume(out float level);
                return (int)Math.Round(level * 100f);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppVolumeHelper] GetAppVolume({pid}): {ex.Message}");
                return -1;
            }
        }

        public static void SetAppVolume(uint pid, int percent)
        {
            try
            {
                var vol = FindAudioSessionByPid(pid);
                if (vol == null) return;

                float level = Math.Max(0f, Math.Min(1f, percent / 100f));
                vol.SetMasterVolume(level, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppVolumeHelper] SetAppVolume({pid}, {percent}): {ex.Message}");
            }
        }

        public static bool IsAppMuted(uint pid)
        {
            try
            {
                var vol = FindAudioSessionByPid(pid);
                if (vol == null) return false;

                vol.GetMute(out bool muted);
                return muted;
            }
            catch
            {
                return false;
            }
        }

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

        private static IAudioSessionManager2 GetSessionManager()
        {
            var dev = GetDefaultDevice()
                ?? throw new InvalidOperationException("No default audio endpoint");
            var iid = IID_IAudioSessionManager2;
            dev.Activate(ref iid, 0x17, IntPtr.Zero, out object obj);
            return (IAudioSessionManager2)obj;
        }

        private static ISimpleAudioVolume FindAudioSessionByPid(uint pid)
        {
            try
            {
                var mgr = GetSessionManager();
                mgr.GetAudioSessionEnumerator(out IAudioSessionEnumerator enumerator);
                enumerator.GetCount(out int count);

                for (int i = 0; i < count; i++)
                {
                    enumerator.GetSession(i, out IAudioSessionControl2 session);
                    if (session == null) continue;

                    try
                    {
                        int hr = session.GetProcessId(out uint sessionPid);
                        if (hr != 0) continue;

                        if (sessionPid == pid)
                        {
                            // Получаем ISimpleAudioVolume из этой сессии
                            var asVolume = session as ISimpleAudioVolume;
                            if (asVolume != null)
                                return asVolume;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AppVolumeHelper] FindSession iteration: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppVolumeHelper] FindAudioSessionByPid: {ex.Message}");
            }

            return null;
        }
    }
}
