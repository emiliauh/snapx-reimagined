// SPDX-License-Identifier: GPL-3.0-or-later
#import <Foundation/Foundation.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <AVFoundation/AVFoundation.h>
#import <CoreGraphics/CoreGraphics.h>
#include <stdint.h>

// All writer and stream operations run on queue. The registry gives callers
// opaque tokens, so a repeated destroy or a concurrent stop cannot dereference
// freed memory. Async blocks retain their session until they have completed.
API_AVAILABLE(macos(13.0))
@interface SXAudioCapture : NSObject <SCStreamOutput, SCStreamDelegate>
@property dispatch_queue_t queue;
@property dispatch_semaphore_t done;
@property(atomic) BOOL stopped;
@property(atomic) BOOL completed;
@property(atomic) BOOL running;
@property(atomic, copy) NSString *error;
@property SCStream *stream;
@property AVAssetWriter *writer;
@property AVAssetWriterInput *video;
@property AVAssetWriterInput *audio;
@property NSString *path;
@property CGDirectDisplayID display;
@property CGRect rect;
@property NSInteger width, height, fps, videoRate, audioRate;
@property BOOL cursor, finishing, started, starting, claimed, wroteAudio, wroteFinalFrame;
@property CMSampleBufferRef lastVideoSample;
@property CMTime firstTime;
@property double duration, firstHostTime;
- (void)begin;
- (void)finish;
@end

