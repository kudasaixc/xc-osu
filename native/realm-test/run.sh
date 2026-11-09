#!/bin/bash
# xc-osu! — fast native realm-core check under node (no .NET rebuild needed). xc softworks (kudasaixc)
set -e
source /var/www/osu/native/emenv.sh >/dev/null
R=/var/www/osu/native; D=$(mktemp -d)
ZL=$(find ~/.dotnet/packs -name libz.a | grep multithread | head -1)
em++ -O1 -pthread -fwasm-exceptions -std=c++17 -I$R/realm-dotnet/wrappers/realm-core/src -I$R/realm-build/realm-core/src \
  ${1:-$R/realm-test/concurrency.cpp} $R/realm-build/realm-core/src/realm/librealm.a $ZL \
  -sEXIT_RUNTIME -sALLOW_MEMORY_GROWTH -sPTHREAD_POOL_SIZE=8 -sPROXY_TO_PTHREAD -o $D/t.js
timeout -s KILL 30 node $D/t.js
