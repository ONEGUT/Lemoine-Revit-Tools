# libs2027

Fallback location for `RevitAPI.dll` and `RevitAPIUI.dll` from **Revit 2027**,
used by the `Debug2027`/`Release2027` build configurations when
`C:\Program Files\Autodesk\Revit 2027` is not present on the build machine
(see `LemoineTools.csproj`).

**Revit 2027 has not shipped yet.** These configurations exist so the project
is ready the day Autodesk releases it, but `*2027` builds will fail until
either a local Revit 2027 install exists or that year's `RevitAPI.dll` /
`RevitAPIUI.dll` are copied into this folder.
