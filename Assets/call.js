(() => {
    const root = document.documentElement;
    const roleAttr = root.getAttribute('data-role') || document.body.getAttribute('data-role') || 'client';
    const role = roleAttr.toLowerCase() === 'operator' ? 'operator' : 'client';
    const isOperator = role === 'operator';

    const localVideo = document.getElementById('local');
    const remoteVideo = document.getElementById('remote');
    const localHint = document.getElementById('localHint');
    const remoteHint = document.getElementById('remoteHint');
    const overlay = document.getElementById('overlay');
    const incomingBanner = document.getElementById('incomingBanner');

    const config = {
        room: 'default',
        ws: '',
        token: null,
        role
    };

    let ws = null;
    let pc = null;
    let localStream = null;
    let joinedRoom = null;
    let pendingClientId = null;
    let currentClientId = null;
    let awaitingStart = false;
    let isUnloading = false;


    function log(...args) {
        console.log('[call]', ...args);
    }

    function postToHost(payload) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify(payload));
            }
        } catch (err) {
            console.warn('postMessage failed', err);
        }
    }

    function reportOperatorState(state) {
        if (isOperator) {
            postToHost({ type: 'operator-state', state });
        }
    }

    function reportClientStatus(status, message) {
        if (!isOperator) {
            postToHost({ type: 'client-status', status, message });
        }
    }

    function emitClientEvent(event, message) {
        if (!isOperator) {
            postToHost({ type: 'client-event', event, message });
        }
    }

    function emitCallState(state) {
        postToHost({ type: 'call-state', state });
    }

    function alertHost(message) {
        postToHost({ type: 'alert', message });
    }

    function deviceError(message) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(`DEVICE_ERROR: ${message}`);
            }
        } catch (err) {
            console.warn('deviceError', err);
        }
    }

    function showBanner(text) {
        if (!incomingBanner) return;
        incomingBanner.textContent = text || '有新的呼入请求';
        incomingBanner.hidden = false;
    }

    function hideBanner() {
        if (!incomingBanner) return;
        incomingBanner.hidden = true;
    }

    function setOverlayText(text) {
        if (!overlay) return;
        if (text) {
            overlay.textContent = text;
            overlay.style.display = 'flex';
        } else {
            overlay.textContent = '';
            overlay.style.display = 'none';
        }
    }

    function setRemoteHint(text) {
        if (!remoteHint) return;
        if (text) {
            remoteHint.textContent = text;
            remoteHint.style.display = 'block';
        } else {
            remoteHint.textContent = '';
            remoteHint.style.display = 'none';
        }
    }

    function setLocalHint(text) {
        if (!localHint) return;
        if (text) {
            localHint.textContent = text;
            localHint.style.display = 'block';
        } else {
            localHint.textContent = '';
            localHint.style.display = 'none';
        }
    }

    function explainGetUserMediaError(err) {
        if (!err || !err.name) return '无法访问摄像头或麦克风';
        switch (err.name) {
            case 'NotAllowedError':
            case 'SecurityError':
                return '已被拒绝访问摄像头/麦克风，请授予权限';
            case 'NotFoundError':
            case 'DevicesNotFoundError':
                return '未检测到可用的摄像头或麦克风设备';
            case 'NotReadableError':
            case 'TrackStartError':
                return '摄像头或麦克风可能正被占用';
            case 'OverconstrainedError':
                return '当前设备不满足采集约束条件';
            default:
                return `${err.name}: ${err.message || ''}`;
        }
    }

    function describeWebSocketClose(evt) {
        if (!evt) return '';
        const { code, reason, wasClean } = evt;
        let codeMessage = '';
        switch (code) {
            case 1000:
                codeMessage = '信令连接已正常关闭';
                break;
            case 1001:
                codeMessage = '信令服务器正在重启或关闭连接';
                break;
            case 1002:
                codeMessage = '信令服务器拒绝了本次连接请求';
                break;
            case 1003:
                codeMessage = '信令服务器不支持当前消息类型';
                break;
            case 1006:
                codeMessage = '与信令服务器的连接异常断开';
                break;
            default:
                break;
        }

        const details = [];
        if (code) {
            details.push(`代码: ${code}`);
        }
        if (reason) {
            details.push(`原因: ${reason}`);
        }
        if (wasClean && code !== 1000) {
            details.push('连接已被服务器正常关闭');
        }

        const suffix = details.length ? `（${details.join('，')}）` : '';

        if (codeMessage) {
            return `${codeMessage}${suffix}`;
        }

        if (details.length) {
            return `信令连接已关闭${suffix}`;
        }

        return '';
    }

    async function ensureOperatorPreview() {
        if (!isOperator) return;
        if (localStream) return;
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            const msg = window.isSecureContext === false
                ? '当前页面非安全上下文（需要 https 虚拟主机），无法访问摄像头/麦克风'
                : '当前环境不支持摄像头/麦克风采集';
            setLocalHint(msg);
            deviceError(msg);
            throw new Error(msg);
        }

        try {
            const devices = await navigator.mediaDevices.enumerateDevices();
            const hasCam = devices.some(d => d.kind === 'videoinput');
            const hasMic = devices.some(d => d.kind === 'audioinput');
            if (!hasCam && !hasMic) {
                const msg = '未检测到可用的摄像头或麦克风设备';
                setLocalHint(msg);
                deviceError(msg);
                throw new Error(msg);
            }
        } catch (err) {
            log('enumerateDevices failed', err);
        }

        try {
            localStream = await navigator.mediaDevices.getUserMedia({ video: true, audio: true });
            if (localVideo) {
                localVideo.srcObject = localStream;
            }
            setLocalHint('');
        } catch (err) {
            const msg = explainGetUserMediaError(err);
            setLocalHint(msg);
            deviceError(msg);
            throw err;
        }
    }

    async function ensureClientMedia() {
        if (isOperator) return;
        if (localStream) return;
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            const msg = window.isSecureContext === false
                ? '当前页面非安全上下文（需要 https 虚拟主机），无法访问摄像头/麦克风'
                : '当前环境不支持摄像头/麦克风采集';
            setOverlayText(msg);
            deviceError(msg);
            throw new Error(msg);
        }

        try {
            localStream = await navigator.mediaDevices.getUserMedia({ video: true, audio: true });
        } catch (err) {
            const msg = explainGetUserMediaError(err);
            setOverlayText(msg);
            deviceError(msg);
            throw err;
        }
    }

    function stopLocalStreamIfNeeded() {
        if (!localStream) return;
        if (isOperator) {
            // 坐席保留预览
            return;
        }
        try {
            localStream.getTracks().forEach(t => t.stop());
        } catch (err) {
            console.warn('stop tracks', err);
        }
        localStream = null;
    }

    function sendControl(type, payload) {
        if (!ws || ws.readyState !== WebSocket.OPEN) return;
        const msg = { type, room: joinedRoom };
        if (payload !== undefined) {
            msg.payload = payload;
        }
        ws.send(JSON.stringify(msg));
    }

    async function ensurePeerConnection(offerSide) {
        if (pc) {
            return pc;
        }

        pc = new RTCPeerConnection({
            iceServers: [{ urls: 'stun:stun.l.google.com:19302' }]
        });

        pc.onicecandidate = evt => {
            if (evt.candidate) {
                sendControl('candidate', evt.candidate);
            }
        };

        pc.ontrack = evt => {
            if (remoteVideo) {
                remoteVideo.srcObject = evt.streams[0];
            }
            setRemoteHint('');
            setOverlayText('');
        };

        pc.onconnectionstatechange = () => {
            log('pc state', pc.connectionState);
            if (pc.connectionState === 'connected') {
                if (isOperator) {
                    reportOperatorState('in-call');
                } else {
                    reportClientStatus('connected', '通话中');
                }
                emitCallState('active');
                awaitingStart = false;
            }

            if (pc.connectionState === 'failed' || pc.connectionState === 'disconnected') {
                emitCallState('ended');
                if (isOperator) {
                    reportOperatorState('ended');
                    setTimeout(() => reportOperatorState('idle'), 300);
                    setRemoteHint('等待访客接入...');
                } else {
                    setOverlayText('连接已断开');
                    emitClientEvent('ended', '通话已结束。');
                }
            }

            if (pc.connectionState === 'closed') {
                emitCallState('ended');
            }
        };

        if (localStream) {
            localStream.getTracks().forEach(track => pc.addTrack(track, localStream));
        }

        if (offerSide) {
            const offer = await pc.createOffer();
            await pc.setLocalDescription(offer);
            sendControl('offer', offer);
        }

        return pc;
    }

    async function onOffer(payload) {
        if (!payload) return;
        try {
            await ensureOperatorPreview();
            await ensurePeerConnection(false);
            await pc.setRemoteDescription(new RTCSessionDescription(payload));
            const answer = await pc.createAnswer();
            await pc.setLocalDescription(answer);
            sendControl('answer', answer);
        } catch (err) {
            console.error('onOffer error', err);
            alertHost('生成 answer 失败，已自动挂断。');
            hangup(true);
        }
    }

    async function onAnswer(payload) {
        if (!payload || !pc) return;
        try {
            await pc.setRemoteDescription(new RTCSessionDescription(payload));
        } catch (err) {
            console.error('setRemoteDescription(answer) failed', err);
        }
    }

    async function onCandidate(payload) {
        if (!payload || !pc) return;
        try {
            await pc.addIceCandidate(new RTCIceCandidate(payload));
        } catch (err) {
            console.warn('addIceCandidate failed', err);
        }
    }

    function cleanupCall(showEndedOverlay, options = {}) {
        const hadConnection = Boolean(pc) || awaitingStart || currentClientId;
        const { silent = false, skipCallState = false, keepWaitingHint = false } = options;

        if (pc) {
            try { pc.onicecandidate = null; pc.ontrack = null; pc.onconnectionstatechange = null; } catch (err) { console.warn(err); }
            try { pc.close(); } catch (err) { console.warn(err); }
        }
        pc = null;
        awaitingStart = false;

        if (remoteVideo) {
            remoteVideo.srcObject = null;
        }

        stopLocalStreamIfNeeded();

        if (hadConnection && !skipCallState) {
            emitCallState('ended');
        }

        if (isOperator) {
            if (!keepWaitingHint) {
                setRemoteHint('等待访客接入...');
            }
            if (!silent) {
                if (hadConnection) {
                    reportOperatorState('ended');
                    setTimeout(() => reportOperatorState('idle'), 300);
                } else {
                    reportOperatorState('idle');
                }
            }
        } else if (showEndedOverlay && !silent) {
            setOverlayText('通话已结束');
        }

        currentClientId = null;
    }

    function hangup(notifyServer) {
        if (notifyServer && pc) {
            sendControl('bye');
        }
        cleanupCall(true);
    }

    function onBye() {
        cleanupCall(true);
        if (!isOperator) {
            emitClientEvent('ended', '坐席已结束通话。');
        }
    }

    function rejectPending() {
        if (!isOperator || !pendingClientId) return;
        sendControl('reject', { clientId: pendingClientId });
        pendingClientId = null;
        hideBanner();
        setRemoteHint('等待访客接入...');
        reportOperatorState('idle');
    }

    function acceptPending() {
        if (!isOperator || !pendingClientId) return;
        awaitingStart = true;
        sendControl('accept', { clientId: pendingClientId });
        reportOperatorState('connecting');
        hideBanner();
    }

    async function startOperatorFlow() {
        try {
            await ensureOperatorPreview();
        } catch (err) {
            log('ensureOperatorPreview failed', err);
            alertHost('无法获取摄像头/麦克风，坐席端无法继续工作。');
            return;
        }
        reportOperatorState('idle');
        setRemoteHint('等待访客接入...');
    }

    async function startClientFlow() {
        reportClientStatus('connecting', '正在连接坐席...');
        setOverlayText('正在连接服务端...');
    }

    async function handleStartMessage() {
        if (isOperator) {
            reportOperatorState('connecting');
            setRemoteHint('正在建立连接...');
            awaitingStart = true;
            try {
                await ensureOperatorPreview();
                await ensurePeerConnection(false);
            } catch (err) {
                log('operator start failed', err);
                hangup(true);
            }
        } else {
            reportClientStatus('connecting', '坐席已接听，正在建立连接...');
            setOverlayText('坐席已接听，正在建立连接...');
            try {
                await ensureClientMedia();
                await ensurePeerConnection(true);
            } catch (err) {
                log('client start failed', err);
                emitClientEvent('error', '无法开启摄像头/麦克风。');
                hangup(true);
            }
        }
    }

    function handleSignal(message) {
        const { type } = message || {};
        if (!type) return;

        switch (type) {
            case 'joined':
                if (isOperator) {
                    reportOperatorState('idle');
                    setRemoteHint('等待访客接入...');
                } else {
                    reportClientStatus('waiting', '等待坐席接听...');
                    setOverlayText('等待坐席接听...');
                }
                break;
            case 'incoming':
                if (isOperator) {
                    pendingClientId = message.clientId || null;
                    showBanner('有新的访客请求通话');
                    setRemoteHint('来访等待接听');
                    reportOperatorState('ringing');
                }
                break;
            case 'incoming-cancelled':
                if (isOperator) {
                    if (pendingClientId && message.clientId === pendingClientId) {
                        pendingClientId = null;
                    }
                    hideBanner();
                    setRemoteHint('等待访客接入...');
                    reportOperatorState('idle');
                }
                break;
            case 'start':
                currentClientId = message.clientId || currentClientId;
                handleStartMessage();
                break;
            case 'reject':
                if (!isOperator) {
                    setOverlayText('坐席已拒绝');
                    emitClientEvent('rejected', '坐席已拒绝本次通话请求。');
                    cleanupCall(false, { silent: true, skipCallState: true });
                }
                break;
            case 'busy':
                if (!isOperator) {
                    setOverlayText('坐席正在忙碌');
                    emitClientEvent('busy', '坐席正在忙碌，请稍后再试。');
                    cleanupCall(false, { silent: true, skipCallState: true });
                }
                break;
            case 'no-operator':
            case 'operator-offline':
                if (!isOperator) {
                    setOverlayText('坐席暂未上线');
                    emitClientEvent('no-operator', '坐席当前离线。');
                    cleanupCall(false, { silent: true, skipCallState: true });
                }
                break;
            case 'offer':
                if (isOperator) {
                    onOffer(message.payload);
                }
                break;
            case 'answer':
                if (!isOperator) {
                    onAnswer(message.payload);
                }
                break;
            case 'candidate':
                onCandidate(message.payload);
                break;
            case 'bye':
                onBye();
                break;
            case 'client-ended':
                if (isOperator) {
                    cleanupCall(true);
                }
                break;
            case 'ringing':
                if (!isOperator) {
                    setOverlayText('等待坐席接听...');
                    reportClientStatus('waiting', '等待坐席接听...');
                }
                break;
            default:
                log('unknown signal', message);
                break;
        }
    }

    function connect() {
        let wsUrl = config.ws || '';
        if (!wsUrl) {
            alertHost('未配置信令服务器地址');
            return;
        }

        if (location.protocol === 'https:' && wsUrl.startsWith('ws://')) {
            wsUrl = wsUrl.replace(/^ws:\/\//, 'wss://');
        }

        config.ws = wsUrl;

        try {
            ws = new WebSocket(wsUrl);
        } catch (err) {
            alertHost('连接信令服务器失败，请检查地址是否正确');
            return;
        }

        ws.onopen = async () => {
            log('ws opened');
            joinedRoom = config.room || 'default';
            ws.send(JSON.stringify({ type: 'join', room: joinedRoom, role, token: config.token }));
            if (isOperator) {
                await startOperatorFlow();
            } else {
                await startClientFlow();
            }
        };


        ws.onerror = err => {
            log('ws error', err);
            if (isOperator) {
                reportOperatorState('offline');
            } else {
                setOverlayText('信令连接失败');
            }
            if (!isUnloading) {
                alertHost('信令连接出错，请检查网络或服务器地址是否正确。');
            }
        };

        ws.onclose = evt => {
            log('ws closed', evt);
            ws = null;
            cleanupCall(false, { silent: true, skipCallState: true, keepWaitingHint: true });
            if (isOperator) {
                reportOperatorState('offline');
                setRemoteHint('信令已断开');
            } else {
                if (!isUnloading) {
                    setOverlayText('信令连接已关闭');
                    emitClientEvent('no-operator', '信令已断开');
                }
            }
            if (!isUnloading) {
                const closeMessage = describeWebSocketClose(evt);
                if (closeMessage) {
                    alertHost(closeMessage);
                }
            }
        };

        ws.onmessage = evt => {
            let data;
            try {
                data = JSON.parse(evt.data);
            } catch (err) {
                return;
            }
            handleSignal(data);
        };
    }

    async function handleHostMessage(message) {
        if (!message || typeof message !== 'object') return;
        switch (message.cmd) {
            case 'join':
                config.room = message.room || 'default';
                config.ws = message.ws || '';
                config.token = message.token || null;
                if (message.role) {
                    config.role = message.role;
                }
                connect();
                break;
            case 'accept':
                acceptPending();
                break;
            case 'reject':
                rejectPending();
                break;
            case 'hangup':
                hangup(true);
                break;
            default:
                break;
        }
    }

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', ev => {
            try {
                handleHostMessage(ev.data);
            } catch (err) {
                console.warn('handleHostMessage error', err);
            }
        });
    }

    window.addEventListener('beforeunload', () => {
        isUnloading = true;
        try {
            if (ws && ws.readyState === WebSocket.OPEN && pc) {
                sendControl('bye');
            }
        } catch (err) {
            console.warn('beforeunload bye failed', err);
        }
    });
})();