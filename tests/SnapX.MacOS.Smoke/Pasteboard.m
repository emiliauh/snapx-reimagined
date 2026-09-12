#import <AppKit/AppKit.h>
int main(int argc, const char **argv) {
    @autoreleasepool {
        if (argc != 3) return 2;
        NSPasteboard *board = NSPasteboard.generalPasteboard;
        NSString *action = @(argv[1]), *path = @(argv[2]);
        if ([action isEqualToString:@"save"]) {
            NSMutableArray *items = [NSMutableArray array];
            for (NSPasteboardItem *item in board.pasteboardItems) {
                NSMutableDictionary *values = [NSMutableDictionary dictionary];
                for (NSString *type in item.types) {
                    NSData *data = [item dataForType:type];
                    if (!data) { fprintf(stderr, "Cannot preserve clipboard representation %s\n", type.UTF8String); return 3; }
                    values[type] = data;
                }
                [items addObject:values];
            }
            return [items writeToFile:path atomically:YES] ? 0 : 4;
        }
        if ([action isEqualToString:@"restore"]) {
            NSArray *values = [NSArray arrayWithContentsOfFile:path];
            if (!values) return 5;
            NSMutableArray *items = [NSMutableArray array];
            for (NSDictionary *value in values) {
                NSPasteboardItem *item = [NSPasteboardItem new];
                for (NSString *type in value) [item setData:value[type] forType:type];
                [items addObject:item];
            }
            [board clearContents];
            return items.count == 0 || [board writeObjects:items] ? 0 : 6;
        }
        if ([action isEqualToString:@"png"]) {
            NSData *data = [board dataForType:NSPasteboardTypePNG];
            return data && [data writeToFile:path atomically:YES] ? 0 : 7;
        }
        return 8;
    }
}
