#!/bin/sh
# Builds the libSDL3.so.0 that a Linux release ships beside the program, from SDL's official source release.
#   ./tools/linux/build_sdl3.sh linux-arm64 [aarch64-linux-gnu]      # a native build on this machine (any architecture)
#   docker run --rm -v "$PWD:/work" -w /work ubuntu:22.04 sh tools/linux/build_sdl3.sh src/Darklands.Host.Sdl/Native/linux-x64
#
# The first argument is the folder under src/Darklands.Host.Sdl/Native that receives the library; it also names the
# RuntimeIdentifier whose RID the host copies into a publish (Darklands.Host.Sdl.csproj), so use the same spelling:
#   linux-x64 | linux-arm64 | linux-arm | win-x64
# An optional second argument is a CMake toolchain prefix for a cross build (for example aarch64-linux-gnu with a
# matching toolchain file); without it the library is built for the running machine.
#
# Built on Ubuntu 22.04 (glibc 2.35) so that it loads on Ubuntu 22.04 and later, Debian 12 and later and SteamOS; SDL opens
# its video and sound back ends (X11, Wayland, PipeWire, PulseAudio, ALSA, ...) at run time, so the system needs none of
# them beyond what it uses. The version and the archive's SHA-256 match the Windows SDL3.dll (THIRD_PARTY_NOTICES.md).
set -eu
VERSION=3.4.20
SHA256=f3bb06f55bed9557227a700af2ea62e49497ebdf58037732a731a5fb3ef8581f
OUT=${1:?usage: build_sdl3.sh <Native output folder, e.g. linux-arm64> [cross toolchain prefix]}
CROSS=${2:-}

case "$OUT" in
  */Native/*) ;;
  *) echo "the output folder is named under src/Darklands.Host.Sdl/Native, for example linux-arm64 (got: $OUT)" >&2; exit 2 ;;
esac

mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends ca-certificates curl build-essential cmake ninja-build pkg-config \
  libasound2-dev libpulse-dev libpipewire-0.3-dev libx11-dev libxext-dev libxrandr-dev libxcursor-dev libxfixes-dev \
  libxi-dev libxss-dev libxtst-dev libxkbcommon-dev libwayland-dev wayland-protocols libdecor-0-dev libdrm-dev libgbm-dev \
  libegl1-mesa-dev libgl1-mesa-dev libgles2-mesa-dev libdbus-1-dev libudev-dev libibus-1.0-dev >/dev/null

WORK=$(mktemp -d)
cd "$WORK"
curl -fsSL -o sdl.tar.gz "https://github.com/libsdl-org/SDL/releases/download/release-$VERSION/SDL3-$VERSION.tar.gz"
echo "$SHA256  sdl.tar.gz" | sha256sum -c -
tar -xzf sdl.tar.gz

# XTEST and XRECORD are optional (SDL_X11_XTEST, SDL_X11_XRECORD); they are off because their headers are missing from some
# minimal build images and nothing in the game uses them. A cross build adds CMAKE_SYSTEM_PROCESSOR through a toolchain file.
CMAKE_FLAGS="-DCMAKE_BUILD_TYPE=Release -DSDL_SHARED=ON -DSDL_STATIC=OFF -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF -DSDL_INSTALL=OFF \
  -DSDL_RPATH=OFF -DSDL_X11_XTEST=OFF -DSDL_X11_XRECORD=OFF"
if [ -n "$CROSS" ]; then
  cat > toolchain.cmake <<EOF
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR ${CROSS#*-})
set(CMAKE_C_COMPILER ${CROSS}-gcc)
set(CMAKE_CXX_COMPILER ${CROSS}-g++)
EOF
  CMAKE_FLAGS="$CMAKE_FLAGS -DCMAKE_TOOLCHAIN_FILE=$WORK/toolchain.cmake"
fi

cmake -S "SDL3-$VERSION" -B build -G Ninja $CMAKE_FLAGS
cmake --build build
# the soname file itself, not the symlink chain; stripped of its symbols for debugging only
cp -L build/libSDL3.so.0 "$OUT/libSDL3.so.0"
strip --strip-unneeded "$OUT/libSDL3.so.0"
file "$OUT/libSDL3.so.0"
sha256sum "$OUT/libSDL3.so.0"
# the newest glibc symbol it needs (the oldest system it loads on)
objdump -T "$OUT/libSDL3.so.0" | grep -o 'GLIBC_[0-9.]*' | sort -t. -k2,2n -k3,3n -u | tail -1
