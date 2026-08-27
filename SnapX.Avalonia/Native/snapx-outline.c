/*
 * snapx-outline - SnapX recording-region outline for Wayland/Hyprland.
 *
 * A standalone wlr-layer-shell client that draws a 2px dashed red rectangle
 * around the region being recorded. Uses only libwayland-client + wl_shm, so
 * it works on any wlroots compositor (Hyprland, Sway, labwc) with no GTK4 /
 * layer-shell-library dependency.
 *
 * The X/Y/W/H arguments are in the target output's LOCAL logical coordinates
 * (i.e. the output work-area space, origin at the output's top-left after the
 * top panel/bar offset). The caller resolves the output and converts global
 * coordinates; this helper only picks the output named by --output (or the
 * geometry-derived one) and renders the dashes.
 *
 * Each invocation creates exactly one two-pixel-wide edge outside the capture
 * rectangle. The managed host starts four invocations for the four edges.
 * This is intentionally not a transparent (W + 2) x (H + 2) surface: if a
 * compositor or GPU driver misinterprets alpha, even that smaller rectangle
 * would obscure the recording itself. A faulty helper can therefore affect at
 * most one thin border line, never the selected content or an entire output.
 *
 * Why a layer-shell surface instead of an Avalonia window:
 *   Avalonia on Linux has no native Wayland backend; its windows are XWayland
 *   toplevels that always appear in "hyprctl clients" as normal decorated,
 *   focusable windows. A wlr-layer-shell OVERLAY surface is not a normal
 *   toplevel: it is absent from hyprctl clients, has no decorations/taskbar/
 *   focus, is click-through, and is excluded from grim/wf-recorder capture.
 *
 * Usage:  snapx-outline <x> <y> <w> <h> --edge top|bottom|left|right [--output <name>]
 *         snapx-outline <x> <y> <w> <h> --controller [--output <name>]
 *                       [--logical-w <w> --logical-h <h>]
 *                       [--work-top <y> --work-bottom <y>]
 *
 * In --controller mode the rectangle is the capture region (output-local
 * logical coordinates) and is used to affix the control tile immediately
 * outside one of the region's edges, aligned with the drawn outline. When no
 * edge has room for the tile, or the geometry is degenerate/unknown, the tile
 * falls back to a screen corner that does not cover the recorded region.
 *
 * --logical-w/--logical-h carry the target output's TRUE fractional-logical
 * size, which the managed host resolves from hyprctl. It cannot be derived
 * here: wl_output.scale is an integer, so a 2560x1440 output at fractional
 * scale 1.6 advertises scale 2 and mode 2560x1440, which would yield 1280x720
 * rather than the real 1600x900 space that the capture region coordinates and
 * the compositor's layer-shell margins both live in.
 *
 * --work-top/--work-bottom carry the target output's usable work area in that
 * same logical space: the band left over after the compositor's reserved
 * insets (panels, docks). A layer surface with exclusive_zone 0 is positioned
 * inside that band, so an anchored-top margin of N lands at logical
 * work_top + N. Without them the controller can place itself under
 * a bottom dock for regions near the bottom of the output.
 * Control: write "quit\n" to stdin, or send SIGTERM/SIGINT, to hide.  A
 *          controller writes pause/stop/abort to stdout when its buttons are
 *          clicked and accepts paused/recording on stdin to refresh its label.
 */
#define _POSIX_C_SOURCE 200809L
#include <stdint.h>
#include <stdio.h>
/* (include anchor) */
#include <stdlib.h>
#include <string.h>
#include <signal.h>
#include <unistd.h>
#include <time.h>
#include <fcntl.h>
#include <limits.h>
#include <errno.h>
#include <math.h>
#include <sys/mman.h>
#include <poll.h>
#include <wayland-client.h>
#include "layer-shell-client.h"
#include "relative-pointer-client.h"

#define MAX_OUTPUTS 16
#define OUTLINE_THICKNESS 2
#define OUTLINE_DASH 14
#define OUTLINE_GAP 6
#define OUTLINE_COLOR 0xFFFF2A2Au

typedef struct { struct wl_shm_pool *pool; struct wl_buffer *buffer; int width; int height; } Frame;

typedef struct {
    struct wl_output *output;
    char name[64];
    int x, y, phys_w, phys_h, scale, done, valid;
    int mode_w, mode_h;   /* current mode, in physical pixels */
} OutInfo;

static struct wl_display *display = NULL;
static struct wl_compositor *compositor = NULL;
static struct wl_shm *shm = NULL;
static struct zwlr_layer_shell_v1 *layer_shell = NULL;
static struct zwlr_layer_surface_v1 *layer_surface = NULL;
static struct wl_surface *surface = NULL;
static struct wl_seat *seat = NULL;
static struct wl_pointer *pointer = NULL;
static struct zwp_relative_pointer_manager_v1 *relative_manager = NULL;
static struct zwp_relative_pointer_v1 *relative_pointer = NULL;
static int relative_ready = 0;
static uint32_t relative_manager_name = 0;
static uint32_t seat_global_name = 0;

static int region_x = 0, region_y = 0, region_w = 0, region_h = 0;  /* output-local logical */
typedef enum { EDGE_NONE, EDGE_TOP, EDGE_BOTTOM, EDGE_LEFT, EDGE_RIGHT } Edge;
static Edge edge = EDGE_NONE;
typedef enum { MODE_OUTLINE, MODE_CONTROLLER } Mode;
static Mode mode = MODE_OUTLINE;
static volatile sig_atomic_t running = 1;
static int controller_paused = 0;
static double pointer_x = -1, pointer_y = -1;
static int debug_log = 0;
static char wanted_output[64];  /* optional output name */
/* Host-provided true fractional-logical size of the target output; <= 0 when
   not supplied, in which case the integer-scale estimate is used. */
static int logical_out_w = 0, logical_out_h = 0;
/* Host-provided usable work area of the target output, in the same logical
   space: the band between the compositor's reserved top and bottom insets.
   work_bottom <= 0 means "not supplied"; the full output height is used. */
static int work_top = 0, work_bottom = 0;

/* Controller card metrics. Every visual element and every pointer hit box is
   derived from these, so the drawing and the click targets cannot drift. */
#define CONTROLLER_WIDTH 340
#define CONTROLLER_HEIGHT 118
#define CONTROLLER_PAD 16              /* content inset from the card edge  */
#define CONTROLLER_BUTTON_X CONTROLLER_PAD
#define CONTROLLER_BUTTON_Y 68
#define CONTROLLER_BUTTON_W 96
#define CONTROLLER_BUTTON_H 34
#define CONTROLLER_BUTTON_GAP 10
#define CONTROLLER_HEADER_H 50
#define CONTROLLER_EDGE_GAP 8          /* space between outline and card    */
#define CONTROLLER_CORNER_MARGIN 16

static int controller_width = CONTROLLER_WIDTH, controller_height = CONTROLLER_HEIGHT;

/*
 * Client-side drag state for the controller tile.
 *
 * A layer-shell surface has no compositor-driven interactive move (that is an
 * xdg_toplevel facility), so the tile moves itself. The primary path consumes
 * relative-pointer-v1 deltas and accumulates them into controller_x/y. Those
 * deltas have a stationary reference frame: they never depend on when the
 * compositor applies the preceding layer-surface position commit. Absolute
 * wl_pointer motion remains only as a fallback when relative-pointer-v1 is not
 * available.
 *
 * controller_x / controller_y are those margins, i.e. the tile's position in
 * the same space controller_placement() computes: x is output-local logical,
 * y is relative to the work area's top inset (a TOP-anchored surface with
 * exclusive_zone 0 resolves margin N to logical work_top + N).
 */
static int controller_x = 0, controller_y = 0;
static int controller_pos_known = 0;
static int dragging = 0;
static double grab_offset_x = 0, grab_offset_y = 0;
static int surface_origin_x = 0, surface_origin_y = 0;
static double drag_accum_x = 0, drag_accum_y = 0;

static void controller_drag_to(double surface_x, double surface_y);
static void controller_drag_by(double dx, double dy);

