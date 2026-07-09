using System;

namespace LemoineTools
{
    // ─────────────────────────────────────────────────────────────────────────
    // Compile-only stand-ins so StepFlowWindow.xaml.cs (glob-included from
    // Source/Framework, unmodified) can build in this Revit-free project.
    //
    // StepFlowWindow references LemoineTools.App.ReloadHandler / .ReloadEvent
    // (Source/App.cs — the real IExternalApplication, excluded here because it
    // references RevitAPI.dll) and LemoineTools.Framework.RevitFailureCapture
    // (excluded because it hooks Autodesk.Revit.UI events). Real App.ReloadHandler
    // is a LemoineTools.Framework.ReloadHandler (itself excluded — it implements
    // Autodesk.Revit.UI.IExternalEventHandler) and real App.ReloadEvent is an
    // Autodesk.Revit.UI.ExternalEvent.
    //
    // These stubs are never meant to run anything — ReloadHandler/ReloadEvent stay
    // permanently null, so StepFlowWindow.ReloadWindow() always takes its documented
    // "Reload infrastructure isn't registered — degrade to a soft reset" fallback
    // (see StepFlowWindow.xaml.cs) instead of ever touching Revit. They exist purely
    // so the null-guarded call sites still type-check.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class App
    {
        internal static PreviewReloadHandlerStub? ReloadHandler => null;
        internal static PreviewExternalEventStub? ReloadEvent => null;
    }

    /// <summary>Shape-compatible stand-in for Framework.ReloadHandler.Enqueue — never invoked,
    /// since App.ReloadHandler is always null (see App stub above).</summary>
    internal sealed class PreviewReloadHandlerStub
    {
        public void Enqueue(Func<Framework.IStepFlowTool> factory, Action<Framework.IStepFlowTool?> onBuilt) { }
    }

    /// <summary>Shape-compatible stand-in for Autodesk.Revit.UI.ExternalEvent.Raise() — never
    /// invoked, since App.ReloadEvent is always null (see App stub above).</summary>
    internal sealed class PreviewExternalEventStub
    {
        public void Raise() { }
    }
}

namespace LemoineTools.Framework
{
    /// <summary>Compile-only stand-in for the real RevitFailureCapture (excluded — it hooks
    /// Autodesk.Revit.UI.UIControlledApplication events). BeginRun() is a genuine no-op here:
    /// this preview app never runs a Revit transaction, so there is nothing to capture.</summary>
    internal static class RevitFailureCapture
    {
        public static void BeginRun() { }
    }
}
