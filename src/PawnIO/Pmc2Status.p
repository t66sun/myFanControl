// SPDX-License-Identifier: 0BSD
// Host must validate 21CX/HYCN42WW, EC identity and PMC2 configuration
// and acquire the existing ISA mutex before invoking this function.
#include <pawnio.inc>

DEFINE_IOCTL_SIZED(ioctl_read_status, 0, 1) {
    out[0] = io_in_byte(0x6C);
    return STATUS_SUCCESS;
}

NTSTATUS:main() {
    new CPUArch:arch = get_arch();
    debug_print("ThinkBook fixed PMC2 status module; arch %d", _:arch);
    if (arch != ARCH_X64)
        return STATUS_NOT_SUPPORTED;
    return STATUS_SUCCESS;
}
