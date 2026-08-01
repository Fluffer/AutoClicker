using System.Runtime.InteropServices;
using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// Third click backend, alongside <see cref="InputSender"/>'s SendInput (real input) and
/// PostMessage (background) paths. UI Automation reaches targets PostMessage cannot reach:
/// WPF, UWP/WinUI and Chrome all expose UIA providers, even though they ignore synthesised
/// window messages.
/// </summary>
/// <remarks>
/// Declared as raw COM interop rather than <c>System.Windows.Automation</c>: the managed UIA
/// wrapper lives in the WPF assemblies, and referencing it would mean adding
/// <c>&lt;UseWPF&gt;</c> to a WinForms app -- pulling the whole WPF stack into a self-contained
/// <c>win-x64</c> publish that already ships as an MSIX. Only the vtable slots this file
/// actually calls are given real signatures; every slot before one of those is declared as a
/// placeholder so the offsets of the real methods stay correct -- a missing or reordered
/// member silently corrupts every call after it. Slots after the last real one are omitted
/// entirely, since they are never reached.
/// </remarks>
internal static class UiaInvoker
{
    private const string ClsidCUIAutomation = "ff48dba4-60ef-4201-aa87-54103eef594e";

    /// <summary>UIA_InvokePatternId.</summary>
    private const int PatternIdInvoke = 10000;

    /// <summary>UIA_LegacyIAccessiblePatternId.</summary>
    private const int PatternIdLegacyIAccessible = 10018;

    // ---- Public API ----

    /// <summary>False when UI Automation could not be stood up on the calling thread at all.</summary>
    public static bool IsAvailable => TryGetAutomation() is not null;

