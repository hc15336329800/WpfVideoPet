'use strict';

const fs = require('fs');
const https = require('https');
const { WebSocketServer, WebSocket } = require('ws');
const { v4: uuid } = require('uuid');

const HOST = process.env.HOST || '0.0.0.0';
const PORT = parseInt(process.env.PORT || '8443', 10);
const SSL_KEY = process.env.SSL_KEY || './certs/server-key.pem';
const SSL_CERT = process.env.SSL_CERT || './certs/server-cert.pem';
const OPERATOR_TOKEN = process.env.OPERATOR_TOKEN || 'lanmao001'; //(客户端需要和服务端保持一致，不然连接不上)

const server = https.createServer({
    key: fs.readFileSync(SSL_KEY),
    cert: fs.readFileSync(SSL_CERT),
});

server.on('tlsClientError', (err, socket) => {
    const remote = `${socket.remoteAddress || 'unknown'}:${socket.remotePort || '0'}`;
    console.warn('[tls] handshake error from %s - %s', remote, err?.message || err);
});

const wss = new WebSocketServer({ server });

/**
 * room 状态结构：
 * {
 *   operator: { id, ws } | null,
 *   pendingClientId: string | null,
 *   currentClientId: string | null,
 *   clients: Map<string, WebSocket>
 * }
 */
const rooms = new Map();

function safeSend(ws, payload) {
    try {
        ws.send(JSON.stringify(payload));
    } catch (err) {
        console.warn('[send] failed', err?.message || err);
    }
}

function getOrCreateRoom(roomId) {
    if (!rooms.has(roomId)) {
        rooms.set(roomId, {
            operator: null,
            pendingClientId: null,
            currentClientId: null,
            clients: new Map(),
        });
    }
    return rooms.get(roomId);
}

function cleanupClient(roomId, clientId, reason) {
    const room = rooms.get(roomId);
    if (!room) return;

    if (room.pendingClientId === clientId) {
        room.pendingClientId = null;
        if (room.operator?.ws) {
            safeSend(room.operator.ws, { type: 'incoming-cancelled', clientId, reason });
        }
    }

    if (room.currentClientId === clientId) {
        room.currentClientId = null;
        if (room.operator?.ws) {
            safeSend(room.operator.ws, { type: 'client-ended', clientId, reason });
            safeSend(room.operator.ws, { type: 'bye', reason: reason || 'client-left' });
        }
    }

    room.clients.delete(clientId);

    if (!room.operator && room.clients.size === 0) {
        rooms.delete(roomId);
    }
}

function cleanupOperator(roomId, reason) {
    const room = rooms.get(roomId);
    if (!room) return;

    const clients = Array.from(room.clients.values());
    clients.forEach((clientWs) => {
        safeSend(clientWs, { type: 'operator-offline', reason });
        try {
            clientWs.close(4005, 'operator-offline');
        } catch (err) {
            console.warn('close client fail', err?.message || err);
        }
    });

    room.clients.clear();
    room.pendingClientId = null;
    room.currentClientId = null;
    room.operator = null;

    rooms.delete(roomId);
}

