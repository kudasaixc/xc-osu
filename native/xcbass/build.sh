#!/bin/bash
# xc-osu! xcbass build — produces Native/bass.a (+ module-name stubs) for osu.Web. xc softworks (kudasaixc)
set -e
cd "$(dirname "$0")"
source /var/www/osu/native/emenv.sh
OUT=build; mkdir -p $OUT
FLAGS="-O3 -pthread -fwasm-exceptions -msimd128 -ffast-math -fno-math-errno"
emcc $FLAGS -std=gnu11 -c xcbass.c -o $OUT/xcbass.o -Wall -Wno-unused-function
emcc $FLAGS -std=gnu11 -c decoders.c -o $OUT/decoders.o -w
em++ $FLAGS -std=c++17 -I third_party/signalsmith-stretch/include -I third_party/signalsmith-linear/include -I third_party -c stretch.cpp -o $OUT/stretch.o
rm -f $OUT/bass.a; emar rcs $OUT/bass.a $OUT/xcbass.o $OUT/decoders.o $OUT/stretch.o
DEST=/var/www/osu/osu/osu.Web/Native
cp $OUT/bass.a $DEST/bass.a
cp stubs/*.c $DEST/
echo "xcbass ok: $(du -h $OUT/bass.a | cut -f1)"
