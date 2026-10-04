// SPDX-License-Identifier: 0BSD
// Upstream Echo uses two native calls to avoid a known interpreter/compiler issue.
// These natives inspect architecture and print a fixed debugger message; no I/O ports.
#include <pawnio.inc>

NTSTATUS:main() {
    new CPUArch:arch = get_arch();
    debug_print(''ThinkBook module load test; arch %d'', _:arch);
    if (arch != ARCH_X64)
        return STATUS_NOT_SUPPORTED;
    return STATUS_SUCCESS;
}
