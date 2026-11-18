#include <stdint.h>
#include <math.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#define TL_EXPORT __declspec(dllexport)
#else
#define TL_EXPORT __attribute__((visibility("default")))
#endif

#if defined(__linux__)
#include <X11/Xlib.h>
#endif

typedef struct {
    uint32_t size;
    uint32_t type;
} LV2_Atom;

typedef struct {
    LV2_Atom atom;
    uint32_t event_count;
} LV2_Atom_Sequence;

typedef struct {
    int64_t frames;
    uint32_t subframes;
    uint32_t type;
    uint32_t size;
} LV2_Atom_Event;

typedef struct {
    const char* URI;
    void* data;
} LV2_Feature;

typedef uint32_t LV2_State_Status;
typedef void* LV2_State_Handle;
typedef LV2_State_Status (*LV2_State_Store_Function)(
    LV2_State_Handle handle, uint32_t key, const void* value, size_t size,
    uint32_t type, uint32_t flags);
typedef const void* (*LV2_State_Retrieve_Function)(
    LV2_State_Handle handle, uint32_t key, size_t* size, uint32_t* type,
    uint32_t* flags);

typedef struct {
    LV2_State_Status (*save)(void* instance, LV2_State_Store_Function store,
                             LV2_State_Handle handle, uint32_t flags,
                             const LV2_Feature* const* features);
    LV2_State_Status (*restore)(void* instance, LV2_State_Retrieve_Function retrieve,
                                LV2_State_Handle handle, uint32_t flags,
                                const LV2_Feature* const* features);
} LV2_State_Interface;

typedef struct Plugin Plugin;

typedef struct {
    const char* URI;
    Plugin* (*instantiate)(const void* descriptor, double sample_rate,
                           const char* bundle_path, const LV2_Feature* const* features);
    void (*connect_port)(Plugin* handle, uint32_t port, void* data);
    void (*activate)(Plugin* handle);
    void (*run)(Plugin* handle, uint32_t sample_count);
    void (*deactivate)(Plugin* handle);
    void (*cleanup)(Plugin* handle);
    const void* (*extension_data)(const char* uri);
} LV2_Descriptor;

struct Plugin {
    double sample_rate;
    float* out_l;
    float* out_r;
    float* gain;
    LV2_Atom_Sequence* midi;
    double phase;
    double frequency;
    int active;
};

static int has_feature(const LV2_Feature* const* features, const char* uri) {
    if (!features) return 0;
    for (int i = 0; features[i]; i++) {
        if (features[i]->URI && strcmp(features[i]->URI, uri) == 0)
            return 1;
    }
    return 0;
}

static Plugin* instantiate(const void* descriptor, double sample_rate,
                           const char* bundle_path, const LV2_Feature* const* features) {
    (void)descriptor;
    (void)bundle_path;

    if (!has_feature(features, "http://lv2plug.in/ns/ext/worker#schedule")
        || !has_feature(features, "http://lv2plug.in/ns/ext/buf-size#boundedBlockLength")
        || !has_feature(features, "http://lv2plug.in/ns/ext/options#options")) {
        return NULL;
    }

    Plugin* p = (Plugin*)calloc(1, sizeof(Plugin));
    if (!p) return NULL;
    p->sample_rate = sample_rate;
    p->frequency = 440.0;
    p->phase = 0.0;
    return p;
}

static void connect_port(Plugin* p, uint32_t port, void* data) {
    switch (port) {
        case 0: p->midi = (LV2_Atom_Sequence*)data; break;
        case 1: p->out_l = (float*)data; break;
        case 2: p->out_r = (float*)data; break;
        case 3: p->gain = (float*)data; break;
        default: break;
    }
}

static void activate(Plugin* p) { (void)p; }
static void deactivate(Plugin* p) { (void)p; }
static void cleanup(Plugin* p) { free(p); }

static void run(Plugin* p, uint32_t count) {
    if (p->midi) {
        uint8_t* cursor = (uint8_t*)p->midi + sizeof(LV2_Atom_Sequence) + sizeof(uint32_t);
        for (uint32_t i = 0; i < p->midi->event_count; i++) {
            LV2_Atom_Event* ev = (LV2_Atom_Event*)cursor;
            uint8_t* body = (uint8_t*)(ev + 1);
            if (ev->size >= 3 && (body[0] & 0xF0) == 0x90 && body[2] > 0) {
                p->active = 1;
                p->phase = 0.0;
            } else if (ev->size >= 3 && ((body[0] & 0xF0) == 0x80 || ((body[0] & 0xF0) == 0x90 && body[2] == 0))) {
                p->active = 0;
            }
            cursor += sizeof(LV2_Atom_Event) + ev->size;
        }
    }

    float gain = p->gain ? *p->gain : 1.0f;
    if (gain < 0.0f) gain = 0.0f;
    if (gain > 1.0f) gain = 1.0f;

    for (uint32_t i = 0; i < count; i++) {
        float sample = 0.0f;
        if (p->active) {
            sample = (float)sin(p->phase) * 0.2f * gain;
            p->phase += 2.0 * 3.14159265358979323846 * p->frequency / p->sample_rate;
            if (p->phase >= 2.0 * 3.14159265358979323846) p->phase -= 2.0 * 3.14159265358979323846;
        }
        if (p->out_l) p->out_l[i] = sample;
        if (p->out_r) p->out_r[i] = sample;
    }
}

static LV2_State_Status state_save(void* instance, LV2_State_Store_Function store,
                                    LV2_State_Handle handle, uint32_t flags,
                                    const LV2_Feature* const* features) {
    (void)flags;
    (void)features;
    Plugin* p = (Plugin*)instance;
    float gain = p->gain ? *p->gain : 1.0f;
    return store(handle, 42, &gain, sizeof(gain), 1, 3) == 0 ? 0 : 1;
}

