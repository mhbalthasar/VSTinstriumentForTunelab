/*
 * TuneLab VST Instruments: native Qt5 LV2 UI host.
 *
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

#include "tlvsti_suil_ui.h"

#include <lv2/ui/ui.h>
#include <suil/suil.h>

#include <QApplication>
#include <QCloseEvent>
#include <QEventLoop>
#include <QVBoxLayout>
#include <QWidget>

#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <mutex>
#include <vector>
#include <string>
#include <thread>

#define TLVSTI_QT5_UI_TYPE "http://lv2plug.in/ns/extensions/ui#Qt5UI"
#define TLVSTI_UI_INVALID_PORT_INDEX UINT32_MAX

namespace {

enum class CommandType { Open, Close, Port, Show };

typedef struct {
  char* plugin_uri;
  char* ui_uri;
  char* ui_type_uri;
  char* bundle_path;
  char* binary_path;
  char* title;
} OpenArgs;

struct TlvstiSuilUiImpl {
  std::mutex lock;
  std::condition_variable cond;
  bool open_done = false;
  int open_result = 1;
  char error[512] = {};

  bool close_done = false;
  bool closed = false;

  QWidget* window = nullptr;
  QWidget* box = nullptr;
  QVBoxLayout* layout = nullptr;

  SuilHost* host = nullptr;
  SuilInstance* instance = nullptr;
  const LV2UI_Idle_Interface* idle = nullptr;
  TlvstiSuilOpenOptions options = {};
  OpenArgs* open_args = nullptr;
  LV2UI_Resize resize = {};
};

struct Command {
  CommandType type;
  TlvstiSuilUi* ui;
  uint32_t port;
  float value;
};

std::mutex g_start_mutex;
bool g_started = false;
std::thread g_thread;

std::mutex g_queue_mutex;
std::condition_variable g_queue_cond;
std::deque<Command> g_queue;

std::mutex g_live_mutex;
std::vector<TlvstiSuilUiImpl*> g_live_uis;

bool g_init_failed = false;
char g_init_error[256] = {};

void set_error(char* error, int capacity, const char* format, ...) {
  if (!error || capacity <= 0)
    return;

  va_list args;
  va_start(args, format);
  vsnprintf(error, (size_t)capacity, format, args);
  va_end(args);
}

void ui_error(TlvstiSuilUiImpl* ui, const char* format, ...) {
  va_list args;
  va_start(args, format);
  vsnprintf(ui->error, sizeof(ui->error), format, args);
  va_end(args);
}

void free_open_args(OpenArgs* args) {
  if (!args)
    return;

  std::free(args->plugin_uri);
  std::free(args->ui_uri);
  std::free(args->ui_type_uri);
  std::free(args->bundle_path);
  std::free(args->binary_path);
  std::free(args->title);
  std::free(args);
}

void host_write(void* controller,
                uint32_t port_index,
                uint32_t buffer_size,
                uint32_t protocol,
                const void* buffer) {
  auto* ui = static_cast<TlvstiSuilUiImpl*>(controller);
  if (ui && ui->options.callbacks && ui->options.callbacks->write)
    ui->options.callbacks->write(
      ui->options.controller, port_index, buffer_size, protocol, buffer);
}

uint32_t host_index(void* controller, const char* symbol) {
  auto* ui = static_cast<TlvstiSuilUiImpl*>(controller);
  if (!ui || !ui->options.callbacks || !ui->options.callbacks->index || !symbol)
    return TLVSTI_UI_INVALID_PORT_INDEX;

  return ui->options.callbacks->index(ui->options.controller, symbol);
}

uint32_t host_subscribe(void* controller,
                        uint32_t port_index,
                        uint32_t protocol,
                        const LV2_Feature* const* features) {
  auto* ui = static_cast<TlvstiSuilUiImpl*>(controller);
  if (ui && ui->options.callbacks && ui->options.callbacks->subscribe)
    return ui->options.callbacks->subscribe(
      ui->options.controller, port_index, protocol, features);

  return 0;
}

uint32_t host_unsubscribe(void* controller,
                          uint32_t port_index,
                          uint32_t protocol,
                          const LV2_Feature* const* features) {
  auto* ui = static_cast<TlvstiSuilUiImpl*>(controller);
  if (ui && ui->options.callbacks && ui->options.callbacks->unsubscribe)
    return ui->options.callbacks->unsubscribe(
      ui->options.controller, port_index, protocol, features);

  return 0;
}

void host_touch(void* controller, uint32_t port_index, bool grabbed) {
  auto* ui = static_cast<TlvstiSuilUiImpl*>(controller);
  if (ui && ui->options.callbacks && ui->options.callbacks->touch)
    ui->options.callbacks->touch(ui->options.controller, port_index, grabbed);
}

int ui_resize(LV2UI_Feature_Handle handle, int width, int height) {
  auto* ui = static_cast<TlvstiSuilUiImpl*>(handle);
  if (!ui || !ui->window || width <= 0 || height <= 0)
    return 1;

  ui->window->resize(width, height);
  return 0;
}

class HostWindow : public QWidget {
public:
  explicit HostWindow(TlvstiSuilUiImpl* ui) : m_ui(ui) {}

protected:
  void closeEvent(QCloseEvent* event) override {
    if (m_ui)
      m_ui->closed = true;
    hide();
    event->ignore();
  }

private:
  TlvstiSuilUiImpl* m_ui;
};

int create_ui(TlvstiSuilUiImpl* ui,
              const char* plugin_uri,
              const char* ui_uri,
              const char* ui_type_uri,
              const char* bundle_path,
              const char* binary_path,
              const char* title) {
  auto* window = new HostWindow(ui);
  window->setWindowTitle(title ? QString::fromUtf8(title) : QStringLiteral("LV2 UI"));
  window->resize(640, 480);

  auto* layout = new QVBoxLayout(window);
  layout->setContentsMargins(0, 0, 0, 0);
  ui->window = window;
  ui->layout = layout;

  ui->host = suil_host_new(host_write, host_index, host_subscribe, host_unsubscribe);
  if (!ui->host) {
    ui_error(ui, "could not create the Suil host");
    return 1;
  }
  suil_host_set_touch_func(ui->host, host_touch);

  unsigned extra = 3;
  unsigned count = 0;
  const LV2_Feature* const* source = ui->options.features;
  while (source && source[count])
    ++count;

  auto** features = static_cast<const LV2_Feature**>(
    std::calloc(count + extra + 1, sizeof(LV2_Feature*)));
  if (!features) {
    ui_error(ui, "out of memory building UI features");
    return 1;
  }

  for (unsigned i = 0; i < count; ++i)
    features[i] = source[i];

  auto* parent_feature = static_cast<LV2_Feature*>(std::calloc(1, sizeof(LV2_Feature)));
  auto* resize_feature = static_cast<LV2_Feature*>(std::calloc(1, sizeof(LV2_Feature)));
  auto* idle_feature = static_cast<LV2_Feature*>(std::calloc(1, sizeof(LV2_Feature)));

  if (!parent_feature || !resize_feature || !idle_feature) {
    ui_error(ui, "out of memory building UI features");
    std::free(parent_feature);
    std::free(resize_feature);
    std::free(idle_feature);
    std::free(features);
    return 1;
  }

  parent_feature->URI = LV2_UI__parent;
  parent_feature->data = window;
  resize_feature->URI = LV2_UI__resize;
  ui->resize.handle = ui;
  ui->resize.ui_resize = ui_resize;
  resize_feature->data = &ui->resize;
  idle_feature->URI = LV2_UI__idleInterface;
  idle_feature->data = nullptr;

  features[count + 0] = parent_feature;
  features[count + 1] = resize_feature;
  features[count + 2] = idle_feature;
  features[count + 3] = nullptr;

  ui->instance = suil_instance_new(ui->host,
                                   ui,
                                   TLVSTI_QT5_UI_TYPE,
                                   plugin_uri,
                                   ui_uri,
                                   ui_type_uri,
                                   bundle_path,
                                   binary_path,
                                   features);

  std::free(features);
  std::free(parent_feature);
  std::free(resize_feature);
  std::free(idle_feature);

  if (!ui->instance) {
    ui_error(ui, "Suil could not instantiate the Qt5 UI");
    return 1;
  }

  auto* widget = static_cast<QWidget*>(suil_instance_get_widget(ui->instance));
  if (!widget) {
    ui_error(ui, "the Qt5 UI returned no widget");
    return 1;
  }

  layout->addWidget(widget);
  ui->idle = static_cast<const LV2UI_Idle_Interface*>(
    suil_instance_extension_data(ui->instance, LV2_UI__idleInterface));

  window->show();
  return 0;
}

void destroy_ui(TlvstiSuilUiImpl* ui) {
  if (ui->instance) {
    suil_instance_free(ui->instance);
    ui->instance = nullptr;
  }

  if (ui->host) {
    suil_host_free(ui->host);
    ui->host = nullptr;
  }

  if (ui->window) {
    delete ui->window;
    ui->window = nullptr;
    ui->box = nullptr;
    ui->layout = nullptr;
  }
}

void process_command(Command& command) {
  auto* ui = reinterpret_cast<TlvstiSuilUiImpl*>(command.ui);

  switch (command.type) {
  case CommandType::Open: {
    int result = 1;
    OpenArgs* args = ui->open_args;
    if (g_init_failed) {
      ui_error(ui, "%s", g_init_error);
    } else if (args) {
      result = create_ui(ui,
                         args->plugin_uri,
                         args->ui_uri,
                         args->ui_type_uri,
                         args->bundle_path,
                         args->binary_path,
                         args->title);
    }
    ui->open_args = nullptr;
    free_open_args(args);

    if (result == 0) {
      std::lock_guard<std::mutex> lock(g_live_mutex);
      g_live_uis.push_back(ui);
    }

    {
      std::lock_guard<std::mutex> lock(ui->lock);
      ui->open_result = result;
      ui->open_done = true;
    }
    ui->cond.notify_all();
    break;
  }
  case CommandType::Close:
    {
      std::lock_guard<std::mutex> lock(g_live_mutex);
      for (auto it = g_live_uis.begin(); it != g_live_uis.end(); ++it) {
        if (*it == ui) {
          g_live_uis.erase(it);
          break;
        }
      }
    }

    destroy_ui(ui);
    {
      std::lock_guard<std::mutex> lock(ui->lock);
      ui->close_done = true;
    }
    ui->cond.notify_all();
    break;
  case CommandType::Port:
    if (ui->instance && !ui->closed) {
      float value = command.value;
      suil_instance_port_event(
        ui->instance, command.port, static_cast<uint32_t>(sizeof(float)), 0, &value);
    }
    break;
  case CommandType::Show:
    if (ui->window && !ui->closed) {
      ui->window->show();
      ui->window->raise();
      ui->window->activateWindow();
    }
    break;
  }
}

void thread_main() {
  if (!qEnvironmentVariableIsSet("DISPLAY")) {
    g_init_failed = true;
    std::snprintf(g_init_error, sizeof(g_init_error), "DISPLAY is not set");
  } else {
    int argc = 1;
    char arg0[] = "tlvsti-qt5";
    char* argv[] = {arg0, nullptr};
    QApplication app(argc, argv);
    QApplication::setQuitOnLastWindowClosed(false);

    for (;;) {
      Command command;
      bool has_command = false;

      {
        std::unique_lock<std::mutex> lock(g_queue_mutex);
        if (!g_queue.empty()) {
          command = g_queue.front();
          g_queue.pop_front();
          has_command = true;
        }
      }

      if (has_command)
        process_command(command);

      QApplication::processEvents(QEventLoop::AllEvents, 10);

      {
        std::lock_guard<std::mutex> lock(g_live_mutex);
        for (auto* live : g_live_uis) {
          if (live->idle && live->instance && !live->closed)
            live->idle->idle(suil_instance_get_handle(live->instance));
        }
      }

      std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
  }

  /* If initialization failed, keep consuming commands so every waiter gets
   * an error instead of hanging. */
  for (;;) {
    Command command;
    bool has_command = false;
    {
      std::unique_lock<std::mutex> lock(g_queue_mutex);
      if (!g_queue.empty()) {
        command = g_queue.front();
        g_queue.pop_front();
        has_command = true;
      }
    }

    if (has_command) {
      if (command.type == CommandType::Open) {
        auto* ui = reinterpret_cast<TlvstiSuilUiImpl*>(command.ui);
        ui_error(ui, "%s", g_init_error);
        {
          std::lock_guard<std::mutex> lock(ui->lock);
          ui->open_result = 1;
          ui->open_done = true;
        }
        ui->cond.notify_all();
      } else if (command.type == CommandType::Close) {
        auto* ui = reinterpret_cast<TlvstiSuilUiImpl*>(command.ui);
        std::lock_guard<std::mutex> lock(ui->lock);
        ui->close_done = true;
        ui->cond.notify_all();
      }
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(10));
  }
}

