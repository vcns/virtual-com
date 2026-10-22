// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
/*
    Instantiates the GUIDs the driver uses. Kept apart from internal.h because winioctl.h and ntddser.h both
    define GUID_DEVINTERFACE_COMPORT, which can be initialised only once per file.
*/
#include <initguid.h>
#include <windows.h>    // winioctl.h: GUID_DEVINTERFACE_COMPORT
#include "vcom.h"       // GUID_DEVINTERFACE_VCOM_HOST
