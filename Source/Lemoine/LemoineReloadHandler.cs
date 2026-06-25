using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Shared ExternalEvent handler that rebuilds a tool's ViewModel on Revit's main thread for
    /// the StepFlowWindow "Reload" action. A tool window can't touch the Revit API from its own
    /// STA thread, so re-capturing the document's options (views, categories, links, …) must be
    /// marshalled here. Each window enqueues a (factory, onBuilt) job then raises the event; the
    /// factory runs on the main thread to produce a fresh tool, and onBuilt hands it back to the
    /// window (which marshals the actual UI rebuild onto its own dispatcher).
    ///
    /// A concurrent queue is used so two windows reloading near-simultaneously can't clobber a
    /// single shared payload field — each Raise has its own job.
    /// </summary>
    public sealed class LemoineReloadHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<(Func<ILemoineTool> factory, Action<ILemoineTool?> onBuilt)> _jobs
            = new ConcurrentQueue<(Func<ILemoineTool>, Action<ILemoineTool?>)>();

        /// <summary>Queue a rebuild. Call immediately before <c>ReloadEvent.Raise()</c>.</summary>
        public void Enqueue(Func<ILemoineTool> factory, Action<ILemoineTool?> onBuilt)
        {
            if (factory == null || onBuilt == null) return;
            _jobs.Enqueue((factory, onBuilt));
        }

        public void Execute(UIApplication app)
        {
            while (_jobs.TryDequeue(out var job))
            {
                ILemoineTool? tool = null;
                try
                {
                    tool = job.factory();
                }
                catch (Exception ex)
                {
                    // Re-capture/construction failed (e.g. the active document was closed) — report
                    // null so the window keeps its current tool and surfaces the failure to the user.
                    LemoineLog.Error("StepFlowWindow reload — rebuilding tool", ex);
                }

                try
                {
                    job.onBuilt(tool);
                }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed("StepFlowWindow reload — onBuilt callback", ex);
                }
            }
        }

        public string GetName() => "Lemoine StepFlow Reload";
    }
}
