#!/usr/bin/env python3
"""Decode real member signatures out of Autodesk.Navisworks.Api.dll using dnfile,
the same metadata-table-walking approach CLAUDE.md prescribes for RevitAPI.dll
(pip install dnfile; walk TypeDef -> PropertyMap/MethodList, decode signature
blobs by hand per ECMA-335 II.23.2) — this project cannot be built on Linux and
ships no ildasm/dotnet/mono, so this is the only way to confirm a Navisworks API
member's real name, type, and signature rather than guessing from memory.

The DLL itself is never committed (licensed — see libs-navis/README.md and
.gitignore); drop a copy at libs-navis/Autodesk.Navisworks.Api.dll (from a
Navisworks install, or one the user provides) before running this.

Usage:
    python3 devtools/navis_dump.py <TypeName substring> [<TypeName substring> ...]

Prints every matching type's fields (with enum constant values), properties,
and methods (with decoded parameter/return types), scoped to their declaring
type — never a bare string search across the whole assembly, which is exactly
how a name can be found without actually belonging to the type you think it
does. Example: `python3 devtools/navis_dump.py "Api.ClipPlaneSet"`.
"""
import os
import sys
import dnfile

PATH = os.path.join(os.path.dirname(__file__), "..", "libs-navis", "Autodesk.Navisworks.Api.dll")

ELEMENT_TYPE_NAMES = {
    0x01: "void", 0x02: "bool", 0x03: "char", 0x04: "sbyte", 0x05: "byte",
    0x06: "short", 0x07: "ushort", 0x08: "int", 0x09: "uint", 0x0a: "long",
    0x0b: "ulong", 0x0c: "float", 0x0d: "double", 0x0e: "string",
    0x18: "IntPtr", 0x19: "UIntPtr", 0x1c: "object",
}


def read_compressed(data, pos):
    b0 = data[pos]
    if b0 & 0x80 == 0:
        return b0, pos + 1
    if b0 & 0xC0 == 0x80:
        val = ((b0 & 0x3F) << 8) | data[pos + 1]
        return val, pos + 2
    val = ((b0 & 0x1F) << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]
    return val, pos + 4


def resolve_coded_typedeforref(coded, tables):
    tag = coded & 0x3
    rid = coded >> 2
    try:
        if tag == 0:
            row = tables.TypeDef.rows[rid - 1]
        elif tag == 1:
            row = tables.TypeRef.rows[rid - 1]
        else:
            return f"TypeSpec#{rid}"
        ns = str(row.TypeNamespace)
        name = str(row.TypeName)
        return f"{ns}.{name}" if ns else name
    except Exception as ex:
        return f"<unresolved tag={tag} rid={rid}: {ex}>"


def decode_type(data, pos, tables):
    et = data[pos]
    pos += 1
    # custom modifiers
    while et in (0x1F, 0x20):
        _, pos = read_compressed(data, pos)
        et = data[pos]
        pos += 1
    if et in ELEMENT_TYPE_NAMES:
        return ELEMENT_TYPE_NAMES[et], pos
    if et in (0x11, 0x12):  # VALUETYPE / CLASS
        coded, pos = read_compressed(data, pos)
        return resolve_coded_typedeforref(coded, tables), pos
    if et == 0x0F:  # PTR
        inner, pos = decode_type(data, pos, tables)
        return inner + "*", pos
    if et == 0x10:  # BYREF
        inner, pos = decode_type(data, pos, tables)
        return "ref " + inner, pos
    if et == 0x1D:  # SZARRAY
        inner, pos = decode_type(data, pos, tables)
        return inner + "[]", pos
    if et == 0x14:  # ARRAY (multi-dim) - shape follows, skip roughly
        inner, pos = decode_type(data, pos, tables)
        rank, pos = read_compressed(data, pos)
        nsizes, pos = read_compressed(data, pos)
        for _ in range(nsizes):
            _, pos = read_compressed(data, pos)
        nlobounds, pos = read_compressed(data, pos)
        for _ in range(nlobounds):
            _, pos = read_compressed(data, pos)
        return f"{inner}[{rank}]", pos
    if et == 0x15:  # GENERICINST
        gen_et = data[pos]  # 0x11 or 0x12
        pos += 1
        coded, pos = read_compressed(data, pos)
        base_name = resolve_coded_typedeforref(coded, tables)
        argc, pos = read_compressed(data, pos)
        args = []
        for _ in range(argc):
            a, pos = decode_type(data, pos, tables)
            args.append(a)
        return f"{base_name}<{', '.join(args)}>", pos
    if et == 0x13:  # VAR (generic type param)
        idx, pos = read_compressed(data, pos)
        return f"!{idx}", pos
    if et == 0x1E:  # MVAR (generic method param)
        idx, pos = read_compressed(data, pos)
        return f"!!{idx}", pos
    if et == 0x16:
        return "TypedReference", pos
    return f"<et=0x{et:02x}>", pos


