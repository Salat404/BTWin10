using System;
using System.Windows;
using System.Windows.Threading;

namespace MyTaskbar
{
    public partial class MainWindow
    {
        // [REMOVED] Secondary monitor taskbar support removed
        // All secondary monitor code has been stripped for single-screen only build

        void InitSecondaryTaskbar() { }
        void SecondarySync_All() { }
        void SecondarySync_Icons() { }
        void NotifySecondaryPositionChanged() { }
        void NotifySecondaryScaleChanged() { }
        void NotifySecondaryMonitorChanged() { }
    }
}
