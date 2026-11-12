#!/bin/sh
# Runs the Unity 6 Linux editor inside this sandbox (Nix based, no FHS):
# libraries come from the steam-run FHS rootfs, audio goes to an ALSA null device.
RF=$(ls -d /nix/store/*steam-run*fhsenv-rootfs | head -1)
ED=/opt/unity/editor/Editor
if [ ! -x "$ED/Unity.run" ]; then
  cp "$ED/Unity" "$ED/Unity.run"
  PE=$(nix build --no-link --print-out-paths nixpkgs#patchelf)/bin/patchelf
  "$PE" --set-interpreter "$RF/usr/lib64/ld-linux-x86-64.so.2" "$ED/Unity.run"
fi
# Shader compiler: its PE loader for D3DCompiler_47.dll needs a GS segment base, which gVisor sandboxes refuse
# (arch_prctl ARCH_SET_GS -> EINVAL, then SIGSEGV in PESetupFS / "failed to read magic number" in the editor).
# Run it under qemu user-mode emulation, which handles the GS base itself.
SC="$ED/Data/Tools/UnityShaderCompiler"
TOOLS="$(dirname "$(readlink -f "$0")")"
if [ ! -x "$SC.real" ]; then
  PE=$(nix build --no-link --print-out-paths nixpkgs#patchelf)/bin/patchelf
  QEMU=$(nix build --no-link --print-out-paths nixpkgs#qemu-user)/bin/qemu-x86_64
  cp "$SC" "$SC.orig"; cp "$SC" "$SC.real"
  "$PE" --set-interpreter "$RF/usr/lib64/ld-linux-x86-64.so.2" "$SC.real"
  printf '#!/bin/sh\nLDP="$LD_LIBRARY_PATH"\nunset LD_LIBRARY_PATH LD_PRELOAD\nexec %s -E LD_LIBRARY_PATH="$LDP" -E LD_PRELOAD=%s/nopi_nogs.so %s.real "$@"\n' "$QEMU" "$TOOLS" "$SC" > "$SC"
  chmod +x "$SC"
fi
[ -f /root/.asoundrc ] || printf 'pcm.!default {\n  type null\n}\nctl.!default {\n  type hw\n  card 0\n}\n' > /root/.asoundrc
export HOME=/root
export ALSA_CONFIG_PATH="$RF/usr/share/alsa/alsa.conf"
export LD_LIBRARY_PATH="$ED:$RF/usr/lib64"
# software rendering (Mesa llvmpipe / lavapipe) for screenshots and builds on a virtual X display
MESA=$(ls -d /nix/store/*-mesa-[0-9]*/ 2>/dev/null | grep -v -E "dev|man|doc" | head -1)
MESA=${MESA%/}
export LD_LIBRARY_PATH="$LD_LIBRARY_PATH:$MESA/lib"
export __GLX_VENDOR_LIBRARY_NAME=mesa
export LIBGL_DRIVERS_PATH="$MESA/lib/dri"
export __EGL_VENDOR_LIBRARY_DIRS="$MESA/share/glvnd/egl_vendor.d"
export VK_ICD_FILENAMES="$MESA/share/vulkan/icd.d/lvp_icd.x86_64.json"
export LIBGL_ALWAYS_SOFTWARE=1
export DISPLAY=${DISPLAY:-:99}
cd /opt/work 2>/dev/null || cd /tmp
export LD_PRELOAD="$(dirname "$(readlink -f "$0")")/nopi.so${LD_PRELOAD:+:$LD_PRELOAD}"
exec $WRAP "$ED/Unity.run" "$@"
