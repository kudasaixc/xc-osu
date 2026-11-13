#!/bin/bash
# xc-osu!: stb_image (StbiSharp's libstbi, tom94/stbi-sharp 8554ce8) compiled to wasm -> Native/stbi.a
# Replaces the managed ImageSharp JPEG/PNG decoders, which are very slow and allocation-heavy under the wasm interpreter.
# xc softworks (kudasaixc)
set -e
cd "$(dirname "$0")"
source "$(dirname "$0")/../emenv.sh"
mkdir -p build
em++ -O3 -pthread -fwasm-exceptions -msimd128 -msse2 -DSTBI__X64_TARGET -std=c++17 -I include -c src/stbi.cpp -o build/stbi.o -w
rm -f build/stbi.a; emar rcs build/stbi.a build/stbi.o
cp build/stbi.a ../../osu/osu.Web/Native/stbi.a
echo "stbi.a -> osu.Web/Native"
