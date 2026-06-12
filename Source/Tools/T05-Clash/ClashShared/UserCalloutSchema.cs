using System;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Clash
{
    /// <summary>
    /// Adoption marker for USER-drawn callout views the Clash Finder takes over as pre-defined
    /// clash groups. Distinct from <see cref="ClashTagSchema"/> (markers) and
    /// <c>AutoDimOwnerSchema</c> (dimensions) so the three never touch each other's elements.
    ///
    /// The GUID is HARDCODED and constant for all time — regenerating it would orphan every
    /// previously-adopted callout, silently re-baselining its group to the room-grown crop.
    ///
    /// Two rectangles are stamped on the adopted view, each as two world corners on the view
    /// plane: the MEMBERSHIP rectangle (the boundary the user originally drew — the authoritative
    /// group definition) and the APPLIED rectangle (the room-grown crop the tool last wrote).
    /// A re-run compares the callout's current crop against the applied rectangle: unchanged
    /// means the tool's growth is still in place and membership keeps the original boundary;
    /// changed means the USER resized the callout by hand, and their edit wins — membership
    /// re-baselines to the current crop.
    /// </summary>
    public static class UserCalloutSchema
    {
        /// <summary>Stable, constant schema GUID. NEVER regenerate.</summary>
        public static readonly Guid SchemaGuid = new Guid("9C5E21B8-4A7F-4D3C-A1B6-0F8E62D947A3");

        /// <summary>Current entity version. Bump only with a migration plan.</summary>
        public const int CurrentVersion = 1;

        private const string SchemaName          = "LemoineUserCallout";
        private const string FieldVersion        = "Version";
        private const string FieldMembershipRect = "MembershipRect";
        private const string FieldAppliedRect    = "AppliedRect";

        /// <summary>One stamped rectangle pair read back from an adopted callout view.</summary>
        public sealed class Stamped
        {
            public XYZ MembershipMin { get; set; } = XYZ.Zero;
            public XYZ MembershipMax { get; set; } = XYZ.Zero;
            public XYZ AppliedMin    { get; set; } = XYZ.Zero;
            public XYZ AppliedMax    { get; set; } = XYZ.Zero;
        }

        /// <summary>
        /// Returns the schema, creating it in this session if absent. Safe to call repeatedly;
        /// <see cref="Schema.Lookup"/> short-circuits after the first build and lets entities
        /// written in a previous session stay readable. Rectangles are stored as invariant-culture
        /// strings (ES double fields require a unit spec; strings avoid that entirely).
        /// </summary>
        public static Schema GetOrCreate()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;

            var sb = new SchemaBuilder(SchemaGuid);
            sb.SetSchemaName(SchemaName);
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.AddSimpleField(FieldVersion, typeof(int));
            sb.AddSimpleField(FieldMembershipRect, typeof(string));
            sb.AddSimpleField(FieldAppliedRect, typeof(string));
            return sb.Finish();
        }

        /// <summary>
        /// Stamps an adopted callout view with its membership + applied rectangles. Must run
        /// inside an open transaction. Failures are logged, not thrown — but an un-stamped
        /// adoption re-baselines its membership to the grown crop next run, so the caller
        /// should surface a false return in the run log.
        /// </summary>
        public static bool Stamp(Element calloutView,
            XYZ membershipMin, XYZ membershipMax, XYZ appliedMin, XYZ appliedMax)
        {
            if (calloutView == null) return false;
            try
            {
                var schema = GetOrCreate();
                var entity = new Entity(schema);
                entity.Set(FieldVersion, CurrentVersion);
                entity.Set(FieldMembershipRect, WriteRect(membershipMin, membershipMax));
                entity.Set(FieldAppliedRect, WriteRect(appliedMin, appliedMax));
                calloutView.SetEntity(entity);
                return true;
            }
            catch (Exception ex)
            {
                LemoineLog.Error("UserCalloutSchema: stamp adopted callout", ex);
                return false;
            }
        }

        /// <summary>Reads the stamped rectangles, or null when the view was never adopted (or
        /// the entity is unreadable). Read-only — no transaction needed.</summary>
        public static Stamped? Read(Element calloutView)
        {
            if (calloutView == null) return null;
            try
            {
                var schema = GetOrCreate();
                if (schema == null) return null;
                var entity = calloutView.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return null;

                var membership = ParseRect(entity.Get<string>(FieldMembershipRect));
                var applied    = ParseRect(entity.Get<string>(FieldAppliedRect));
                if (membership == null || applied == null)
                {
                    // A valid entity with unreadable rects means a corrupted/foreign write —
                    // surface it: the caller will treat the view as un-adopted and re-baseline
                    // its group to the current crop.
                    LemoineLog.Warn("UserCalloutSchema: read adopted callout",
                        "stamped rectangles unreadable — view treated as un-adopted (group re-baselines to its current crop)");
                    return null;
                }
                return new Stamped
                {
                    MembershipMin = membership.Value.min,
                    MembershipMax = membership.Value.max,
                    AppliedMin    = applied.Value.min,
                    AppliedMax    = applied.Value.max,
                };
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("UserCalloutSchema: read adopted callout", ex);
                return null;
            }
        }

        private static string WriteRect(XYZ min, XYZ max) => string.Format(
            CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R};{3:R},{4:R},{5:R}",
            min.X, min.Y, min.Z, max.X, max.Y, max.Z);

        private static (XYZ min, XYZ max)? ParseRect(string? s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var halves = s!.Split(';');
            if (halves.Length != 2) return null;
            var a = ParsePoint(halves[0]);
            var b = ParsePoint(halves[1]);
            if (a == null || b == null) return null;
            return (a, b);
        }

        private static XYZ? ParsePoint(string s)
        {
            var parts = s.Split(',');
            if (parts.Length != 3) return null;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) return null;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) return null;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)) return null;
            return new XYZ(x, y, z);
        }
    }
}
