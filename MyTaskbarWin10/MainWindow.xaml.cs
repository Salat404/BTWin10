using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyTaskbar.Helpers;
using IOPath = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

// ═══════════════════════════════════════════════════════════════════════════════
// CHANGELOG:
// [FIX-1]  IgnoredProcesses: "calculator", "systemsettings", "applicationframehost"
// [FIX-2]  ResolveUwpAppName: только точные совпадения (==) и StartsWith с разделителем
// [FIX-3]  TryCloseSystemStartMenu: WM_KEYDOWN/VK_ESCAPE вместо WM_CLOSE
// [FIX-4]  Win10 acrylic: проверка билда >= 18362
// [FIX-5]  GetBestIcon: полная иерархия источников иконок
// [FIX-6]  UWP дедупликация: защита от IntPtr.Zero
// [FIX-7]  Watchdog: восстановление системного таскбара при аварийном выходе
// [FIX-8]  WM_POWERBROADCAST: обработка выхода из сна
// [FIX-9]  NEW_GROUP_CONFIRM_MS: задержка подтверждения новой группы
//
// [STAB-1] SendMessage → SendMessageTimeout (200ms, SMTO_ABORTIFHUNG)
// [STAB-2] EnumWindows: флаг реентрантности + IsHungAppWindow
// [STAB-3] DwmRegisterThumbnail: IsWindow + IsHungAppWindow перед вызовом
// [STAB-4] SafeGetWindowText: через SendMessageTimeout
// [STAB-5] WM_WTSSESSION_CHANGE: блокировка экрана = как sleep
// [STAB-6] Флаги реентрантности для каждого таймера (Interlocked)
// [STAB-7] Shell-иконки асинхронно в Task.Run с CancellationToken 500ms
// [STAB-8] SafeGetProcessName: полная обработка всех исключений + HasExited
// [STAB-9] _thumbnailHandles: lock при Add/Clear
// [STAB-10] ApplyBluetoothState: Dispatcher.CheckAccess + IsLoaded
// [STAB-11] IsWindow-проверка перед GetWindowRect/GetWindowLong
// [STAB-12] ConcurrentDictionary для PID-кэшей, ограничение 256 записей
// [STAB-13] PostMessage: IsWindow-проверка перед отправкой WM_CLOSE
// [STAB-14] PositionTaskbar: IsLoaded-guard
// [STAB-15] MainWindow_Closed: каждый таймер в отдельном try/catch
// [FIX-GHOST-v2] WS_EX_NOACTIVATE: ghost-фильтр для calculator/systemsettings вместо IgnoredProcesses
// [FIX-GHOST-v3] DWMWA_CLOAKED: надёжный ghost-фильтр для ВСЕХ UWP-окон (calculator, settings и др.);
//                добавлен DwmGetWindowAttribute + DWMWA_CLOAKED в начало EnumWindows;
//                "calculatorapp" добавлен в IsUwpAppName(); resume-блок 4s→8s
// [FIX-DRAG] NcHitTestHook + WS_EX_LAYERED: панель больше не мешает drag&drop других программ
// [GAME-1]  ClipCursor/GetClipCursor/GetSystemMetrics P/Invoke для детекции FPS-захвата курсора
// [GAME-2]  FullscreenBlockerWindow: прозрачный полноэкранный оверлей при fullscreen-режиме
// [GAME-3]  OnWinKeyDown в fullscreen: только ShowTaskbar, без ShowMenu (меню Пуск не открывается)
// [GAME-4]  CheckEdgeReveal: ShowFullscreenBlocker при FPS-режиме
// [GAME-5]  HideTaskbar: снимает блокер вместе с панелью
// [GAME-6]  ShowFullscreenBlocker: тулбар поднимается выше блокера (Topmost toggle)
// [GAME-7]  FullscreenBlockerWindow: дырка только для тулбара (HTTRANSPARENT)
// [GAME-8]  MenuVisibilityChanged: блокер прячется при открытии меню, возвращается при закрытии
// [GAME-9]  SuspendBlockerForAppWindow: блокер снимается при открытии окна из панели/меню Пуск;
//           CheckFullscreen восстанавливает блокер только когда игра снова получает фокус
// [FIX-TASKMGR] SafeGetProcessName: fallback через PROCESS_QUERY_LIMITED_INFORMATION
//           (TaskmgrWatcher.GetProcessNameByPid) при Win32Exception — позволяет видеть
//           taskmgr и другие привилегированные процессы без прав администратора.
//           Новый файл: Helpers/TaskmgrWatcher.cs
// [FIX-TASKMGR-STARTMENU] OnWinKeyDown + TryCloseSystemStartMenuDelayed:
//           При диспетчере задач в фокусе (High IL) Windows Shell открывает оригинальный
//           Пуск через внутренний IPC-канал (~80-150мс после Win-key), в обход LowLevel
//           keyboard hook. TryCloseSystemStartMenu() вызванный до ShowMenu() не помогает —
//           Пуск ещё не открылся. Добавлена IsTaskmgrForeground() (проверка по window class
//           "TaskManagerWindow") и TryCloseSystemStartMenuDelayed(180мс): после ShowMenu()
//           ждём 180мс и закрываем Пуск повторно — к этому моменту он уже открылся и
//           гарантированно закроется. Своё меню остаётся единственным видимым.
// ═══════════════════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    // [FIX-DRAG2] POINT на уровне namespace для использования в IDropTarget
    [StructLayout(LayoutKind.Sequential)]
    struct NsPoint { public int x, y; }

    // [FIX-DRAG2] COM-интерфейс IDropTarget для перехвата OLE drag&drop
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] object pDataObj, uint grfKeyState, NsPoint pt, ref uint pdwEffect);
        [PreserveSig] int DragOver(uint grfKeyState, NsPoint pt, ref uint pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] object pDataObj, uint grfKeyState, NsPoint pt, ref uint pdwEffect);
    }

    // [FIX-DRAG2] Пустой IDropTarget — Windows видит что окно участвует в OLE drop,
    // но мы возвращаем DROPEFFECT_NONE, что позволяет дропу пройти к окну под нами.
    class PassthroughDropTarget : IDropTarget
    {
        public int DragEnter(object pDataObj, uint grfKeyState, NsPoint pt, ref uint pdwEffect)
        { pdwEffect = 0; return 0; }
        public int DragOver(uint grfKeyState, NsPoint pt, ref uint pdwEffect)
        { pdwEffect = 0; return 0; }
        public int DragLeave() { return 0; }
        public int Drop(object pDataObj, uint grfKeyState, NsPoint pt, ref uint pdwEffect)
        { pdwEffect = 0; return 0; }
    }

    public partial class MainWindow : Window
    {
        // ── Implementation is split across partial files: ────────────────
        // MainWindow.Interop.cs      — P/Invoke, structs, delegates
        // MainWindow.Fields.cs       — constants, fields, AppGroup class
        // MainWindow.Core.cs         — constructor, Loaded, Closed, SafeRun
        // MainWindow.Icons.cs        — icon loading hierarchy
        // MainWindow.AppLists.cs     — process filter lists, UWP resolving
        // MainWindow.Startup.cs      — acrylic, Bluetooth, power/sleep events
        // MainWindow.Fullscreen.cs   — edge reveal, fullscreen, FPS blocker
        // MainWindow.Groups.cs       — window group tracking (EnumWindows)
        // MainWindow.Media.cs        — brightness + volume controls
        // MainWindow.Menu.cs         — menu window, button animation
        // MainWindow.Keyboard.cs     — keyboard hook, Win key handling
        // MainWindow.TaskbarWatch.cs — taskbar watcher + shell restore
        // MainWindow.Wifi.cs         — Wi-Fi icon tracker
        // MainWindow.Language.cs     — language/input layout switcher
        // MainWindow.Battery.cs      — battery tracker
        // MainWindow.TrayIcon.cs     — system tray (NotifyIcon)
        // MainWindow.DragReorder.cs  — drag-reorder of taskbar buttons
        // MainWindow.Buttons.cs      — button setup, active window tracker
        // MainWindow.ContextMenu.cs  — context menu, pin/unpin
        // MainWindow.Preview.cs      — DWM thumbnail preview window
        // MainWindow.DpiAttention.cs — DPI auto-scaling, attention hook
        // MainWindow.Flash.cs        — flash timer, button highlight
        // MainWindow.Settings.cs     — position, clock, start button, settings I/O
    }
}
