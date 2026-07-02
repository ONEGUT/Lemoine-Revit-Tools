# libs2026

Fallback location for `RevitAPI.dll` and `RevitAPIUI.dll` from **Revit 2026**,
used by the `Debug2026`/`Release2026` build configurations when
`C:\Program Files\Autodesk\Revit 2026` is not present on the build machine
(see `LemoineTools.csproj`).

Copy the two DLLs from a Revit 2026 install (or the Revit 2026 API SDK) into
this folder to build `*2026` configurations without a local Revit 2026
install.
