// SPDX-License-Identifier: MIT
// LoRa (Aether Red / CircleLink) transport over a serial-attached RYLR-class
// SX127x/SX126x module. POSIX (termios + pthread). Mirrors the C#/Go/Rust drivers.
//
// Verification status: real driver; compiles on the library's Linux/macOS toolchain.
// Runtime-UNVERIFIED — not exercised against a physical module.

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <stdint.h>
#include <stdbool.h>
#include <pthread.h>
#include <unistd.h>
#include <fcntl.h>
#include <termios.h>

#include "aethernet/transport.h"
#include "aethernet/transport_lora.h"

#define LORA_MAX_PEERS 256
#define LORA_LINE_MAX  1024

typedef struct {
    char     uhid[128];
    uint16_t address;
} lora_peer_t;

typedef struct {
    int   fd;
    bool  available;
    aethernet_lora_options_t opts;

    aethernet_transport_on_data_received callback;
    void *user_data;

    lora_peer_t peers[LORA_MAX_PEERS];
    int         peer_count;

    pthread_t reader;
    bool      reader_started;
    bool      reader_running;

    aethernet_transport_metrics_t metrics;
    pthread_mutex_t lock;
} lora_state_t;

// ── helpers ──────────────────────────────────────────────────────────────────

static speed_t baud_to_speed(int baud) {
    switch (baud) {
        case 9600:   return B9600;
        case 19200:  return B19200;
        case 38400:  return B38400;
        case 57600:  return B57600;
        case 115200: return B115200;
        default:     return B115200;
    }
}

static void write_line(int fd, const char *s) {
    (void)write(fd, s, strlen(s));
    (void)write(fd, "\r\n", 2);
}

static const char HEX[] = "0123456789ABCDEF";

static char *hex_encode(const uint8_t *data, size_t len) {
    char *out = (char *)malloc(len * 2 + 1);
    if (!out) return NULL;
    for (size_t i = 0; i < len; i++) {
        out[i * 2]     = HEX[(data[i] >> 4) & 0xF];
        out[i * 2 + 1] = HEX[data[i] & 0xF];
    }
    out[len * 2] = '\0';
    return out;
}

static int hex_val(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return -1;
}

static uint8_t *hex_decode(const char *s, size_t *out_len) {
    size_t n = strlen(s);
    if (n == 0 || n % 2 != 0) return NULL;
    uint8_t *out = (uint8_t *)malloc(n / 2);
    if (!out) return NULL;
    for (size_t i = 0; i < n; i += 2) {
        int hi = hex_val(s[i]);
        int lo = hex_val(s[i + 1]);
        if (hi < 0 || lo < 0) { free(out); return NULL; }
        out[i / 2] = (uint8_t)((hi << 4) | lo);
    }
    *out_len = n / 2;
    return out;
}

// RYLR inbound frame: +RCV=<address>,<length>,<hexdata>,<rssi>,<snr>
static void handle_line(lora_state_t *st, char *line) {
    if (strncmp(line, "+RCV=", 5) != 0) return;
    char *saveptr = NULL;
    char *addr_s = strtok_r(line + 5, ",", &saveptr);
    char *len_s  = strtok_r(NULL, ",", &saveptr);
    char *hex_s  = strtok_r(NULL, ",", &saveptr);
    if (!addr_s || !len_s || !hex_s) return;

    long addr = strtol(addr_s, NULL, 10);
    size_t data_len = 0;
    uint8_t *data = hex_decode(hex_s, &data_len);
    if (!data) return;

    pthread_mutex_lock(&st->lock);
    aethernet_transport_on_data_received cb = st->callback;
    void *ud = st->user_data;
    pthread_mutex_unlock(&st->lock);

    if (cb) {
        char sender[32];
        snprintf(sender, sizeof(sender), "%ld", addr);
        cb(sender, data, data_len, ud);
    }
    free(data);
}

