# Draft GitHub issue — not submitted

Target repository: https://github.com/namazso/PawnIO.Modules

Title: Supported signed module for ITE PMC2 at 0x68/0x6C on ThinkBook 14 G4+ (21CX)

## Request

Is there a supported signed module for accessing the second ITE PMC channel at data port 0x68 and status/command port 0x6C? If none exists, would a restricted PMC2 query module be appropriate for this repository? I can contribute a small implementation following the maintainers' preferred API and supply local test results.

## Measured configuration

Windows 11, Lenovo ThinkBook 14 G4+ IAP, machine type 21CX, BIOS HYCN42WW. PawnIO 2.2.0.0 is already installed. One elevated configuration diagnostic through the existing signed LpcIO module reported:

| Item | Value |
| --- | --- |
| Direct and indirect EC chip ID | 0x5571 |
| Mapping register 0x1060, before/after | 0x00 |
| LDN 0x11 activate / bases | 0x01 / 0x62, 0x66 |
| LDN 0x12 activate / bases | 0x01 / 0x68, 0x6C |
| Original LDN | 0xFE, restored and read back |

No activation, base-address, mapping, fan-target or PMC command payload was written in that diagnostic. Selector changes were restored. Existing EC feedback reads through 4E/4F yield two plausible RPM channels; this does not establish PMC2 query/control behavior.

## Current module restriction

The signed LpcIO.bin embedded in LibreHardwareMonitor at commit 677a3a56abde9adff5abdb42db3a7638c9137572 has SHA256 b3896a1cab0d808fca31fe2ebcae045d59dac690da87b17c858bb8da357eb45e.

Offline decoding of that exact binary shows ioctl_pio_inb rejects 0x68/0x6C with STATUS_ACCESS_DENIED after selecting slot 1, with zero discovered BARs, before calling native I/O. Its BAR-addition path also rejects bases below 0x100. I have not scanned BARs or attempted to reconfigure the EC. The current LpcACPIEC source allows only 0x62/0x66.

A proposed elevated status probe was canceled before launch; there is no hardware execution result for 0x6C. The exact-binary result above is offline evidence, not a claimed driver error observed on hardware.

## Candidate protocol and proposed first step

Static analysis of the HYEC42WW candidate from Lenovo's HYCN42WW package shows a command dispatcher reading internal PM2STS/PM2DI (0x1510/0x1514) and responding through PM2DO (0x1511). The bank mapping and runtime reachability remain unverified.

The first intended operation would be a status-only read. A subsequent restricted query interface could permit command D5 with only these parameters:

| Parameter | Static candidate response |
| --- | --- |
| 0x12 | One byte from XDATA 0x8138 |
| 0x18 | First RPM word, low byte then high byte |
| 0x19 | Second RPM word, low byte then high byte |

This would write query request bytes to the PMC ports, but would not expose manual fan targets, PWM, automatic-mode changes or unrestricted I/O. User-mode code would gate the exact model/BIOS and EC configuration, use the required bus synchronization, reject pending output before a request, bound polling, and stop after a partial response or failed transaction. The appropriate synchronization with existing Windows/Lenovo drivers needs clarification; a user-mode mutex alone is not a guarantee against kernel clients.

No PMC2 commands have been executed. I am seeking the supported signed transport and review of the proposed scope before claiming hardware compatibility. I am not requesting an unrestricted driver or signature-check bypass.

## Available evidence

Locally retained: raw PNP configuration diagnostic; module hash and full instruction listing; candidate dispatch table and relevant instruction bytes; a pure in-memory query implementation with partial-response/fault checks. I can provide the relevant small extracts if useful, without serial numbers or a firmware image upload.
