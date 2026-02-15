// Google Cast Web Sender SDK Interop
// This file provides JavaScript interop for Chromecast functionality

let castContext = null;
let remotePlayer = null;
let remotePlayerController = null;
let dotNetHelper = null;

// Initialize Google Cast SDK
export function initializeCast(dotNetReference) {
    dotNetHelper = dotNetReference;
    
    // Wait for Cast SDK to load
    window['__onGCastApiAvailable'] = function(isAvailable) {
        if (isAvailable) {
            initializeCastApi();
        } else {
            console.log('Google Cast SDK not available');
        }
    };
}

function initializeCastApi() {
    try {
        // Initialize Cast Context
        castContext = cast.framework.CastContext.getInstance();
        
        // Set Cast options
        castContext.setOptions({
            receiverApplicationId: chrome.cast.media.DEFAULT_MEDIA_RECEIVER_APP_ID,
            autoJoinPolicy: chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED
        });

        // Create remote player and controller
        remotePlayer = new cast.framework.RemotePlayer();
        remotePlayerController = new cast.framework.RemotePlayerController(remotePlayer);

        // Listen for cast state changes
        castContext.addEventListener(
            cast.framework.CastContextEventType.CAST_STATE_CHANGED,
            onCastStateChanged
        );

        // Listen for session state changes
        castContext.addEventListener(
            cast.framework.CastContextEventType.SESSION_STATE_CHANGED,
            onSessionStateChanged
        );

        // Listen for remote player changes
        remotePlayerController.addEventListener(
            cast.framework.RemotePlayerEventType.IS_CONNECTED_CHANGED,
            onPlayerStateChanged
        );

        console.log('Google Cast SDK initialized successfully');
    } catch (error) {
        console.error('Error initializing Cast SDK:', error);
    }
}

function onCastStateChanged(event) {
    console.log('Cast state changed:', event.castState);
    
    const isConnected = event.castState === cast.framework.CastState.CONNECTED;
    
    if (dotNetHelper) {
        dotNetHelper.invokeMethodAsync('OnCastStateChanged', isConnected);
    }
}

function onSessionStateChanged(event) {
    console.log('Session state changed:', event.sessionState);
    
    if (event.sessionState === cast.framework.SessionState.SESSION_STARTED ||
        event.sessionState === cast.framework.SessionState.SESSION_RESUMED) {
        
        const session = castContext.getCurrentSession();
        if (session && session.getCastDevice()) {
            const deviceName = session.getCastDevice().friendlyName;
            
            if (dotNetHelper) {
                dotNetHelper.invokeMethodAsync('OnDeviceNameChanged', deviceName);
            }
        }
    }
}

function onPlayerStateChanged() {
    if (remotePlayer.isConnected) {
        const session = castContext.getCurrentSession();
        if (session && session.getCastDevice()) {
            const deviceName = session.getCastDevice().friendlyName;
            
            if (dotNetHelper) {
                dotNetHelper.invokeMethodAsync('OnDeviceNameChanged', deviceName);
                dotNetHelper.invokeMethodAsync('OnCastStateChanged', true);
            }
        }
    } else {
        if (dotNetHelper) {
            dotNetHelper.invokeMethodAsync('OnCastStateChanged', false);
        }
    }
}

export function isCastAvailable() {
    if (!castContext) return false;
    
    const castState = castContext.getCastState();
    return castState !== cast.framework.CastState.NO_DEVICES_AVAILABLE;
}

export function isCastConnected() {
    if (!remotePlayer) return false;
    return remotePlayer.isConnected;
}

export function getDeviceName() {
    if (!castContext) return null;
    
    const session = castContext.getCurrentSession();
    if (session && session.getCastDevice()) {
        return session.getCastDevice().friendlyName;
    }
    
    return null;
}

export function loadMedia(mediaUrl, title, subtitle, imageUrl) {
    if (!castContext) {
        console.error('Cast context not initialized');
        return;
    }

    const session = castContext.getCurrentSession();
    if (!session) {
        console.error('No active cast session');
        return;
    }

    // Create media info
    const mediaInfo = new chrome.cast.media.MediaInfo(mediaUrl, 'application/x-mpegURL');
    
    // Set metadata
    const metadata = new chrome.cast.media.GenericMediaMetadata();
    metadata.metadataType = chrome.cast.media.MetadataType.GENERIC;
    metadata.title = title;
    metadata.subtitle = subtitle;
    
    if (imageUrl) {
        metadata.images = [new chrome.cast.Image(imageUrl)];
    }
    
    mediaInfo.metadata = metadata;

    // Create load request
    const request = new chrome.cast.media.LoadRequest(mediaInfo);
    request.autoplay = true;
    request.currentTime = 0;

    // Load media
    session.loadMedia(request).then(
        () => {
            console.log('Media loaded successfully');
            if (dotNetHelper) {
                dotNetHelper.invokeMethodAsync('OnMediaStatusChanged', 'PLAYING');
            }
        },
        (error) => {
            console.error('Error loading media:', error);
        }
    );
}

export function play() {
    if (!remotePlayerController) return;
    
    if (remotePlayer.isPaused) {
        remotePlayerController.playOrPause();
    }
}

export function pause() {
    if (!remotePlayerController) return;
    
    if (!remotePlayer.isPaused) {
        remotePlayerController.playOrPause();
    }
}

export function stop() {
    if (!remotePlayerController) return;
    remotePlayerController.stop();
}

export function seek(positionSeconds) {
    if (!remotePlayer || !remotePlayerController) return;
    
    remotePlayer.currentTime = positionSeconds;
    remotePlayerController.seek();
}

export function requestCastSession() {
    if (!castContext) {
        console.error('Cast context not initialized');
        return;
    }

    castContext.requestSession().then(
        () => console.log('Cast session started'),
        (error) => console.error('Error starting cast session:', error)
    );
}