static long long now_ms(void) {
    struct timespec ts;
    if (clock_gettime(CLOCK_MONOTONIC, &ts) != 0) return 0;
    return (long long)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

static int controller_button_x(int index) {
    return CONTROLLER_BUTTON_X + index * (CONTROLLER_BUTTON_W + CONTROLLER_BUTTON_GAP);
}

static OutInfo outputs[MAX_OUTPUTS];
static int n_outputs = 0;
static struct wl_output *target_output = NULL;
static const OutInfo *target_info = NULL;

static Frame frame = { .pool = NULL, .buffer = NULL };
static int layer_frame_ready = 0;

static void on_sig(int sig) { (void)sig; running = 0; }

static int create_pool_file(size_t size) {
    char path[] = "/tmp/snapx-outline-XXXXXX";
    int fd = mkstemp(path);
    if (fd < 0) return -1;
    unlink(path);
    if (ftruncate(fd, (off_t)size) < 0) { close(fd); return -1; }
    return fd;
}

static void release_buffer(void *data, struct wl_buffer *buffer) { (void)data; (void)buffer; }
static const struct wl_buffer_listener buffer_listener = { release_buffer };

static void draw_into(uint32_t *pixels, int w, int h) {
    /* A 2px dashed rectangle edge: red dash segments separated by fully
       transparent gaps. The surface is only two pixels thin, so a transparent
       gap cannot produce a visible black strip even if a compositor or GPU
       driver mishandles alpha - the worst case is a missing border, not an
       obscured recording. The dash runs along the surface's long axis so the
       horizontal (top/bottom) and vertical (left/right) edges share a
       pattern. */
    const uint32_t clear = 0x00000000u;
    const int period = OUTLINE_DASH + OUTLINE_GAP;
    const int horizontal = w >= h;
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++) {
            int along = horizontal ? x : y;
            pixels[(size_t)y * w + x] =
                (along % period) < OUTLINE_DASH ? OUTLINE_COLOR : clear;
        }
}

static void fill_rect(uint32_t *pixels, int w, int h, int x, int y, int rw, int rh, uint32_t colour) {
    int x0 = x < 0 ? 0 : x, y0 = y < 0 ? 0 : y;
    int x1 = x + rw > w ? w : x + rw, y1 = y + rh > h ? h : y + rh;
    for (int py = y0; py < y1; py++)
        for (int px = x0; px < x1; px++)
            pixels[(size_t)py * w + px] = colour;
}

/* wl_shm ARGB8888 uses premultiplied alpha. The controller deliberately
   leaves its outer corners transparent, so it reads as a compact card rather
   than a hard-edged opaque rectangle. */
static uint32_t colour_with_alpha(uint32_t colour, int alpha) {
    uint32_t r = (colour >> 16) & 0xff, g = (colour >> 8) & 0xff, b = colour & 0xff;
    return ((uint32_t)alpha << 24) | ((r * alpha / 255) << 16) |
           ((g * alpha / 255) << 8) | (b * alpha / 255);
}

static void blend_pixel(uint32_t *pixel, uint32_t source) {
    uint32_t sa = source >> 24, da = *pixel >> 24, inv = 255 - sa;
    uint32_t sr = (source >> 16) & 0xff, sg = (source >> 8) & 0xff, sb = source & 0xff;
    uint32_t dr = (*pixel >> 16) & 0xff, dg = (*pixel >> 8) & 0xff, db = *pixel & 0xff;
    *pixel = ((sa + da * inv / 255) << 24) |
             ((sr + dr * inv / 255) << 16) |
             ((sg + dg * inv / 255) << 8) | (sb + db * inv / 255);
}

static int rounded_coverage(int px, int py, int x, int y, int rw, int rh, int radius) {
    static const float samples[4][2] = {{0.25f,0.25f},{0.75f,0.25f},{0.25f,0.75f},{0.75f,0.75f}};
    int covered = 0;
    for (int i = 0; i < 4; i++) {
        float sx = px + samples[i][0], sy = py + samples[i][1];
        float cx = sx < x + radius ? x + radius : (sx > x + rw - radius ? x + rw - radius : sx);
        float cy = sy < y + radius ? y + radius : (sy > y + rh - radius ? y + rh - radius : sy);
        float dx = sx - cx, dy = sy - cy;
        if (dx * dx + dy * dy <= (float)radius * radius) covered++;
    }
    return covered * 255 / 4;
}

static void draw_rounded_rect(uint32_t *pixels, int w, int h, int x, int y, int rw, int rh,
                              int radius, uint32_t colour) {
    for (int py = y < 0 ? 0 : y; py < y + rh && py < h; py++)
        for (int px = x < 0 ? 0 : x; px < x + rw && px < w; px++) {
            int coverage = rounded_coverage(px, py, x, y, rw, rh, radius);
            if (coverage) blend_pixel(&pixels[(size_t)py * w + px], colour_with_alpha(colour, coverage));
        }
}

static const unsigned char *glyph_for_char(char c) {
    /* One consistent 5x7 uppercase-cap-height face. Every glyph uses the full
       7-row body and the same 5px advance box, so labels line up on a shared
       baseline instead of the previous ad-hoc mixture of heights. Lowercase
       input is folded to the same caps to keep the tile visually uniform. */
    static const unsigned char glyphs[26][7] = {
        {14,17,17,31,17,17,17},  /* A */ {30,17,17,30,17,17,30},  /* B */
        {14,17,16,16,16,17,14},  /* C */ {28,18,17,17,17,18,28},  /* D */
        {31,16,16,30,16,16,31},  /* E */ {31,16,16,30,16,16,16},  /* F */
        {14,17,16,23,17,17,15},  /* G */ {17,17,17,31,17,17,17},  /* H */
        {14,4,4,4,4,4,14},       /* I */ {7,2,2,2,2,18,12},       /* J */
        {17,18,20,24,20,18,17},  /* K */ {16,16,16,16,16,16,31},  /* L */
        {17,27,21,21,17,17,17},  /* M */ {17,25,25,21,19,19,17},  /* N */
        {14,17,17,17,17,17,14},  /* O */ {30,17,17,30,16,16,16},  /* P */
        {14,17,17,17,21,18,13},  /* Q */ {30,17,17,30,20,18,17},  /* R */
        {15,16,16,14,1,1,30},    /* S */ {31,4,4,4,4,4,4},        /* T */
        {17,17,17,17,17,17,14},  /* U */ {17,17,17,17,17,10,4},   /* V */
        {17,17,17,21,21,27,17},  /* W */ {17,17,10,4,10,17,17},   /* X */
        {17,17,10,4,4,4,4},      /* Y */ {31,1,2,4,8,16,31}       /* Z */
    };
    static const unsigned char digits[10][7] = {
        {14,17,19,21,25,17,14},{4,12,4,4,4,4,14},{14,17,1,2,4,8,31},
        {31,2,4,2,1,17,14},{2,6,10,18,31,2,2},{31,16,30,1,1,17,14},
        {6,8,16,30,17,17,14},{31,1,2,4,8,8,8},{14,17,17,14,17,17,14},
        {14,17,17,15,1,2,12}
    };
    static const unsigned char dash[7] = {0,0,0,31,0,0,0};
    static const unsigned char dot[7] = {0,0,0,0,0,12,12};
    static const unsigned char colon[7] = {0,12,12,0,12,12,0};
    if (c >= '0' && c <= '9') return digits[c - '0'];
    if (c >= 'a' && c <= 'z') c -= 'a' - 'A';
    if (c >= 'A' && c <= 'Z') return glyphs[c - 'A'];
    if (c == '-') return dash;
    if (c == '.') return dot;
    if (c == ':') return colon;
    return NULL;
}

/* Uniform metrics: a 5px glyph box, one scaled pixel of side bearing and an
   extra tracking column so 2x labels are readable rather than cramped. */
#define GLYPH_W 5
#define GLYPH_H 7

static int glyph_advance(char c, int scale, int tracking) {
    return (c == ' ' ? 3 : GLYPH_W + 1) * scale + (c == ' ' ? 0 : tracking);
}

