#!/usr/bin/env python3
# xc-osu! (xc softworks, kudasaixc) — fixes .NET's generated native->managed callback thunks for struct-by-value args.
# wasm32 C ABI passes multi-field structs indirectly (as a pointer to a copy); the generated thunk declares them as
# `void * argN` but then hands the interpreter `&argN` (pointer to the pointer), so the managed side reads garbage.
# The interpreter expects a pointer to the struct data, i.e. `argN` itself.
import re, sys

# callback thunk name suffix -> zero-based indices of struct-by-value parameters (from realm-dotnet delegate signatures)
STRUCT_ARGS = {
    'Realm_Realms_SharedRealmHandle_LogMessage': [1, 2],                  # StringValue, StringValue
    'Realm_Realms_SharedRealmHandle_GetNativeSchema': [0],                # Native.Schema
    'Realm_Realms_SharedRealmHandle_HandleOpenRealmCallback': [2],        # NativeException
    'Realm_Realms_SharedRealmHandle_OnTaskCompleted': [2],                # NativeException
    'Realm_Realms_SharedRealmHandle_OnMigration': [3],                    # Native.Schema
}

path = sys.argv[1]
src = open(path).read()
fixed = 0
for name, idxs in STRUCT_ARGS.items():
    m = re.search(r'\nwasm_native_to_interp_' + name + r' \(.*?\n}\n', src, re.S)
    if not m:
        print(f'fix-pinvoke-structs: warning: {name} not found', file=sys.stderr)
        continue
    body = m.group(0)
    new = body
    for i in idxs:
        new = new.replace(f'(int*)&arg{i},', f'(int*)arg{i},').replace(f'(int*)&arg{i})', f'(int*)arg{i})')
    if new != body:
        fixed += 1
        src = src[:m.start()] + new + src[m.end():]
open(path, 'w').write(src)
print(f'fix-pinvoke-structs: patched {fixed}/{len(STRUCT_ARGS)} callback thunks in {path}')
