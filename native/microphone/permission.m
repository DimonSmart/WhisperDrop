#import <AVFoundation/AVFoundation.h>

typedef void (*PDMicrophonePermissionCallback)(void *context, int status);

int pd_microphone_authorization_status(void) {
    return (int)[AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio];
}

void pd_request_microphone_access(void *context, PDMicrophonePermissionCallback callback) {
    [AVCaptureDevice requestAccessForMediaType:AVMediaTypeAudio completionHandler:^(BOOL granted) {
        callback(context, granted ? (int)AVAuthorizationStatusAuthorized : (int)AVAuthorizationStatusDenied);
    }];
}
