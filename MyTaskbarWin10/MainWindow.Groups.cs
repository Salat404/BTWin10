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

// ═══════════════════════════════════════════════════════════════════
// MainWindow.Groups.cs — Window group tracking (EnumWindows core)
// ═══════════════════════════════════════════════════════════════════

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        // ГРУППЫ ОКОН [STAB-2] [FIX-6] [FIX-8] [FIX-9]
        // ═════════════════════════════════════════════════════════════════════
        void UpdateWindowGroups()
        {
            // [STAB-6] Не запускать новый тик если предыдущий ещё идёт
            if (System.Threading.Interlocked.CompareExchange(ref _enumBusyInt, 1, 0) != 0) return;
            try { UpdateWindowGroupsCore(); }
            finally { System.Threading.Interlocked.Exchange(ref _enumBusyInt, 0); }
        }

        void UpdateWindowGroupsCore()
        {
            if (_resumingFromSleep) return;

            IntPtr shellWindow = GetShellWindow();
            IntPtr myHwnd;
            try { myHwnd = new WindowInteropHelper(this).Handle; } catch { myHwnd = IntPtr.Zero; }

            var fresh = new Dictionary<string, List<IntPtr>>(16, StringComparer.OrdinalIgnoreCase);

            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (hWnd == shellWindow || hWnd == myHwnd) return true;
                    if (!IsWindowVisible(hWnd)) return true;

                    // [FIX-GHOST-v3] Cloaked-окна — это ghost-призраки UWP (calculator, settings и др.).
                    // Windows держит их в памяти заранее; DWM помечает их CLOAKED=2 (shell).
                    // Это надёжнее WS_EX_NOACTIVATE и работает после sleep/resume.
                    try
                    {
                        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                            return true;
                    }
                    catch { /* dwmapi unavailable — continuing without filter */ }

                    // [STAB-2] Пропуск зависших окон — не блокируемся
                    try { if (IsHungAppWindow(hWnd)) return true; } catch { return true; }
                    // [STAB-11] Проверка валидности
                    if (!IsWindow(hWnd)) return true;

                    // [STAB-4] Безопасный GetWindowText
                    string title = SafeGetWindowText(hWnd);
                    if (string.IsNullOrWhiteSpace(title) || title == "Program Manager") return true;
                    
                    // [FIX-DRAG-GHOST] Игнорируем временные окна драга с именами вроде "OleDropTargetWindow" или пустым текстом
                    // которые создаёт Windows при перетаскивании файлов на рабочий стол
                    string wndClassCheck = GetWindowClass(hWnd);
                    if (!string.IsNullOrEmpty(wndClassCheck) && 
                        (wndClassCheck.Contains("OleDropTargetWindow") || 
                         wndClassCheck.Contains("DragWindow") ||
                         wndClassCheck == "CLIPBRDWND"))
                        return true;

                    // [FIX-D3DPROXY] Фильтр по window class — убирает служебные/proxy-окна
                    // (D3DProxyWindow, GameOverlayWindow и т.п.) которые рендер-движки и Steam
                    // создают как технические контейнеры. Они не являются окнами приложения.
                    {
                        string wndClass = GetWindowClass(hWnd);
                        if (!string.IsNullOrEmpty(wndClass) && IgnoredWindowClasses.Contains(wndClass))
                            return true;
                    }

                    int style = GetWindowLong(hWnd, GWL_STYLE);
                    if ((style & WS_CHILD) != 0) return true;
                    int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);

                    GetWindowThreadProcessId(hWnd, out uint winPid);
                    if (winPid == 0) return true;

                    // [STAB-8] Безопасное получение имени процесса
                    if (!_pidNameCache.TryGetValue(winPid, out string exeName))
                    {
                        exeName = SafeGetProcessName(winPid);
                        if (string.IsNullOrEmpty(exeName)) return true;
                        // [STAB-12] Ограничение размера кэша
                        if (_pidNameCache.Count < 256)
                            _pidNameCache.TryAdd(winPid, exeName);
                    }

                    if (string.IsNullOrEmpty(exeName)) return true;
                    
                    // [FIX-EXPLORER-GHOST] Для Explorer: очень короткие titles (1-4 символа) это служебные окна
                    // которые создаются при drag&drop операциях на рабочий стол
                    if (exeName == "explorer" && !string.IsNullOrEmpty(title) && title.Length < 5)
                    {
                        return true;  // Пропускаем служебное окно
                    }
                    
                    if (IgnoredProcesses.Contains(exeName)) return true;
                    if (ProcessAliases.TryGetValue(exeName, out string alias)) exeName = alias;

                    if (exeName != "applicationframehost"
                        && IsUwpAppName(exeName)
                        && !DirectUwpProcesses.Contains(exeName))
                        return true;

                    // [FIX-GHOST-v3] Ghost-фильтр для прямых UWP-процессов (calculatorapp, systemsettings и др.)
                    // Windows создаёт их заранее в фоне с флагом WS_EX_NOACTIVATE — снимает при реальном открытии.
                    // Ранее этот фильтр применялся только в ветке applicationframehost — теперь и здесь.
                    if (IsUwpAppName(exeName) && !DirectUwpProcesses.Contains(exeName))
                    {
                        int directExStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                        if ((directExStyle & WS_EX_NOACTIVATE) != 0) return true;
                    }

                    if (exeName == "applicationframehost")
                    {
                        string u = ResolveUwpAppName(title);
                        if (!string.IsNullOrEmpty(u))
                        {
                            // [FIX-GHOST v2] Ghost-окна UWP (calculator, systemsettings и др.)
                            // создаются Windows заранее в фоне с флагом WS_EX_NOACTIVATE.
                            // Пока пользователь не открыл приложение — этот флаг стоит.
                            // При реальном открытии Windows снимает WS_EX_NOACTIVATE.
                            // Фильтруем по этому флагу вместо полного игнора через IgnoredProcesses.
                            int afExStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                            if ((afExStyle & WS_EX_NOACTIVATE) != 0) return true;

                            if (IgnoredProcesses.Contains(u)) return true;
                            if (!IsWindowVisible(hWnd)) return true;
                            if (!IsIconic(hWnd))
                            {
                                // [STAB-11]
                                if (!IsWindow(hWnd)) return true;
                                if (!GetWindowRect(hWnd, out RECT wr)) return true;
                                if (wr.right - wr.left < 50 || wr.bottom - wr.top < 50) return true;
                            }
                            if (!HasLiveUwpChild(hWnd)) return true;
                            exeName = u;
                        }
                        else return true;
                    }

                    if (DirectUwpProcesses.Contains(exeName))
                    {
                        string canonical = exeName.ToLowerInvariant();
                        if (canonical == "microsoft.photos") canonical = "photos";
                        exeName = canonical;
                    }

                    bool forced = ForceShowProcesses.Contains(exeName);
                    bool isToolWin = (exStyle & WS_EX_TOOLWINDOW) != 0;
                    bool isAppWin = (exStyle & WS_EX_APPWINDOW) != 0;
                    if (!forced && isToolWin && !isAppWin) return true;

                    if (!IsIconic(hWnd))
                    {
                        if (!IsWindow(hWnd)) return true;
                        if (GetWindowRect(hWnd, out RECT sc) && (sc.right - sc.left < 50 || sc.bottom - sc.top < 50))
                            return true;
                    }

                    // [PER-WINDOW] Для браузеров с несколькими профилями каждое окно
                    // получает уникальный ключ группы — как в оригинальном Win10.
                    bool isPerWindow = PerWindowProcesses.Contains(exeName);
                    string groupKey = isPerWindow ? (exeName + ":" + hWnd.ToString("X")) : exeName;

                    if (!fresh.ContainsKey(groupKey)) fresh[groupKey] = new List<IntPtr>(2);
                    fresh[groupKey].Add(hWnd);

                    if (!_groups.ContainsKey(groupKey))
                    {
                        // [ICON-HASH] Для per-window: проверяем хэш иконки ДО задержки подтверждения.
                        // Если совпадает с существующей группой — мгновенный merge, без 1800мс ожидания.
                        if (isPerWindow)
                        {
                            _pidPathCache.TryGetValue(winPid, out string epEarly);
                            BitmapSource iconEarly = GetCachedIconSafe(hWnd, winPid, epEarly, groupKey);
                            if (iconEarly != null)
                            {
                                string hashEarly = ComputeIconHash(iconEarly);
                                if (hashEarly != null
                                    && _iconHashToGroupKey.TryGetValue(hashEarly, out string existingKeyEarly)
                                    && _groups.TryGetValue(existingKeyEarly, out AppGroup existingGroupEarly))
                                {
                                    if (!fresh.ContainsKey(existingKeyEarly)) fresh[existingKeyEarly] = new List<IntPtr>(2);
                                    if (!fresh[existingKeyEarly].Contains(hWnd)) fresh[existingKeyEarly].Add(hWnd);
                                    fresh.Remove(groupKey);
                                    _pendingNewGroups.Remove(groupKey);
                                    _groups[groupKey] = existingGroupEarly;
                                    Debug.WriteLine($"[MyTaskbar][ICON-HASH] {exeName} HWND={hWnd:X} → fast-merged into {existingKeyEarly}");
                                    return true;
                                }
                            }
                        }

                        // [FIX-9] Задержка подтверждения
                        if (!_pendingNewGroups.TryGetValue(groupKey, out DateTime firstSeen))
                        {
                            _pendingNewGroups[groupKey] = DateTime.UtcNow;
                            return true;
                        }
                        if ((DateTime.UtcNow - firstSeen).TotalMilliseconds < NEW_GROUP_CONFIRM_MS)
                            return true;

                        _pendingNewGroups.Remove(groupKey);
                        _pidPathCache.TryGetValue(winPid, out string ep);
                        bool uwpIcon = IsUwpAppName(exeName);
                        // [STAB-7] Быстрый синхронный путь — только HICON
                        // [PER-WINDOW-ICON] передаём groupKey чтобы per-window группы
                        // кэшировались по HWND (у каждого профиля Chrome — своя иконка)
                        BitmapSource icon = uwpIcon
                            ? GetCachedUwpIcon(exeName)
                            : GetCachedIconSafe(hWnd, winPid, ep, groupKey);

                        // [ICON-HASH] Для per-window процессов проверяем хэш иконки.
                        // Вытащенная вкладка создаёт новый HWND, но иконка профиля та же —
                        // добавляем окно в существующую группу, а не создаём новую кнопку.
                        if (isPerWindow && icon != null)
                        {
                            string iconHash = ComputeIconHash(icon);
                            if (iconHash != null
                                && _iconHashToGroupKey.TryGetValue(iconHash, out string existingKey)
                                && _groups.TryGetValue(existingKey, out AppGroup existingGroup))
                            {
                                // Совпадение по иконке — добавляем HWND в fresh[existingKey],
                                // чтобы основной цикл обновил Hwnds и превью увидело окно.
                                if (!fresh.ContainsKey(existingKey)) fresh[existingKey] = new List<IntPtr>(2);
                                if (!fresh[existingKey].Contains(hWnd)) fresh[existingKey].Add(hWnd);
                                // Убираем новый groupKey из fresh — он не нужен (нет своей группы)
                                fresh.Remove(groupKey);
                                _pendingNewGroups.Remove(groupKey);
                                // Алиас: чтобы при следующих энумерациях этот HWND шёл через ветку else
                                _groups[groupKey] = existingGroup;
                                Debug.WriteLine($"[MyTaskbar][ICON-HASH] {exeName} HWND={hWnd:X} → merged into {existingKey}");
                                return true; // кнопка не создаётся
                            }

                            // Новый профиль — регистрируем хэш
                            string newHash = iconHash;
                            var g2 = new AppGroup { ExeName = exeName, GroupKey = groupKey, Icon = icon, LastTitle = title, IsUwp = uwpIcon, IconHash = newHash };
                            if (newHash != null) _iconHashToGroupKey[newHash] = groupKey;
                            var b2 = CreateAppButton(title, icon, 24);
                            SetupGroupButton(b2, g2); g2.Button = b2; _groups[groupKey] = g2;
                            try { AddButtonAnimated(b2); } catch { }
                            if (icon == null) LoadIconAsync(g2, hWnd, winPid, ep);
                            return true;
                        }

                        var g = new AppGroup { ExeName = exeName, GroupKey = groupKey, Icon = icon, LastTitle = title, IsUwp = uwpIcon };
                        var b = CreateAppButton(title, icon, uwpIcon ? 24 : 24);
                        SetupGroupButton(b, g); g.Button = b; _groups[groupKey] = g;
                        try { AddButtonAnimated(b); } catch { }
                        // [STAB-7] Shell-иконки догружаем асинхронно
                        if (icon == null) LoadIconAsync(g, hWnd, winPid, ep);
                    }
                    else
                    {
                        var g = _groups[groupKey];

                        // [ICON-HASH] Алиас — просто пропускаем, слияние fresh делается после EnumWindows
                        string realKey = g.GroupKey ?? g.ExeName;
                        if (realKey != groupKey) return true;

                        if (g.Icon == null)
                        {
                            _pidPathCache.TryGetValue(winPid, out string ep);
                            LoadIconAsync(g, hWnd, winPid, ep); // [STAB-7]
                        }
                        else if (isPerWindow)
                        {
                            // [PER-WINDOW-ICON] Для браузерных профилей периодически
                            // проверяем не сменилась ли иконка (смена аватара профиля Chrome).
                            // Проверяем через WM_GETICON — быстро, без блокировки UI.
                            string hwndKey = $"hwnd:{hWnd.ToString("X")}";
                            if (!_iconCache.ContainsKey(hwndKey))
                            {
                                // Иконки для этого HWND ещё нет в кэше — загружаем
                                _pidPathCache.TryGetValue(winPid, out string ep2);
                                g.Icon = null; // сбросим чтобы LoadIconAsync отработал
                                LoadIconAsync(g, hWnd, winPid, ep2);
                            }
                        }
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[MyTaskbar] EnumWindows inner: {ex.Message}"); }
                return true;
            }, IntPtr.Zero);

            // [FIX-6] UWP дедупликация с защитой от IntPtr.Zero
            foreach (var key in fresh.Keys.ToList())
            {
                if (!IsUwpAppName(key)) continue;
                var list = fresh[key];
                if (list.Count <= 1) continue;
                try
                {
                    IntPtr best = IntPtr.Zero;
                    foreach (var h in list)
                    {
                        if (h == IntPtr.Zero || !IsWindow(h)) continue;
                        IntPtr core = FindWindowEx(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                        if (core == IntPtr.Zero)
                            core = FindWindowEx(h, IntPtr.Zero, "ApplicationFrameInputSinkWindow", null);
                        if (core != IntPtr.Zero) { best = h; break; }
                    }
                    if (best == IntPtr.Zero)
                        best = list.FirstOrDefault(h => h != IntPtr.Zero && IsWindow(h) && !IsIconic(h));
                    if (best == IntPtr.Zero)
                        best = list.FirstOrDefault(h => h != IntPtr.Zero && IsWindow(h));
                    if (best != IntPtr.Zero) fresh[key] = new List<IntPtr> { best };
                    else fresh.Remove(key);
                }
                catch { if (list.Count > 0) fresh[key] = new List<IntPtr> { list[0] }; }
            }

            // [FIX-9] Чистим pending от умерших окон
            foreach (var key in _pendingNewGroups.Keys.ToList())
                if (!fresh.ContainsKey(key)) _pendingNewGroups.Remove(key);

            // [STAB-12] Очистка PID-кэша по размеру
            if (_pidNameCache.Count > 200) { _pidNameCache.Clear(); _pidPathCache.Clear(); }

            // [ICON-HASH] Слияние fresh: для каждого алиаса переносим его HWND в fresh реального ключа
            foreach (var kvp in _groups)
            {
                try
                {
                    var g = kvp.Value;
                    string realKey = g.GroupKey ?? g.ExeName;
                    if (realKey == kvp.Key) continue; // не алиас
                    if (!fresh.TryGetValue(kvp.Key, out var aliasList)) continue;
                    if (!fresh.ContainsKey(realKey)) fresh[realKey] = new List<IntPtr>(aliasList.Count);
                    foreach (var h in aliasList)
                        if (!fresh[realKey].Contains(h)) fresh[realKey].Add(h);
                    fresh.Remove(kvp.Key);
                }
                catch { }
            }

            bool needLayout = false;
            var toRemove = new List<string>(4);
            var aliasesToRemove = new List<string>(4); // [ICON-HASH] алиасы без живого HWND
            foreach (var kvp in _groups)
            {
                try
                {
                    var g = kvp.Value;
                    // [ICON-HASH] Алиасы (groupKey != g.GroupKey) — пропускаем обновление Hwnds:
                    // их Hwnds управляются через основную группу (g.GroupKey).
                    if (kvp.Key != (g.GroupKey ?? g.ExeName))
                    {
                        // [ICON-HASH] Алиас живёт пока его HWND существует.
                        // fresh уже слит в realKey, поэтому проверяем IsWindow напрямую.
                        // Извлекаем HWND из ключа вида "exeName:HEXHWND"
                        bool hwndAlive = false;
                        try
                        {
                            int colon = kvp.Key.LastIndexOf(':');
                            if (colon >= 0 && IntPtr.Size == 8)
                            {
                                if (long.TryParse(kvp.Key.Substring(colon + 1), System.Globalization.NumberStyles.HexNumber, null, out long h))
                                    hwndAlive = IsWindow(new IntPtr(h));
                            }
                            else if (colon >= 0)
                            {
                                if (int.TryParse(kvp.Key.Substring(colon + 1), System.Globalization.NumberStyles.HexNumber, null, out int h))
                                    hwndAlive = IsWindow(new IntPtr(h));
                            }
                        }
                        catch { }
                        if (!hwndAlive) aliasesToRemove.Add(kvp.Key);
                        continue;
                    }
                    g.Hwnds = fresh.TryGetValue(kvp.Key, out var list2) ? list2 : new List<IntPtr>(0);
                    if (g.Hwnds.Count == 0 && !g.IsPinned) toRemove.Add(kvp.Key);
                    else UpdateGroupBadge(g);
                }
                catch { }
            }
            foreach (var aliasKey in aliasesToRemove)
                // [FIX-2.4] Чистим PID-кэш
                try { if (_groups.TryGetValue(aliasKey, out var ag)) CleanPidCacheForGroup(ag); _groups.Remove(aliasKey); } catch { }
            foreach (var exe in toRemove)
            {
                try
                {
                    if (_groups.TryGetValue(exe, out var g))
                    {
                        if (g.IconHash != null) _iconHashToGroupKey.Remove(g.IconHash); // [ICON-HASH]
                        // [FIX-2.4] Чистим PID-кэш для удалённой группы
                        CleanPidCacheForGroup(g);
                        _groups.Remove(exe); needLayout = true; RemoveButtonAnimated(g.Button, g);
                    }
                }
                catch { }
            }
            if (needLayout) try { PositionTaskbar(); } catch { }
        }

        static bool HasLiveUwpChild(IntPtr frameHwnd)
        {
            if (frameHwnd == IntPtr.Zero || !IsWindow(frameHwnd)) return false;
            IntPtr child = FindWindowEx(frameHwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (child != IntPtr.Zero && IsWindow(child)) return true;
            child = FindWindowEx(frameHwnd, IntPtr.Zero, "ApplicationFrameInputSinkWindow", null);
            return child != IntPtr.Zero && IsWindow(child);
        }

    }
}
