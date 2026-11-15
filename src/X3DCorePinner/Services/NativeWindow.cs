// X3D Core Pinner - Copyright (c) 2026 PR FRAG - MIT License
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace X3DCorePinner.Services
{
    /// <summary>Win32 helpers for the main window.</summary>
    internal static class NativeWindow
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Dark title bar on Windows 10 20H1+ and Windows 11. Best-effort.</summary>
        public static void UseDarkTitleBar(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
            }
            catch
            {
                // Older Windows - keep the default title bar.
            }
        }
    }
}
