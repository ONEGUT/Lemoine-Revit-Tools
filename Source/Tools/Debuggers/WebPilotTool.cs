using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Framework.Web;

namespace LemoineTools.Tools.Debuggers
{
    /// <summary>
    /// Phase 2b pilot tool (IWebTool): pick an element category, count it in the active document.
    /// The simplest real tool that exercises the whole HTML path - spec-driven steps, C#-side
    /// validation, and a genuine ExternalEvent run - so it is the template for the Phase 3
    /// migration waves. Developer-only, so strings stay hardcoded (AppStrings exemption).
    /// </summary>
    public class WebPilotTool : IWebTool, IWebToolCleanup
    {
        private readonly WebPilotEventHandler _handler;
        private readonly ExternalEvent         _event;
        private string _category = "walls";

        private static readonly (string Value, string Label)[] Categories =
        {
            ("walls", "Walls"), ("doors", "Doors"), ("windows", "Windows"),
            ("floors", "Floors"), ("ceilings", "Ceilings"),
        };

        public WebPilotTool(WebPilotEventHandler handler, ExternalEvent externalEvent)
        {
            _handler = handler;
            _event   = externalEvent;
        }

        public string Title    => "Web Pilot - Count Elements";
        public string RunLabel => "Count in Revit →";

        public event EventHandler? ValidationChanged;

        public IReadOnlyList<WebStep> BuildSteps()
        {
            var pick = new WebStep("cat", "Category").Add(
                WebInput.SingleSelect("category", "Element category", _category,
                    Categories.Select(c => new WebOption(c.Value, c.Label))));

            var run = new WebStep("run", "Review & Run", required: false).Add(
                WebInput.Warn("note",
                    "Read-only: counts elements of the chosen category in the active document. Nothing is modified."));

            return new List<WebStep> { pick, run };
        }

        public void OnState(string stepId, string inputId, object? value)
        {
            if (inputId == "category" && value is string s && !string.IsNullOrEmpty(s))
            {
                _category = s;
                ValidationChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool IsStepValid(string stepId) =>
            stepId != "cat" || !string.IsNullOrEmpty(_category);

        public bool CanRun() => !string.IsNullOrEmpty(_category);

        public string SummaryFor(string stepId)
        {
            if (stepId != "cat") return "";
            var match = Categories.FirstOrDefault(c => c.Value == _category);
            return string.IsNullOrEmpty(match.Label) ? _category : match.Label;
        }

        public bool IsStepVisible(string stepId) => true;

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            _handler.CategoryToken = _category;
            _handler.OnLog         = pushLog;
            _handler.OnProgress    = onProgress;
            _handler.OnComplete    = onComplete;
            _event.Raise();
        }

        public void OnWindowClosed()
        {
            _handler.OnLog = null; _handler.OnProgress = null; _handler.OnComplete = null;
        }
    }
}
