// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
#include "internal.h"

VOID
RingInitialize(
    _Out_ PRING_BUFFER Ring,
    _In_ PUCHAR Storage,
    _In_ size_t Capacity
    )
{
    Ring->Data = Storage;
    Ring->Capacity = Capacity;
    Ring->Head = 0;
    Ring->Count = 0;
}

size_t
RingWrite(
    _Inout_ PRING_BUFFER Ring,
    _In_reads_bytes_(Length) const UCHAR *Source,
    _In_ size_t Length
    )
{
    size_t toCopy = min(Length, RingFree(Ring));
    size_t tail = (Ring->Head + Ring->Count) % Ring->Capacity;
    size_t first = min(toCopy, Ring->Capacity - tail);

    CopyMemory(Ring->Data + tail, Source, first);
    CopyMemory(Ring->Data, Source + first, toCopy - first);
    Ring->Count += toCopy;
    return toCopy;
}

size_t
RingRead(
    _Inout_ PRING_BUFFER Ring,
    _Out_writes_bytes_to_(Length, return) PUCHAR Destination,
    _In_ size_t Length
    )
{
    size_t toCopy = min(Length, Ring->Count);
    size_t first = min(toCopy, Ring->Capacity - Ring->Head);

    CopyMemory(Destination, Ring->Data + Ring->Head, first);
    CopyMemory(Destination + first, Ring->Data, toCopy - first);
    Ring->Head = (Ring->Head + toCopy) % Ring->Capacity;
    Ring->Count -= toCopy;
    if (Ring->Count == 0) {
        Ring->Head = 0;
    }
    return toCopy;
}
