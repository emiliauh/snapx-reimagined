// SPDX-License-Identifier: GPL-3.0-or-later

#import <AppKit/AppKit.h>

typedef void (*SnapXTrayClickCallback)(void);

enum {
    SnapXTrayClickIgnored = 0,
    SnapXTrayClickPrimary = 1,
    SnapXTrayClickMenu = 2,
    SnapXTrayClickConsume = 3
};

static SnapXTrayClickCallback snapxClickCallback;
static id snapxEventMonitor;

__attribute__((visibility("default"))) int snapx_tray_classify_event(
    unsigned long eventType,
    unsigned long modifierFlags) {
    if (eventType == NSEventTypeRightMouseUp ||
        (eventType == NSEventTypeLeftMouseUp &&
         (modifierFlags & NSEventModifierFlagControl) != 0)) {
        return SnapXTrayClickMenu;
    }
    if (eventType == NSEventTypeLeftMouseUp)
        return SnapXTrayClickPrimary;
    if (eventType == NSEventTypeLeftMouseDown &&
        (modifierFlags & NSEventModifierFlagControl) == 0)
        return SnapXTrayClickConsume;
    return SnapXTrayClickIgnored;
}

static BOOL snapxEventTargetsStatusButton(NSEvent *event) {
    NSWindow *window = event.window;
    NSView *contentView = window.contentView;
    if (!window || !contentView) return NO;

    NSPoint point = [contentView convertPoint:event.locationInWindow fromView:nil];
    NSView *view = [contentView hitTest:point] ?: contentView;
    while (view) {
        if ([view isKindOfClass:NSStatusBarButton.class]) return YES;
        view = view.superview;
    }
    return NO;
}

static void snapxSetRecordingEnabledOnMainThread(BOOL enabled) {
    if (!enabled) {
        if (snapxEventMonitor) {
            [NSEvent removeMonitor:snapxEventMonitor];
            snapxEventMonitor = nil;
        }
        return;
    }

    if (snapxEventMonitor) return;
    snapxEventMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:
        NSEventMaskLeftMouseDown | NSEventMaskLeftMouseUp
        handler:^NSEvent *(NSEvent *event) {
            if (!snapxEventTargetsStatusButton(event)) {
                return event;
            }

            // Consume both halves of an ordinary primary click so AppKit does
            // not open the NSStatusItem menu. Dispatch the stop action once on
            // mouse-up. Right-click and Control-click never enter this path.
            int action = snapx_tray_classify_event(
                (unsigned long)event.type,
                (unsigned long)event.modifierFlags);
            if (action == SnapXTrayClickMenu || action == SnapXTrayClickIgnored)
                return event;
            if (action == SnapXTrayClickPrimary && snapxClickCallback)
                snapxClickCallback();
            return nil;
        }];
}

__attribute__((visibility("default"))) int snapx_tray_initialize(
    SnapXTrayClickCallback callback) {
    snapxClickCallback = callback;
    return NSClassFromString(@"NSStatusBarButton") != nil ? 1 : 0;
}

__attribute__((visibility("default"))) void snapx_tray_set_recording_enabled(int enabled) {
    void (^update)(void) = ^{
        snapxSetRecordingEnabledOnMainThread(enabled != 0);
    };
    if (NSThread.isMainThread) update();
    else dispatch_async(dispatch_get_main_queue(), update);
}

__attribute__((visibility("default"))) void snapx_tray_shutdown(void) {
    void (^shutdown)(void) = ^{
        snapxSetRecordingEnabledOnMainThread(NO);
        snapxClickCallback = NULL;
    };
    if (NSThread.isMainThread) shutdown();
    else dispatch_async(dispatch_get_main_queue(), shutdown);
}
