using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// HTML analogue of <see cref="ToolsOverviewWindow"/> — the read-only field guide to every
    /// tool. The whole catalog (categories -> tool cards with blurb / feeds / fed-by / example)
    /// ships in one payload; the page handles category tabs and feeds/fed-by chip navigation
    /// client-side. "Dummy run" buttons post a <c>runDemo</c> action which opens the tool's demo
    /// in a real (WPF) StepFlowWindow on its own STA thread, exactly as the WPF overview does.
    /// </summary>
    public sealed class WebToolsOverviewWindow : WebWindowBase
    {
        public WebToolsOverviewWindow()
            : base(AppStrings.T("overview.window.title"), 760, 680) { }

        protected override string PageFileName => "toolsoverview.html";

        protected override Dictionary<string, object?> BuildInitPayload() => new Dictionary<string, object?>
        {
            ["title"]      = AppStrings.T("overview.window.title"),
            ["close"]      = AppStrings.T("overview.window.close"),
            ["footerHint"] = AppStrings.T("overview.window.footerHint"),
            ["runButton"]  = AppStrings.T("overview.window.runButton"),
            ["fedByLabel"] = AppStrings.T("overview.window.fedBy"),
            ["feedsLabel"] = AppStrings.T("overview.window.feeds"),
            ["categories"] = ToolsOverviewCatalog.Categories.Select(c => new Dictionary<string, object?>
            {
                ["id"]    = c.Id,
                ["name"]  = c.Name,
                ["glyph"] = c.Glyph,
                ["intro"] = c.Intro,
                ["tools"] = c.Tools.Select(t => new Dictionary<string, object?>
                {
                    ["name"]    = t.Name,
                    ["glyph"]   = t.Glyph,
                    ["blurb"]   = t.Blurb,
                    ["feeds"]   = t.Feeds.ToList(),
                    ["fedBy"]   = t.FedBy.ToList(),
                    ["example"] = t.Example,
                    ["hasDemo"] = ToolsOverviewDemos.For(t.Name) != null,
                }).ToList(),
            }).ToList(),
        };

        protected override bool HandleAction(string action, IReadOnlyDictionary<string, object?> payload)
        {
            if (action != "runDemo") return false;
            LaunchDemo(Str(payload, "tool"));
            return true;
        }

        // Opens the tool's demo in a real StepFlowWindow on its own dedicated STA thread (the demo
        // tools are IStepFlowTool, not IWebTool). Mirrors ToolsOverviewWindow.LaunchDemo: the ready
        // gate is released even on a construction throw so the caller thread never blocks forever.
        private static void LaunchDemo(string toolName)
        {
            var spec = ToolsOverviewDemos.For(toolName);
            if (spec == null) return;

            var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    var win = new StepFlowWindow(new OverviewDemoTool(spec));
                    win.Closed += (s, e) => System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                    win.Show();
                    ready.Set();
                    System.Windows.Threading.Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("WebToolsOverview: launch demo run", ex);
                    ready.Set();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            ready.Wait();
        }
    }
}
