# libs-navis — Navisworks API fallback

`LemoineNavisworks.csproj` resolves the Navisworks .NET API from your installed
Navisworks first:

1. `C:\Program Files\Autodesk\Navisworks Manage 2026`
2. `C:\Program Files\Autodesk\Navisworks Manage 2025`
3. this `libs-navis\` folder (fallback)

If you build on a machine **without** Navisworks installed (or with a different
edition/path), drop a copy of **`Autodesk.Navisworks.Api.dll`** into this folder.
It is found in the Navisworks install root, e.g.
`C:\Program Files\Autodesk\Navisworks Manage 2025\Autodesk.Navisworks.Api.dll`.

The DLL is **licensed and git-ignored** (`libs-navis/*.dll`) — it is never
committed. `Private=False` in the csproj means it is referenced for compilation
only and not copied to the plugin output.
