// SPDX-License-Identifier: 0BSD
// Fixed query-only port operations. Host holds ISA mutex and validates this
// machine's EC identity/mapping and active PMC2 68/6C configuration first.
#include <pawnio.inc>

DEFINE_IOCTL(ioctl_read_status) {
    if (in_size != 0 || out_size != 1) return STATUS_INVALID_PARAMETER;
    out[0] = io_in_byte(0x6C);
    return STATUS_SUCCESS;
}
DEFINE_IOCTL(ioctl_read_data) {
    if (in_size != 0 || out_size != 1) return STATUS_INVALID_PARAMETER;
    out[0] = io_in_byte(0x68);
    return STATUS_SUCCESS;
}
DEFINE_IOCTL(ioctl_query_command) {
    if (in_size != 1 || out_size != 0 || in[0] != 0xD5) return STATUS_INVALID_PARAMETER;
    io_out_byte(0x6C, 0xD5);
    return STATUS_SUCCESS;
}
DEFINE_IOCTL(ioctl_query_parameter) {
    if (in_size != 1 || out_size != 0) return STATUS_INVALID_PARAMETER;
    if (in[0] != 0x12 && in[0] != 0x18 && in[0] != 0x19) return STATUS_INVALID_PARAMETER;
    io_out_byte(0x68, in[0]);
    return STATUS_SUCCESS;
}
NTSTATUS:main() {
    new CPUArch:arch = get_arch();
    debug_print("ThinkBook fixed PMC2 query module; arch %d", _:arch);
    if (arch != ARCH_X64) return STATUS_NOT_SUPPORTED;
    return STATUS_SUCCESS;
}