wss.on('connection', (ws, req) => {
    const clientId = uuid();
    ws.clientId = clientId;
    ws.roomId = null;
    ws.role = null;
    ws.isAlive = true;
    ws.remoteAddress = req?.socket?.remoteAddress || 'unknown';
    console.log('[ws] connection accepted from %s (clientId=%s)', ws.remoteAddress, clientId);
    ws.on('pong', () => { ws.isAlive = true; });

    ws.on('error', (err) => {
        console.error('[ws error] %s (clientId=%s)', err?.message || err, ws.clientId);
    });

    ws.on('message', (data) => {
        let msg = {};
        try {
            msg = JSON.parse(data.toString());
        } catch {
            return;
        }

        const { type, room, role, token, payload } = msg;
        const roomId = room || 'default';

        if (type === 'join') {
            ws.roomId = roomId;
            ws.clientId = clientId;
            ws.role = role === 'operator' ? 'operator' : 'client';
            console.log('[join] %s requested role=%s room=%s (token=%s)', ws.remoteAddress, ws.role, roomId, token ? '***' : '<empty>');

            const roomState = getOrCreateRoom(roomId);

            if (ws.role === 'operator') {
                if (OPERATOR_TOKEN && token !== OPERATOR_TOKEN) {
                    console.warn('[auth] token mismatch from %s (clientId=%s)', ws.remoteAddress, clientId);
                    safeSend(ws, { type: 'unauthorized' });
                    try { ws.close(4003, 'unauthorized'); } catch { }
                    return;
                }

                if (roomState.operator && roomState.operator.ws && roomState.operator.ws.readyState === WebSocket.OPEN) {
                    console.warn('[join] duplicate operator for room=%s from %s', roomId, ws.remoteAddress);
                    safeSend(ws, { type: 'operator-exists' });
                    try { ws.close(4004, 'operator-exists'); } catch { }
                    return;
                }

                roomState.operator = { id: clientId, ws };
                console.log('[operator] joined room=%s (clientId=%s from %s)', roomId, clientId, ws.remoteAddress);
                safeSend(ws, { type: 'joined', role: 'operator', clientId });
                return;
            }

            if (!roomState.operator || roomState.operator.ws.readyState !== WebSocket.OPEN) {
                console.warn('[client] no operator online for room=%s (clientId=%s from %s)', roomId, clientId, ws.remoteAddress);
                safeSend(ws, { type: 'no-operator' });
                try { ws.close(4001, 'no-operator'); } catch { }
                return;
            }

            if (roomState.pendingClientId || roomState.currentClientId) {
                console.warn('[client] room=%s busy for clientId=%s from %s', roomId, clientId, ws.remoteAddress);
                safeSend(ws, { type: 'busy' });
                try { ws.close(4000, 'busy'); } catch { }
                return;
            }

            roomState.clients.set(clientId, ws);
            roomState.pendingClientId = clientId;

            safeSend(ws, { type: 'joined', role: 'client', clientId });
            safeSend(ws, { type: 'ringing' });

            const operatorId = roomState.operator?.id || '<none>';
            console.log('[client] pending call room=%s clientId=%s operator=%s (from %s)', roomId, clientId, operatorId, ws.remoteAddress);
            safeSend(roomState.operator.ws, { type: 'incoming', clientId });
            return;
        }

        if (!ws.roomId) {
            return;
        }

        const roomState = rooms.get(ws.roomId);
        if (!roomState) {
            return;
        }

        if (type === 'accept' && ws.role === 'operator') {
            const targetId = payload?.clientId || roomState.pendingClientId;
            if (!targetId) {
                return;
            }
            const clientWs = roomState.clients.get(targetId);
            if (!clientWs) {
                roomState.pendingClientId = null;
                safeSend(ws, { type: 'incoming-cancelled', clientId: targetId, reason: 'client-missing' });
                return;
            }

            roomState.currentClientId = targetId;
            roomState.pendingClientId = null;
            safeSend(clientWs, { type: 'start', operatorId: clientId });
            safeSend(ws, { type: 'start', clientId: targetId });
            console.log('[call] operator=%s accepted client=%s room=%s', clientId, targetId, ws.roomId);
            return;
        }

        if (type === 'reject' && ws.role === 'operator') {
            const targetId = payload?.clientId || roomState.pendingClientId;
            if (!targetId) {
                return;
            }
            const clientWs = roomState.clients.get(targetId);
            if (clientWs) {
                safeSend(clientWs, { type: 'reject' });
                try { clientWs.close(4002, 'rejected'); } catch { }
            }
            cleanupClient(ws.roomId, targetId, 'rejected');
            console.log('[call] operator=%s rejected client=%s room=%s', clientId, targetId, ws.roomId);
            return;
        }

        if (['offer', 'answer', 'candidate'].includes(type)) {
            if (ws.role === 'operator') {
                const targetId = roomState.currentClientId;
                if (!targetId) return;
                const clientWs = roomState.clients.get(targetId);
                if (clientWs) {
                    safeSend(clientWs, { type, payload });
                }
            } else if (ws.role === 'client') {
                if (roomState.currentClientId !== clientId) return;
                const operator = roomState.operator;
                if (operator?.ws && operator.ws.readyState === WebSocket.OPEN) {
                    safeSend(operator.ws, { type, payload });
                }
            }
            return;
        }

        if (type === 'bye') {
            if (ws.role === 'client') {
                const operator = roomState.operator;
                if (operator?.ws) {
                    safeSend(operator.ws, { type: 'bye', clientId });
                }
                cleanupClient(ws.roomId, clientId, 'client-bye');
                console.log('[bye] client=%s left room=%s', clientId, ws.roomId);
            } else if (ws.role === 'operator') {
                const targetId = roomState.currentClientId;
                if (targetId) {
                    const clientWs = roomState.clients.get(targetId);
                    if (clientWs) {
                        safeSend(clientWs, { type: 'bye' });
                    }
                    cleanupClient(ws.roomId, targetId, 'operator-bye');
                    console.log('[bye] operator=%s ended call with client=%s room=%s', clientId, targetId, ws.roomId);
                }
            }
        }
    });

    ws.on('close', (code, reasonBuffer) => {
        const role = ws.role;
        const roomId = ws.roomId;
        const reason = Buffer.isBuffer(reasonBuffer) ? reasonBuffer.toString() : reasonBuffer;
        console.log('[ws] connection closed clientId=%s role=%s room=%s code=%s reason=%s', ws.clientId, role || '<none>', roomId || '<none>', code, reason || '<empty>');
        if (!roomId) {
            return;
        }
        if (role === 'operator') {
            cleanupOperator(roomId, 'operator-closed');
        } else if (role === 'client') {
            cleanupClient(roomId, ws.clientId, 'client-closed');
        }
    });
});

const HEARTBEAT_MS = 30_000;
const interval = setInterval(() => {
    wss.clients.forEach((socket) => {
        if (socket.isAlive === false) {
            try { socket.terminate(); } catch { }
            return;
        }
        socket.isAlive = false;
        try { socket.ping(); } catch { }
    });
}, HEARTBEAT_MS);

wss.on('close', () => clearInterval(interval));

server.listen(PORT, HOST, () => {
    const shownHost = HOST === '0.0.0.0' ? 'YOUR_IP_OR_HOST' : HOST;
    console.log(`Signaling server on wss://${shownHost}:${PORT}`);
    console.log('角色说明：operator 端须携带 OPERATOR_TOKEN = lanmao001（可变），client 端无需提供 token。');
});