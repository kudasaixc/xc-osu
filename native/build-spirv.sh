#!/bin/bash
set -e
N=$(cd "$(dirname "$0")" && pwd)
source $N/emenv.sh
cd $N/veldrid-spirv
mkdir -p build-wasm && cd build-wasm
FLAGS="-pthread -fwasm-exceptions -msimd128 -O2"
emcmake cmake .. -G Ninja -DCMAKE_BUILD_TYPE=Release -DPYTHON_EXECUTABLE=$(which python3) \
  -DCMAKE_C_FLAGS="$FLAGS" -DCMAKE_CXX_FLAGS="$FLAGS" \
  -DSPIRV_CROSS_ENABLE_TESTS=OFF -DSPIRV_CROSS_CLI=OFF -DSHADERC_SKIP_EXAMPLES=ON -DSPIRV_SKIP_EXECUTABLES=ON -DSPIRV_SKIP_TESTS=ON \
  -DENABLE_GLSLANG_BINARIES=OFF -DSHADERC_ENABLE_SHARED_CRATE=OFF -DSPIRV_TOOLS_BUILD_STATIC=ON -DSHADERC_SKIP_COPYRIGHT_CHECK=ON
ninja -j4 veldrid-spirv
# merge everything into one archive for NativeFileReference
mkdir -p merge && cd merge && rm -f *.o
for a in $(find .. -name "*.a" -not -path "*/merge/*"); do d=$(basename $a .a); mkdir -p $d; (cd $d && emar x ../$a); done
emar rcs ../libveldrid-spirv-all.a $(find . -name "*.o")
echo DONE