static void *reader_thread(void *arg) {
    lora_state_t *st = (lora_state_t *)arg;
    char line[LORA_LINE_MAX];
    size_t pos = 0;
    while (st->reader_running) {
        char c;
        ssize_t r = read(st->fd, &c, 1);
        if (r < 0) break;       // port closed / fatal
        if (r == 0) continue;   // VTIME timeout, no data — re-check reader_running
        if (c == '\n' || c == '\r') {
            if (pos > 0) {
                line[pos] = '\0';
                handle_line(st, line);
                pos = 0;
            }
        } else if (pos < LORA_LINE_MAX - 1) {
            line[pos++] = c;
        } else {
            pos = 0; // overflow guard on line noise
        }
    }
    return NULL;
}

// ── vtable ───────────────────────────────────────────────────────────────────

static bool lora_send(void *handle, const char *peer_uhid, const uint8_t *data, size_t data_len) {
    lora_state_t *st = (lora_state_t *)handle;
    if (!st || !data || data_len == 0) return false;

    pthread_mutex_lock(&st->lock);
    if (!st->available) { pthread_mutex_unlock(&st->lock); return false; }
    uint16_t addr = 0; // 0 = broadcast
    if (peer_uhid) {
        for (int i = 0; i < st->peer_count; i++) {
            if (strcmp(st->peers[i].uhid, peer_uhid) == 0) { addr = st->peers[i].address; break; }
        }
    }
    int fd = st->fd;
    pthread_mutex_unlock(&st->lock);

    char *hex = hex_encode(data, data_len);
    if (!hex) return false;
    char cmd[LORA_LINE_MAX];
    int n = snprintf(cmd, sizeof(cmd), "AT+SEND=%u,%zu,%s\r\n",
                     (unsigned)addr, strlen(hex), hex);
    bool ok = (n > 0 && (size_t)n < sizeof(cmd) && write(fd, cmd, (size_t)n) == (ssize_t)n);
    free(hex);
    aethernet_transport_metrics_record_sample(&st->metrics, 0, ok, ok ? data_len : 0);
    return ok;
}

static bool lora_is_connected(void *handle, const char *peer_uhid) {
    (void)peer_uhid;
    lora_state_t *st = (lora_state_t *)handle;
    return st && st->available; // connectionless broadcast medium
}

static void lora_set_on_data_received(void *handle,
                                      aethernet_transport_on_data_received callback,
                                      void *user_data) {
    lora_state_t *st = (lora_state_t *)handle;
    if (!st) return;
    pthread_mutex_lock(&st->lock);
    st->callback = callback;
    st->user_data = user_data;
    pthread_mutex_unlock(&st->lock);
}

static aethernet_transport_metrics_t *lora_get_metrics(void *handle) {
    lora_state_t *st = (lora_state_t *)handle;
    return st ? &st->metrics : NULL;
}

static void lora_destroy(void *handle) {
    lora_state_t *st = (lora_state_t *)handle;
    if (!st) return;

    pthread_mutex_lock(&st->lock);
    st->available = false;
    st->reader_running = false;
    int fd = st->fd;
    st->fd = -1;
    pthread_mutex_unlock(&st->lock);

    if (fd >= 0) close(fd);            // wakes a blocked read with an error
    if (st->reader_started) pthread_join(st->reader, NULL);

    pthread_mutex_destroy(&st->lock);
    free(st);
}

// ── construction ─────────────────────────────────────────────────────────────

static void apply_defaults(aethernet_lora_options_t *o) {
    if (o->baud_rate == 0)        o->baud_rate = 115200;
    if (o->address == 0)          o->address = 1;
    if (o->network_id == 0)       o->network_id = 18;
    if (o->band_hz == 0)          o->band_hz = 868500000LL;
    if (o->spreading_factor == 0) o->spreading_factor = 9;
    if (o->bandwidth_index == 0)  o->bandwidth_index = 7;
    if (o->coding_rate == 0)      o->coding_rate = 1;
    if (o->preamble_length == 0)  o->preamble_length = 12;
}