@implementation SXAudioCapture
- (void)dealloc {
    if (_lastVideoSample) CFRelease(_lastVideoSample);
}
- (instancetype)init {
    if ((self = [super init])) {
        _queue = dispatch_queue_create("com.snapx.system-audio", DISPATCH_QUEUE_SERIAL);
        _done = dispatch_semaphore_create(0);
        _error = @"";
    }
    return self;
}
- (void)fail:(NSString *)message {
    if (self.error.length == 0) self.error = message ?: @"System audio capture failed.";
    self.stopped = YES;
    [self finish];
}
- (void)complete {
    self.stream = nil;
    self.completed = YES;
    dispatch_semaphore_signal(self.done);
}
- (void)finishWriter {
    if (!self.started || self.writer.status != AVAssetWriterStatusWriting) {
        [self.writer cancelWriting];
        if (!self.error.length && !self.started) self.error = @"Recording stopped before a video frame was captured.";
        [self complete];
        return;
    }
    double elapsed = NSProcessInfo.processInfo.systemUptime - self.firstHostTime;
    if (self.duration > 0) elapsed = MIN(elapsed, self.duration);
    CMTime endTime = CMTimeAdd(self.firstTime, CMTimeMakeWithSeconds(MAX(0, elapsed), 1000000000));
    // endSession alone does not extend the final video sample. A static
    // desktop may supply no new image near stop, so repeat its retained frame
    // with new timing to preserve wall-clock duration in the resulting MP4.
    CMTime frameDuration = CMTimeMake(1, (int32_t)self.fps);
    CMTime finalTime = CMTimeSubtract(endTime, frameDuration);
    if (!self.wroteFinalFrame && self.lastVideoSample &&
        CMTimeCompare(finalTime, CMSampleBufferGetPresentationTimeStamp(self.lastVideoSample)) > 0) {
        if (!self.video.readyForMoreMediaData) {
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 10 * NSEC_PER_MSEC), self.queue, ^{ [self finishWriter]; });
            return;
        }
        CMSampleTimingInfo timing = { frameDuration, finalTime, kCMTimeInvalid };
        CMSampleBufferRef tail = NULL;
        OSStatus result = CMSampleBufferCreateCopyWithNewTiming(kCFAllocatorDefault,
            self.lastVideoSample, 1, &timing, &tail);
        BOOL appended = result == noErr && [self.video appendSampleBuffer:tail];
        if (tail) CFRelease(tail);
        if (!appended) {
            if (!self.error.length) self.error = self.writer.error.localizedDescription ?: @"Unable to finalize the last video frame.";
            [self.writer cancelWriting]; [self complete]; return;
        }
    }
    self.wroteFinalFrame = YES;
    // ScreenCaptureKit can deliver no audio buffers during silence. An empty
    // writer input is omitted from MP4, breaking stream-copy concatenation
    // when a later pause/resume segment contains audio. Seed only that empty
    // track at the shared session origin; video still defines segment length.
    if (!self.wroteAudio) {
        if (!self.audio.readyForMoreMediaData) {
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 10 * NSEC_PER_MSEC), self.queue, ^{
                [self finishWriter];
            });
            return;
        }
        AudioStreamBasicDescription format = {0};
        format.mSampleRate = 48000;
        format.mFormatID = kAudioFormatLinearPCM;
        format.mFormatFlags = kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked;
        format.mBytesPerPacket = 4; format.mFramesPerPacket = 1;
        format.mBytesPerFrame = 4; format.mChannelsPerFrame = 2; format.mBitsPerChannel = 16;
        CMAudioFormatDescriptionRef description = NULL;
        CMBlockBufferRef data = NULL;
        CMSampleBufferRef silence = NULL;
        OSStatus result = CMAudioFormatDescriptionCreate(kCFAllocatorDefault, &format, 0, NULL,
            0, NULL, NULL, &description);
        if (result == noErr) result = CMBlockBufferCreateWithMemoryBlock(kCFAllocatorDefault,
            NULL, 4096, kCFAllocatorDefault, NULL, 0, 4096, 0, &data);
        if (result == noErr) result = CMBlockBufferFillDataBytes(0, data, 0, 4096);
        if (result == noErr) result = CMAudioSampleBufferCreateReadyWithPacketDescriptions(
            kCFAllocatorDefault, data, description, 1024, self.firstTime, NULL, &silence);
        BOOL appended = result == noErr && [self.audio appendSampleBuffer:silence];
        if (silence) CFRelease(silence);
        if (data) CFRelease(data);
        if (description) CFRelease(description);
        if (!appended) {
            if (!self.error.length) self.error = self.writer.error.localizedDescription ?: @"Unable to finalize the silent system audio track.";
            [self.writer cancelWriting];
            [self complete];
            return;
        }
        self.wroteAudio = YES;
    }
    // End the session explicitly even when a static desktop supplies no new
    // frames near stop. The first sample anchors the stream and host clocks.
    [self.writer endSessionAtSourceTime:endTime];
    [self.video markAsFinished];
    [self.audio markAsFinished];
    [self.writer finishWritingWithCompletionHandler:^{
        dispatch_async(self.queue, ^{
            if (self.writer.status != AVAssetWriterStatusCompleted && !self.error.length)
                self.error = self.writer.error.localizedDescription ?: @"Unable to finalize recording.";
            [self complete];
        });
    }];
}
- (void)finish {
    if (self.finishing || self.completed) return;
    // stopCapture before startCapture has completed is not a cancellation of
    // its asynchronous start. Wait for that callback and stop the live stream.
    if (self.starting) return;
    self.finishing = YES;
    if (self.stream) {
        [self.stream stopCaptureWithCompletionHandler:^(NSError *error) {
            dispatch_async(self.queue, ^{
                if (error && !self.error.length) self.error = error.localizedDescription;
                [self finishWriter];
            });
        }];
    } else [self finishWriter];
}
- (void)begin {
    if (self.stopped) { [self finish]; return; }
    [SCShareableContent getShareableContentExcludingDesktopWindows:NO onScreenWindowsOnly:YES
        completionHandler:^(SCShareableContent *content, NSError *error) {
        dispatch_async(self.queue, ^{
            if (self.stopped || self.completed) { [self finish]; return; }
            if (error) { [self fail:error.localizedDescription]; return; }
            SCDisplay *display = nil;
            for (SCDisplay *candidate in content.displays)
                if (candidate.displayID == self.display) { display = candidate; break; }
            if (!display) { [self fail:@"The selected display is no longer available."]; return; }
            NSError *writerError = nil;
            self.writer = [[AVAssetWriter alloc] initWithURL:[NSURL fileURLWithPath:self.path]
                fileType:AVFileTypeMPEG4 error:&writerError];
            if (!self.writer) { [self fail:writerError.localizedDescription]; return; }
            self.video = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeVideo outputSettings:@{
                AVVideoCodecKey: AVVideoCodecTypeH264, AVVideoWidthKey: @(self.width), AVVideoHeightKey: @(self.height),
                AVVideoCompressionPropertiesKey: @{AVVideoAverageBitRateKey: @(self.videoRate),
                    AVVideoExpectedSourceFrameRateKey: @(self.fps), AVVideoMaxKeyFrameIntervalKey: @(self.fps * 2)}}];
            self.audio = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeAudio outputSettings:@{
                AVFormatIDKey: @(kAudioFormatMPEG4AAC), AVSampleRateKey: @48000,
                AVNumberOfChannelsKey: @2, AVEncoderBitRateKey: @(self.audioRate)}];
            self.video.expectsMediaDataInRealTime = YES;
            self.audio.expectsMediaDataInRealTime = YES;
            if (![self.writer canAddInput:self.video] || ![self.writer canAddInput:self.audio]) {
                [self fail:@"The selected H.264/AAC recording configuration is unsupported."]; return;
            }
            [self.writer addInput:self.video]; [self.writer addInput:self.audio];
            SCStreamConfiguration *config = [SCStreamConfiguration new];
            config.sourceRect = self.rect;
            config.width = self.width; config.height = self.height;
            config.minimumFrameInterval = CMTimeMake(1, (int32_t)self.fps);
            config.queueDepth = 6;
            config.showsCursor = self.cursor;
            config.capturesAudio = YES; config.excludesCurrentProcessAudio = YES;
            config.sampleRate = 48000; config.channelCount = 2;
            if (@available(macOS 15.0, *)) config.captureMicrophone = NO;
            SCRunningApplication *selfApplication = nil;
            pid_t processIdentifier = NSProcessInfo.processInfo.processIdentifier;
            for (SCRunningApplication *application in content.applications)
                if (application.processID == processIdentifier) { selfApplication = application; break; }
            NSArray<SCRunningApplication *> *excludedApplications = selfApplication ? @[selfApplication] : @[];
            SCContentFilter *filter = [[SCContentFilter alloc]
                initWithDisplay:display excludingApplications:excludedApplications exceptingWindows:@[]];
            self.stream = [[SCStream alloc] initWithFilter:filter configuration:config delegate:self];
            NSError *outputError = nil;
            if (![self.stream addStreamOutput:self type:SCStreamOutputTypeScreen sampleHandlerQueue:self.queue error:&outputError] ||
                ![self.stream addStreamOutput:self type:SCStreamOutputTypeAudio sampleHandlerQueue:self.queue error:&outputError]) {
                [self fail:outputError.localizedDescription]; return;
            }
            self.starting = YES;
            [self.stream startCaptureWithCompletionHandler:^(NSError *startError) {
                dispatch_async(self.queue, ^{
                    self.starting = NO;
                    if (startError) [self fail:startError.localizedDescription];
                    else { self.running = YES; if (self.stopped) [self finish]; }
                });
            }];
        });
    }];
}
- (void)stream:(SCStream *)stream didStopWithError:(NSError *)error {
    (void)stream;
    dispatch_async(self.queue, ^{ [self fail:error.localizedDescription]; });
}
- (void)stream:(SCStream *)stream didOutputSampleBuffer:(CMSampleBufferRef)sample ofType:(SCStreamOutputType)type {
    (void)stream;
    if (self.stopped || self.finishing || !CMSampleBufferIsValid(sample) || !CMSampleBufferDataIsReady(sample)) return;
    if (type == SCStreamOutputTypeScreen) {
        NSArray *attachments = (__bridge NSArray *)CMSampleBufferGetSampleAttachmentsArray(sample, NO);
        NSNumber *status = attachments.firstObject[SCStreamFrameInfoStatus];
        if (!status || (status.integerValue != SCFrameStatusComplete && status.integerValue != SCFrameStatusIdle) ||
            !CMSampleBufferGetImageBuffer(sample)) return;
    } else if (type != SCStreamOutputTypeAudio) return;
    CMTime timestamp = CMSampleBufferGetPresentationTimeStamp(sample);
    if (!CMTIME_IS_NUMERIC(timestamp)) return;
    if (!self.started) {
        // Anchor both tracks to the first video frame; pre-roll audio is dropped.
        if (type != SCStreamOutputTypeScreen) return;
        if (![self.writer startWriting]) { [self fail:self.writer.error.localizedDescription]; return; }
        [self.writer startSessionAtSourceTime:timestamp];
        self.firstTime = timestamp; self.firstHostTime = NSProcessInfo.processInfo.systemUptime; self.started = YES;
    }
    if (CMTimeCompare(timestamp, self.firstTime) < 0) return;
    if (self.duration > 0 && CMTimeGetSeconds(CMTimeSubtract(timestamp, self.firstTime)) >= self.duration) {
        self.stopped = YES; [self finish]; return;
    }
    AVAssetWriterInput *input = type == SCStreamOutputTypeScreen ? self.video : self.audio;
    if (input.readyForMoreMediaData) {
        if (![input appendSampleBuffer:sample]) [self fail:self.writer.error.localizedDescription];
        else if (type == SCStreamOutputTypeAudio) self.wroteAudio = YES;
        else {
            if (self.lastVideoSample) CFRelease(self.lastVideoSample);
            self.lastVideoSample = (CMSampleBufferRef)CFRetain(sample);
        }
    }
}
@end