static LV2_State_Status state_restore(void* instance, LV2_State_Retrieve_Function retrieve,
                                      LV2_State_Handle handle, uint32_t flags,
                                      const LV2_Feature* const* features) {
    (void)flags;
    (void)features;
    Plugin* p = (Plugin*)instance;
    size_t size = 0;
    uint32_t type = 0;
    uint32_t value_flags = 0;
    const void* value = retrieve(handle, 42, &size, &type, &value_flags);
    if (value && size == sizeof(float) && p->gain)
        *p->gain = *(const float*)value;
    return 0;
}

static const LV2_State_Interface state_interface = {
    state_save,
    state_restore,
};

static const void* extension_data(const char* uri) {
    if (uri && strcmp(uri, "http://lv2plug.in/ns/ext/state#interface") == 0)
        return &state_interface;
    return NULL;
}

static const LV2_Descriptor descriptor = {
    "urn:tunelab:test-lv2",
    instantiate,
    connect_port,
    activate,
    run,
    deactivate,
    cleanup,
    extension_data
};

TL_EXPORT const LV2_Descriptor* lv2_descriptor(uint32_t index) {
    return index == 0 ? &descriptor : NULL;
}

// 鈹€鈹€ LV2 UI (WindowsUI) 鈹€鈹€
typedef void* LV2UI_Handle;
typedef void* LV2UI_Widget;
typedef void (*LV2UI_Write_Function)(void* controller, uint32_t port,
                                     uint32_t size, uint32_t format,
                                     const void* buffer);

typedef struct LV2UI_Descriptor LV2UI_Descriptor;
struct LV2UI_Descriptor {
    const char* URI;
    LV2UI_Handle (*instantiate)(const LV2UI_Descriptor* descriptor,
                                const char* plugin_uri,
                                const char* bundle_path,
                                LV2UI_Write_Function write_function,
                                void* controller,
                                LV2UI_Widget* widget,
                                const LV2_Feature* const* features);
    void (*cleanup)(LV2UI_Handle handle);
    void (*port_event)(LV2UI_Handle handle, uint32_t port, uint32_t size,
                       uint32_t format, const void* buffer);
    const void* (*extension_data)(const char* uri);
};

typedef struct {
    uintptr_t window;
    uintptr_t display;
} TestUi;

static uintptr_t ui_feature(const LV2_Feature* const* features, const char* uri) {
    if (!features) return 0;
    for (int i = 0; features[i]; i++) {
        if (features[i]->URI && strcmp(features[i]->URI, uri) == 0)
            return (uintptr_t)features[i]->data;
    }
    return 0;
}

static LV2UI_Handle ui_instantiate(const LV2UI_Descriptor* descriptor,
                                   const char* plugin_uri,
                                   const char* bundle_path,
                                   LV2UI_Write_Function write_function,
                                   void* controller,
                                   LV2UI_Widget* widget,
                                   const LV2_Feature* const* features) {
    (void)descriptor; (void)plugin_uri; (void)bundle_path;
    (void)write_function; (void)controller;

    TestUi* ui = (TestUi*)calloc(1, sizeof(TestUi));
    if (!ui) return NULL;

#ifdef _WIN32
    HWND parent = (HWND)ui_feature(features, "http://lv2plug.in/ns/extensions/ui#WindowsUI");
    if (!parent)
        parent = (HWND)ui_feature(features, "http://lv2plug.in/ns/extensions/ui#parent");

    HWND child = CreateWindowExW(0, L"STATIC", L"TuneLab test LV2 UI",
                                 WS_CHILD | WS_VISIBLE,
                                 0, 0, 480, 320, parent, NULL, NULL, NULL);
    ui->window = (uintptr_t)child;
    if (widget) *widget = (void*)(uintptr_t)child;
#elif defined(__linux__)
    Display* display = (Display*)ui_feature(features, "http://lv2plug.in/ns/extensions/ui#X11UI");
    Window parent = (Window)ui_feature(features, "http://lv2plug.in/ns/extensions/ui#parent");

    if (!display)
        return NULL;

    Window child = XCreateSimpleWindow(display, parent, 0, 0, 480, 320, 0, 0, 0xffffff);
    XMapWindow(display, child);
    XFlush(display);
    ui->display = (uintptr_t)display;
    ui->window = (uintptr_t)child;
    if (widget) *widget = (void*)(uintptr_t)child;
#else
    if (widget) *widget = NULL;
#endif
    return ui;
}

static void ui_cleanup(LV2UI_Handle handle) {
#if defined(_WIN32)
    TestUi* ui = (TestUi*)handle;
    if (ui && ui->window) DestroyWindow((HWND)ui->window);
#elif defined(__linux__)
    TestUi* ui = (TestUi*)handle;
    if (ui && ui->window && ui->display) {
        Display* display = (Display*)ui->display;
        XDestroyWindow(display, (Window)ui->window);
        XFlush(display);
    }
#endif
    free(handle);
}

static void ui_port_event(LV2UI_Handle handle, uint32_t port, uint32_t size,
                          uint32_t format, const void* buffer) {
    (void)handle; (void)port; (void)size; (void)format; (void)buffer;
}

static const void* ui_extension_data(const char* uri) {
    (void)uri;
    return NULL;
}

static const LV2UI_Descriptor ui_descriptor = {
    "urn:tunelab:test-lv2-ui",
    ui_instantiate,
    ui_cleanup,
    ui_port_event,
    ui_extension_data,
};

TL_EXPORT const LV2UI_Descriptor* lv2ui_descriptor(uint32_t index) {
    return index == 0 ? &ui_descriptor : NULL;
}