static int text_width(const char *text, int scale, int tracking) {
    int width = 0, last = 0;
    for (; *text; text++) { last = glyph_advance(*text, scale, tracking); width += last; }
    return width > 0 ? width - (scale + tracking) : 0;
}

static int text_height(int scale) { return GLYPH_H * scale; }

static void draw_text(uint32_t *pixels, int w, int h, int x, int y, const char *text,
                      int scale, int tracking, uint32_t colour) {
    for (; *text; text++) {
        const unsigned char *bits = glyph_for_char(*text);
        if (bits) {
            for (int py = 0; py < GLYPH_H; py++)
                for (int px = 0; px < GLYPH_W; px++)
                    if (bits[py] & (1 << (GLYPH_W - 1 - px)))
                        fill_rect(pixels, w, h, x + px * scale, y + py * scale, scale, scale, colour);
        }
        x += glyph_advance(*text, scale, tracking);
    }
}

/* Centres a label inside a rect on both axes using the shared glyph metrics,
   so button captions never sit high, low or clipped. */
static void draw_text_centred(uint32_t *pixels, int w, int h, int rx, int ry, int rw, int rh,
                              const char *text, int scale, int tracking, uint32_t colour) {
    int tw = text_width(text, scale, tracking);
    int th = text_height(scale);
    draw_text(pixels, w, h, rx + (rw - tw) / 2, ry + (rh - th) / 2, text, scale, tracking, colour);
}

/* Anti-aliased disc: the status dot is small, so hard edges there read as a
   jagged artefact next to the rounded card. */
static void draw_circle(uint32_t *pixels, int w, int h, int cx, int cy, int radius, uint32_t colour) {
    static const float samples[4][2] = {{0.25f,0.25f},{0.75f,0.25f},{0.25f,0.75f},{0.75f,0.75f}};
    for (int py = cy - radius - 1; py <= cy + radius + 1; py++) {
        if (py < 0 || py >= h) continue;
        for (int px = cx - radius - 1; px <= cx + radius + 1; px++) {
            if (px < 0 || px >= w) continue;
            int covered = 0;
            for (int i = 0; i < 4; i++) {
                float dx = (px + samples[i][0]) - cx, dy = (py + samples[i][1]) - cy;
                if (dx * dx + dy * dy <= (float)radius * radius) covered++;
            }
            if (covered)
                blend_pixel(&pixels[(size_t)py * w + px], colour_with_alpha(colour, covered * 255 / 4));
        }
    }
}

/*
 * Controller card layout (340x118), all values shared with pointer_button:
 *
 *   +--------------------------------------------------+  card, r=14
 *   |  (o) RECORDING            SNAPX            |  header band, 0..50
 *   |--------------------------------------------------|  hairline divider
 *   |  [ PAUSE ]   [ STOP ]   [ ABORT ]                |  buttons, y=68..102
 *   +--------------------------------------------------+
 */
static void draw_controller(uint32_t *pixels, int w, int h) {
    const uint32_t card = 0xFF1B1F27u, header = 0xFF232935u;
    const uint32_t white = 0xFFF5F7FAu, muted = 0xFF97A3B6u;
    const uint32_t hairline = 0xFF333B49u;
    const uint32_t live = 0xFFFF4D5Eu, amber = 0xFFF5B942u;
    const uint32_t pill_live = 0xFF3A1D24u, pill_paused = 0xFF3A2F19u;
    const uint32_t accent = controller_paused ? amber : live;
    const char *status = controller_paused ? "PAUSED" : "RECORDING";
    const char *labels[3] = { controller_paused ? "RESUME" : "PAUSE", "STOP", "ABORT" };
    /* Neutral surfaces for the two safe actions, a warm surface for the
       destructive one; intent is legible without relying on the label alone. */
    const uint32_t button_bg[3] = { 0xFF2E3846u, 0xFF2E3846u, 0xFF43242Cu };
    const uint32_t button_fg[3] = { white, white, 0xFFFFD3D8u };

    /* Fully transparent margin, then a soft drop shadow under a rounded card:
       the tile reads as a floating control, not a pasted rectangle. */
    fill_rect(pixels, w, h, 0, 0, w, h, 0x00000000u);
    draw_rounded_rect(pixels, w, h, 3, 5, w - 6, h - 6, 15, 0x33000000u);
    draw_rounded_rect(pixels, w, h, 2, 3, w - 4, h - 5, 15, 0x4D000000u);
    draw_rounded_rect(pixels, w, h, 2, 2, w - 4, h - 6, 14, card);
    /* Two-tone treatment: the header band is a slightly lighter surface than
       the action area, separated by a single hairline. */
    draw_rounded_rect(pixels, w, h, 2, 2, w - 4, CONTROLLER_HEADER_H, 14, header);
    fill_rect(pixels, w, h, 2, CONTROLLER_HEADER_H - 12, w - 4, 12, header);
    fill_rect(pixels, w, h, 1, CONTROLLER_HEADER_H, w - 2, 1, hairline);

    /* Status pill: accent-tinted surface, accent dot, plain caps label. */
    int pill_h = 26, pill_y = (CONTROLLER_HEADER_H - pill_h) / 2 + 1;
    int label_w = text_width(status, 2, 1);
    int pill_w = 20 + 12 + label_w + 14;
    draw_rounded_rect(pixels, w, h, CONTROLLER_PAD, pill_y, pill_w, pill_h, pill_h / 2,
                      controller_paused ? pill_paused : pill_live);
    draw_circle(pixels, w, h, CONTROLLER_PAD + 15, pill_y + pill_h / 2, 5, accent);
    draw_text(pixels, w, h, CONTROLLER_PAD + 26, pill_y + (pill_h - text_height(2)) / 2,
              status, 2, 1, white);

    /* Right-aligned product mark keeps the header balanced at both densities. */
    int mark_w = text_width("SNAPX", 1, 2);
    draw_text(pixels, w, h, w - CONTROLLER_PAD - mark_w,
              pill_y + (pill_h - text_height(1)) / 2, "SNAPX", 1, 2, muted);

    /* Three evenly spaced buttons on one baseline; captions centred on both
       axes from the same metrics the hit boxes use. */
    for (int i = 0; i < 3; i++) {
        int x = controller_button_x(i);
        draw_rounded_rect(pixels, w, h, x, CONTROLLER_BUTTON_Y, CONTROLLER_BUTTON_W,
                          CONTROLLER_BUTTON_H, 8, button_bg[i]);
        draw_text_centred(pixels, w, h, x, CONTROLLER_BUTTON_Y, CONTROLLER_BUTTON_W,
                          CONTROLLER_BUTTON_H, labels[i], 2, 1, button_fg[i]);
    }
}