aethernet_transport_t *aethernet_lora_transport_new(const aethernet_lora_options_t *options) {
    if (!options || !options->port_name) return NULL;

    int fd = open(options->port_name, O_RDWR | O_NOCTTY);
    if (fd < 0) return NULL;

    struct termios tio;
    if (tcgetattr(fd, &tio) != 0) { close(fd); return NULL; }
    cfmakeraw(&tio);
    speed_t spd = baud_to_speed(options->baud_rate ? options->baud_rate : 115200);
    cfsetispeed(&tio, spd);
    cfsetospeed(&tio, spd);
    tio.c_cflag |= (CLOCAL | CREAD);
    tio.c_cc[VMIN]  = 0;   // non-blocking-ish: return after VTIME even with no data
    tio.c_cc[VTIME] = 10;  // 1.0s read timeout so the reader thread can poll its run flag
    if (tcsetattr(fd, TCSANOW, &tio) != 0) { close(fd); return NULL; }

    lora_state_t *st = (lora_state_t *)calloc(1, sizeof(lora_state_t));
    if (!st) { close(fd); return NULL; }
    st->fd = fd;
    st->opts = *options;
    apply_defaults(&st->opts);
    pthread_mutex_init(&st->lock, NULL);
    aethernet_transport_metrics_init(&st->metrics);

    // Configure the radio.
    char cmd[128];
    snprintf(cmd, sizeof(cmd), "AT+ADDRESS=%u", (unsigned)st->opts.address);   write_line(fd, cmd);
    snprintf(cmd, sizeof(cmd), "AT+NETWORKID=%d", st->opts.network_id);        write_line(fd, cmd);
    snprintf(cmd, sizeof(cmd), "AT+BAND=%lld", st->opts.band_hz);              write_line(fd, cmd);
    snprintf(cmd, sizeof(cmd), "AT+PARAMETER=%d,%d,%d,%d",
             st->opts.spreading_factor, st->opts.bandwidth_index,
             st->opts.coding_rate, st->opts.preamble_length);                 write_line(fd, cmd);

    st->available = true;
    st->reader_running = true;
    if (pthread_create(&st->reader, NULL, reader_thread, st) == 0) {
        st->reader_started = true;
    }

    aethernet_transport_t *transport =
        (aethernet_transport_t *)malloc(sizeof(aethernet_transport_t));
    aethernet_transport_vtable_t *vtable =
        (aethernet_transport_vtable_t *)malloc(sizeof(aethernet_transport_vtable_t));
    if (!transport || !vtable) {
        free(transport);
        free(vtable);
        st->reader_running = false;
        close(fd);
        if (st->reader_started) pthread_join(st->reader, NULL);
        pthread_mutex_destroy(&st->lock);
        free(st);
        return NULL;
    }

    memset(vtable, 0, sizeof(*vtable));
    vtable->name = "Aether Red (LoRa/CircleLink)";
    vtable->send = lora_send;
    vtable->is_connected = lora_is_connected;
    vtable->set_on_data_received = lora_set_on_data_received;
    vtable->destroy = lora_destroy;
    vtable->get_metrics = lora_get_metrics;
    vtable->max_bandwidth_bps = 37500;   // SF7/BW125 ≈ 37.5 kbps
    vtable->power_cost_relative = 50;    // high TX power
    vtable->max_range_meters = 15000;    // up to ~15 km LOS

    transport->vtable = vtable;
    transport->handle = st;
    return transport;
}

bool aethernet_lora_transport_register_peer(aethernet_transport_t *transport,
                                            const char *uhid,
                                            uint16_t address) {
    if (!transport || !uhid) return false;
    lora_state_t *st = (lora_state_t *)transport->handle;
    if (!st) return false;

    pthread_mutex_lock(&st->lock);
    bool ok = false;
    if (st->peer_count < LORA_MAX_PEERS) {
        lora_peer_t *p = &st->peers[st->peer_count];
        strncpy(p->uhid, uhid, sizeof(p->uhid) - 1);
        p->uhid[sizeof(p->uhid) - 1] = '\0';
        p->address = address;
        st->peer_count++;
        ok = true;
    }
    pthread_mutex_unlock(&st->lock);
    return ok;
}
