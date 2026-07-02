# libs2025

Fallback location for `RevitAPI.dll` and `RevitAPIUI.dll` from **Revit 2025**,
used by the `Debug2025`/`Release2025` build configurations when
`C:\Program Files\Autodesk\Revit 2025` is not present on the build machine
(see `LemoineTools.csproj`).

Copy the two DLLs from a Revit 2025 install (or the Revit 2025 API SDK) into
this folder to build `*2025` configurations without a local Revit 2025
install.
