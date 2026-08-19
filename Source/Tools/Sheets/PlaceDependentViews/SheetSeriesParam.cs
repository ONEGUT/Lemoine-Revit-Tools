using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LemoineTools.Tools.Sheets.PlaceDependentViews
{
    /// <summary>
    /// One text parameter a sheet can actually carry, captured from the document on the Revit
    /// thread at command launch, plus the values already in use for it.
    ///
    /// This replaces the old "type the parameter's name and hope" field. Writing a sheet parameter
    /// BY NAME is unreliable: <c>LookupParameter</c> returns only the first name match and silently
    /// picks the wrong duplicate, and <c>GetParameters(name)</c> returns all of them in no defined
    /// order — which is why the Sheet Series value never landed. Identity is carried here instead:
    /// a <see cref="Guid"/> for a shared parameter (stable and unambiguous), and the definition's
    /// own ElementId for a project parameter.
    /// </summary>
    public sealed class SheetSeriesParam
    {
        public SheetSeriesParam(string name, bool isShared, Guid guid, ElementId definitionId)
        {
            Name         = name ?? "";
            IsShared     = isShared;
            SharedGuid   = guid;
            DefinitionId = definitionId ?? ElementId.InvalidElementId;
        }

        /// <summary>Parameter name as Revit shows it.</summary>
        public string Name { get; }

        /// <summary>Shared parameters are bound by GUID; project parameters by definition id.</summary>
        public bool IsShared { get; }

        /// <summary>The shared-parameter GUID — <see cref="System.Guid.Empty"/> for a project parameter.</summary>
        public Guid SharedGuid { get; }

        /// <summary>The definition's ElementId, used to identify a project parameter exactly.</summary>
        public ElementId DefinitionId { get; }

        /// <summary>Values already present on sheets in this project, offered as suggestions.</summary>
        public List<string> ExistingValues { get; } = new List<string>();

        /// <summary>Label for the picker: the name plus which kind of parameter it is, because two
        /// parameters can share a name and only the kind tells them apart on sight.</summary>
        public string Label => Name + (IsShared
            ? "   " + AppStringsShim.Shared
            : "   " + AppStringsShim.Project);

        /// <summary>Resolves this parameter on a specific element by identity, never by name.</summary>
        public Parameter? Resolve(Element element)
        {
            if (element == null) return null;
            if (IsShared && SharedGuid != Guid.Empty) return element.get_Parameter(SharedGuid);

            if (DefinitionId != ElementId.InvalidElementId)
            {
                foreach (Parameter p in element.Parameters)
                {
                    if (p?.Definition is InternalDefinition def && def.Id == DefinitionId) return p;
                }
            }
            return null;
        }
    }

    /// <summary>Label fragments for <see cref="SheetSeriesParam.Label"/>, kept behind a tiny shim so
    /// the record stays a plain data type that the settings-only (no-document) path can construct.</summary>
    internal static class AppStringsShim
    {
        public static string Shared  => LemoineTools.Framework.AppStrings.T("testing.placeDependentViews.labels.paramShared");
        public static string Project => LemoineTools.Framework.AppStrings.T("testing.placeDependentViews.labels.paramProject");
    }
}
