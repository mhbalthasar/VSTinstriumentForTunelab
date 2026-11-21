/*
 * TuneLab VST Instruments: small native LV2 UI host built on Suil.
 *
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * The bridge process owns these toolkit windows.  They are deliberately
 * native Gtk3/Qt5 windows: the TuneLab host is an Avalonia application, and
 * borrowing an Avalonia Window for a plugin UI would mix two GUI toolkits in
 * one event loop.
 */
#ifndef TLVSTI_SUIL_UI_H
#define TLVSTI_SUIL_UI_H

#include <stdbool.h>
#include <stdint.h>

#include <lv2/core/lv2.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct TlvstiSuilUi TlvstiSuilUi;

typedef struct {
  void (*write)(void* controller,
                uint32_t port_index,
                uint32_t buffer_size,
                uint32_t protocol,
                const void* buffer);
  uint32_t (*index)(void* controller, const char* port_symbol);
  uint32_t (*subscribe)(void* controller,
                        uint32_t port_index,
                        uint32_t protocol,
                        const LV2_Feature* const* features);
  uint32_t (*unsubscribe)(void* controller,
                          uint32_t port_index,
                          uint32_t protocol,
                          const LV2_Feature* const* features);
  void (*touch)(void* controller, uint32_t port_index, bool grabbed);
} TlvstiSuilCallbacks;

typedef struct {
  /* NULL-terminated LV2_Feature array.  Borrowed for the duration of open. */
  const LV2_Feature* const* features;
  const TlvstiSuilCallbacks* callbacks;
  void* controller;
} TlvstiSuilOpenOptions;

/*
 * All functions return 0 on success.  On failure a human-readable reason is
 * written to error (when error_capacity is non-zero).
 */
int tlvsti_gtk2_open(const char* plugin_uri,
                     const char* ui_uri,
                     const char* ui_type_uri,
                     const char* bundle_path,
                     const char* binary_path,
                     const char* title,
                     const TlvstiSuilOpenOptions* options,
                     TlvstiSuilUi** out_ui,
                     char* error,
                     int error_capacity);

void tlvsti_gtk2_close(TlvstiSuilUi* ui);
void tlvsti_gtk2_port_event(TlvstiSuilUi* ui, uint32_t port_index, float value);
void tlvsti_gtk2_show(TlvstiSuilUi* ui);

int tlvsti_gtk3_open(const char* plugin_uri,
                     const char* ui_uri,
                     const char* ui_type_uri,
                     const char* bundle_path,
                     const char* binary_path,
                     const char* title,
                     const TlvstiSuilOpenOptions* options,
                     TlvstiSuilUi** out_ui,
                     char* error,
                     int error_capacity);

void tlvsti_gtk3_close(TlvstiSuilUi* ui);
void tlvsti_gtk3_port_event(TlvstiSuilUi* ui, uint32_t port_index, float value);
void tlvsti_gtk3_show(TlvstiSuilUi* ui);

int tlvsti_qt5_open(const char* plugin_uri,
                    const char* ui_uri,
                    const char* ui_type_uri,
                    const char* bundle_path,
                    const char* binary_path,
                    const char* title,
                    const TlvstiSuilOpenOptions* options,
                    TlvstiSuilUi** out_ui,
                    char* error,
                    int error_capacity);

void tlvsti_qt5_close(TlvstiSuilUi* ui);
void tlvsti_qt5_port_event(TlvstiSuilUi* ui, uint32_t port_index, float value);
void tlvsti_qt5_show(TlvstiSuilUi* ui);

#ifdef __cplusplus
}
#endif

#endif /* TLVSTI_SUIL_UI_H */