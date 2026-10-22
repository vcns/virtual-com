// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
/*
    Public interface of the virtual COM port driver, shared with user-mode applications.

    Each virtual port is one device with two ends:
      - the COM end, opened by ordinary serial applications as \\.\COMn; and
      - the host end, opened by the application that supplies the data (for example a TCP bridge) through the
        GUID_DEVINTERFACE_VCOM_HOST device interface, whose path ends in "\HOST".

    Bytes written to one end are read from the other, unchanged. The host end reads and writes like a pipe: a read
    completes as soon as at least one byte is available. While the host end is open, the COM end sees CTS, DSR
    and DCD asserted (the host can change this with IOCTL_VCOM_SET_MODEM_STATUS).
*/
#pragma once

// {13DBB15D-28F8-4AEB-81B8-C45B6F98B810}
DEFINE_GUID(GUID_DEVINTERFACE_VCOM_HOST, 0x13dbb15d, 0x28f8, 0x4aeb, 0x81, 0xb8, 0xc4, 0x5b, 0x6f, 0x98, 0xb8, 0x10);

#define VCOM_HOST_REFERENCE_STRING L"HOST"

// Host-end control codes (function codes 0x900 and up are vendor-defined).
#define IOCTL_VCOM_GET_PORT_STATE    CTL_CODE(FILE_DEVICE_SERIAL_PORT, 0x900, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_VCOM_SET_MODEM_STATUS  CTL_CODE(FILE_DEVICE_SERIAL_PORT, 0x901, METHOD_BUFFERED, FILE_WRITE_ACCESS)

// VCOM_PORT_STATE.Flags
#define VCOM_STATE_COM_OPEN   0x00000001  // an application has the COM end open
#define VCOM_STATE_DTR        0x00000002  // the application has raised DTR
#define VCOM_STATE_RTS        0x00000004  // the application has raised RTS
#define VCOM_STATE_BREAK      0x00000008  // the application is holding a break condition

// Output of IOCTL_VCOM_GET_PORT_STATE. The serial settings are whatever the COM-end application last set; they
// don't affect the data, which always passes through unchanged.
typedef struct _VCOM_PORT_STATE {
    ULONG  Size;                // sizeof(VCOM_PORT_STATE)
    ULONG  Flags;               // VCOM_STATE_*
    ULONG  BaudRate;
    UCHAR  DataBits;            // 5-8
    UCHAR  Parity;              // NO_PARITY, ODD_PARITY, EVEN_PARITY, MARK_PARITY, SPACE_PARITY
    UCHAR  StopBits;            // STOP_BIT_1, STOP_BITS_1_5, STOP_BITS_2
    UCHAR  Reserved;
    ULONG  BytesWaitingForCom;  // written by the host, not yet read by the application
    ULONG  BytesWaitingForHost; // written by the application, not yet read by the host
    WCHAR  PortName[16];        // e.g. L"COM9"
} VCOM_PORT_STATE;

// Modem-status lines as the COM end sees them, in the bit positions GetCommModemStatus reports
// (MS_CTS_ON, MS_DSR_ON, MS_RING_ON, MS_RLSD_ON).
#define VCOM_LINE_CTS   0x10
#define VCOM_LINE_DSR   0x20
#define VCOM_LINE_RI    0x40
#define VCOM_LINE_DCD   0x80

// Input of IOCTL_VCOM_SET_MODEM_STATUS: a ULONG combination of VCOM_LINE_* flags. Other bits are ignored.