static NSMutableDictionary<NSNumber *, SXAudioCapture *> *sessions;
static NSObject *registryLock;
static uint64_t nextToken = 1;
static void initializeRegistry(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{ sessions = [NSMutableDictionary new]; registryLock = [NSObject new]; });
}
static SXAudioCapture *lookup(uint64_t token) API_AVAILABLE(macos(13.0));
static SXAudioCapture *lookup(uint64_t token) {
    initializeRegistry();
    @synchronized(registryLock) { return sessions[@(token)]; }
}
int snapx_system_audio_available(void) {
    if (@available(macOS 13.0, *)) return 1;
    return 0;
}
uint64_t snapx_system_audio_create(const char *path, uint32_t display, double x, double y,
    double width, double height, int outputWidth, int outputHeight, int fps, int cursor,
    double duration, int videoBitrate, int audioBitrate) {
    if (@available(macOS 13.0, *)) {
        if (!path || !isfinite(x) || !isfinite(y) || !isfinite(width) || !isfinite(height) ||
            width <= 0 || height <= 0 || outputWidth < 2 || outputHeight < 2 ||
            outputWidth % 2 || outputHeight % 2 || fps < 1 || fps > 240 ||
            !isfinite(duration) || duration < 0 || videoBitrate <= 0 || audioBitrate <= 0) return 0;
        SXAudioCapture *s = [SXAudioCapture new];
        s.path = [NSString stringWithUTF8String:path];
        if (!s.path.length) return 0;
        s.display = display; s.rect = CGRectMake(x, y, width, height);
        s.width = outputWidth; s.height = outputHeight; s.fps = fps; s.cursor = cursor != 0;
        s.duration = duration; s.videoRate = videoBitrate; s.audioRate = audioBitrate;
        initializeRegistry();
        @synchronized(registryLock) { uint64_t token = nextToken++; sessions[@(token)] = s; return token; }
    }
    return 0;
}
int snapx_system_audio_run(uint64_t token) {
    @autoreleasepool {
        if (@available(macOS 13.0, *)) {
            SXAudioCapture *s = lookup(token);
            if (!s) return 0;
            @synchronized(s) { if (s.claimed) return 0; s.claimed = YES; }
            dispatch_async(s.queue, ^{ [s begin]; });
            double began = NSProcessInfo.processInfo.systemUptime;
            double stopping = 0;
            while (!s.completed) {
                double now = NSProcessInfo.processInfo.systemUptime;
                if (!s.running && now - began > 20 && !s.stopped) {
                    s.error = @"Timed out starting screen and system audio capture. Check Screen Recording permission.";
                    s.stopped = YES;
                }
                if (s.duration > 0 && s.started && now - s.firstHostTime >= s.duration) s.stopped = YES;
                if (s.running && !s.started && now - began > 20 && !s.stopped) {
                    s.error = @"No screen frames arrived. Check screen capture permission and the selected display.";
                    s.stopped = YES;
                }
                if (s.stopped) {
                    if (stopping == 0) { stopping = now; dispatch_async(s.queue, ^{ [s finish]; }); }
                    if (now - stopping > 4) {
                        s.error = @"Timed out finalizing screen and system audio capture.";
                        // A pending start callback retains the session and
                        // will stop its stream when it eventually arrives.
                        dispatch_async(s.queue, ^{ [s.writer cancelWriting]; if (!s.starting) s.stream = nil; });
                        return 0;
                    }
                }
                dispatch_semaphore_wait(s.done, dispatch_time(DISPATCH_TIME_NOW, 100 * NSEC_PER_MSEC));
            }
            return s.error.length == 0 ? 1 : 0;
        }
        return 0;
    }
}
void snapx_system_audio_stop(uint64_t token) {
    if (@available(macOS 13.0, *)) { SXAudioCapture *s = lookup(token); s.stopped = YES; }
}
// Copies UTF-8 into caller storage; no borrowed string lifetime crosses the ABI.
void snapx_system_audio_error(uint64_t token, char *buffer, int capacity) {
    if (!buffer || capacity < 1) return;
    buffer[0] = 0;
    if (@available(macOS 13.0, *)) {
        NSString *error = lookup(token).error ?: @"Invalid recording handle.";
        snprintf(buffer, (size_t)capacity, "%s", error.UTF8String);
    }
}
void snapx_system_audio_destroy(uint64_t token) {
    if (@available(macOS 13.0, *)) {
        initializeRegistry();
        @synchronized(registryLock) {
            SXAudioCapture *s = sessions[@(token)]; s.stopped = YES;
            [sessions removeObjectForKey:@(token)];
        }
    }
}
