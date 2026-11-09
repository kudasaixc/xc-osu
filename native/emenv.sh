D=$HOME/.dotnet/packs
export DOTNET_EMSCRIPTEN_LLVM_ROOT=$D/Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.linux-x64/10.0.14/tools/bin
export DOTNET_EMSCRIPTEN_BINARYEN_ROOT=$D/Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.linux-x64/10.0.14/tools
export DOTNET_EMSCRIPTEN_NODE_JS=$(ls $D/Microsoft.NET.Runtime.Emscripten.3.1.56.Node.linux-x64/10.0.14/tools/bin/node)
export EM_CONFIG=$D/Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.linux-x64/10.0.14/tools/emscripten/.emscripten
export EM_CACHE=$D/Microsoft.NET.Runtime.Emscripten.3.1.56.Cache.linux-x64/10.0.14/tools/emscripten/cache
export PATH=$D/Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.linux-x64/10.0.14/tools/emscripten:$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/venv/bin:$PATH