static Frame make_frame(int w, int h) {
    Frame fr = { .pool = NULL, .buffer = NULL };
    if (w <= 0 || h <= 0 || w > INT_MAX / 4) return fr;
    size_t stride = (size_t)w * 4;
    size_t size = stride * (size_t)h;
    if (h > INT_MAX || size > (size_t)INT_MAX) return fr;
    int fd = create_pool_file(size);
    if (fd < 0) return fr;
    uint8_t *data = mmap(NULL, (size_t)size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (data == MAP_FAILED) { close(fd); return fr; }
    fr.pool = wl_shm_create_pool(shm, fd, size);
    if (!fr.pool) { munmap(data, size); close(fd); return fr; }
    // wl_shm_create_pool takes ownership of the file descriptor, so closing it
    // here is required to avoid leaking one FD on every frame redraw.
    close(fd);
    fr.buffer = wl_shm_pool_create_buffer(fr.pool, 0, w, h, stride, WL_SHM_FORMAT_ARGB8888);
    if (!fr.buffer) { wl_shm_pool_destroy(fr.pool); fr.pool = NULL; munmap(data, size); return fr; }
    if (mode == MODE_CONTROLLER) draw_controller((uint32_t *)data, w, h);
    else draw_into((uint32_t *)data, w, h);
    munmap(data, (size_t)size);
    wl_buffer_add_listener(fr.buffer, &buffer_listener, NULL);
    fr.width = w;
    fr.height = h;
    return fr;
}

static void destroy_frame(Frame *fr) {
    if (!fr) return;
    if (fr->buffer) wl_buffer_destroy(fr->buffer);
    if (fr->pool) wl_shm_pool_destroy(fr->pool);
    fr->buffer = NULL; fr->pool = NULL;
    fr->width = 0;
    fr->height = 0;
}

/* Ensure a frame is allocated and attached for the layer surface. This is
   deliberately amortized: the drag path sends many position/margin commits,
   and each one can produce a configure. Reallocating and re-rendering the
   full card buffer on every configure is what made the tile feel like it was
   racing/lagging the pointer. Content does not change during a drag, so only
   the first configure (or an explicit content/state update) allocates and
   renders once; later configures just acknowledge and reuse the buffer. */
static void ensure_layer_frame(int width, int height) {
    if (layer_frame_ready && frame.buffer &&
        frame.width == width && frame.height == height) {
        return;
    }
    destroy_frame(&frame);
    frame = make_frame(width, height);
    if (frame.buffer) {
        wl_surface_attach(surface, frame.buffer, 0, 0);
        wl_surface_damage_buffer(surface, 0, 0, width, height);
        wl_surface_commit(surface);
        layer_frame_ready = 1;
    }
}

static void layer_surface_configure(void *data, struct zwlr_layer_surface_v1 *ls,
                                    uint32_t serial, uint32_t w, uint32_t h) {
    (void)data;
    zwlr_layer_surface_v1_ack_configure(ls, serial);
    if (w == 0 || w > (uint32_t)(INT_MAX / 4) || h == 0 || h > (uint32_t)INT_MAX) return;
    ensure_layer_frame((int)w, (int)h);
}

static void layer_surface_closed(void *data, struct zwlr_layer_surface_v1 *ls) {
    (void)data; (void)ls; running = 0;
}
static const struct zwlr_layer_surface_v1_listener layer_surface_listener = {
    .configure = layer_surface_configure, .closed = layer_surface_closed
};

static void output_geometry(void *data, struct wl_output *o, int x, int y,
                            int phys_w, int phys_h, int subpixel, const char *make,
                            const char *model, int transform) {
    (void)o; (void)subpixel; (void)make; (void)model; (void)transform;
    OutInfo *info = (OutInfo *)data; if (!info) return;
    info->x = x; info->y = y; info->phys_w = phys_w; info->phys_h = phys_h;
    info->valid = (phys_w > 0 && phys_h > 0);
}
static void output_scale(void *data, struct wl_output *o, int factor) {
    (void)o; OutInfo *info = (OutInfo *)data; if (info) info->scale = factor > 0 ? factor : 1;
}
static void output_name(void *data, struct wl_output *o, const char *name) {
    (void)o; OutInfo *info = (OutInfo *)data;
    if (info && name) { strncpy(info->name, name, sizeof(info->name) - 1); info->name[sizeof(info->name) - 1] = 0; }
}
static void output_done(void *data, struct wl_output *o) { (void)o; OutInfo *info = (OutInfo *)data; if (info) info->done = 1; }
static void output_mode(void *data, struct wl_output *o, uint32_t fl, int w, int h, int r) {
    (void)o; (void)r;
    /* wl_output.geometry reports the panel size in MILLIMETRES, so it cannot
       be used for placement. The current mode carries the pixel size, which
       divided by the scale gives the logical size the layer-shell margins are
       expressed in. */
    OutInfo *info = (OutInfo *)data;
    if (!info || !(fl & WL_OUTPUT_MODE_CURRENT)) return;
    if (w > 0 && h > 0) { info->mode_w = w; info->mode_h = h; }
}
static void output_description(void *data, struct wl_output *o, const char *description) {
    (void)o; (void)description;
    // The description is optional; ignore it.
}
static const struct wl_output_listener output_listener = {
    .geometry = output_geometry, .mode = output_mode, .done = output_done,
    .scale = output_scale, .name = output_name, .description = output_description
};

static void pointer_enter(void *data, struct wl_pointer *p, uint32_t serial,
                          struct wl_surface *s, wl_fixed_t sx, wl_fixed_t sy) {
    (void)data; (void)p; (void)serial; (void)s;
    pointer_x = wl_fixed_to_double(sx); pointer_y = wl_fixed_to_double(sy);
    if (debug_log) fprintf(stderr, "[%lld] ENTER sx=%.2f sy=%.2f\n", now_ms(), pointer_x, pointer_y);
}
static void pointer_leave(void *data, struct wl_pointer *p, uint32_t serial, struct wl_surface *s) {
    (void)data; (void)p; (void)serial; (void)s;
    /* A drag is held by the pointer's implicit grab, so motion keeps being
       delivered here (with coordinates outside the surface) until the button
       is released. Keep the coordinates intact while that grab is active. */
    if (debug_log) fprintf(stderr, "[%lld] LEAVE sx=%.2f sy=%.2f drag=%d\n", now_ms(), pointer_x, pointer_y, dragging);
    if (dragging) return;
    pointer_x = pointer_y = -1;
}
static void pointer_motion(void *data, struct wl_pointer *p, uint32_t time, wl_fixed_t sx, wl_fixed_t sy) {
    (void)data; (void)p; (void)time;
    pointer_x = wl_fixed_to_double(sx); pointer_y = wl_fixed_to_double(sy);
    if (debug_log) fprintf(stderr, "[%lld] MOTION sx=%.2f sy=%.2f drag=%d rel=%d\n", now_ms(), pointer_x, pointer_y, dragging, relative_ready);
    if (!dragging) return;
    if (!relative_ready) controller_drag_to(pointer_x, pointer_y);
}
static void pointer_button(void *data, struct wl_pointer *p, uint32_t serial, uint32_t time,
                           uint32_t button, uint32_t state) {
    (void)data; (void)p; (void)serial; (void)time;
    if (mode != MODE_CONTROLLER || button != 0x110) return;
    if (debug_log) fprintf(stderr, "[%lld] BTN state=%u sx=%.2f sy=%.2f drag=%d rel=%d\n", now_ms(), state, pointer_x, pointer_y, dragging, relative_ready);
    if (state != WL_POINTER_BUTTON_STATE_PRESSED) {
        dragging = 0;   /* any release ends the drag, wherever it happens */
        drag_accum_x = drag_accum_y = 0;
        return;
    }
    /* The header band (status pill + product mark) is the drag handle. It
       ends at CONTROLLER_HEADER_H, well above the button row at
       CONTROLLER_BUTTON_Y, so grabbing the tile can never swallow a
       PAUSE/STOP/ABORT click. */
    if (pointer_y >= 0 && pointer_y < CONTROLLER_HEADER_H &&
        pointer_x >= 0 && pointer_x < controller_width) {
        if (controller_pos_known) {
            grab_offset_x = pointer_x; grab_offset_y = pointer_y;
            surface_origin_x = controller_x; surface_origin_y = controller_y;
            drag_accum_x = drag_accum_y = 0;
            dragging = 1;
        }
        return;
    }
    /* Hit boxes are derived from the same metrics draw_controller uses, with a
       small forgiving inset around each drawn rect, so the visual design and
       the click targets can never drift apart. */
    const char *commands[3] = { "pause\n", "stop\n", "abort\n" };
    const int slop = 2;
    if (pointer_y < CONTROLLER_BUTTON_Y - slop ||
        pointer_y >= CONTROLLER_BUTTON_Y + CONTROLLER_BUTTON_H + slop) return;
    for (int i = 0; i < 3; i++) {
        int x = controller_button_x(i);
        if (pointer_x >= x - slop && pointer_x < x + CONTROLLER_BUTTON_W + slop) {
            fputs(commands[i], stdout); fflush(stdout);
            return;
        }
    }
}
static void pointer_axis(void *data, struct wl_pointer *p, uint32_t time, uint32_t axis, wl_fixed_t value) {
    (void)data; (void)p; (void)time; (void)axis; (void)value;
}
static void pointer_frame(void *data, struct wl_pointer *p) { (void)data; (void)p; }
static void pointer_axis_source(void *data, struct wl_pointer *p, uint32_t axis_source) {
    (void)data; (void)p; (void)axis_source;
}
static void pointer_axis_stop(void *data, struct wl_pointer *p, uint32_t time, uint32_t axis) {
    (void)data; (void)p; (void)time; (void)axis;
}
static void pointer_axis_discrete(void *data, struct wl_pointer *p, uint32_t axis, int32_t discrete) {
    (void)data; (void)p; (void)axis; (void)discrete;
}
static void pointer_axis_value120(void *data, struct wl_pointer *p, uint32_t axis, int32_t value120) {
    (void)data; (void)p; (void)axis; (void)value120;
}
static void pointer_axis_relative_direction(void *data, struct wl_pointer *p, uint32_t axis, uint32_t direction) {
    (void)data; (void)p; (void)axis; (void)direction;
}
static void pointer_warp(void *data, struct wl_pointer *p, wl_fixed_t x, wl_fixed_t y) {
    (void)data; (void)p; (void)x; (void)y;
}
static const struct wl_pointer_listener pointer_listener = {
    .enter = pointer_enter, .leave = pointer_leave, .motion = pointer_motion,
    .button = pointer_button, .axis = pointer_axis, .frame = pointer_frame,
    .axis_source = pointer_axis_source, .axis_stop = pointer_axis_stop,
    .axis_discrete = pointer_axis_discrete, .axis_value120 = pointer_axis_value120,
    .axis_relative_direction = pointer_axis_relative_direction,
#ifdef WL_POINTER_WARP_SINCE_VERSION
    /* The warp member exists only on newer libwayland headers. */
    .warp = pointer_warp
#endif
};

static void relative_motion(void *data, struct zwp_relative_pointer_v1 *rp,
                            uint32_t utime_hi, uint32_t utime_lo,
                            wl_fixed_t dx, wl_fixed_t dy,
                            wl_fixed_t dx_unaccel, wl_fixed_t dy_unaccel) {
    (void)data; (void)rp; (void)utime_hi; (void)utime_lo;
    (void)dx_unaccel; (void)dy_unaccel;
    /* A relative motion in a fixed-point 24.8 unit is at most a few thousand
       logical pixels in one event on real physical input. wl_fixed_t uses a
       signed 32-bit value, so a malformed/sentinel event (for example an
       absolute virtual device feeding INT32_MIN) would otherwise be treated
       as a multi-million-pixel jump and be clamped to a corner. Reject any
       delta whose integer part is implausible for a single motion; a real
       delta never reaches this threshold. */
    double rdx = wl_fixed_to_double(dx), rdy = wl_fixed_to_double(dy);
    if (fabs(rdx) > 16384.0 || fabs(rdy) > 16384.0) {
        if (debug_log) fprintf(stderr, "[%lld] REL_REJECT dx=%.2f dy=%.2f\n", now_ms(), rdx, rdy);
        return;
    }
    if (debug_log) fprintf(stderr, "[%lld] REL dx=%.2f dy=%.2f drag=%d rel=%d\n", now_ms(), wl_fixed_to_double(dx), wl_fixed_to_double(dy), dragging, relative_ready);
    if (!dragging) return;
    controller_drag_by(rdx, rdy);
}
static const struct zwp_relative_pointer_v1_listener relative_pointer_listener = {
    .relative_motion = relative_motion
};

static void destroy_relative_pointer(void) {
    relative_ready = 0;
    // If the relative source disappears mid-drag, the absolute fallback must
    // start from the tile's current position, not the press-time origin, or
    // the next absolute motion would jump toward the original grab point.
    surface_origin_x = controller_x;
    surface_origin_y = controller_y;
    if (relative_pointer) {
        zwp_relative_pointer_v1_destroy(relative_pointer);
        relative_pointer = NULL;
    }
}

static void create_relative_pointer(void) {
    if (relative_pointer || !relative_manager || !pointer) return;
    relative_pointer = zwp_relative_pointer_manager_v1_get_relative_pointer(
        relative_manager, pointer);
    if (!relative_pointer) return;
    zwp_relative_pointer_v1_add_listener(
        relative_pointer, &relative_pointer_listener, NULL);
    relative_ready = 1;
}

static void seat_capabilities(void *data, struct wl_seat *s, enum wl_seat_capability caps) {
    (void)data;
    if ((caps & WL_SEAT_CAPABILITY_POINTER) && !pointer) {
        pointer = wl_seat_get_pointer(s);
        wl_pointer_add_listener(pointer, &pointer_listener, NULL);
        create_relative_pointer();
    } else if (!(caps & WL_SEAT_CAPABILITY_POINTER) && pointer) {
        /* If the pointer disappears before its release event, the implicit
           grab is gone too; never leave a half-held drag behind. */
        dragging = 0;
        drag_accum_x = drag_accum_y = 0;
        pointer_x = pointer_y = -1;
        destroy_relative_pointer();
        if (pointer && wl_proxy_get_version((struct wl_proxy *)pointer) >= WL_POINTER_RELEASE_SINCE_VERSION)
            wl_pointer_release(pointer);
        else
            wl_pointer_destroy(pointer);
        pointer = NULL;
    }
}
static void seat_global_name_event(void *data, struct wl_seat *s, const char *name) {
    (void)data; (void)s; (void)name;
}
static const struct wl_seat_listener seat_listener = {
    .capabilities = seat_capabilities, .name = seat_global_name_event
};

static void registry_global(void *data, struct wl_registry *reg, uint32_t name,
                            const char *iface, uint32_t version) {
    (void)data;
    if (strcmp(iface, wl_compositor_interface.name) == 0 && version >= 4) {
        compositor = wl_registry_bind(reg, name, &wl_compositor_interface, 4);
    } else if (strcmp(iface, wl_shm_interface.name) == 0) {
        shm = wl_registry_bind(reg, name, &wl_shm_interface, 1);
    } else if (strcmp(iface, zwlr_layer_shell_v1_interface.name) == 0) {
        layer_shell = wl_registry_bind(reg, name, &zwlr_layer_shell_v1_interface, 4);
    } else if (strcmp(iface, zwp_relative_pointer_manager_v1_interface.name) == 0 &&
               !relative_manager) {
        relative_manager = wl_registry_bind(
            reg, name, &zwp_relative_pointer_manager_v1_interface, 1);
        relative_manager_name = name;
        create_relative_pointer();
    } else if (strcmp(iface, wl_seat_interface.name) == 0 && !seat) {
        uint32_t bind_version = version < 5 ? version : 5;
        seat = wl_registry_bind(reg, name, &wl_seat_interface, bind_version);
        seat_global_name = name;
        wl_seat_add_listener(seat, &seat_listener, NULL);
    } else if (strcmp(iface, wl_output_interface.name) == 0 && version >= 4 && n_outputs < MAX_OUTPUTS) {
        OutInfo *info = &outputs[n_outputs];
        memset(info, 0, sizeof(*info)); info->scale = 1;
        info->output = wl_registry_bind(reg, name, &wl_output_interface, 4);
        wl_output_add_listener(info->output, &output_listener, info);
        n_outputs++;
    }
}
static void registry_global_remove(void *data, struct wl_registry *reg, uint32_t name) {
    (void)data; (void)reg;
    if (relative_manager && name == relative_manager_name) {
        destroy_relative_pointer();
        zwp_relative_pointer_manager_v1_destroy(relative_manager);
        relative_manager = NULL;
        relative_manager_name = 0;
    }
    // Also tear down a removed seat rather than retaining a stale pointer and
    // preventing the replacement seat from being bound.
    if (seat && name == seat_global_name) {
        if (pointer) {
            dragging = 0;
            drag_accum_x = drag_accum_y = 0;
            pointer_x = pointer_y = -1;
            destroy_relative_pointer();
            if (pointer && wl_proxy_get_version((struct wl_proxy *)pointer) >= WL_POINTER_RELEASE_SINCE_VERSION)
                wl_pointer_release(pointer);
            else
                wl_pointer_destroy(pointer);
            pointer = NULL;
        }
        if (wl_proxy_get_version((struct wl_proxy *)seat) >= WL_SEAT_RELEASE_SINCE_VERSION)
            wl_seat_release(seat);
        else
            wl_seat_destroy(seat);
        seat = NULL;
        seat_global_name = 0;
    }
}
static const struct wl_registry_listener registry_listener = {
    .global = registry_global, .global_remove = registry_global_remove
};

static void select_target(void) {
    if (wanted_output[0]) {
        for (int i = 0; i < n_outputs; i++) {
            if (outputs[i].output && strcmp(outputs[i].name, wanted_output) == 0) {
                target_output = outputs[i].output; target_info = &outputs[i]; return;
            }
        }
    }
    /* Fallback: first output with valid geometry. */
    for (int i = 0; i < n_outputs; i++) {
        if (outputs[i].output && outputs[i].valid) {
            target_output = outputs[i].output; target_info = &outputs[i]; return;
        }
    }
    if (n_outputs > 0) { target_output = outputs[0].output; target_info = &outputs[0]; }
}

/*
 * True logical size of the target output, in the coordinate space the
 * compositor interprets layer-shell margins in.
 *
 * The host-provided --logical-w/--logical-h win whenever they are present:
 * wl_output.scale is an integer, so on a fractional-scaled output (2560x1440
 * at scale 1.6 advertises scale 2) mode/scale under-reports the space by a
 * large factor and every right/left placement test would be rejected against
 * a phantom 1280x720 output. The mode/integer-scale path remains only as a
 * fallback for callers that do not pass the true size.
 */
static int output_logical_size(const OutInfo *info, int *out_w, int *out_h) {
    if (logical_out_w > 0 && logical_out_h > 0) {
        *out_w = logical_out_w;
        *out_h = logical_out_h;
        return 1;
    }
    if (!info || info->mode_w <= 0 || info->mode_h <= 0) return 0;
    int scale = info->scale > 0 ? info->scale : 1;
    *out_w = info->mode_w / scale;
    *out_h = info->mode_h / scale;
    return *out_w > 0 && *out_h > 0;
}

/*
 * Absolute-motion fallback for compositors without relative-pointer-v1. Move
 * the tile to the position implied by the pointer grab, clamped so it always
 * stays fully on the output and inside the compositor's usable work area.
 *
 * wl_pointer coordinates are wl_surface coordinates. With the default buffer
 * scale of 1 they use the same logical units as layer-shell sizes and margins,
 * including on a fractionally-scaled output. Adding the last committed margin
 * reconstructs the pointer's output-logical position; subtracting the fixed
 * press-time grab offset then gives the desired new margin. Updating the saved
 * origin after every commit is required by this fallback because subsequent
 * motion is relative to the surface's new position. The primary relative-delta
 * path below intentionally has no dependency on this moving reference frame.
 *
 * The surface is re-anchored to TOP|LEFT unconditionally: the initial
 * placement may have used any corner anchor, and mixing a BOTTOM/RIGHT anchor
 * with a top-left position would make the margins mean the opposite of what
 * was computed. Once the user drags, position is expressed one way only.
 *
 * Horizontal clamp is against the full output width; vertical clamp is
 * against the work-area band, which is the same range controller_placement()
 * treats as legal, so a dragged tile cannot end up under a reserved dock.
 */
static void controller_drag_to(double surface_x, double surface_y) {
    if (!controller_pos_known || !layer_surface || !surface) return;
    int output_w = 0, output_h = 0;
    if (!output_logical_size(target_info, &output_w, &output_h)) return;

    int work_h = output_h;
    if (work_bottom > work_top) {
        work_h = work_bottom - work_top;
        if (work_h > output_h) work_h = output_h;
    }

    int max_x = output_w - controller_width;
    int max_y = work_h - controller_height;
    if (max_x < 0) max_x = 0;
    if (max_y < 0) max_y = 0;

    double gx = surface_origin_x + surface_x;
    double gy = surface_origin_y + surface_y;
    double desired_x = gx - grab_offset_x;
    double desired_y = gy - grab_offset_y;
    int nx = (int)(desired_x < 0 ? desired_x - 0.5 : desired_x + 0.5);
    int ny = (int)(desired_y < 0 ? desired_y - 0.5 : desired_y + 0.5);
    if (nx < 0) nx = 0; else if (nx > max_x) nx = max_x;
    if (ny < 0) ny = 0; else if (ny > max_y) ny = max_y;

    /* Never let a drag put the controls over recorded pixels. Preserve the
       outside edge from which the card approached the region; the user can
       still route it around a corner and continue on another side. */
    int region_right = region_x + region_w;
    int region_bottom = region_y + region_h;
    if (region_w > 1 && region_h > 1 &&
        nx < region_right && nx + controller_width > region_x &&
        ny < region_bottom && ny + controller_height > region_y) {
        if (controller_x >= region_right) {
            nx = region_right + CONTROLLER_EDGE_GAP;
        } else if (controller_x + controller_width <= region_x) {
            nx = region_x - CONTROLLER_EDGE_GAP - controller_width;
        } else if (controller_y >= region_bottom) {
            ny = region_bottom + CONTROLLER_EDGE_GAP;
        } else if (controller_y + controller_height <= region_y) {
            ny = region_y - CONTROLLER_EDGE_GAP - controller_height;
        }
    }
    if (nx == controller_x && ny == controller_y) return;

    controller_x = nx; controller_y = ny;
    zwlr_layer_surface_v1_set_anchor(layer_surface,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT);
    zwlr_layer_surface_v1_set_margin(layer_surface, controller_y, 0, 0, controller_x);
    wl_surface_commit(surface);
    surface_origin_x = controller_x; surface_origin_y = controller_y;
}

/*
 * Move the tile in the stationary reference frame supplied by
 * relative-pointer-v1. controller_x/y are the sole accumulated position;
 * unlike the absolute fallback above, this path never reconstructs pointer
 * position from the moving wl_surface or updates surface_origin_x/y.
 */
static void controller_drag_by(double dx, double dy) {
    if (!controller_pos_known || !layer_surface || !surface) return;
    int output_w = 0, output_h = 0;
    if (!output_logical_size(target_info, &output_w, &output_h)) return;

    int work_h = output_h;
    if (work_bottom > work_top) {
        work_h = work_bottom - work_top;
        if (work_h > output_h) work_h = output_h;
    }

    int max_x = output_w - controller_width;
    int max_y = work_h - controller_height;
    if (max_x < 0) max_x = 0;
    if (max_y < 0) max_y = 0;

    /* Accumulate fractional deltas instead of rounding each event: a 0.4 px
       motion repeats forever on a high-resolution device, and per-event
       rounding would discard it and make the tile appear stuck. Round only
       the cumulative target, keeping the remainder for the next motion. */
    drag_accum_x += dx;
    drag_accum_y += dy;
    int move_x = (int)round(drag_accum_x);
    int move_y = (int)round(drag_accum_y);
    int nx = controller_x + move_x;
    int ny = controller_y + move_y;
    if (debug_log) fprintf(stderr, "[%lld] DRAG_BY dx=%.2f dy=%.2f from=(%d,%d) to=(%d,%d) drag=%d\n", now_ms(), dx, dy, controller_x, controller_y, nx, ny, dragging);
    if (nx < 0) nx = 0; else if (nx > max_x) nx = max_x;
    if (ny < 0) ny = 0; else if (ny > max_y) ny = max_y;

    /* Never let a drag put the controls over recorded pixels. Preserve the
       outside edge from which the card approached the region; the user can
       still route it around a corner and continue on another side. */
    int region_right = region_x + region_w;
    int region_bottom = region_y + region_h;
    if (region_w > 1 && region_h > 1 &&
        nx < region_right && nx + controller_width > region_x &&
        ny < region_bottom && ny + controller_height > region_y) {
        if (controller_x >= region_right) {
            nx = region_right + CONTROLLER_EDGE_GAP;
        } else if (controller_x + controller_width <= region_x) {
            nx = region_x - CONTROLLER_EDGE_GAP - controller_width;
        } else if (controller_y >= region_bottom) {
            ny = region_bottom + CONTROLLER_EDGE_GAP;
        } else if (controller_y + controller_height <= region_y) {
            ny = region_y - CONTROLLER_EDGE_GAP - controller_height;
        }
    }


    // Consume the requested rounded deltas even when clamping suppressed the
    // movement. Otherwise repeated input against a boundary accumulates an
    // ever-larger remainder that must be unwound before opposite motion can
    // move the tile again.
    drag_accum_x -= move_x;
    drag_accum_y -= move_y;

    if (nx == controller_x && ny == controller_y) return;

    controller_x = nx; controller_y = ny;
    zwlr_layer_surface_v1_set_anchor(layer_surface,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT);
    zwlr_layer_surface_v1_set_margin(layer_surface, controller_y, 0, 0, controller_x);
    wl_surface_commit(surface);
    if (debug_log) fprintf(stderr, "[%lld] DRAG_COMMIT x=%d y=%d\n", now_ms(), controller_x, controller_y);
}

/*
 * Affix the control tile just outside the capture rectangle, aligned with the
 * drawn outline, and fall back to a screen corner when there is no room.
 *
 * Placement order:
 *   1. immediately right of the region, top-aligned with it;
 *   2. immediately left of the region, top-aligned with it;
 *   3. corner fallback (see controller_corner).
 *
 * The tile is never allowed to overlap the recorded region: an edge placement
 * is only accepted when the whole card fits in the space outside that edge and
 * inside the output. Degenerate geometry (w/h <= 1, the historical 1x1
 * placeholder, a region that fills the output, or an output whose logical size
 * is unknown) always takes the corner fallback.
 *
 * Returns 1 when the anchor/margin outputs describe an edge placement, and 0
 * for the corner fallback.
 */
static int controller_placement(uint32_t *anchor, int *margin_top, int *margin_left) {
    int output_w = 0, output_h = 0;
    if (!output_logical_size(target_info, &output_w, &output_h)) return 0;

    /* Degenerate or placeholder geometry carries no usable edge. */
    if (region_w <= 1 || region_h <= 1) return 0;
    if (region_x < 0 || region_y < 0) return 0;
    if (region_x + region_w > output_w || region_y + region_h > output_h) return 0;
    /* A full-output region leaves no outside space on any edge. */
    if (region_w >= output_w && region_h >= output_h) return 0;
    if (controller_width > output_w || controller_height > output_h) return 0;

    /* Vertically align with the region's top edge, nudged so the card stays
       fully inside the compositor's usable work area for regions near the
       bottom of the output.

       `top` is a layer-shell TOP margin, and a surface with exclusive_zone 0
       is laid out inside the work area, so margin N resolves to logical
       work_top + N. region_y arrives in that same work-area-relative space
       (the host already subtracted the reserved top inset), so the usable
       band for the margin is [0, work_h - controller_height].

       Clamping against the full output height instead would let a card for a
       bottom region slide under a reserved bottom dock: on a 900-high output
       with insets [top 26, bottom 53] the work area is 821 tall, so the last
       legal margin is 703 (logical y 729..847), not 782. */
    int work_h = output_h;
    if (work_bottom > work_top) {
        work_h = work_bottom - work_top;
        if (work_h > output_h) work_h = output_h;
    }
    /* A work area too short for the card has no valid edge placement; the
       corner fallback keeps the tile visible instead. */
    if (controller_height > work_h) return 0;

    int top = region_y;
    if (top + controller_height > work_h) top = work_h - controller_height;
    if (top < 0) top = 0;

    int right_left = region_x + region_w + CONTROLLER_EDGE_GAP;
    if (right_left + controller_width <= output_w) {
        *anchor = ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT;
        *margin_top = top; *margin_left = right_left;
        return 1;
    }

    int left_left = region_x - CONTROLLER_EDGE_GAP - controller_width;
    if (left_left >= 0) {
        *anchor = ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT;
        *margin_top = top; *margin_left = left_left;
        return 1;
    }

    return 0;
}

/*
 * Corner fallback. Bottom-right is preferred; when the region's geometry is
 * usable and a bottom-right card would still land on the recorded pixels, the
 * first corner that stays clear of the region is used instead, so the tile is
 * always visible and never covers the capture.
 */
static void controller_corner(uint32_t *anchor, int *margin_top, int *margin_bottom,
                              int *margin_left, int *margin_right) {
    static const uint32_t corners[4] = {
        ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM | ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM | ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT
    };
    const int m = CONTROLLER_CORNER_MARGIN;
    int chosen = 0;

    int output_w = 0, output_h = 0;
    if (region_w > 1 && region_h > 1 && output_logical_size(target_info, &output_w, &output_h)) {
        {
            for (int i = 0; i < 4; i++) {
                int right = (corners[i] & ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT) != 0;
                int bottom = (corners[i] & ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM) != 0;
                int cx = right ? output_w - m - controller_width : m;
                int cy = bottom ? output_h - m - controller_height : m;
                int overlaps = cx < region_x + region_w && cx + controller_width > region_x &&
                               cy < region_y + region_h && cy + controller_height > region_y;
                if (!overlaps) { chosen = i; break; }
            }
        }
    }

    *anchor = corners[chosen];
    int right = (corners[chosen] & ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT) != 0;
    int bottom = (corners[chosen] & ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM) != 0;
    *margin_top = bottom ? 0 : m;
    *margin_bottom = bottom ? m : 0;
    *margin_left = right ? 0 : m;
    *margin_right = right ? m : 0;
}

int main(int argc, char **argv) {
    if (argc < 5) { fprintf(stderr, "usage: snapx-outline X Y W H --edge top|bottom|left|right [--output NAME]\n"); return 2; }
    region_x = atoi(argv[1]); region_y = atoi(argv[2]);
    region_w = atoi(argv[3]); region_h = atoi(argv[4]);
    for (int i = 5; i < argc; i++) {
        if (strcmp(argv[i], "--edge") == 0 && i + 1 < argc) {
            const char *value = argv[++i];
            if (strcmp(value, "top") == 0) edge = EDGE_TOP;
            else if (strcmp(value, "bottom") == 0) edge = EDGE_BOTTOM;
            else if (strcmp(value, "left") == 0) edge = EDGE_LEFT;
            else if (strcmp(value, "right") == 0) edge = EDGE_RIGHT;
        }
        else if (strcmp(argv[i], "--controller") == 0) { mode = MODE_CONTROLLER; }
        else if (strcmp(argv[i], "--debug") == 0) { debug_log = 1; }
        else if (strcmp(argv[i], "--output") == 0 && i + 1 < argc) { strncpy(wanted_output, argv[++i], sizeof(wanted_output) - 1); }
        else if (strcmp(argv[i], "--logical-w") == 0 && i + 1 < argc) { logical_out_w = atoi(argv[++i]); }
        else if (strcmp(argv[i], "--logical-h") == 0 && i + 1 < argc) { logical_out_h = atoi(argv[++i]); }
        else if (strcmp(argv[i], "--work-top") == 0 && i + 1 < argc) { work_top = atoi(argv[++i]); }
        else if (strcmp(argv[i], "--work-bottom") == 0 && i + 1 < argc) { work_bottom = atoi(argv[++i]); }
    }
    /* Only a complete, positive pair is usable; a partial one would mix the
       host's fractional space with the integer-scale estimate. */
    if (logical_out_w <= 0 || logical_out_h <= 0) { logical_out_w = 0; logical_out_h = 0; }
    /* Likewise the work area: an inverted or partial band would clamp against
       a phantom height, so drop it and fall back to the full output. */
    if (work_top < 0 || work_bottom <= work_top) { work_top = 0; work_bottom = 0; }
    /* An outline needs a real rectangle and an edge. The controller is
       defensive by design: degenerate geometry is not an error there, it just
       selects the corner fallback. */
    if (mode == MODE_OUTLINE && (region_w <= 0 || region_h <= 0 || edge == EDGE_NONE)) {
        fprintf(stderr, "invalid region or edge\n"); return 2;
    }

    signal(SIGTERM, on_sig); signal(SIGINT, on_sig);
    display = wl_display_connect(NULL);
    if (!display) { fprintf(stderr, "no wayland display\n"); return 1; }
    struct wl_registry *registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &registry_listener, NULL);
    wl_display_roundtrip(display); wl_display_roundtrip(display);
    if (!compositor) { fprintf(stderr, "no wl_compositor\n"); return 1; }
    if (!shm)         { fprintf(stderr, "no wl_shm\n"); return 1; }
    if (!layer_shell) { fprintf(stderr, "no zwlr_layer_shell_v1\n"); return 3; }

    select_target();

    surface = wl_compositor_create_surface(compositor);

    // Outline edges are click-through; the compact recording controller is
    // deliberately interactive and retains the default full-surface region.
    struct wl_region *empty_input = NULL;
    if (mode == MODE_OUTLINE) {
        empty_input = wl_compositor_create_region(compositor);
        wl_surface_set_input_region(surface, empty_input);
    }

    layer_surface = zwlr_layer_shell_v1_get_layer_surface(
        layer_shell, surface, target_output, ZWLR_LAYER_SHELL_V1_LAYER_OVERLAY,
        "snapx-recording-outline");
    zwlr_layer_surface_v1_add_listener(layer_surface, &layer_surface_listener, NULL);
    int surface_w = 1, surface_h = 1;
    int margin_x = region_x, margin_y = region_y;
    if (mode == MODE_CONTROLLER) {
        surface_w = controller_width; surface_h = controller_height;
        uint32_t anchor = ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM | ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT;
        int m_top = 0, m_right = CONTROLLER_CORNER_MARGIN;
        int m_bottom = CONTROLLER_CORNER_MARGIN, m_left = 0;
        /* Preferred: affixed to the side of the recorded region, aligned with
           the outline. Otherwise a screen corner clear of the region. */
        if (!controller_placement(&anchor, &m_top, &m_left)) {
            controller_corner(&anchor, &m_top, &m_bottom, &m_left, &m_right);
        } else {
            m_bottom = 0; m_right = 0;
        }
        /* Seed the drag origin from wherever the initial placement landed.
           Edge placements are already TOP|LEFT margins; a corner fallback is
           converted into the same top-left space so the first drag continues
           from the tile's actual position instead of snapping to a corner.
           Without a known logical output size there is nothing to clamp a
           drag against, so the tile simply stays fixed. */
        int out_w = 0, out_h = 0;
        if (output_logical_size(target_info, &out_w, &out_h)) {
            int work_h = out_h;
            if (work_bottom > work_top) {
                work_h = work_bottom - work_top;
                if (work_h > out_h) work_h = out_h;
            }
            int right = (anchor & ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT) != 0;
            int bottom = (anchor & ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM) != 0;
            controller_x = right ? out_w - m_right - controller_width : m_left;
            controller_y = bottom ? work_h - m_bottom - controller_height : m_top;
            if (controller_x < 0) controller_x = 0;
            if (controller_y < 0) controller_y = 0;
            controller_pos_known = 1;
        }
        zwlr_layer_surface_v1_set_size(layer_surface, (uint32_t)surface_w, (uint32_t)surface_h);
        zwlr_layer_surface_v1_set_anchor(layer_surface, anchor);
        zwlr_layer_surface_v1_set_exclusive_zone(layer_surface, 0);
        zwlr_layer_surface_v1_set_margin(layer_surface, m_top, m_right, m_bottom, m_left);
        zwlr_layer_surface_v1_set_keyboard_interactivity(layer_surface, 0);
        wl_surface_commit(surface);
        wl_display_roundtrip(display);
        goto event_loop;
    }
    switch (edge) {
        case EDGE_TOP:
            surface_w = region_w; surface_h = OUTLINE_THICKNESS;
            margin_x = region_x; margin_y = region_y - OUTLINE_THICKNESS;
            break;
        case EDGE_BOTTOM:
            surface_w = region_w; surface_h = OUTLINE_THICKNESS;
            margin_x = region_x; margin_y = region_y + region_h;
            break;
        case EDGE_LEFT:
            surface_w = OUTLINE_THICKNESS; surface_h = region_h;
            margin_x = region_x - OUTLINE_THICKNESS; margin_y = region_y;
            break;
        case EDGE_RIGHT:
            surface_w = OUTLINE_THICKNESS; surface_h = region_h;
            margin_x = region_x + region_w; margin_y = region_y;
            break;
        default: break;
    }
    /* Edges above/left of an output have nowhere safe to draw. Refuse them
       instead of allowing a compositor to clamp them onto captured pixels. */
    if (margin_x < 0 || margin_y < 0) return 0;
    zwlr_layer_surface_v1_set_size(layer_surface, (uint32_t)surface_w, (uint32_t)surface_h);
    zwlr_layer_surface_v1_set_anchor(layer_surface,
        ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT);
    zwlr_layer_surface_v1_set_exclusive_zone(layer_surface, 0);
    zwlr_layer_surface_v1_set_margin(layer_surface,
        margin_y, 0, 0, margin_x);
    zwlr_layer_surface_v1_set_keyboard_interactivity(layer_surface, 0);
    wl_surface_commit(surface);
    if (empty_input) wl_region_destroy(empty_input);
    wl_display_roundtrip(display);

event_loop:
    struct pollfd fds[2];
    char stdin_buf[256];
    while (running) {
        fds[0].fd = wl_display_get_fd(display); fds[0].events = POLLIN; fds[0].revents = 0;
        fds[1].fd = STDIN_FILENO; fds[1].events = POLLIN; fds[1].revents = 0;
        wl_display_flush(display);
        int pr = poll(fds, 2, 200);
        if (pr < 0) { if (errno == EINTR) continue; break; }
        // Track errors/hangs on both descriptors. A pipe/display closure is
        // often delivered as POLLHUP without POLLIN, so only waiting for
        // POLLIN would spin instead of exiting with the parent.
        if ((fds[0].revents & (POLLERR | POLLHUP | POLLNVAL)) != 0) { running = 0; break; }
        if (fds[0].revents & POLLIN) { if (wl_display_dispatch(display) < 0) { running = 0; break; } }
        if ((fds[1].revents & (POLLERR | POLLHUP | POLLNVAL)) != 0) { running = 0; break; }
        if (fds[1].revents & POLLIN) {
            ssize_t n = read(STDIN_FILENO, stdin_buf, sizeof(stdin_buf) - 1);
            if (n <= 0) { running = 0; }
            else {
                stdin_buf[n] = 0;
                if (strstr(stdin_buf, "quit")) running = 0;
                else if (mode == MODE_CONTROLLER && (strstr(stdin_buf, "paused") || strstr(stdin_buf, "recording"))) {
                    controller_paused = strstr(stdin_buf, "paused") != NULL;
                    /* State changed, so re-render the existing fixed-size
                       controller card. Position is unrelated to a state
                       update; reuse the frame machinery and do not touch
                       anchors/margins here. */
                    layer_frame_ready = 0;
                    ensure_layer_frame(controller_width, controller_height);
                }
            }
        }
    }
    if (layer_surface) zwlr_layer_surface_v1_destroy(layer_surface);
    if (surface) wl_surface_destroy(surface);
    destroy_relative_pointer();
    if (pointer) {
        if (pointer && wl_proxy_get_version((struct wl_proxy *)pointer) >= WL_POINTER_RELEASE_SINCE_VERSION)
            wl_pointer_release(pointer);
        else
            wl_pointer_destroy(pointer);
    }
    if (relative_manager) zwp_relative_pointer_manager_v1_destroy(relative_manager);
    if (seat) {
        if (wl_proxy_get_version((struct wl_proxy *)seat) >= WL_SEAT_RELEASE_SINCE_VERSION)
            wl_seat_release(seat);
        else
            wl_seat_destroy(seat);
    }
    destroy_frame(&frame);
    if (compositor) wl_compositor_destroy(compositor);
    if (shm) wl_shm_destroy(shm);
    if (layer_shell) zwlr_layer_shell_v1_destroy(layer_shell);
    wl_display_disconnect(display);
    return 0;
}