def decode_method_sig(blob, tables):
    pos = 0
    flags = blob[pos]; pos += 1
    has_this = bool(flags & 0x20)
    is_generic = bool(flags & 0x10)
    if is_generic:
        _, pos = read_compressed(blob, pos)  # generic param count
    paramcount, pos = read_compressed(blob, pos)
    ret, pos = decode_type(blob, pos, tables)
    params = []
    for _ in range(paramcount):
        p, pos = decode_type(blob, pos, tables)
        params.append(p)
    return has_this, ret, params


def decode_property_sig(blob, tables):
    pos = 0
    flags = blob[pos]; pos += 1
    has_this = bool(flags & 0x20)
    paramcount, pos = read_compressed(blob, pos)
    ptype, pos = decode_type(blob, pos, tables)
    params = []
    for _ in range(paramcount):
        p, pos = decode_type(blob, pos, tables)
        params.append(p)
    return has_this, ptype, params


def main():
    needles = [n.lower() for n in sys.argv[1:]]
    if not needles:
        print("usage: navis_dump.py <TypeName substring> [...]")
        sys.exit(1)

    d = dnfile.dnPE(PATH, fast_load=False)
    tables = d.net.mdtables

    for row in tables.TypeDef.rows:
        name = str(row.TypeName)
        ns = str(row.TypeNamespace)
        full = f"{ns}.{name}" if ns else name
        if not any(n in full.lower() for n in needles):
            continue

        is_enum = False
        try:
            is_enum = "System.Enum" in resolve_extends(row, tables)
        except Exception:
            pass

        print(f"\n{'='*100}\n{full}\n{'='*100}")

        # Fields (enum members carry Constant values; look those up too)
        for f in row.FieldList:
            frow = f.row
            fname = str(frow.Name)
            if fname == "value__":
                continue
            const_val = find_constant(tables, "Field", f.row_index)
            if const_val is not None:
                print(f"  const  {fname} = {const_val}")
            else:
                print(f"  field  {fname}")

        # Properties
        pmap = find_property_map(tables, row)
        if pmap:
            for prow in pmap:
                pname = str(prow.row.Name)
                try:
                    has_this, ptype, params = decode_property_sig(prow.row.Type.value, tables)
                except Exception as ex:
                    ptype, params = f"<decode failed: {ex}>", []
                sig = f"({', '.join(params)})" if params else ""
                print(f"  prop   {ptype} {pname}{sig}")

        # Methods (skip property accessors' redundant listing? keep them — useful to confirm exact names)
        for m in row.MethodList:
            mrow = m.row
            mname = str(mrow.Name)
            try:
                has_this, ret, params = decode_method_sig(mrow.Signature.value, tables)
            except Exception as ex:
                ret, params = f"<decode failed: {ex}>", []
            print(f"  method {ret} {mname}({', '.join(params)})")


def resolve_extends(row, tables):
    ext = row.Extends
    if ext is None:
        return ""
    try:
        return resolve_coded_typedeforref_from_index(ext, tables)
    except Exception:
        return ""


def resolve_coded_typedeforref_from_index(mdindex, tables):
    # Extends is already an MDTableIndex-like object in dnfile for TypeDef rows in some versions;
    # handle both raw-int coded values and pre-resolved row objects defensively.
    if hasattr(mdindex, "row"):
        r = mdindex.row
        ns, name = str(getattr(r, "TypeNamespace", "")), str(getattr(r, "TypeName", ""))
        return f"{ns}.{name}" if ns else name
    return ""


def find_property_map(tables, typedef_row):
    pm = tables.PropertyMap
    if not pm:
        return None
    for prow in pm.rows:
        parent = prow.Parent
        target = parent.row if hasattr(parent, "row") else parent
        if target is typedef_row:
            return prow.PropertyList
    return None


def find_constant(tables, parent_table_name, parent_row_index):
    const_tbl = tables.Constant
    if not const_tbl:
        return None
    for crow in const_tbl.rows:
        parent = crow.Parent
        ptable = parent.table if hasattr(parent, "table") else None
        pidx = parent.row_index if hasattr(parent, "row_index") else None
        if pidx == parent_row_index and ptable is not None and ptable.name == parent_table_name:
            raw = crow.Value.value if hasattr(crow.Value, "value") else crow.Value
            try:
                return int.from_bytes(raw, "little", signed=True)
            except Exception:
                return raw
    return None


if __name__ == "__main__":
    main()
