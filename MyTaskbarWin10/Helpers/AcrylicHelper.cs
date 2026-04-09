using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MyTaskbar.Helpers
{
    /// <summary>
    /// Применяет Acrylic / Blur эффект к WPF-окну как в Windows 10/11.
    /// Работает на .NET Framework 4.7.2, Windows 10 build 1703+ и Windows 11.
    /// </summary>
    public static class AcrylicHelper
    {
        // ── WinAPI структуры ────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState AccentState;
            public uint AccentFlags;
            public uint GradientColor;  // AABBGGRR
            public uint AnimationId;
        }

        private enum AccentState : uint
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_GRADIENT = 1,
            ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
            ACCENT_ENABLE_BLURBEHIND = 3,   // Win10 Aero Blur
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,   // Win10 1803+ / Win11 Acrylic
            ACCENT_INVALID_STATE = 5,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public WindowCompositionAttribute Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        private enum WindowCompositionAttribute : uint
        {
            WCA_ACCENT_POLICY = 19,
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        // ── Публичный API ───────────────────────────────────────────────

        /// <summary>
        /// Включить Acrylic blur (Windows 10 1803+ / Windows 11).
        /// tintColor — цвет подкраски в формате #AARRGGBB, например 0xCC1A1A2E.
        /// </summary>
        public static void EnableAcrylic(Window window, uint tintColorAARRGGBB = 0xCC1C1C28)
        {
            Apply(window, AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, tintColorAARRGGBB);
        }

        /// <summary>
        /// Включить обычный Aero Blur (Windows 10 и старше, работает везде).
        /// tintColor — цвет подкраски в формате #AARRGGBB.
        /// </summary>
        public static void EnableBlur(Window window, uint tintColorAARRGGBB = 0xCC1C1C28)
        {
            Apply(window, AccentState.ACCENT_ENABLE_BLURBEHIND, tintColorAARRGGBB);
        }

        /// <summary>
        /// Отключить эффект, вернуть прозрачное окно.
        /// </summary>
        public static void Disable(Window window)
        {
            Apply(window, AccentState.ACCENT_DISABLED, 0);
        }

        // ── Внутренняя реализация ───────────────────────────────────────

        private static void Apply(Window window, AccentState state, uint gradientColor)
        {
            if (window == null) return;

            // AARRGGBB → AABBGGRR (формат WinAPI)
            uint bgr = ArgbToAbgr(gradientColor);

            var accent = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = 0x20,          // border flag — убирает белую рамку
                GradientColor = bgr,
            };

            int accentSize = Marshal.SizeOf(accent);
            IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    Data = accentPtr,
                    SizeOfData = accentSize,
                };

                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                    SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }

        // AARRGGBB → AABBGGRR
        private static uint ArgbToAbgr(uint argb)
        {
            byte a = (byte)((argb >> 24) & 0xFF);
            byte r = (byte)((argb >> 16) & 0xFF);
            byte g = (byte)((argb >> 8) & 0xFF);
            byte b = (byte)(argb & 0xFF);
            return (uint)((a << 24) | (b << 16) | (g << 8) | r);
        }

        // ── Вспомогательный метод для подбора цвета ─────────────────────

        /// <summary>
        /// Преобразует WPF Color + прозрачность в uint AARRGGBB для передачи в Enable*.
        /// </summary>
        public static uint ToTintColor(Color color, byte alpha = 0xCC)
        {
            return (uint)((alpha << 24) | (color.R << 16) | (color.G << 8) | color.B);
        }
    }
}