#!/bin/bash
# xc-osu!: one-shot build environment for a dev PC (WSL2 Ubuntu 22.04/24.04). xc softworks (kudasaixc)
#
#   Windows:  wsl --install -d Ubuntu-24.04     (then open Ubuntu)
#   Ubuntu:   git clone https://github.com/kudasaixc/xc-osu ~/xc-osu && bash ~/xc-osu/tools/setup-wsl.sh
#
# Keep the repo inside the Linux filesystem (~/), NOT under /mnt/c: builds are ~10x slower there.
# Afterwards:
#   tools/devpub.sh                               # interpreter build  -> out/dev/wwwroot
#   tools/aotpub.sh                               # mixed-AOT build    -> out/aot/wwwroot
#   XC_DEPLOY_SSH=admin@<vps> tools/deploy.sh aot # push it live
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
sudo apt-get update
sudo apt-get install -y build-essential python3 python3-venv rsync curl git ca-certificates libicu-dev
if [ ! -x "$HOME/.dotnet/dotnet" ]; then
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --version 10.0.403 --install-dir "$HOME/.dotnet" || bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
fi
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1
grep -q 'DOTNET_ROOT' ~/.bashrc || echo 'export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1' >> ~/.bashrc
dotnet workload install wasm-tools wasm-experimental --skip-manifest-update || dotnet workload install wasm-tools wasm-experimental
# give WSL enough RAM for parallel AOT (Windows side: %UserProfile%\.wslconfig)
echo
echo "Tip: create C:\\Users\\<you>\\.wslconfig with:"
echo "  [wsl2]"
echo "  memory=24GB"
echo "  processors=12"
echo "then run 'wsl --shutdown' once."
echo
cd "$ROOT/osu/osu.Web" && dotnet restore
echo "xc-osu! build env ready. Run: $ROOT/tools/aotpub.sh"
