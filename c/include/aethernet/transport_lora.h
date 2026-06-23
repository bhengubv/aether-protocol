// SPDX-License-Identifier: MIT
// LoRa (Aether Red / CircleLink) transport over a serial-attached RYLR-class
// SX127x/SX126x module. Mirrors the C#, Go, and Rust LoRaSerialTransport.
//
// POSIX implementation (termios + pthread), matching the rest of the C library.
// Verification status: real driver; compiles on the library's Linux/macOS toolchain
// (same as CI). Runtime-UNVERIFIED — not exercised against a physical module.

#ifndef AETHERNET_TRANSPORT_LORA_H
#define AETHERNET_TRANSPORT_LORA_H

#include <stdint.h>
#include <stdbool.h>
#include "transport.h"

#ifdef __cplusplus
extern "C" {
#endif

/** Configuration for a RYLR-class serial LoRa module. */
typedef struct {
    const char *port_name;     /* "/dev/ttyUSB0" — required. */
    int         baud_rate;     /* 0 => 115200. */
    uint16_t    address;       /* this node's LoRa address (1-65535); 0 => 1. */
    int         network_id;    /* RYLR network id; 0 => 18. */
    long long   band_hz;       /* EU868=868500000, US915=915000000; 0 => EU868. */
    int         spreading_factor; /* 7-12; 0 => 9. */
    int         bandwidth_index;  /* 7=125kHz,8=250,9=500; 0 => 7. */
    int         coding_rate;      /* 1=4/5; 0 => 1. */
    int         preamble_length;  /* 0 => 12. */
} aethernet_lora_options_t;

/**
 * Create a LoRa serial transport: opens the port, configures the radio, and starts
 * a reader thread. Returns NULL if the port cannot be opened.
 * Free with aethernet_transport_destroy().
 */
aethernet_transport_t *aethernet_lora_transport_new(const aethernet_lora_options_t *options);

/** Maps a peer UHID to a numeric LoRa node address (1-65535) for directed sends. */
bool aethernet_lora_transport_register_peer(aethernet_transport_t *transport,
                                            const char *uhid,
                                            uint16_t address);

#ifdef __cplusplus
}
#endif

#endif /* AETHERNET_TRANSPORT_LORA_H */
