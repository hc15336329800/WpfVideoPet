(() => {
    const state = {
        role: 'client',
        room: 'default',
        token: null,
        wsUrl: '',
        ws: null,
        pendingClientId: null,
        currentClientId: null,
        peer: null,
        localStream: null,
        remoteStream: new MediaStream(),
        isPaused: false,
        callState: 'idle',
        expectingCloseSocket: null,
        autoplayPrompted: false,
    };

    const statusBar = document.getElementById('statusBar');
    const localVideo = document.getElementById('localVideo');
    const remoteVideo = document.getElementById('remoteVideo');
    remoteVideo.srcObject = state.remoteStream;

    function updateLocalPreviewVisibility() {
        if (!localVideo) {
            return;
        }
        if (state.role === 'operator') {
            localVideo.classList.remove('hidden');
        } else {
            localVideo.classList.add('hidden');
        }
    }

    function tryPlayRemoteStream() {
        if (!remoteVideo || typeof remoteVideo.play !== 'function') {
            return;
        }
        try {
            const result = remoteVideo.play();
            if (result && typeof result.then === 'function') {
                result.catch((err) => {
                    log('remote video autoplay blocked', err);
                    if (state.autoplayPrompted) {
                        return;
                    }
                    state.autoplayPrompted = true;
                    if (state.role === 'operator') {
                        updateStatusBar('访客视频已接通，如未听到声音请点击页面允许播放。');
                    } else {
                        setClientStatus('视频已接通，如未听到声音请点击页面允许播放。');
                    }
                });
            }
        } catch (err) {
            log('remote video play error', err);
        }
    }

    updateLocalPreviewVisibility();

    function log(...args) {
        console.log('[call]', ...args);
    }

    function sendToHost(payload) {
        try {
            const json = JSON.stringify(payload ?? {});
            if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
                window.chrome.webview.postMessage(json);
            } else if (window.external && typeof window.external.notify === 'function') {
                window.external.notify(json);
            } else {
                log('Host message', payload);
            }
        } catch (err) {
            log('failed to post host message', err);
        }
    }

    function updateStatusBar(message) {
        if (typeof message === 'string' && statusBar) {
            statusBar.textContent = message;
        }
    }

    function setClientStatus(message) {
        if (!message) {
            return;
        }
        updateStatusBar(message);
        sendToHost({ type: 'client-status', message });
    }

    function emitClientEvent(event, message) {
        sendToHost({ type: 'client-event', event, message });
    }

    function emitClientError(code, message) {
        sendToHost({ type: 'client-error', code, message });
    }

    function emitAlert(message) {
        if (!message) {
            return;
        }
        sendToHost({ type: 'alert', message });
    }

    function setOperatorState(stateName, displayMessage) {
        if (displayMessage) {
            updateStatusBar(displayMessage);
        }
        sendToHost({ type: 'operator-state', state: stateName });
    }

    function setCallState(callState) {
        if (state.callState === callState) {
            return;
        }
        state.callState = callState;
        sendToHost({ type: 'call-state', state: callState });
    }

    function resetRemoteStream() {
        try {
            state.remoteStream.getTracks().forEach((track) => track.stop());
        } catch (err) {
            log('failed to stop remote tracks', err);
        }
        state.remoteStream = new MediaStream();
        if (remoteVideo) {
            remoteVideo.srcObject = state.remoteStream;
        }
    }

    function stopLocalStream() {
        if (!state.localStream) {
            return;
        }
        try {
            state.localStream.getTracks().forEach((track) => track.stop());
        } catch (err) {
            log('failed to stop local tracks', err);
        }
        state.localStream = null;
        if (localVideo) {
            localVideo.srcObject = null;
        }
    }

    function destroyPeerConnection(stopLocal = true) {
        if (state.peer) {
            try {
                state.peer.ontrack = null;
                state.peer.onicecandidate = null;
                state.peer.onconnectionstatechange = null;
                state.peer.close();
            } catch (err) {
                log('failed to close peer', err);
            }
        }
        state.peer = null;
        resetRemoteStream();
        if (stopLocal) {
            stopLocalStream();
        }
    }

    async function ensureLocalStream() {
        if (state.localStream) {
            return state.localStream;
        }
        try {
            const stream = await navigator.mediaDevices.getUserMedia({ video: true, audio: true });
            state.localStream = stream;
            if (localVideo) {
                localVideo.srcObject = stream;
            }
            return stream;
        } catch (err) {
            const message = '无法获取本地音视频设备权限，请检查系统设置。';
            emitClientError('media-error', message);
            updateStatusBar(message);
            throw err;
        }
    }

    function togglePause(paused) {
        state.isPaused = paused;
        if (!state.localStream) {
            return;
        }
        state.localStream.getTracks().forEach((track) => {
            track.enabled = !paused;
        });
    }

    function sendSignal(type, payload) {
        if (!state.ws || state.ws.readyState !== WebSocket.OPEN) {
            log('skip signal send, ws not ready', type);
            return;
        }
        const message = { type, room: state.room };
        if (payload !== undefined) {
            message.payload = payload;
        }
        state.ws.send(JSON.stringify(message));
    }

    function attachPeerEventHandlers(peer) {
        peer.onicecandidate = (evt) => {
            if (evt.candidate) {
                sendSignal('candidate', evt.candidate);
            }
        };

        peer.ontrack = (evt) => {
            evt.streams.forEach((stream) => {
                stream.getTracks().forEach((track) => state.remoteStream.addTrack(track));
            });
            if (remoteVideo && !remoteVideo.srcObject) {
                remoteVideo.srcObject = state.remoteStream;
            }
            tryPlayRemoteStream();
            if (state.role === 'operator') {
                setOperatorState('in-call');
                setCallState('active');
                updateStatusBar('通话已建立');
             } else {
                setClientStatus('坐席已接通');
                setCallState('active');
            }
        };

        peer.onconnectionstatechange = () => {
            const status = peer.connectionState;
            log('peer connection state', status);
            if (status === 'failed' || status === 'disconnected') {
                if (state.role === 'operator') {
                    setOperatorState('ended', '连接已断开');
                } else {
                    emitClientEvent('ended', '连接已断开');
                    setClientStatus('连接已断开');
                }
                setCallState('ended');
            }
        };
    }

    async function createPeerConnection() {
        await ensureLocalStream();
        const peer = new RTCPeerConnection({
            iceServers: [
                { urls: 'stun:stun.l.google.com:19302' },
            ],
        });
        state.localStream.getTracks().forEach((track) => peer.addTrack(track, state.localStream));
        attachPeerEventHandlers(peer);
        state.peer = peer;
        return peer;
    }

    async function beginClientNegotiation() {
        try {
            const peer = state.peer ?? await createPeerConnection();
            const offer = await peer.createOffer();
            await peer.setLocalDescription(offer);
            sendSignal('offer', offer);
        } catch (err) {
            log('failed to start for client', err);
            emitClientError('offer-error', '创建本地 Offer 失败，请稍后重试。');
        }
    }

    async function handleOffer(offer) {
        try {
            const peer = state.peer ?? await createPeerConnection();
            await peer.setRemoteDescription(new RTCSessionDescription(offer));
            const answer = await peer.createAnswer();
            await peer.setLocalDescription(answer);
            sendSignal('answer', answer);
        } catch (err) {
            log('failed to handle offer', err);
            emitClientError('answer-error', '处理远端 Offer 失败，请稍后重试。');
        }
    }

    async function handleAnswer(answer) {
        if (!state.peer) {
            log('no peer for answer');
            return;
        }
        try {
            await state.peer.setRemoteDescription(new RTCSessionDescription(answer));
        } catch (err) {
            log('failed to set remote answer', err);
        }
    }

    async function handleCandidate(candidate) {
        if (!state.peer) {
            return;
        }
        try {
            await state.peer.addIceCandidate(new RTCIceCandidate(candidate));
        } catch (err) {
            log('add candidate failed', err);
        }
    }

    function cleanupAfterCall(message, isError = false) {
        destroyPeerConnection(true);
        state.currentClientId = null;
        state.pendingClientId = null;
        setCallState('ended');
        if (state.role === 'operator') {
            const display = message || '通话已结束';
            setOperatorState('ended', display);
        } else {
            const display = message || '通话已结束';
            if (isError) {
                emitClientError('client-error', display);
            } else {
                emitClientEvent('ended', display);
            }
            setClientStatus(display);
        }
    }

    function handleSignalMessage(evt) {
        let data;
        try {
            data = JSON.parse(evt.data);
        } catch (err) {
            log('invalid signal message', evt.data);
            return;
        }

        const type = data?.type;
        const payload = data?.payload;

        switch (type) {
            case 'joined': {
                if (state.role === 'operator') {
                    setOperatorState('idle', '等待访客呼入');
                } else {
                    setClientStatus('信令连接成功，正在等待坐席响应...');
                }
                break;
            }
            case 'incoming': {
                if (state.role === 'operator') {
                    state.pendingClientId = data.clientId || payload?.clientId || null;
                    setOperatorState('ringing', '有新的访客请求');
                }
                break;
            }
            case 'incoming-cancelled': {
                if (state.role === 'operator') {
                    const cancelledId = data.clientId || payload?.clientId;
                    if (!state.pendingClientId || !cancelledId || state.pendingClientId === cancelledId) {
                        state.pendingClientId = null;
                        setOperatorState('ended', '访客已取消呼叫');
                    }
                }
                break;
            }
            case 'start': {
                if (state.role === 'operator') {
                    state.currentClientId = data.clientId || payload?.clientId || state.pendingClientId;
                    state.pendingClientId = null;
                    setOperatorState('connecting', '正在建立连接...');
                    setCallState('connecting');
                    createPeerConnection().catch((err) => log('createPeerConnection error', err));
                } else {
                    setClientStatus('坐席已接听，正在建立连接...');
                    setCallState('connecting');
                    beginClientNegotiation().catch((err) => log('beginClientNegotiation error', err));
                }
                break;
            }
            case 'offer': {
                handleOffer(payload).catch((err) => log('handleOffer error', err));
                break;
            }
            case 'answer': {
                handleAnswer(payload).catch((err) => log('handleAnswer error', err));
                break;
            }
            case 'candidate': {
                handleCandidate(payload).catch((err) => log('handleCandidate error', err));
                break;
            }
            case 'reject': {
                if (state.role !== 'operator') {
                    emitClientEvent('rejected', '坐席已拒绝本次通话。');
                    cleanupAfterCall('坐席已拒绝本次通话。');
                }
                break;
            }
            case 'busy': {
                if (state.role !== 'operator') {
                    emitClientEvent('busy', '坐席正在忙碌，请稍后再试。');
                    cleanupAfterCall('坐席正在忙碌，请稍后再试。');
                }
                break;
            }
            case 'no-operator': {
                if (state.role !== 'operator') {
                    emitClientEvent('no-operator', '坐席当前离线。');
                    cleanupAfterCall('坐席当前离线。');
                }
                break;
            }
            case 'operator-offline': {
                if (state.role !== 'operator') {
                    emitClientEvent('operator-offline', '坐席已离线。');
                    cleanupAfterCall('坐席已离线。');
                }
                break;
            }
            case 'client-ended': {
                if (state.role === 'operator') {
                    cleanupAfterCall('访客已结束通话。');
                }
                break;
            }
            case 'bye': {
                cleanupAfterCall(state.role === 'operator' ? '访客已挂断。' : '坐席已挂断。');
                break;
            }
            case 'unauthorized': {
                emitAlert('坐席鉴权失败，请检查访问凭证。');
                cleanupAfterCall('鉴权失败。', true);
                break;
            }
            case 'operator-exists': {
                emitAlert('当前房间已存在一个坐席，请勿重复登录。');
                cleanupAfterCall('已有其他坐席在线。', true);
                break;
            }
            default: {
                log('unhandled signal message', data);
                break;
            }
        }
    }

    function handleWsClose(evt) {
        const socket = evt?.target;
        const intentional = socket && socket === state.expectingCloseSocket;
        if (intentional) {
            state.expectingCloseSocket = null;
        }
        log('signal closed', evt.code, evt.reason, 'intentional=', intentional);
        if (intentional) {
            destroyPeerConnection(true);
            return;
        }
        state.expectingCloseSocket = null;
        if (state.role === 'operator') {
            setOperatorState('offline', '信令连接已断开');
        } else {
            emitClientEvent('ws-error', '信令连接已断开');
            setClientStatus('信令连接已断开');
        }
        setCallState('ended');
        destroyPeerConnection(true);
    }

    function handleWsError(evt) {
        log('signal error', evt);
        if (state.role === 'operator') {
            setOperatorState('offline', '信令连接异常');
        } else {
            emitClientEvent('signal-error', '信令连接异常');
            setClientStatus('信令连接异常');
        }
    }

    function bindWebSocket(ws) {
        ws.addEventListener('open', () => {
            log('signal opened');
            const joinPayload = {
                type: 'join',
                room: state.room,
                role: state.role,
            };
            if (state.token) {
                joinPayload.token = state.token;
            }
            ws.send(JSON.stringify(joinPayload));
            if (state.role === 'operator') {
                setOperatorState('connecting', '正在连接信令服务器...');
            } else {
                setClientStatus('正在连接信令服务器...');
            }
        });
        ws.addEventListener('message', handleSignalMessage);
        ws.addEventListener('close', handleWsClose);
        ws.addEventListener('error', handleWsError);
    }

    function connectSignal() {
        if (!state.wsUrl) {
            emitClientError('config-error', '信令服务器地址未配置。');
            return;
        }
        try {
            if (state.ws) {
                state.expectingCloseSocket = state.ws;
                state.ws.close(1000, 'reconnect');
            }
        } catch (err) {
            log('close old ws fail', err);
        }
        try {
            const ws = new WebSocket(state.wsUrl);
            state.ws = ws;
            bindWebSocket(ws);
        } catch (err) {
            log('connect signal fail', err);
            emitClientError('ws-error', '无法连接信令服务器。');
        }
    }

    function handleJoin(message) {
        state.role = message.role === 'operator' ? 'operator' : 'client';
        state.room = message.room || 'default';
        state.token = message.token || null;
        state.wsUrl = message.ws || message.signalServer || '';
        state.isPaused = false;
        state.pendingClientId = null;
        state.currentClientId = null;
        state.autoplayPrompted = false;
        destroyPeerConnection(true);
        updateLocalPreviewVisibility();
        if (state.role === 'operator') {
            updateStatusBar('正在初始化坐席端...');
        } else {
            setClientStatus('正在准备访客端...');
        }
        connectSignal();
    }

    function handleAccept() {
        if (state.role !== 'operator') {
            return;
        }
        const target = state.pendingClientId;
        if (!target) {
            log('no pending client to accept');
            return;
        }
        sendSignal('accept', { clientId: target });
        setOperatorState('connecting', '正在接通访客...');
    }

    function handleReject() {
        if (state.role !== 'operator') {
            return;
        }
        const target = state.pendingClientId;
        if (!target) {
            return;
        }
        sendSignal('reject', { clientId: target });
        state.pendingClientId = null;
        setOperatorState('ended', '已拒绝访客请求');
    }

    function handleHangup() {
        if (state.role === 'operator') {
            if (state.currentClientId) {
                sendSignal('bye', { clientId: state.currentClientId });
                cleanupAfterCall('已结束通话');
            }
        } else {
            sendSignal('bye');
            cleanupAfterCall('已结束通话');
        }
    }

    function handlePause() {
        togglePause(true);
    }

    function handleResume() {
        togglePause(false);
    }

    function handleHostMessage(event) {
        if (!event) {
            return;
        }
        let data = event.data;
        if (typeof data === 'string') {
            try {
                data = JSON.parse(data);
            } catch (err) {
                log('invalid host message', data);
                return;
            }
        }
        if (!data || typeof data !== 'object') {
            return;
        }
        switch (data.type) {
            case 'join':
                handleJoin(data);
                break;
            case 'accept':
                handleAccept();
                break;
            case 'reject':
                handleReject();
                break;
            case 'hangup':
                handleHangup();
                break;
            case 'pause':
                handlePause();
                break;
            case 'resume':
                handleResume();
                break;
            default:
                log('unknown host command', data);
                break;
        }
    }

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', handleHostMessage);
    } else if (window.addEventListener) {
        window.addEventListener('message', handleHostMessage);
    }

    window.addEventListener('unload', () => {
        try {
            if (state.ws) {
                state.expectingCloseSocket = state.ws;
                state.ws.close(1000, 'window-unload');
            }
        } catch (err) {
            log('close ws on unload fail', err);
        }
        destroyPeerConnection(true);
    });
})();