    /// <summary>
    /// Finds the UIA element at a screen point and invokes it, without moving the cursor.
    /// Tries <see cref="IUIAutomationInvokePattern"/> first, then falls back to
    /// <see cref="IUIAutomationLegacyIAccessiblePattern"/> -- plenty of elements (notably in
    /// Chrome) expose Legacy but not Invoke.
    /// </summary>
    /// <remarks>
    /// Must never throw: this runs inside the engine's action loop, where an exception would
    /// abort the whole sequence. COM fails here in many shapes -- <see cref="COMException"/>,
    /// <see cref="InvalidCastException"/>, <see cref="NotImplementedException"/> from partial
    /// providers, null elements -- all of which are caught and turned into <c>false</c> plus a
    /// human-readable <paramref name="failureReason"/>.
    /// </remarks>
    /// <remarks>
    /// Also refuses to invoke the desktop root: a point on no display, or one over bare
    /// desktop (a closed window, a moved monitor, a sequence replayed on a different screen
    /// setup), still resolves to *some* UIA element -- the Desktop pane, which happily exposes
    /// <see cref="IUIAutomationLegacyIAccessiblePattern"/>. Invoking it would "succeed" while
    /// doing nothing the user asked for, and the engine treats a <c>false</c> return as a soft
    /// miss it just carries on past -- silently doing nothing is worse than a loud failure here.
    /// </remarks>
    public static bool TryInvokeAt(int screenX, int screenY, out string failureReason)
    {
        failureReason = "";
        try
        {
            if (!SystemInformation.VirtualScreen.Contains(screenX, screenY))
            {
                failureReason = $"point {screenX},{screenY} is outside the virtual screen";
                return false;
            }

            IUIAutomation? automation = TryGetAutomation();
            if (automation is null)
            {
                failureReason = "UI Automation is not available on this machine.";
                return false;
            }

            IUIAutomationElement? element = ElementAt(automation, screenX, screenY);
            if (element is null)
            {
                failureReason = $"no UI Automation element found at {screenX},{screenY}";
                return false;
            }

            if (IsDesktopRoot(automation, element))
            {
                failureReason = $"point {screenX},{screenY} is not over any window (resolved to the desktop)";
                return false;
            }

            if (TryGetPattern(element, PatternIdInvoke, out IUIAutomationInvokePattern? invoke))
            {
                invoke!.Invoke();
                return true;
            }

            if (TryGetPattern(element, PatternIdLegacyIAccessible, out IUIAutomationLegacyIAccessiblePattern? legacy))
            {
                legacy!.DoDefaultAction();
                return true;
            }

            failureReason = $"element at {screenX},{screenY} exposes no invokable pattern";
            return false;
        }
#pragma warning disable CA1031 // COM providers throw unpredictably; this boundary must never propagate into the engine loop.
        catch (Exception ex)
        {
            failureReason = $"UI Automation invoke failed at {screenX},{screenY}: {ex.Message}";
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>Cheap probe for the editor UI: what UIA sees at a point, or null.</summary>
    public static string? DescribeAt(int screenX, int screenY)
    {
        try
        {
            IUIAutomation? automation = TryGetAutomation();
            if (automation is null) return null;

            IUIAutomationElement? element = ElementAt(automation, screenX, screenY);
            if (element is null) return null;

            string name = SafeGet(() => element.CurrentName) ?? "";
            int controlType = SafeGet(() => element.CurrentControlType);
            bool invoke = TryGetPattern(element, PatternIdInvoke, out IUIAutomationInvokePattern? _);
            bool legacy = TryGetPattern(element, PatternIdLegacyIAccessible, out IUIAutomationLegacyIAccessiblePattern? _);

            string patterns = (invoke, legacy) switch
            {
                (true, true) => "Invoke, LegacyIAccessible",
                (true, false) => "Invoke",
                (false, true) => "LegacyIAccessible",
                _ => "none",
            };

            return $"\"{name}\" (control type {controlType}) -- patterns: {patterns}";
        }
#pragma warning disable CA1031 // Diagnostic probe: any COM failure just means "nothing to show".
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    // ---- Element / pattern lookup ----

    private static IUIAutomationElement? ElementAt(IUIAutomation automation, int screenX, int screenY)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        return automation.ElementFromPoint(pt);
    }

    /// <summary>
    /// True when <paramref name="element"/> is the desktop's own root element -- what
    /// <see cref="IUIAutomation.ElementFromPoint"/> resolves to when a point isn't over any
    /// window. Any failure here (root element unavailable, comparison throws) is treated as
    /// "not the desktop" rather than blocking a click that might otherwise be legitimate.
    /// </summary>
    private static bool IsDesktopRoot(IUIAutomation automation, IUIAutomationElement element)
    {
        try
        {
            IUIAutomationElement? root = automation.GetRootElement();
            return root is not null && automation.CompareElements(element, root);
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool TryGetPattern<T>(IUIAutomationElement element, int patternId, out T? pattern)
        where T : class
    {
        pattern = null;
        try
        {
            object? raw = element.GetCurrentPattern(patternId);
            pattern = raw as T;
            return pattern is not null;
        }
        catch (COMException)
        {
            return false;
        }
        catch (NotImplementedException)
        {
            return false;
        }
    }

    private static T? SafeGet<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch (COMException)
        {
            return default;
        }
        catch (NotImplementedException)
        {
            return default;
        }
    }

    // ---- Automation instance, one per thread ----

    /// <summary>
    /// Cached per calling thread rather than in one process-wide static: UI Automation's
    /// client-side COM objects are not guaranteed safe to hand across apartments without
    /// proper marshalling, and the engine calls this from its own worker thread (a plain
    /// <see cref="Thread"/>, MTA by default) rather than the UI thread. Keeping creation and
    /// use on the same thread sidesteps the question entirely.
    /// </summary>
    [ThreadStatic]
    private static IUIAutomation? t_automation;

    [ThreadStatic]
    private static bool t_unavailable;

    private static IUIAutomation? TryGetAutomation()
    {
        if (t_automation is not null) return t_automation;
        if (t_unavailable) return null;

        try
        {
            EnsureComInitialized();

            Type? comType = Type.GetTypeFromCLSID(new Guid(ClsidCUIAutomation));
            if (comType is null)
            {
                t_unavailable = true;
                return null;
            }

            object instance = Activator.CreateInstance(comType)!;
            t_automation = (IUIAutomation)instance;
            return t_automation;
        }
#pragma warning disable CA1031 // Any failure here means "UI Automation unavailable on this thread", not a crash.
        catch (Exception)
        {
            t_unavailable = true;
            return null;
        }
#pragma warning restore CA1031
    }

    private const uint COINIT_MULTITHREADED = 0x0;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    /// <summary>
    /// UI Automation's client-side objects work from any apartment, but the calling thread
    /// still needs COM initialised at all. A plain <see cref="Thread"/> is MTA by default in
    /// .NET, yet nothing calls <c>CoInitializeEx</c> on it implicitly the way an STA UI thread
    /// does. <c>RPC_E_CHANGED_MODE</c> means some other code on this thread already
    /// initialised COM (e.g. as STA); that is harmless here since UI Automation does not
    /// require MTA specifically -- it just requires COM to be up. <c>CoUninitialize</c> is
    /// deliberately never called: this thread may live for the lifetime of the app, and
    /// tearing down COM out from under other code on the same thread would be worse than
    /// leaving it initialised.
    /// </summary>
    private static void EnsureComInitialized()
    {
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        if (hr < 0 && hr != RPC_E_CHANGED_MODE)
            throw new InvalidOperationException($"CoInitializeEx failed: 0x{hr:X8}");
    }

    // ---- COM: IUIAutomation ----
    // Real signatures for CompareElements (slot 1), GetRootElement (slot 3) and
    // ElementFromPoint (slot 5); slots 2 and 4 are placeholders.

    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        // Explicit Bool marshalling: the default for a bool in a COM interface is the 2-byte
        // VARIANT_BOOL, but this method returns a 4-byte Win32 BOOL. Getting it wrong would
        // not crash — IsDesktopRoot would just quietly return the wrong answer, either
        // letting a desktop-root invoke through or blocking a legitimate click.
        [return: MarshalAs(UnmanagedType.Bool)]
        bool CompareElements(IUIAutomationElement? el1, IUIAutomationElement? el2);

        void Stub02_CompareRuntimeIds();

        IUIAutomationElement? GetRootElement();

        void Stub04_ElementFromHandle();

        IUIAutomationElement? ElementFromPoint(POINT pt);
    }

    // ---- COM: IUIAutomationElement ----
    // Real signatures for GetCurrentPattern (slot 14), CurrentControlType (slot 19) and
    // CurrentName (slot 21); every other slot up to and including 21 is a placeholder.

    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void Stub01_SetFocus();
        void Stub02_GetRuntimeId();
        void Stub03_FindFirst();
        void Stub04_FindAll();
        void Stub05_FindFirstBuildCache();
        void Stub06_FindAllBuildCache();
        void Stub07_BuildUpdatedCache();
        void Stub08_GetCurrentPropertyValue();
        void Stub09_GetCurrentPropertyValueEx();
        void Stub10_GetCachedPropertyValue();
        void Stub11_GetCachedPropertyValueEx();
        void Stub12_GetCurrentPatternAs();
        void Stub13_GetCachedPatternAs();

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? GetCurrentPattern(int patternId);

        void Stub15_GetCachedPattern();
        void Stub16_GetCachedParent();
        void Stub17_GetCachedChildren();
        void Stub18_CurrentProcessId();

        int CurrentControlType { get; }

        void Stub20_CurrentLocalizedControlType();

        string CurrentName { get; }
    }

    // ---- COM: IUIAutomationInvokePattern ----
    // Invoke is the only member (vtable slot 1); no placeholders needed.

    [ComImport]
    [Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationInvokePattern
    {
        void Invoke();
    }

    // ---- COM: IUIAutomationLegacyIAccessiblePattern ----
    // Real signature for DoDefaultAction (slot 2); Select (slot 1) is a placeholder.

    [ComImport]
    [Guid("828055ad-355b-4435-86d5-3b51c14a9b1b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationLegacyIAccessiblePattern
    {
        void Stub01_Select();
        void DoDefaultAction();
    }
}
