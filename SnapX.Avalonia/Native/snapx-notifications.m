// SPDX-License-Identifier: GPL-3.0-or-later
#import <Foundation/Foundation.h>
#import <UserNotifications/UserNotifications.h>

typedef void (*SnapXNotificationCallback)(int64_t token, int status);
// status: 0 denied, 1 submitted, 2 clicked, 3 dismissed, -1 failed.
static SnapXNotificationCallback callback;
static NSMutableArray<void (^)(BOOL)> *authorizationWaiters;
static BOOL requestedAuthorization;

@interface SnapXNotificationDelegate : NSObject <UNUserNotificationCenterDelegate>
@end
@implementation SnapXNotificationDelegate
- (void)userNotificationCenter:(UNUserNotificationCenter *)center
      willPresentNotification:(UNNotification *)notification
        withCompletionHandler:(void (^)(UNNotificationPresentationOptions))completionHandler {
    completionHandler(UNNotificationPresentationOptionBanner | UNNotificationPresentationOptionList);
}
- (void)userNotificationCenter:(UNUserNotificationCenter *)center
 didReceiveNotificationResponse:(UNNotificationResponse *)response
        withCompletionHandler:(void (^)(void))completionHandler {
    int64_t token = [response.notification.request.identifier longLongValue];
    if (callback) callback(token, [response.actionIdentifier isEqualToString:UNNotificationDefaultActionIdentifier] ? 2 : 3);
    completionHandler();
}
@end

static BOOL available(void) {
    return NSBundle.mainBundle.bundleIdentifier.length > 0 &&
        [NSBundle.mainBundle.bundleURL.pathExtension.lowercaseString isEqualToString:@"app"];
}

__attribute__((visibility("default"))) int snapx_notifications_available(void) {
    @autoreleasepool { return available() ? 1 : 0; }
}

static void authorize(void (^completion)(BOOL)) {
    // Called on the main queue, including all changes to the waiter collection.
    if (authorizationWaiters) { [authorizationWaiters addObject:[completion copy]]; return; }
    authorizationWaiters = [NSMutableArray arrayWithObject:[completion copy]];
    void (^finish)(BOOL) = ^(BOOL granted) {
        dispatch_async(dispatch_get_main_queue(), ^{
            NSArray *waiters = authorizationWaiters;
            authorizationWaiters = nil;
            for (void (^waiter)(BOOL) in waiters) waiter(granted);
        });
    };
    UNUserNotificationCenter *center = UNUserNotificationCenter.currentNotificationCenter;
    [center getNotificationSettingsWithCompletionHandler:^(UNNotificationSettings *settings) {
        dispatch_async(dispatch_get_main_queue(), ^{
            if (settings.authorizationStatus == UNAuthorizationStatusNotDetermined && !requestedAuthorization) {
                requestedAuthorization = YES;
                [center requestAuthorizationWithOptions:UNAuthorizationOptionAlert | UNAuthorizationOptionSound
                                      completionHandler:^(BOOL granted, NSError *error) { finish(granted && !error); }];
            } else {
                finish(settings.authorizationStatus == UNAuthorizationStatusAuthorized ||
                       settings.authorizationStatus == UNAuthorizationStatusProvisional);
            }
        });
    }];
}

__attribute__((visibility("default"))) void snapx_notifications_send(
    const char *title, const char *body, int64_t token, SnapXNotificationCallback resultCallback) {
    @autoreleasepool {
        if (!available()) { if (resultCallback) resultCallback(token, -1); return; }
        NSString *notificationTitle = title ? [NSString stringWithUTF8String:title] : @"SnapX";
        NSString *notificationBody = body ? [NSString stringWithUTF8String:body] : @"";
        dispatch_async(dispatch_get_main_queue(), ^{
            @try {
                callback = resultCallback;
                static SnapXNotificationDelegate *delegate;
                if (!delegate) delegate = [SnapXNotificationDelegate new];
                UNUserNotificationCenter *center = UNUserNotificationCenter.currentNotificationCenter;
                center.delegate = delegate;
                authorize(^(BOOL granted) {
                    if (!granted) { if (resultCallback) resultCallback(token, 0); return; }
                    UNMutableNotificationContent *content = [UNMutableNotificationContent new];
                    content.title = notificationTitle ?: @"SnapX";
                    content.body = notificationBody ?: @"";
                    // SnapX's existing sound setting owns completion sounds.
                    NSString *identifier = [NSString stringWithFormat:@"%lld", (long long)token];
                    UNNotificationRequest *request = [UNNotificationRequest requestWithIdentifier:identifier content:content trigger:nil];
                    [center addNotificationRequest:request withCompletionHandler:^(NSError *error) {
                        if (resultCallback) resultCallback(token, error ? -1 : 1);
                    }];
                });
            } @catch (NSException *exception) {
                if (resultCallback) resultCallback(token, -1);
            }
        });
    }
}
