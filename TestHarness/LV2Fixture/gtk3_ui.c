/*
 * Synthetic Gtk3 LV2 UI used to exercise the native Suil host.
 *
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

#include <lv2/core/lv2.h>
#include <lv2/ui/ui.h>

#include <gtk/gtk.h>

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
  GtkWidget* root;
  GtkWidget* label;
} TestGtk3Ui;

static LV2UI_Handle instantiate(const LV2UI_Descriptor* descriptor,
                                const char* plugin_uri,
                                const char* bundle_path,
                                LV2UI_Write_Function write_function,
                                LV2UI_Controller controller,
                                LV2UI_Widget* widget,
                                const LV2_Feature* const* features) {
  (void)descriptor;
  (void)plugin_uri;
  (void)bundle_path;
  (void)write_function;
  (void)controller;
  (void)features;

  TestGtk3Ui* ui = (TestGtk3Ui*)calloc(1, sizeof(TestGtk3Ui));
  if (!ui)
    return NULL;

  ui->root = gtk_box_new(GTK_ORIENTATION_VERTICAL, 4);
  ui->label = gtk_label_new("TuneLab Gtk3 UI");
  gtk_box_pack_start(GTK_BOX(ui->root), ui->label, FALSE, FALSE, 8);

  GtkWidget* button = gtk_button_new_with_label("Gtk3 button");
  gtk_box_pack_start(GTK_BOX(ui->root), button, FALSE, FALSE, 8);
  gtk_widget_show_all(ui->root);

  if (widget)
    *widget = ui->root;

  return ui;
}

static void cleanup(LV2UI_Handle handle) {
  TestGtk3Ui* ui = (TestGtk3Ui*)handle;
  if (!ui)
    return;

  if (ui->root)
    gtk_widget_destroy(ui->root);

  free(ui);
}

static void port_event(LV2UI_Handle handle,
                       uint32_t port_index,
                       uint32_t buffer_size,
                       uint32_t format,
                       const void* buffer) {
  (void)handle;
  (void)port_index;
  (void)buffer_size;
  (void)format;
  (void)buffer;
}

static int idle(LV2UI_Handle handle) {
  (void)handle;
  return 0;
}

static const LV2UI_Idle_Interface idle_interface = {idle};

static const void* extension_data(const char* uri) {
  if (uri && strcmp(uri, LV2_UI__idleInterface) == 0)
    return &idle_interface;

  return NULL;
}

static const LV2UI_Descriptor descriptor = {
  "urn:tunelab:test-gtk3-ui",
  instantiate,
  cleanup,
  port_event,
  extension_data,
};

LV2_SYMBOL_EXPORT const LV2UI_Descriptor*
lv2ui_descriptor(uint32_t index) {
  return index == 0 ? &descriptor : NULL;
}