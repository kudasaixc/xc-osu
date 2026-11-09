#!/bin/bash
# xc-osu! — build the original Realm .NET wrappers (realm-core 20.1.0) for browser-wasm (pthreads). xc softworks (kudasaixc)
set -e
source /var/www/osu/native/emenv.sh
SRC=/var/www/osu/native/realm-dotnet/wrappers
B=/var/www/osu/native/realm-build
mkdir -p $B && cd $B
FLAGS="-pthread -fwasm-exceptions -msimd128"
ZL=~/.dotnet/packs/Microsoft.NETCore.App.Runtime.Mono.multithread.browser-wasm/10.0.14/runtimes/browser-wasm/native/libz.a
emcmake cmake -G Ninja $SRC \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_C_FLAGS="$FLAGS" -DCMAKE_CXX_FLAGS="$FLAGS" \
  -DREALM_CORE_SUBMODULE_BUILD=ON -DREALM_BUILD_LIB_ONLY=ON -DREALM_ENABLE_ENCRYPTION=OFF \
  -DREALM_ENABLE_SYNC=OFF -DREALM_NO_TESTS=ON -DREALM_ENABLE_GEOSPATIAL=ON \
  -DZLIB_LIBRARY=$ZL -DZLIB_INCLUDE_DIR=/var/www/osu/native/zlib-inc \
  -DBUILD_SHARED_LIBS=OFF
cd $B && rm -f realm-wrappers.a && emar -M < merge.mri && cp realm-wrappers.a /var/www/osu/osu/osu.Web/Native/
