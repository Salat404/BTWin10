using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MyTaskbar.Helpers
{
    /// <summary>
    /// [DBG-CURSOR] Высокочастотный логгер позиции курсора.
    /// Пишет в файл %TEMP%\MyTaskbar_cursor_debug.log
    /// Включить: CursorDebugLogger.Enable();
    /// Выключить: CursorDebugLogger.Disable();
    /// </summary>
    public static class CursorDebugLogger
    {
        // ── P/Invoke ──────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lp);
        [DllImport("user32.dll")] static extern bool GetClipCursor(out RECT lp);
        [DllImport("user32.dll")] static extern bool GetCursorInfo(out CURSORINFO pci);

        // ── Состояние ─────────────────────────────────────────────────────────
        static volatile bool _enabled = false;
        public static bool IsEnabled => _enabled;
        static Thread _thread;
        static StreamWriter _writer;
        static readonly object _writerLock = new object();

        static int _lastX = int.MinValue;
        static int _lastY = int.MinValue;
        static int _lastClipLeft = int.MinValue;
        static int _lastClipTop = int.MinValue;
        static int _lastClipRight = int.MinValue;
        static int _lastClipBottom = int.MinValue;
        static bool _lastCursorHidden = false;

        // Интервал опроса — как можно меньше (≈1 мс, реально ~1–2 мс на Win)
        static int _intervalMs = 1;

        public static string LogPath =>
            Path.Combine(Path.GetTempPath(), "MyTaskbar_cursor_debug.log");

        // ── Публичный API ─────────────────────────────────────────────────────

        /// <summary>Включить логгер. Вызывать из UI-потока.</summary>
        public static void Enable(int intervalMs = 1)
        {
            if (_enabled) return;
            _intervalMs = intervalMs;
            _enabled = true;

            try
            {
                _writer = new StreamWriter(LogPath, append: false, encoding: Encoding.UTF8)
                {
                    AutoFlush = false  // флушим вручную каждые N строк для скорости
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DBG-CURSOR] Не удалось открыть лог: {ex.Message}");
                _enabled = false;
                return;
            }

            WriteHeader();

            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "CursorDebugPoller",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();

            Debug.WriteLine($"[DBG-CURSOR] Запущен. Лог: {LogPath}");
        }

        /// <summary>Выключить логгер.</summary>
        public static void Disable()
        {
            _enabled = false;
            _thread?.Join(500);
            lock (_writerLock)
            {
                try { _writer?.Flush(); _writer?.Close(); } catch { }
                _writer = null;
            }
            Debug.WriteLine("[DBG-CURSOR] Остановлен.");
        }

        // ── Ручные события (вызывать из кода MyTaskbar) ───────────────────────

        /// <summary>Записать произвольное событие в лог.</summary>
        public static void LogEvent(string tag, string details = "")
        {
            if (!_enabled) return;
            if (!GetCursorPos(out POINT p)) return;
            GetClipCursor(out RECT clip);
            bool hidden = IsCursorHidden();

            string line = Format("EVENT", p.X, p.Y, clip, hidden, $"[{tag}] {details}");
            WriteLine(line);
        }

        /// <summary>Вызывать ПЕРЕД SetCursorPos.</summary>
        public static void LogBeforeSetCursorPos(int targetX, int targetY, string caller)
        {
            if (!_enabled) return;
            GetCursorPos(out POINT p);
            GetClipCursor(out RECT clip);
            string line = Format("SET_CURSOR_PRE",
                p.X, p.Y, clip, IsCursorHidden(),
                $"caller={caller} → target=({targetX},{targetY})");
            WriteLine(line);
        }

        /// <summary>Вызывать ПОСЛЕ SetCursorPos.</summary>
        public static void LogAfterSetCursorPos(int targetX, int targetY, string caller)
        {
            if (!_enabled) return;
            GetCursorPos(out POINT p);
            GetClipCursor(out RECT clip);
            string line = Format("SET_CURSOR_POST",
                p.X, p.Y, clip, IsCursorHidden(),
                $"caller={caller} moved_to=({targetX},{targetY}) actual=({p.X},{p.Y})");
            WriteLine(line);
        }

        /// <summary>Вызывать при ClipCursor.</summary>
        public static void LogClipCursor(int l, int t, int r, int b, string caller)
        {
            if (!_enabled) return;
            GetCursorPos(out POINT p);
            string line = Format("CLIP_CURSOR",
                p.X, p.Y, new RECT { Left = l, Top = t, Right = r, Bottom = b },
                IsCursorHidden(),
                $"caller={caller}");
            WriteLine(line);
        }

        /// <summary>Вызывать при ClipCursor(IntPtr.Zero) — снятие зажима.</summary>
        public static void LogClipCursorRelease(string caller)
        {
            if (!_enabled) return;
            GetCursorPos(out POINT p);
            GetClipCursor(out RECT clip); // ещё не снят
            string line = Format("CLIP_RELEASE",
                p.X, p.Y, clip, IsCursorHidden(), $"caller={caller}");
            WriteLine(line);
        }

        // ── Внутренняя логика ─────────────────────────────────────────────────

        static void PollLoop()
        {
            int flushCounter = 0;
            var sw = Stopwatch.StartNew();

            while (_enabled)
            {
                long ticks = sw.ElapsedMilliseconds;

                if (GetCursorPos(out POINT p))
                {
                    GetClipCursor(out RECT clip);
                    bool hidden = IsCursorHidden();

                    // Логируем ВСЕГДА позицию (для графика) + изменения ClipCursor / видимости
                    bool clipChanged =
                        clip.Left   != _lastClipLeft  ||
                        clip.Top    != _lastClipTop    ||
                        clip.Right  != _lastClipRight  ||
                        clip.Bottom != _lastClipBottom;

                    bool visChanged = hidden != _lastCursorHidden;
                    bool moved = (p.X != _lastX || p.Y != _lastY);

                    string extra = "";
                    if (clipChanged) extra += $" CLIP_CHANGED→({clip.Left},{clip.Top},{clip.Right},{clip.Bottom})";
                    if (visChanged)  extra += $" CURSOR_HIDDEN={hidden}";

                    // Всегда пишем (для непрерывного графика), но можно переключить на "только при изменении"
                    string line = Format("POLL", p.X, p.Y, clip, hidden, extra.TrimStart());
                    WriteLine(line);

                    _lastX = p.X; _lastY = p.Y;
                    _lastClipLeft   = clip.Left;
                    _lastClipTop    = clip.Top;
                    _lastClipRight  = clip.Right;
                    _lastClipBottom = clip.Bottom;
                    _lastCursorHidden = hidden;
                }

                flushCounter++;
                if (flushCounter >= 200) // флуш каждые ~200 мс при интервале 1 мс
                {
                    lock (_writerLock)
                    {
                        try { _writer?.Flush(); } catch { }
                    }
                    flushCounter = 0;
                }

                Thread.Sleep(_intervalMs);
            }

            // финальный флуш
            lock (_writerLock) { try { _writer?.Flush(); } catch { } }
        }

        static bool IsCursorHidden()
        {
            try
            {
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
                if (GetCursorInfo(out ci))
                    return (ci.flags & 0x00000001 /*CURSOR_SHOWING*/) == 0;
            }
            catch { }
            return false;
        }

        static string Format(string kind, int x, int y, RECT clip, bool hidden, string extra)
        {
            // Формат: timestamp_ms | kind | x | y | clip(L,T,R,B) | hidden | extra
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string clipStr = $"({clip.Left},{clip.Top},{clip.Right},{clip.Bottom})";
            int clipW = clip.Right - clip.Left;
            int clipH = clip.Bottom - clip.Top;
            string clipSize = (clipW <= 0 && clipH <= 0) ? "FULL" :
                              (clipW <= 2 && clipH <= 2) ? $"TINY{clipW}x{clipH}" :
                              $"{clipW}x{clipH}";
            return $"{ms}\t{kind}\t{x}\t{y}\t{clipStr}\t{clipSize}\t{(hidden ? "HIDDEN" : "visible")}\t{extra}";
        }

        static void WriteHeader()
        {
            lock (_writerLock)
            {
                try
                {
                    _writer?.WriteLine($"# MyTaskbar CursorDebugLogger  started={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                    _writer?.WriteLine($"# interval={_intervalMs}ms");
                    _writer?.WriteLine("# cols: unix_ms | kind | cursor_x | cursor_y | clip_rect | clip_size | cursor_visible | extra");
                    _writer?.WriteLine("# kind: POLL=periodic | SET_CURSOR_PRE/POST=SetCursorPos call | CLIP_CURSOR=ClipCursor | CLIP_RELEASE=ClipCursor(null) | EVENT=manual");
                    _writer?.Flush();
                }
                catch { }
            }
        }

        static int _writeCounter = 0;
        static void WriteLine(string line)
        {
            lock (_writerLock)
            {
                try { _writer?.WriteLine(line); } catch { }
            }
        }
    }
}
