/*
 * TuneLab VST Instruments: native Gtk2 LV2 UI host.
 *
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

#include "tlvsti_suil_ui.h"

#include <lv2/ui/ui.h>
#include <suil/suil.h>

#include <gtk/gtk.h>
#include <glib.h>

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define TLVSTI_GTK2_UI_TYPE "http://lv2plug.in/ns/extensions/ui#GtkUI"
#define TLVSTI_UI_INVALID_PORT_INDEX UINT32_MAX

typedef enum {
  TLVSTI_CMD_OPEN,
  TLVSTI_CMD_CLOSE,
  TLVSTI_CMD_PORT,
  TLVSTI_CMD_SHOW
} TlvstiCommandType;

typedef struct {
  TlvstiCommandType type;
  TlvstiSuilUi* ui;
  uint32_t port;
  float value;
} TlvstiCommand;

typedef struct {
  char* plugin_uri;
  char* ui_uri;
  char* ui_type_uri;
  char* bundle_path;
  char* binary_path;
  char* title;
} TlvstiOpenArgs;

struct TlvstiSuilUi {
  GMutex lock;
  GCond cond;
  gboolean open_done;
  int open_result;
  char error[512];

  gboolean close_done;
  gboolean closed;
  gboolean show_requested;

  GtkWidget* window;
  GtkWidget* box;

  SuilHost* host;
  SuilInstance* instance;
  const LV2UI_Idle_Interface* idle;
  TlvstiSuilOpenOptions options;
  TlvstiOpenArgs* open_args;
  LV2UI_Resize resize;
};

static GAsyncQueue* g_commands;
static GThread* g_thread;
static gboolean g_started;
static GMutex g_start_lock;
static char g_init_error[256];
static GPtrArray* g_live_uis;

static void set_error(char* error, int capacity, const char* format, ...)
{
  if (!error || capacity <= 0)
    return;

  va_list args;
  va_start(args, format);
  vsnprintf(error, (size_t)capacity, format, args);
  va_end(args);
}

static void ui_error(TlvstiSuilUi* ui, const char* format, ...)
{
  va_list args;
  va_start(args, format);
  vsnprintf(ui->error, sizeof(ui->error), format, args);
  va_end(args);
}

static uint32_t host_index(void* controller, const char* symbol)
{
  TlvstiSuilUi* ui = (TlvstiSuilUi*)controller;
  if (!ui || !ui->options.callbacks || !ui->options.callbacks->index || !symbol)
    return TLVSTI_UI_INVALID_PORT_INDEX;

  return ui->options.callbacks->index(ui->options.controller, symbol);
}

static void host_write(void* controller,
                       uint32_t port_index,
                       uint32_t buffer_size,
                       uint32_t protocol,
                       const void* buffer)
{
  TlvstiSuilUi* ui = (TlvstiSuilUi*)controller;
  if (ui && ui->options.callbacks && ui->options.callbacks->write)
    ui->options.callbacks->write(
      ui->options.controller, port_index, buffer_size, protocol, buffer);
}

static uint32_t host_subscribe(void* controller,
                               uint32_t port_index,
                               uint32_t protocol,
                               const LV2_Feature* const* features)
{
  TlvstiSuilUi* ui = (TlvstiSuilUi*)controller;
  if (ui && ui->options.callbacks && ui->options.callbacks->subscribe)
    return ui->options.callbacks->subscribe(
      ui->options.controller, port_index, protocol, features);

  return 0;
}

static uint32_t host_unsubscribe(void* controller,
                                 uint32_t port_index,
                                 uint32_t protocol,
                                 const LV2_Feature* const* features)
{
  TlvstiSuilUi* ui = (TlvstiSuilUi*)controller;
  if (ui && ui->options.callbacks && ui->options.callbacks->unsubscribe)
    return ui->options.callbacks->unsubscribe(
      ui->options.controller, port_index, protocol, features);

  return 0;
}

static void host_touch(void* controller, uint32_t port_index, bool grabbed)
{
  TlvstiSuilUi* ui = (TlvstiSuilUi*)controller;
  if (ui && ui->options.callbacks && ui->options.callbacks->touch)
    ui->options.callbacks->touch(ui->options.controller, port_index, grabbed);
}

static int ui_resize(LV2UI_Feature_Handle handle, int width, int height)
{
  TlvstiSuilUi* ui = (TlvstiSuilUi*)handle;
  if (!ui || !ui->window || width <= 0 || height <= 0)
    return 1;

  gtk_window_resize(GTK_WINDOW(ui->window), width, height);
  return 0;
}

static gboolean on_delete(GtkWidget* widget, GdkEvent* event, gpointer data)
{
  (void)widget;
  (void)event;
  TlvstiSuilUi* ui = (TlvstiSuilUi*)data;
  if (ui)
    ui->closed = TRUE;
  gtk_widget_hide(GTK_WIDGET(widget));
  return TRUE;
}

static int create_ui(TlvstiSuilUi* ui,
                     const char* plugin_uri,
                     const char* ui_uri,
                     const char* ui_type_uri,
                     const char* bundle_path,
                     const char* binary_path,
                     const char* title)
{
  ui->window = gtk_window_new(GTK_WINDOW_TOPLEVEL);
  gtk_window_set_title(GTK_WINDOW(ui->window), title ? title : "LV2 UI");
  gtk_window_set_default_size(GTK_WINDOW(ui->window), 640, 480);
  g_signal_connect(ui->window, "delete-event", G_CALLBACK(on_delete), ui);

  ui->box = gtk_vbox_new(FALSE, 0);
  gtk_container_add(GTK_CONTAINER(ui->window), ui->box);

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

  const LV2_Feature** features =
    (const LV2_Feature**)calloc(count + extra + 1, sizeof(LV2_Feature*));
  if (!features) {
    ui_error(ui, "out of memory building UI features");
    return 1;
  }

  for (unsigned i = 0; i < count; ++i)
    features[i] = source[i];

  LV2_Feature* parent_feature = (LV2_Feature*)calloc(1, sizeof(LV2_Feature));
  LV2_Feature* resize_feature = (LV2_Feature*)calloc(1, sizeof(LV2_Feature));
  LV2_Feature* idle_feature = (LV2_Feature*)calloc(1, sizeof(LV2_Feature));

  if (!parent_feature || !resize_feature || !idle_feature) {
    ui_error(ui, "out of memory building UI features");
    free(parent_feature);
    free(resize_feature);
    free(idle_feature);
    free(features);
    return 1;
  }

  parent_feature->URI = LV2_UI__parent;
  parent_feature->data = ui->box;
  resize_feature->URI = LV2_UI__resize;
  ui->resize.handle = ui;
  ui->resize.ui_resize = ui_resize;
  resize_feature->data = &ui->resize;
  idle_feature->URI = LV2_UI__idleInterface;
  idle_feature->data = NULL;

  features[count + 0] = parent_feature;
  features[count + 1] = resize_feature;
  features[count + 2] = idle_feature;
  features[count + 3] = NULL;

  ui->instance = suil_instance_new(ui->host,
                                   ui,
                                   TLVSTI_GTK2_UI_TYPE,
                                   plugin_uri,
                                   ui_uri,
                                   ui_type_uri,
                                   bundle_path,
                                   binary_path,
                                   features);

  free(features);
  free(parent_feature);
  free(resize_feature);
  free(idle_feature);

  if (!ui->instance) {
    ui_error(ui, "Suil could not instantiate the Gtk2 UI");
    return 1;
  }

  GtkWidget* widget = (GtkWidget*)suil_instance_get_widget(ui->instance);
  if (!widget) {
    ui_error(ui, "the Gtk2 UI returned no widget");
    return 1;
  }

  gtk_box_pack_start(GTK_BOX(ui->box), widget, TRUE, TRUE, 0);
  ui->idle = (const LV2UI_Idle_Interface*)suil_instance_extension_data(
    ui->instance, LV2_UI__idleInterface);

  gtk_widget_show_all(ui->window);
  return 0;
}

static void free_open_args(TlvstiOpenArgs* args)
{
  if (!args)
    return;

  g_free(args->plugin_uri);
  g_free(args->ui_uri);
  g_free(args->ui_type_uri);
  g_free(args->bundle_path);
  g_free(args->binary_path);
  g_free(args->title);
  free(args);
}

static void destroy_ui(TlvstiSuilUi* ui)
{
  if (ui->instance) {
    suil_instance_free(ui->instance);
    ui->instance = NULL;
  }

  if (ui->host) {
    suil_host_free(ui->host);
    ui->host = NULL;
  }

  if (ui->window) {
    gtk_widget_destroy(ui->window);
    ui->window = NULL;
    ui->box = NULL;
  }
}

static void process_command(TlvstiCommand* command)
{
  TlvstiSuilUi* ui = command->ui;

  switch (command->type) {
  case TLVSTI_CMD_OPEN: {
    int result = 1;
    TlvstiOpenArgs* args = ui->open_args;
    if (g_init_error[0] != '\0') {
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
    ui->open_args = NULL;
    free_open_args(args);
    if (result == 0)
      g_ptr_array_add(g_live_uis, ui);

    g_mutex_lock(&ui->lock);
    ui->open_result = result;
    ui->open_done = TRUE;
    g_cond_signal(&ui->cond);
    g_mutex_unlock(&ui->lock);
    break;
  }
  case TLVSTI_CMD_CLOSE:
    for (guint i = 0; i < g_live_uis->len; ++i) {
      if (g_ptr_array_index(g_live_uis, i) == ui) {
        g_ptr_array_remove_index(g_live_uis, i);
        break;
      }
    }
    destroy_ui(ui);
    g_mutex_lock(&ui->lock);
    ui->close_done = TRUE;
    g_cond_signal(&ui->cond);
    g_mutex_unlock(&ui->lock);
    break;
  case TLVSTI_CMD_PORT:
    if (ui->instance && !ui->closed) {
      float value = command->value;
      suil_instance_port_event(
        ui->instance, command->port, (uint32_t)sizeof(float), 0, &value);
    }
    break;
  case TLVSTI_CMD_SHOW:
    if (ui->window && !ui->closed)
      gtk_window_present(GTK_WINDOW(ui->window));
    break;
  }
}

static gpointer thread_main(gpointer data)
{
  (void)data;

  if (!gtk_init_check(NULL, NULL)) {
    g_mutex_lock(&g_start_lock);
    snprintf(g_init_error, sizeof(g_init_error), "gtk_init_check failed (no DISPLAY?)");
    g_mutex_unlock(&g_start_lock);
  }

  for (;;) {
    TlvstiCommand* command;
    while ((command = (TlvstiCommand*)g_async_queue_try_pop(g_commands)) != NULL) {
      process_command(command);
      free(command);
    }

    if (g_init_error[0] == '\0') {
      while (gtk_events_pending())
        gtk_main_iteration_do(FALSE);
      gtk_main_iteration_do(FALSE);

      for (guint i = 0; i < g_live_uis->len; ++i) {
        TlvstiSuilUi* live = (TlvstiSuilUi*)g_ptr_array_index(g_live_uis, i);
        if (live->idle && live->instance && !live->closed) {
          live->idle->idle(suil_instance_get_handle(live->instance));
        }
      }
    }

    g_usleep(10000);
  }

  return NULL;
}

static void ensure_thread(void)
{
  g_mutex_lock(&g_start_lock);
  if (!g_started) {
    g_commands = g_async_queue_new();
    g_live_uis = g_ptr_array_new();
    g_thread = g_thread_new("tlvsti-gtk2", thread_main, NULL);
    g_started = TRUE;
  }
  g_mutex_unlock(&g_start_lock);
}

static int wait_for_open(TlvstiSuilUi* ui)
{
  gint64 deadline = g_get_monotonic_time() + 10 * G_TIME_SPAN_SECOND;
  g_mutex_lock(&ui->lock);
  while (!ui->open_done && g_get_monotonic_time() < deadline)
    g_cond_wait_until(&ui->cond, &ui->lock, deadline);
  int result = ui->open_done ? ui->open_result : 1;
  g_mutex_unlock(&ui->lock);
  return result;
}

int tlvsti_gtk2_open(const char* plugin_uri,
                     const char* ui_uri,
                     const char* ui_type_uri,
                     const char* bundle_path,
                     const char* binary_path,
                     const char* title,
                     const TlvstiSuilOpenOptions* options,
                     TlvstiSuilUi** out_ui,
                     char* error,
                     int error_capacity)
{
  if (!plugin_uri || !ui_uri || !ui_type_uri || !bundle_path || !binary_path ||
      !options || !options->callbacks || !out_ui) {
    set_error(error, error_capacity, "invalid Gtk2 UI open arguments");
    return 1;
  }

  *out_ui = NULL;
  TlvstiSuilUi* ui = (TlvstiSuilUi*)calloc(1, sizeof(TlvstiSuilUi));
  if (!ui) {
    set_error(error, error_capacity, "out of memory");
    return 1;
  }

  g_mutex_init(&ui->lock);
  g_cond_init(&ui->cond);
  ui->options = *options;

  ensure_thread();

  TlvstiCommand* command = (TlvstiCommand*)calloc(1, sizeof(TlvstiCommand));
  if (!command) {
    set_error(error, error_capacity, "out of memory");
    free(ui);
    return 1;
  }

  /* The worker needs the actual URIs.  Stash them in the command so the
   * toolkit thread never reads C# strings after the caller returns. */
  char* stored_plugin = g_strdup(plugin_uri);
  char* stored_ui = g_strdup(ui_uri);
  char* stored_type = g_strdup(ui_type_uri);
  char* stored_bundle = g_strdup(bundle_path);
  char* stored_binary = g_strdup(binary_path);
  char* stored_title = g_strdup(title ? title : "LV2 UI");

  command->type = TLVSTI_CMD_OPEN;
  command->ui = ui;

  TlvstiOpenArgs* args = (TlvstiOpenArgs*)calloc(1, sizeof(TlvstiOpenArgs));
  if (!args) {
    g_free(stored_plugin);
    g_free(stored_ui);
    g_free(stored_type);
    g_free(stored_bundle);
    g_free(stored_binary);
    g_free(stored_title);
    free(command);
    free(ui);
    set_error(error, error_capacity, "out of memory");
    return 1;
  }

  args->plugin_uri = stored_plugin;
  args->ui_uri = stored_ui;
  args->ui_type_uri = stored_type;
  args->bundle_path = stored_bundle;
  args->binary_path = stored_binary;
  args->title = stored_title;

  /* Attach the open arguments to the UI object so process_command can use
   * them.  They are freed by process_command after the open attempt. */
  ui->open_args = args;
  g_async_queue_push(g_commands, command);

  int result = wait_for_open(ui);
  if (result != 0) {
    set_error(error, error_capacity, "%s", ui->error[0] ? ui->error : "Gtk2 UI open failed");
    /* The toolkit thread may still own the window on timeout.  Closing is
     * best-effort; the process stays alive even if it never started. */
    if (ui->open_done)
      tlvsti_gtk2_close(ui);
    free_open_args(ui->open_args);
    g_mutex_clear(&ui->lock);
    g_cond_clear(&ui->cond);
    free(ui);
    return 1;
  }

  *out_ui = ui;
  return 0;
}

