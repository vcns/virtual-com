// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
/*
    Fixed-size byte ring buffer. Not thread-safe; callers hold the device lock.
*/
#pragma once

typedef struct _RING_BUFFER {
    PUCHAR  Data;
    size_t  Capacity;
    size_t  Head;       // index of the oldest byte
    size_t  Count;      // bytes stored
} RING_BUFFER, *PRING_BUFFER;

VOID RingInitialize(_Out_ PRING_BUFFER Ring, _In_ PUCHAR Storage, _In_ size_t Capacity);

// Copies up to Length bytes in; returns how many fitted.
size_t RingWrite(_Inout_ PRING_BUFFER Ring, _In_reads_bytes_(Length) const UCHAR *Source, _In_ size_t Length);

// Copies up to Length bytes out; returns how many were available.
size_t RingRead(_Inout_ PRING_BUFFER Ring, _Out_writes_bytes_to_(Length, return) PUCHAR Destination, _In_ size_t Length);

FORCEINLINE size_t RingCount(_In_ const RING_BUFFER *Ring) { return Ring->Count; }
FORCEINLINE size_t RingFree(_In_ const RING_BUFFER *Ring) { return Ring->Capacity - Ring->Count; }
FORCEINLINE VOID RingClear(_Inout_ PRING_BUFFER Ring) { Ring->Head = 0; Ring->Count = 0; }
