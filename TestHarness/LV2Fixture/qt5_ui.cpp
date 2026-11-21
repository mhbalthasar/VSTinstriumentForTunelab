/*
 * Synthetic Qt5 LV2 UI used to exercise the native Suil host.
 *
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

#include <lv2/core/lv2.h>
#include <lv2/ui/ui.h>

#include <QBoxLayout>
#include <QLabel>
#include <QPushButton>
#include <QWidget>

#include <cstdint>
#include <cstdlib>
#include <cstring>

struct TestQt5Ui {
  QWidget* root = nullptr;
};

extern "C" LV2UI_Handle instantiate(const LV2UI_Descriptor* descriptor,
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

  auto* ui = new (std::nothrow) TestQt5Ui();
  if (!ui)
    return nullptr;

  ui->root = new QWidget();
  auto* layout = new QBoxLayout(QBoxLayout::TopToBottom, ui->root);
  layout->addWidget(new QLabel(QStringLiteral("TuneLab Qt5 UI"), ui->root));
  layout->addWidget(new QPushButton(QStringLiteral("Qt5 button"), ui->root));
  ui->root->resize(360, 180);

  if (widget)
    *widget = ui->root;

  return ui;
}

extern "C" void cleanup(LV2UI_Handle handle) {
  auto* ui = static_cast<TestQt5Ui*>(handle);
  if (!ui)
    return;

  delete ui->root;
  delete ui;
}

extern "C" void port_event(LV2UI_Handle handle,
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

extern "C" const void* extension_data(const char* uri) {
  if (uri && std::strcmp(uri, LV2_UI__idleInterface) == 0)
    return &idle_interface;

  return nullptr;
}

static const LV2UI_Descriptor descriptor = {
  "urn:tunelab:test-qt5-ui",
  instantiate,
  cleanup,
  port_event,
  extension_data,
};

extern "C" LV2_SYMBOL_EXPORT const LV2UI_Descriptor*
lv2ui_descriptor(uint32_t index) {
  return index == 0 ? &descriptor : nullptr;
}