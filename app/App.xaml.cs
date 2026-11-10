using System;
using Microsoft.Extensions.Logging;
using Uno.Resizetizer;

namespace SqlApp;

public partial class App : Application
{
    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        this.InitializeComponent();
    }

    public static Window? AppWindow { get; set; }
    protected Window? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new Window();
        AppWindow = MainWindow;
        MainWindow.Title = "Capstone SQL AI Assistant";

#if WINDOWS
        try
        {
            MainWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        catch { }
#endif

#if DEBUG
        // MainWindow.UseStudio();
#endif


        // Do not repeat app initialization when the Window already has content,
        // just ensure that the window is active
        if (MainWindow.Content is not Frame rootFrame)
        {
            // Create a Frame to act as the navigation context and navigate to the first page
            rootFrame = new Frame();

            // Place the frame in the current Window
            MainWindow.Content = rootFrame;

            rootFrame.NavigationFailed += OnNavigationFailed;
        }

        if (rootFrame.Content == null)
        {
            // When the navigation stack isn't restored navigate to the first page,
            // configuring the new page by passing required information as a navigation
            // parameter
            rootFrame.Navigate(typeof(MainPage), args.Arguments);
        }

        MainWindow.SetWindowIcon();
        // Ensure the current window is active
        MainWindow.Activate();

#if WINDOWS
        ApplyWindowConstraints(MainWindow);
#endif
    }

#if WINDOWS
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [System.Runtime.InteropServices.DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private static SubclassProc? _subclassDelegate;

    private static void ApplyWindowConstraints(Window window)
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hWnd == IntPtr.Zero) return;

            const int minWidthDip = 900;
            const int minHeightDip = 580;

            _subclassDelegate = (hwnd, uMsg, wParam, lParam, uIdSubclass, dwRefData) =>
            {
                if (uMsg == 0x0024) // WM_GETMINMAXINFO
                {
                    DefSubclassProc(hwnd, uMsg, wParam, lParam);
                    var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    uint dpi = GetDpiForWindow(hwnd);
                    if (dpi == 0) dpi = 96;
                    double scale = dpi / 96.0;

                    mmi.ptMinTrackSize.x = Math.Max(mmi.ptMinTrackSize.x, (int)(minWidthDip * scale));
                    mmi.ptMinTrackSize.y = Math.Max(mmi.ptMinTrackSize.y, (int)(minHeightDip * scale));

                    System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
                    return IntPtr.Zero;
                }
                return DefSubclassProc(hwnd, uMsg, wParam, lParam);
            };

            SetWindowSubclass(hWnd, _subclassDelegate, (UIntPtr)101, IntPtr.Zero);

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                uint dpi = GetDpiForWindow(hWnd);
                if (dpi == 0) dpi = 96;
                double scale = dpi / 96.0;

                int initialW = (int)(1200 * scale);
                int initialH = (int)(800 * scale);
                if (appWindow.Size.Width < (int)(minWidthDip * scale) || appWindow.Size.Height < (int)(minHeightDip * scale))
                {
                    appWindow.Resize(new Windows.Graphics.SizeInt32(initialW, initialH));
                }
            }
        }
        catch { }
    }
#endif

    /// <summary>
    /// Invoked when Navigation to a certain page fails
    /// </summary>
    /// <param name="sender">The Frame which failed navigation</param>
    /// <param name="e">Details about the navigation failure</param>
    void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        throw new InvalidOperationException($"Failed to load {e.SourcePageType.FullName}: {e.Exception}");
    }

    /// <summary>
    /// Configures global Uno Platform logging
    /// </summary>
    public static void InitializeLogging()
    {
#if DEBUG
        // Logging is disabled by default for release builds, as it incurs a significant
        // initialization cost from Microsoft.Extensions.Logging setup. If startup performance
        // is a concern for your application, keep this disabled. If you're running on the web or
        // desktop targets, you can use URL or command line parameters to enable it.
        //
        // For more performance documentation: https://platform.uno/docs/articles/Uno-UI-Performance.html

        var factory = LoggerFactory.Create(builder =>
        {
#if __WASM__
            builder.AddProvider(new global::Uno.Extensions.Logging.WebAssembly.WebAssemblyConsoleLoggerProvider());
#elif __IOS__
            builder.AddProvider(new global::Uno.Extensions.Logging.OSLogLoggerProvider());

            // Log to the Visual Studio Debug console
            builder.AddConsole();
#else
            builder.AddConsole();
#endif

            // Exclude logs below this level
            builder.SetMinimumLevel(LogLevel.Information);

            // Default filters for Uno Platform namespaces
            builder.AddFilter("Uno", LogLevel.Warning);
            builder.AddFilter("Windows", LogLevel.Warning);
            builder.AddFilter("Microsoft", LogLevel.Warning);

            // Suppress non-critical development and platform fallback warnings
            builder.AddFilter("Uno.UI.RemoteControl", LogLevel.None);
            builder.AddFilter("Uno.UI.HotDesign", LogLevel.None);
            builder.AddFilter("Uno.UI.Runtime.Skia.Win32", LogLevel.Error);
            builder.AddFilter("Microsoft.UI.Xaml.Documents.UnicodeText", LogLevel.Error);

            // Generic Xaml events
            // builder.AddFilter("Microsoft.UI.Xaml", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.VisualStateGroup", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.StateTriggerBase", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.UIElement", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.FrameworkElement", LogLevel.Trace );

            // Layouter specific messages
            // builder.AddFilter("Microsoft.UI.Xaml.Controls", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Controls.Layouter", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Controls.Panel", LogLevel.Debug );

            // builder.AddFilter("Windows.Storage", LogLevel.Debug );

            // Binding related messages
            // builder.AddFilter("Microsoft.UI.Xaml.Data", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Data", LogLevel.Debug );

            // Binder memory references tracking
            // builder.AddFilter("Uno.UI.DataBinding.BinderReferenceHolder", LogLevel.Debug );

            // DevServer and HotReload related
            // builder.AddFilter("Uno.UI.RemoteControl", LogLevel.Information);

            // Debug JS interop
            // builder.AddFilter("Uno.Foundation.WebAssemblyRuntime", LogLevel.Debug );
        });

        global::Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = factory;

#if HAS_UNO
        global::Uno.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();
#endif
#endif
    }
}