void ensure_thread() {
  std::lock_guard<std::mutex> lock(g_start_mutex);
  if (!g_started) {
    g_thread = std::thread(thread_main);
    g_thread.detach();
    g_started = true;
  }
}

int wait_for_open(TlvstiSuilUiImpl* ui) {
  std::unique_lock<std::mutex> lock(ui->lock);
  if (!ui->cond.wait_for(lock,
                         std::chrono::seconds(10),
                         [ui] { return ui->open_done; }))
    return 1;

  return ui->open_result;
}

} // namespace

extern "C" int tlvsti_qt5_open(const char* plugin_uri,
                               const char* ui_uri,
                               const char* ui_type_uri,
                               const char* bundle_path,
                               const char* binary_path,
                               const char* title,
                               const TlvstiSuilOpenOptions* options,
                               TlvstiSuilUi** out_ui,
                               char* error,
                               int error_capacity) {
  if (!plugin_uri || !ui_uri || !ui_type_uri || !bundle_path || !binary_path ||
      !options || !options->callbacks || !out_ui) {
    set_error(error, error_capacity, "invalid Qt5 UI open arguments");
    return 1;
  }

  *out_ui = nullptr;
  auto* ui = new (std::nothrow) TlvstiSuilUiImpl();
  if (!ui) {
    set_error(error, error_capacity, "out of memory");
    return 1;
  }

  ui->options = *options;

  auto* args = static_cast<OpenArgs*>(std::calloc(1, sizeof(OpenArgs)));
  if (!args) {
    delete ui;
    set_error(error, error_capacity, "out of memory");
    return 1;
  }

  args->plugin_uri = strdup(plugin_uri);
  args->ui_uri = strdup(ui_uri);
  args->ui_type_uri = strdup(ui_type_uri);
  args->bundle_path = strdup(bundle_path);
  args->binary_path = strdup(binary_path);
  args->title = strdup(title ? title : "LV2 UI");

  if (!args->plugin_uri || !args->ui_uri || !args->ui_type_uri ||
      !args->bundle_path || !args->binary_path || !args->title) {
    free_open_args(args);
    delete ui;
    set_error(error, error_capacity, "out of memory");
    return 1;
  }

  ui->open_args = args;
  ensure_thread();

  {
    std::lock_guard<std::mutex> lock(g_queue_mutex);
    g_queue.push_back(Command{CommandType::Open, reinterpret_cast<TlvstiSuilUi*>(ui), 0, 0.0f});
  }

  if (wait_for_open(ui) != 0) {
    set_error(error, error_capacity, "%s",
              ui->error[0] ? ui->error : "Qt5 UI open failed");
    free_open_args(ui->open_args);
    delete ui;
    return 1;
  }

  *out_ui = reinterpret_cast<TlvstiSuilUi*>(ui);
  return 0;
}

extern "C" void tlvsti_qt5_close(TlvstiSuilUi* ui) {
  if (!ui)
    return;

  auto* impl = reinterpret_cast<TlvstiSuilUiImpl*>(ui);
  {
    std::lock_guard<std::mutex> lock(g_queue_mutex);
    g_queue.push_back(Command{CommandType::Close, ui, 0, 0.0f});
  }

  {
    std::unique_lock<std::mutex> lock(impl->lock);
    impl->cond.wait_for(lock, std::chrono::seconds(5), [impl] {
      return impl->close_done;
    });
  }

  delete impl;
}

extern "C" void tlvsti_qt5_port_event(TlvstiSuilUi* ui, uint32_t port_index, float value) {
  if (!ui)
    return;

  std::lock_guard<std::mutex> lock(g_queue_mutex);
  g_queue.push_back(Command{CommandType::Port, ui, port_index, value});
}

extern "C" void tlvsti_qt5_show(TlvstiSuilUi* ui) {
  if (!ui)
    return;

  std::lock_guard<std::mutex> lock(g_queue_mutex);
  g_queue.push_back(Command{CommandType::Show, ui, 0, 0.0f});
}