void tlvsti_gtk2_close(TlvstiSuilUi* ui)
{
  if (!ui)
    return;

  TlvstiCommand* command = (TlvstiCommand*)calloc(1, sizeof(TlvstiCommand));
  if (!command) {
    free(ui);
    return;
  }

  command->type = TLVSTI_CMD_CLOSE;
  command->ui = ui;
  g_async_queue_push(g_commands, command);

  gint64 deadline = g_get_monotonic_time() + 5 * G_TIME_SPAN_SECOND;
  g_mutex_lock(&ui->lock);
  while (!ui->close_done && g_get_monotonic_time() < deadline)
    g_cond_wait_until(&ui->cond, &ui->lock, deadline);
  g_mutex_unlock(&ui->lock);

  g_mutex_clear(&ui->lock);
  g_cond_clear(&ui->cond);
  free(ui);
}

void tlvsti_gtk2_port_event(TlvstiSuilUi* ui, uint32_t port_index, float value)
{
  if (!ui)
    return;

  TlvstiCommand* command = (TlvstiCommand*)calloc(1, sizeof(TlvstiCommand));
  if (!command)
    return;

  command->type = TLVSTI_CMD_PORT;
  command->ui = ui;
  command->port = port_index;
  command->value = value;
  g_async_queue_push(g_commands, command);
}

void tlvsti_gtk2_show(TlvstiSuilUi* ui)
{
  if (!ui)
    return;

  TlvstiCommand* command = (TlvstiCommand*)calloc(1, sizeof(TlvstiCommand));
  if (!command)
    return;

  command->type = TLVSTI_CMD_SHOW;
  command->ui = ui;
  g_async_queue_push(g_commands, command);
}