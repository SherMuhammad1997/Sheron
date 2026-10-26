#!/usr/bin/env bash
# Rebuilds third_party/libvpl/libvpl.dll: the Intel oneVPL dispatcher (MIT, see LICENSE), v2.16.0,
# cross-compiled for Windows x64 with MinGW on Ubuntu 24.04. Only the dispatcher is built (no tests,
# no examples); the MinGW runtime is linked in, so the DLL needs only Windows system DLLs.
#
#   sudo apt-get install g++-mingw-w64-x86-64-posix gcc-mingw-w64-x86-64-posix mingw-w64-x86-64-dev cmake git
#   third_party/libvpl/build-libvpl.sh <work folder>
set -euo pipefail

work="${1:?usage: build-libvpl.sh <work folder>}"
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$work"
cd "$work"

git clone --depth 1 --branch v2.16.0 https://github.com/intel/libvpl src
test "$(git -C src rev-parse HEAD)" = "778a66d6c6537f08eabb91955dbbf1bce3812894"

cat > mingw64.cmake <<'EOF'
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR x86_64)
set(CMAKE_C_COMPILER x86_64-w64-mingw32-gcc-posix)
set(CMAKE_CXX_COMPILER x86_64-w64-mingw32-g++-posix)
set(CMAKE_RC_COMPILER x86_64-w64-mingw32-windres)
set(CMAKE_FIND_ROOT_PATH /usr/x86_64-w64-mingw32)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
EOF

cmake -S src -B build -DCMAKE_TOOLCHAIN_FILE="$work/mingw64.cmake" -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=ON -DBUILD_TESTS=OFF -DBUILD_EXAMPLES=OFF -DINSTALL_EXAMPLES=OFF \
    -DCMAKE_SHARED_LINKER_FLAGS="-static -static-libgcc -static-libstdc++"
cmake --build build -j"$(nproc)"
x86_64-w64-mingw32-strip --strip-unneeded -o "$here/libvpl.dll" build/libvpl.dll

# Check the struct layouts the C# code relies on (VplApi.cs) against these headers, under the Windows x64 ABI.
gcc -I src/api "$here/vpl-layout.c" -o vpl-layout
./vpl-layout | diff - "$here/vpl-layout.txt"
echo "libvpl.dll rebuilt; layouts unchanged."
