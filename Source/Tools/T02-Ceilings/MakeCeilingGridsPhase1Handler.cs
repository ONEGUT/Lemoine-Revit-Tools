using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Ceilings
{
    public sealed class CeilingTypeEntry
    {
        public string Source      { get; set; } = "";
        public string FamilyName  { get; set; } = "";
        public string TypeName    { get; set; } = "";
        public bool   IsLinked    { get; set; }
        public long   TypeIdValue { get; set; }
    }

    public sealed class MakeCeilingGridsPhase1Handler : IExternalEventHandler
    {
        public bool            IncludeHost { get; set; } = true;
        public List<ElementId> LinkInstIds { get; set; } = new List<ElementId>();

        public Action<List<CeilingTypeEntry>>? OnTypesLoaded { get; set; }
        public Action<string>?                 OnError       { get; set; }

        public string GetName() => "LemoineTools.Tools.Ceilings.MakeCeilingGridsPhase1Handler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            try
            {
                var results = new List<CeilingTypeEntry>();
                var seen    = new HashSet<string>(StringComparer.Ordinal);

                void CollectFrom(Document srcDoc, string sourceName, bool linked)
                {
                    var types = new FilteredElementCollector(srcDoc)
                        .OfClass(typeof(CeilingType))
                        .Cast<CeilingType>();

                    foreach (var t in types)
                    {
                        string family   = t.FamilyName ?? "";
                        string typeName = t.Name       ?? "";
                        string key      = $"{sourceName}|{family}|{typeName}";
                        if (!seen.Add(key)) continue;

                        results.Add(new CeilingTypeEntry
                        {
                            Source      = sourceName,
                            FamilyName  = family,
                            TypeName    = typeName,
                            IsLinked    = linked,
                            TypeIdValue = linked ? 0L : t.Id.Value,
                        });
                    }
                }

                if (IncludeHost)
                    CollectFrom(doc, "(Host document)", false);

                foreach (var id in LinkInstIds)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        LemoineLog.Warn("MakeCeilingGridsPhase1", "Stopped by user — work so far preserved.");
                        break;
                    }

                    var li = doc.GetElement(id) as RevitLinkInstance;
                    var ld = li?.GetLinkDocument();
                    if (ld == null) continue;
                    CollectFrom(ld, ld.Title ?? li!.Name, true);
                }

                OnTypesLoaded?.Invoke(results);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
            }
            finally
            {
                // Session-long static handler — drop the scan's payload.
                LinkInstIds = new List<ElementId>();
            }
        }
    }
}
