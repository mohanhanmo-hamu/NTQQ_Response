/**
 * NTQQ <-> QQToastTray Bridge (qq-bridge.js v8)
 *
 * Improvements in v8 (v1.2.0):
 *  - Reads LocalPort, AuthToken, FollowQQStartup, VerboseLog dynamically from config.json
 *  - Sends X-Auth-Token header on /notify and /poll for local IPC security
 *  - Reports running NTQQ version, loader patch status, and synced DND count to tray
 *  - Privacy-safe logging by default (redacts message plaintext unless VerboseLog is enabled)
 *  - Automatic log rotation at 512 KB to prevent unbounded disk growth
 *  - Bounded in-memory caches (contacts, avatars, seen message IDs)
 *  - Preserves all v7 reliability fixes: DND filtering, native click reuse, real main-window
 *    detection, and graceful exit flag handling
 */

(function () {
  try {
    const argvStr = (process.argv || []).join(' ');
    if (argvStr.includes('--type=')) {
      return;
    }

    // Guard against double-loading if both qqt.js and legacy qqtoast.js ever trigger
    if (global.__NTQQ_RESPONSE_BRIDGE_LOADED__) {
      return;
    }
    global.__NTQQ_RESPONSE_BRIDGE_LOADED__ = true;

    const http = require('http');
    const fs = require('fs');
    const path = require('path');
    const Module = require('module');

    const BRIDGE_VERSION = '8';
    const DEFAULT_PORT = 39876;
    const LOG_FILE = path.join(__dirname, 'bridge-debug.log');
    const LOG_BACKUP_FILE = path.join(__dirname, 'bridge-debug.log.1');
    const LOG_MAX_BYTES = 512 * 1024;
    const CONFIG_FILE = path.join(__dirname, 'config.json');
    const EXIT_FLAG_FILE = path.join(__dirname, 'tray-exit.flag');
    const BRIDGE_START_MS = Date.now();
    const LOADER_STATUS = String(process.env.QQTOAST_LOADER_STATUS || 'legacy');

    function detectRunningQQVersion() {
      try {
        const resPath = process.resourcesPath || '';
        if (resPath) {
          const verDir = path.basename(path.dirname(resPath));
          if (/^\d+\.\d+/.test(verDir)) return verDir;
        }
        const exeDir = path.basename(path.dirname(process.execPath || ''));
        if (/^\d+\.\d+/.test(exeDir)) return exeDir;
      } catch (e) {}
      return 'unknown';
    }
    const RUNNING_QQ_VERSION = detectRunningQQVersion();

    // ---------------------------------------------------------------------
    // Config & rotating privacy-safe logger
    // ---------------------------------------------------------------------
    let cachedConfig = null;
    let cachedConfigAt = 0;

    function readConfigSafely(forceFresh) {
      const now = Date.now();
      if (!forceFresh && cachedConfig && (now - cachedConfigAt) < 2000) {
        return cachedConfig;
      }
      for (let attempt = 0; attempt < 2; attempt++) {
        try {
          if (!fs.existsSync(CONFIG_FILE)) return cachedConfig;
          let raw = fs.readFileSync(CONFIG_FILE, 'utf8');
          if (raw.charCodeAt(0) === 0xFEFF) raw = raw.slice(1);
          raw = raw.replace(/^\uFEFF/, '').trim();
          if (!raw) continue;
          cachedConfig = JSON.parse(raw);
          cachedConfigAt = now;
          return cachedConfig;
        } catch (e) {}
      }
      return cachedConfig;
    }

    function getConfigValue(name, defaultValue, forceFresh) {
      const cfg = readConfigSafely(forceFresh);
      if (cfg && typeof cfg === 'object') {
        const key = Object.keys(cfg).find(k => String(k).toLowerCase() === name.toLowerCase());
        if (key !== undefined && cfg[key] !== undefined && cfg[key] !== null) {
          return cfg[key];
        }
      }
      return defaultValue;
    }

    function getTrayPort() {
      const p = Number(getConfigValue('LocalPort', DEFAULT_PORT, false));
      return (p >= 1024 && p <= 65535) ? p : DEFAULT_PORT;
    }

    function getAuthToken() {
      return String(getConfigValue('AuthToken', '', false) || '');
    }

    function isVerboseLog() {
      return getConfigValue('VerboseLog', false, false) === true;
    }

    let logWriteCount = 0;
    function rotateLogIfNeeded() {
      try {
        if (!fs.existsSync(LOG_FILE)) return;
        const st = fs.statSync(LOG_FILE);
        if (st.size >= LOG_MAX_BYTES) {
          try { if (fs.existsSync(LOG_BACKUP_FILE)) fs.unlinkSync(LOG_BACKUP_FILE); } catch (e) {}
          fs.renameSync(LOG_FILE, LOG_BACKUP_FILE);
        }
      } catch (e) {}
    }

    function log(...args) {
      try {
        logWriteCount++;
        if (logWriteCount === 1 || logWriteCount % 50 === 0) {
          rotateLogIfNeeded();
        }
        const line = `[${new Date().toISOString()}] [PID:${process.pid}] ` + args.map(a => {
          if (typeof a === 'object') {
            try { return JSON.stringify(a); } catch { return String(a); }
          }
          return String(a);
        }).join(' ') + '\n';
        fs.appendFileSync(LOG_FILE, line, 'utf8');
      } catch (e) {}
    }

    log(`=== qq-bridge.js v${BRIDGE_VERSION} initialized | QQ=${RUNNING_QQ_VERSION} | loader=${LOADER_STATUS} ===`);

    // ---------------------------------------------------------------------
    // Bounded Map helper & kernel state
    // ---------------------------------------------------------------------
    function setBounded(map, key, value, maxSize) {
      if (map.has(key)) map.delete(key);
      map.set(key, value);
      if (map.size > maxSize) {
        const firstKey = map.keys().next().value;
        map.delete(firstKey);
      }
    }

    let wrapperApi = null;
    let sessionInstance = null;
    let msgServiceInstance = null;
    let buddyServiceInstance = null;
    let groupServiceInstance = null;
    let profileServiceInstance = null;
    let recentContactServiceInstance = null;
    let avatarServiceInstance = null;
    let kernelListenerId = null;
    let keptListenerRef = null;
    let selfUid = '';
    let selfUin = '';

    const seenMsgIds = new Set();
    const contactNameCache = new Map();
    const avatarPathCache = new Map();
    const dndPeerMap = new Map();
    let lastMsgMeta = { peerUid: '', peerName: '', at: 0 };

    // ---------------------------------------------------------------------
    // Local HTTP IPC with Tray
    // ---------------------------------------------------------------------
    function buildAuthHeaders(extra) {
      const headers = Object.assign({}, extra || {});
      const token = getAuthToken();
      if (token) {
        headers['X-Auth-Token'] = token;
      }
      return headers;
    }

    function postToTray(endpoint, payload) {
      try {
        const data = Buffer.from(JSON.stringify(payload), 'utf8');
        const req = http.request({
          hostname: '127.0.0.1',
          port: getTrayPort(),
          path: endpoint,
          method: 'POST',
          headers: buildAuthHeaders({
            'Content-Type': 'application/json; charset=utf-8',
            'Content-Length': data.length
          }),
          timeout: 2000
        }, (res) => {
          res.resume();
        });
        req.on('error', () => {});
        req.write(data);
        req.end();
      } catch (e) {}
    }

    // ---------------------------------------------------------------------
    // Rich media & message element parsing
    // ---------------------------------------------------------------------
    function findValidLocalPath(pic) {
      if (!pic) return '';
      if (pic.sourcePath && typeof pic.sourcePath === 'string' && fs.existsSync(pic.sourcePath)) {
        try {
          if (fs.statSync(pic.sourcePath).size > 0) return pic.sourcePath;
        } catch (e) {}
      }
      if (pic.thumbPath) {
        let values = [];
        if (pic.thumbPath instanceof Map) {
          values = Array.from(pic.thumbPath.values());
        } else if (typeof pic.thumbPath === 'object') {
          values = Object.values(pic.thumbPath);
        }
        for (const p of values) {
          if (p && typeof p === 'string' && fs.existsSync(p)) {
            try {
              if (fs.statSync(p).size > 0) return p;
            } catch (e) {}
          }
        }
      }
      return '';
    }

    async function extractImagePath(picElement, msg, el) {
      if (!picElement) return '';
      let p = findValidLocalPath(picElement);
      if (p) return p;

      if (msgServiceInstance && typeof msgServiceInstance.downloadRichMedia === 'function') {
        try {
          msgServiceInstance.downloadRichMedia({
            peerUid: String(msg.peerUid || ''),
            chatType: Number(msg.chatType || 1),
            msgId: String(msg.msgId || '0'),
            elementId: String((el && el.elementId) || '0'),
            downloadType: 2, // 2 = thumb, 1 = original
            thumbSize: 0,
            filePath: '',
            fileModelId: '0',
            downSourceType: 0,
            triggerType: 1
          });
        } catch (err) {
          log('downloadRichMedia error:', err.message);
        }

        for (let i = 0; i < 4; i++) {
          await new Promise((resolve) => setTimeout(resolve, 150));
          p = findValidLocalPath(picElement);
          if (p) return p;
        }
      }
      return '';
    }

    function formatElements(elements) {
      if (!Array.isArray(elements)) return { text: '', isAtMe: false };
      const parts = [];
      let isAtMe = false;

      for (const el of elements) {
        if (!el) continue;
        if (el.textElement) {
          if (el.textElement.content) {
            parts.push(el.textElement.content);
          }
          const atType = Number(el.textElement.atType || 0);
          const atNtUid = String(el.textElement.atNtUid || '');
          const atUid = String(el.textElement.atUid || '');
          if (atType === 1) {
            isAtMe = true;
          } else if (atType === 2 && ((selfUid && atNtUid === selfUid) || (selfUin && atUid === String(selfUin)))) {
            isAtMe = true;
          }
        } else if (el.picElement) {
          parts.push('[图片]');
        } else if (el.faceElement) {
          parts.push('[表情]');
        } else if (el.pttElement) {
          parts.push('[语音]');
        } else if (el.videoElement) {
          parts.push('[视频]');
        } else if (el.fileElement) {
          parts.push(`[文件: ${el.fileElement.fileName || ''}]`);
        } else if (el.replyElement) {
          parts.push('[回复] ');
          if (selfUid && el.replyElement.senderUid === selfUid) {
            isAtMe = true;
          }
        } else if (el.arkElement) {
          parts.push('[卡片消息]');
        } else if (el.marketFaceElement) {
          parts.push(el.marketFaceElement.faceName || '[表情]');
        }
      }
      return { text: parts.join('').trim(), isAtMe };
    }

    async function resolveContactName(chatType, peerUid, senderUid, msg) {
      let sName = msg.sendRemarkName || msg.sendMemberName || msg.sendNickName || contactNameCache.get(senderUid) || '';
      let pName = msg.peerName || contactNameCache.get(peerUid) || '';

      if (!sName && senderUid) {
        try {
          if (buddyServiceInstance) {
            if (typeof buddyServiceInstance.getBuddyRemark === 'function') {
              const r = await buddyServiceInstance.getBuddyRemark([senderUid]);
              if (r && r instanceof Map && r.get(senderUid)) sName = r.get(senderUid);
              else if (r && typeof r === 'object' && r[senderUid]) sName = r[senderUid];
            }
            if (!sName && typeof buddyServiceInstance.getBuddyNick === 'function') {
              const n = await buddyServiceInstance.getBuddyNick([senderUid]);
              if (n && n instanceof Map && n.get(senderUid)) sName = n.get(senderUid);
              else if (n && typeof n === 'object' && n[senderUid]) sName = n[senderUid];
            }
          }
        } catch (e) {}
      }

      if (!sName && senderUid && profileServiceInstance) {
        try {
          if (typeof profileServiceInstance.getCoreAndBaseInfo === 'function') {
            const infoMap = await profileServiceInstance.getCoreAndBaseInfo('nodeStore', [senderUid]);
            if (infoMap && infoMap instanceof Map && infoMap.get(senderUid)) {
              const info = infoMap.get(senderUid);
              sName = (info.coreInfo && (info.coreInfo.remark || info.coreInfo.nick)) || '';
            }
          }
        } catch (e) {}
      }

      if (sName && senderUid) setBounded(contactNameCache, senderUid, sName, 2000);
      if (pName && peerUid) setBounded(contactNameCache, peerUid, pName, 2000);

      if (chatType === 1) {
        const finalName = sName || pName || contactNameCache.get(peerUid) || (msg.senderUin ? String(msg.senderUin) : 'QQ好友');
        if (finalName !== 'QQ好友') setBounded(contactNameCache, peerUid, finalName, 2000);
        return { senderName: finalName, peerName: finalName };
      } else {
        return {
          senderName: sName || (msg.senderUin ? String(msg.senderUin) : '群成员'),
          peerName: pName || peerUid
        };
      }
    }

    // ---------------------------------------------------------------------
    // Incoming message pipeline
    // ---------------------------------------------------------------------
    async function handleIncomingMsgList(msgList, sourceTag, isActiveEvent) {
      if (!Array.isArray(msgList)) return;
      const nowSec = Math.floor(Date.now() / 1000);

      for (const msg of msgList) {
        try {
          if (!msg || !msg.msgId) continue;
          const msgIdStr = String(msg.msgId);

          const msgTime = Number(msg.msgTime || 0);
          if (msgTime > 0 && Math.abs(nowSec - msgTime) > 45) {
            continue;
          }

          // Ignore self messages
          if (msg.sendType === 1 || msg.isSelf) continue;
          if (selfUid && msg.senderUid === selfUid) continue;
          if (selfUin && String(msg.senderUin) === String(selfUin)) continue;
          if (Number(msg.chatType) === 1 && msg.senderUid && msg.peerUid && msg.senderUid !== msg.peerUid) {
            selfUid = msg.senderUid;
            continue;
          }

          const chatType = Number(msg.chatType || 0);
          if (chatType !== 1 && chatType !== 2 && chatType !== 100) continue;

          const peerUid = String(msg.peerUid || msg.peerUin || '');
          if (!peerUid) continue;

          const { text: content, isAtMe } = formatElements(msg.elements);
          if (!content) continue;

          if (msg.peerName && peerUid) setBounded(contactNameCache, peerUid, msg.peerName, 2000);
          if ((msg.sendRemarkName || msg.sendNickName) && msg.senderUid) {
            setBounded(contactNameCache, msg.senderUid, msg.sendRemarkName || msg.sendNickName, 2000);
          }

          // DND filtering: muted groups only emit onRecvMsg, not onRecvActiveMsg
          if (chatType === 2 && !isActiveEvent && !isAtMe) {
            continue;
          }
          if (dndPeerMap.get(peerUid) === true && !isAtMe) {
            continue;
          }

          if (seenMsgIds.has(msgIdStr)) continue;
          seenMsgIds.add(msgIdStr);
          if (seenMsgIds.size > 500) {
            const first = seenMsgIds.values().next().value;
            seenMsgIds.delete(first);
          }

          lastMsgMeta = { peerUid: peerUid, peerName: String(msg.peerName || ''), at: Date.now() };
          associateNativeNotifications(peerUid, lastMsgMeta.peerName, lastMsgMeta.at);

          const { senderName, peerName } = await resolveContactName(chatType, peerUid, String(msg.senderUid || ''), msg);
          lastMsgMeta = { peerUid: peerUid, peerName: peerName || lastMsgMeta.peerName, at: Date.now() };
          associateNativeNotifications(peerUid, lastMsgMeta.peerName, lastMsgMeta.at);

          let avatarPath = avatarPathCache.get(peerUid) || '';
          try {
            if (!avatarPath && avatarServiceInstance) {
              if (chatType === 2 && typeof avatarServiceInstance.getGroupAvatarPath === 'function') {
                avatarPath = avatarServiceInstance.getGroupAvatarPath(peerUid, 0) || '';
              } else if (typeof avatarServiceInstance.getAvatarPath === 'function') {
                const targetUid = chatType === 1 ? (msg.senderUid || peerUid) : peerUid;
                avatarPath = avatarServiceInstance.getAvatarPath(targetUid, 0, false) || '';
              }
              if (avatarPath) setBounded(avatarPathCache, peerUid, avatarPath, 2000);
            }
          } catch (e) {}

          let imagePath = '';
          let pendingPicEl = null;
          if (Array.isArray(msg.elements)) {
            for (const el of msg.elements) {
              if (el && el.picElement) {
                imagePath = await extractImagePath(el.picElement, msg, el);
                if (imagePath) {
                  break;
                } else {
                  pendingPicEl = el;
                }
              }
            }
          }

          if (isVerboseLog()) {
            log(`Notify (${sourceTag}) [chatType=${chatType}, atMe=${isAtMe}] peer=${peerName}(${peerUid}) img=${imagePath ? 'yes' : 'no'}: ${content}`);
          } else {
            log(`Notify (${sourceTag}) [chatType=${chatType}, atMe=${isAtMe}] peerUid=${peerUid} img=${imagePath ? 'yes' : 'no'} len=${content.length}`);
          }

          postToTray('/notify', {
            msgId: msgIdStr,
            chatType: chatType,
            peerUid: peerUid,
            peerName: peerName,
            senderUid: String(msg.senderUid || ''),
            senderName: senderName,
            content: isAtMe && !content.startsWith('[有人@我]') ? `[有人@我] ${content}` : content,
            avatarPath: String(avatarPath || ''),
            imagePath: String(imagePath || ''),
            isImageUpdate: false
          });

          // Background retry for slow image thumbnail downloads
          if (!imagePath && pendingPicEl && pendingPicEl.picElement) {
            const targetPicEl = pendingPicEl.picElement;
            let checkCount = 0;
            const timer = setInterval(() => {
              checkCount++;
              try {
                const asyncPath = findValidLocalPath(targetPicEl);
                if (asyncPath) {
                  clearInterval(timer);
                  log(`Async image ready for msgId=${msgIdStr}`);
                  postToTray('/notify', {
                    msgId: msgIdStr,
                    chatType: chatType,
                    peerUid: peerUid,
                    peerName: peerName,
                    senderUid: String(msg.senderUid || ''),
                    senderName: senderName,
                    content: '',
                    avatarPath: String(avatarPath || ''),
                    imagePath: asyncPath,
                    isImageUpdate: true
                  });
                  return;
                }
              } catch (e) {}
              if (checkCount >= 8) {
                clearInterval(timer);
              }
            }, 400);
          }
        } catch (err) {
          log('Error handling msg item:', err.message);
        }
      }
    }

    // ---------------------------------------------------------------------
    // NTQQ Kernel listeners & DND synchronization
    // ---------------------------------------------------------------------
    function attachCustomMsgListener(svc) {
      if (!svc || kernelListenerId !== null) return;
      try {
        const rawTarget = {
          onRecvMsg(msgList) {
            handleIncomingMsgList(msgList, 'Kernel.onRecvMsg', false);
          },
          onRecvActiveMsg(msgList) {
            handleIncomingMsgList(msgList, 'Kernel.onRecvActiveMsg', true);
          },
          onRichMediaDownloadComplete(notifyInfo) {
            try {
              if (notifyInfo && notifyInfo.filePath && fs.existsSync(notifyInfo.filePath)) {
                postToTray('/notify', {
                  msgId: String(notifyInfo.msgId || ''),
                  chatType: Number(notifyInfo.chatType || 1),
                  peerUid: String(notifyInfo.peerUid || ''),
                  peerName: '',
                  senderUid: '',
                  senderName: '',
                  content: '',
                  avatarPath: '',
                  imagePath: notifyInfo.filePath,
                  isImageUpdate: true
                });
              }
            } catch (e) {}
          }
        };
        keptListenerRef = new Proxy(rawTarget, {
          get(target, prop) {
            if (prop in target) return target[prop];
            if (typeof prop === 'string' && prop.startsWith('on')) {
              return function (...cbArgs) {
                if (prop === 'onRecvActiveMsg') {
                  handleIncomingMsgList(cbArgs[0], 'KernelProxy.onRecvActiveMsg', true);
                } else if (prop === 'onRecvMsg') {
                  handleIncomingMsgList(cbArgs[0], 'KernelProxy.onRecvMsg', false);
                }
              };
            }
            return target[prop];
          },
          has(target, prop) {
            if (typeof prop === 'string' && prop.startsWith('on')) return true;
            return prop in target;
          }
        });

        kernelListenerId = svc.addKernelMsgListener(keptListenerRef);
        log('Registered KernelMsgListener id =', kernelListenerId);
      } catch (e) {
        log('attachCustomMsgListener error:', e.message);
      }
    }

    function processContactList(list, source) {
      if (!Array.isArray(list) || list.length === 0) return;
      for (const c of list) {
        if (!c) continue;
        if (Array.isArray(c.nestedChangedList) && c.nestedChangedList.length > 0) {
          processContactList(c.nestedChangedList, source + '.nested');
        }
        if (!c.peerUid) continue;
        const uid = String(c.peerUid);
        const name = c.remark || c.peerName || c.sendNickName;
        if (name) setBounded(contactNameCache, uid, String(name), 2000);
        if (c.avatarPath) setBounded(avatarPathCache, uid, String(c.avatarPath), 2000);

        const cType = Number(c.chatType || 0);
        const shield = Number(c.shieldFlag || 0);
        if (c.isMsgDisturb === true || c.notDisturb === true || Number(c.notifiedType) === 2) {
          dndPeerMap.set(uid, true);
        } else if (cType === 2 && shield > 1) {
          dndPeerMap.set(uid, true);
        } else if (cType === 1 && shield >= 1) {
          dndPeerMap.set(uid, true);
        } else if (c.isMsgDisturb === false && ((cType === 2 && shield === 1) || (cType === 1 && shield === 0))) {
          if (!dndPeerMap.has(uid)) {
            dndPeerMap.set(uid, false);
          }
        }
      }
    }

    function processGroupList(groupList) {
      if (!Array.isArray(groupList) || groupList.length === 0) return;
      for (const g of groupList) {
        if (!g || !g.groupCode) continue;
        const gCode = String(g.groupCode);
        if (g.groupName) setBounded(contactNameCache, gCode, String(g.groupName), 2000);
        if (g.remarkName) setBounded(contactNameCache, gCode, String(g.remarkName), 2000);
        const mask = Number(g.cmdUinMsgMask || g.msgMask || 0);
        if (mask === 1) {
          dndPeerMap.set(gCode, false);
        } else if (mask > 1) {
          dndPeerMap.set(gCode, true);
        }
      }
    }

    let groupListenerId = null;
    let keptGroupListenerRef = null;
    function attachGroupListener(gSvc) {
      if (!gSvc || groupListenerId !== null || typeof gSvc.addKernelGroupListener !== 'function') return;
      try {
        const target = {
          onGroupListUpdate(updateType, groupList) {
            processGroupList(groupList);
          }
        };
        keptGroupListenerRef = new Proxy(target, {
          get(t, prop) {
            if (prop in t) return t[prop];
            if (typeof prop === 'string' && prop.startsWith('on')) {
              return function (...args) {
                if (prop === 'onGroupListUpdate') {
                  processGroupList(args[1] || args[0]);
                }
              };
            }
            return t[prop];
          },
          has(t, prop) {
            if (typeof prop === 'string' && prop.startsWith('on')) return true;
            return prop in t;
          }
        });
        groupListenerId = gSvc.addKernelGroupListener(keptGroupListenerRef);
        log('Registered KernelGroupListener id =', groupListenerId);
      } catch (e) {
        log('attachGroupListener err:', e.message);
      }
    }

    async function inspectDndSettings() {
      try {
        if (recentContactServiceInstance) {
          if (typeof recentContactServiceInstance.getRecentContactInfos === 'function') {
            const infos = await recentContactServiceInstance.getRecentContactInfos();
            if (infos && Array.isArray(infos.relation)) {
              processContactList(infos.relation, 'getRecentContactInfos.relation');
            } else if (infos && infos.detail && Array.isArray(infos.detail)) {
              processContactList(infos.detail, 'getRecentContactInfos.detail');
            } else if (infos && infos.info && Array.isArray(infos.info.changedList)) {
              processContactList(infos.info.changedList, 'getRecentContactInfos.changedList');
            }
          }
          if (typeof recentContactServiceInstance.getRecentContactListSnapShot === 'function') {
            const snap = await recentContactServiceInstance.getRecentContactListSnapShot(200);
            if (snap && snap.info && Array.isArray(snap.info.changedList)) {
              processContactList(snap.info.changedList, 'getRecentContactListSnapShot');
            }
          }
        }
      } catch (e) {}
      try {
        if (groupServiceInstance) {
          if (typeof groupServiceInstance.getGroupList === 'function') {
            await groupServiceInstance.getGroupList(true);
            await groupServiceInstance.getGroupList(false);
          }
          if (typeof groupServiceInstance.getGroupMsgMask === 'function') {
            await groupServiceInstance.getGroupMsgMask();
          }
        }
      } catch (e) {}
    }

    function tryAcquireKernelMsgService() {
      if (msgServiceInstance) return msgServiceInstance;
      if (!wrapperApi || !wrapperApi.NodeIQQNTWrapperSession) return null;

      const S = wrapperApi.NodeIQQNTWrapperSession;
      if (typeof S.getNTWrapperSession !== 'function') return null;

      for (const key of ['nt_1', 'default', '']) {
        try {
          const sess = key === '' ? S.getNTWrapperSession() : S.getNTWrapperSession(key);
          if (sess && typeof sess.getMsgService === 'function') {
            const svc = sess.getMsgService();
            if (svc && typeof svc.sendMsg === 'function') {
              sessionInstance = sess;
              msgServiceInstance = svc;
              try {
                if (typeof sess.getAvatarService === 'function') avatarServiceInstance = sess.getAvatarService();
                if (typeof sess.getBuddyService === 'function') buddyServiceInstance = sess.getBuddyService();
                if (typeof sess.getProfileService === 'function') profileServiceInstance = sess.getProfileService();
                if (typeof sess.getGroupService === 'function') groupServiceInstance = sess.getGroupService();
                if (typeof sess.getRecentContactService === 'function') recentContactServiceInstance = sess.getRecentContactService();
              } catch (e) {}
              log(`Acquired NTQQ kernel session via key=${JSON.stringify(key)}`);
              attachCustomMsgListener(svc);
              if (groupServiceInstance) attachGroupListener(groupServiceInstance);
              inspectDndSettings();
              setTimeout(inspectDndSettings, 3500);
              setTimeout(inspectDndSettings, 9000);
              return svc;
            }
          }
        } catch (e) {}
      }
      return null;
    }

    async function sendReplyMessage(chatType, peerUid, textContent) {
      log(`Sending quick reply -> chatType=${chatType}, peerUid=${peerUid}, len=${String(textContent || '').length}`);
      const svc = tryAcquireKernelMsgService();
      const peer = {
        chatType: Number(chatType),
        peerUid: String(peerUid),
        guildId: ''
      };
      const elements = [
        {
          elementType: 1,
          elementId: '',
          textElement: {
            content: String(textContent),
            atType: 0,
            atUid: '',
            atTinyId: '',
            atNtUid: ''
          }
        }
      ];

      if (svc && typeof svc.sendMsg === 'function') {
        try {
          await svc.sendMsg('0', peer, elements, new Map());
          return true;
        } catch (e1) {
          try {
            await svc.sendMsg('0', peer, elements);
            return true;
          } catch (e2) {
            log('sendMsg failed:', e2.message);
          }
        }
      }
      return false;
    }

    // Hook wrapper.node loading to capture NTQQ kernel API
    const origDlopen = process.dlopen;
    process.dlopen = function (moduleObj, filename, ...rest) {
      const ret = origDlopen.call(this, moduleObj, filename, ...rest);
      try {
        if (typeof filename === 'string') {
          const base = path.basename(filename).toLowerCase();
          if (base === 'wrapper.node' && moduleObj && moduleObj.exports) {
            wrapperApi = moduleObj.exports;
          }
        }
      } catch (e) {}
      return ret;
    };

    // ---------------------------------------------------------------------
    // Native Electron notification capture & click reuse
    // ---------------------------------------------------------------------
    let lastTrayAliveMs = 0;
    const nativeNotifications = [];

    function associateNativeNotifications(peerUid, peerName, atMs) {
      if (!peerUid || !atMs) return;
      for (let i = nativeNotifications.length - 1; i >= 0; i--) {
        const rec = nativeNotifications[i];
        if (atMs - rec.ts > 8000) break;
        const delta = Math.abs(rec.ts - atMs);
        if (delta > 5000) continue;
        const prevDelta = rec.assocAt ? Math.abs(rec.ts - rec.assocAt) : Infinity;
        if (!rec.peerUid || delta < prevDelta) {
          rec.peerUid = peerUid;
          rec.peerName = peerName || rec.peerName;
          rec.assocAt = atMs;
        }
      }
    }

    function captureNativeNotification(n) {
      try {
        const now = Date.now();
        const recentMsg = (now - lastMsgMeta.at < 3000) ? lastMsgMeta : null;
        const record = {
          ts: now,
          peerUid: recentMsg ? recentMsg.peerUid : '',
          peerName: recentMsg ? recentMsg.peerName : '',
          assocAt: recentMsg ? recentMsg.at : 0,
          title: String(n.title || ''),
          body: String(n.body || ''),
          tag: String(n.tag || ''),
          clickListeners: (typeof n.listenerCount === 'function') ? n.listenerCount('click') : -1,
          ref: n
        };
        nativeNotifications.push(record);
        while (nativeNotifications.length > 30) {
          nativeNotifications.shift();
        }
      } catch (e) {}
    }

    function emitNativeNotificationClick(item) {
      if (nativeNotifications.length === 0) return false;

      const peerUid = String((item && item.peerUid) || '');
      const name = String((item && item.peerName) || '').trim();
      const now = Date.now();
      let pick = null;

      if (peerUid) {
        for (let i = nativeNotifications.length - 1; i >= 0; i--) {
          const rec = nativeNotifications[i];
          if (now - rec.ts > 5 * 60 * 1000) continue;
          if (rec.peerUid === peerUid) {
            pick = rec;
            break;
          }
        }
      }

      if (!pick && name) {
        for (let i = nativeNotifications.length - 1; i >= 0; i--) {
          const rec = nativeNotifications[i];
          if (now - rec.ts > 5 * 60 * 1000) continue;
          if (rec.peerName === name ||
              (rec.title && rec.title.indexOf(name) >= 0) ||
              (rec.body && rec.body.indexOf(name) >= 0)) {
            pick = rec;
            break;
          }
        }
      }

      if (!pick) return false;

      try {
        const count = (typeof pick.ref.listenerCount === 'function') ? pick.ref.listenerCount('click') : 0;
        if (count <= 0) return false;
        pick.ref.emit('click', {});
        return true;
      } catch (e) {
        log('emitNativeNotificationClick error:', e.message);
        return false;
      }
    }

    function inspectAndCacheIpcPayload(item) {
      if (!item || typeof item !== 'object') return;
      const cmd = String(item.cmdName || item.callbackId || '');
      const payload = item.payload;

      if (cmd.includes('onRecentContactListChanged') || cmd.includes('onRecentContact')) {
        const list = (payload && payload[0] && payload[0].changedList) || (payload && payload.changedList);
        if (Array.isArray(list)) {
          processContactList(list, 'IPC:' + cmd);
        }
      }

      if (cmd.includes('onGroupListUpdate') || cmd.includes('GroupList')) {
        const gList = (payload && payload[0] && payload[0].groupList) || (payload && payload.groupList) || (payload && payload[1]);
        if (Array.isArray(gList)) {
          processGroupList(gList);
        }
      }

      if (cmd.includes('onRecvActiveMsg')) {
        const msgList = (payload && payload[0] && payload[0].msgList) || (payload && payload.msgList);
        if (Array.isArray(msgList)) {
          handleIncomingMsgList(msgList, 'IPC:' + cmd, true);
        }
      } else if (cmd.includes('onRecvMsg')) {
        const msgList = (payload && payload[0] && payload[0].msgList) || (payload && payload.msgList);
        if (Array.isArray(msgList)) {
          handleIncomingMsgList(msgList, 'IPC:' + cmd, false);
        }
      } else if (cmd.includes('onRichMediaDownloadComplete')) {
        try {
          const info = (payload && payload[0]) || payload;
          if (info && info.filePath && fs.existsSync(info.filePath)) {
            postToTray('/notify', {
              msgId: String(info.msgId || ''),
              chatType: Number(info.chatType || 1),
              peerUid: String(info.peerUid || ''),
              peerName: '',
              senderUid: '',
              senderName: '',
              content: '',
              avatarPath: '',
              imagePath: info.filePath,
              isImageUpdate: true
            });
          }
        } catch (e) {}
      }
    }

    let electronHooked = false;
    function hookElectronApp(electron) {
      if (electronHooked || !electron || !electron.app) return;
      electronHooked = true;

      try {
        if (electron.Notification && electron.Notification.prototype) {
          const origShow = electron.Notification.prototype.show;
          electron.Notification.prototype.show = function (...args) {
            captureNativeNotification(this);
            if (Date.now() - lastTrayAliveMs < 5000 && getConfigValue('Enabled', true, false) !== false) {
              return;
            }
            return origShow.apply(this, args);
          };
        }
      } catch (e) {}

      electron.app.on('browser-window-created', (event, window) => {
        try {
          const wc = window.webContents;
          if (!wc || wc.__qqToastHooked) return;
          wc.__qqToastHooked = true;

          const origSend = wc.send;
          wc.send = function (channel, ...args) {
            try {
              for (const arg of args) {
                if (!arg || typeof arg !== 'object') continue;
                if (Array.isArray(arg.LocalLoginInfoList) && arg.LocalLoginInfoList[0]) {
                  const first = arg.LocalLoginInfoList[0];
                  if (first.uid && !selfUid) selfUid = first.uid;
                  if (first.uin && !selfUin) selfUin = first.uin;
                }
                const items = Array.isArray(arg) ? arg : [arg];
                for (const item of items) {
                  inspectAndCacheIpcPayload(item);
                }
              }
            } catch (e) {}
            return origSend.apply(this, [channel, ...args]);
          };
        } catch (e) {}
      });
    }

    const origModuleLoad = Module._load;
    Module._load = function (request, parent, isMain) {
      const exported = origModuleLoad.apply(this, arguments);
      try {
        if (request === 'electron' || request === 'electron/main') {
          hookElectronApp(exported);
        }
      } catch (e) {}
      return exported;
    };

    // ---------------------------------------------------------------------
    // Safe main-window activation (avoids blank #/blank helper window)
    // ---------------------------------------------------------------------
    function describeWindow(win) {
      try {
        const bounds = win.getBounds ? win.getBounds() : { width: 0, height: 0 };
        let url = '';
        try { url = win.webContents && win.webContents.getURL ? String(win.webContents.getURL() || '') : ''; } catch (e) {}
        return {
          id: win.id,
          width: bounds.width || 0,
          height: bounds.height || 0,
          visible: win.isVisible ? win.isVisible() : false,
          resizable: win.isResizable ? win.isResizable() : false,
          minimizable: win.isMinimizable ? win.isMinimizable() : false,
          url: url
        };
      } catch (e) {
        return { id: -1, width: 0, height: 0, visible: false, resizable: false, minimizable: false, url: '' };
      }
    }

    function pickQQMainWindow(electron) {
      const all = electron.BrowserWindow.getAllWindows().filter(w => w && !(w.isDestroyed && w.isDestroyed()));
      const infos = all.map(win => ({ win: win, info: describeWindow(win) })).filter(x => {
        const i = x.info;
        if (i.width < 600 || i.height < 400) return false;
        if (/devtools/i.test(i.url)) return false;
        return true;
      });

      const isBlankHelper = (url) => /#\/blank/i.test(url) || /chatPoolWin=1/i.test(url);
      const isMainPage = (url) => /#\/main(\/|$)/i.test(url) || /\/main\/message/i.test(url);

      let pool = infos.filter(x => isMainPage(x.info.url) && !isBlankHelper(x.info.url));
      if (pool.length === 0) {
        pool = infos.filter(x => !isBlankHelper(x.info.url));
      }
      if (pool.length === 0) return null;

      pool.sort((a, b) => {
        if (a.info.resizable !== b.info.resizable) return a.info.resizable ? -1 : 1;
        if (a.info.minimizable !== b.info.minimizable) return a.info.minimizable ? -1 : 1;
        return (b.info.width * b.info.height) - (a.info.width * a.info.height);
      });

      return pool[0].win;
    }

    function showMainWindow(win) {
      try {
        if (win.isMinimized && win.isMinimized()) win.restore();
        if (win.show) win.show();
        if (win.focus) win.focus();
        if (win.moveTop) win.moveTop();
        if (win.webContents && win.webContents.focus) win.webContents.focus();
        return true;
      } catch (e) {
        log('showMainWindow error:', e.message);
        return false;
      }
    }

    function repaintQQWindows(electron, win) {
      try {
        const all = electron.webContents.getAllWebContents();
        for (const wc of all) {
          try {
            if (wc && !wc.isDestroyed() && typeof wc.invalidate === 'function') wc.invalidate();
          } catch (e) {}
        }
        if (win.webContents && typeof win.webContents.invalidate === 'function') {
          win.webContents.invalidate();
        }
      } catch (e) {}
    }

    function handleOpenChat(item) {
      const nativeClicked = emitNativeNotificationClick(item);
      try {
        let electron = null;
        try { electron = require('electron'); } catch (e) {}
        if (!electron || !electron.BrowserWindow || typeof electron.BrowserWindow.getAllWindows !== 'function') {
          return;
        }

        const ensureQQWindow = (delayMs) => {
          setTimeout(() => {
            try {
              const w = pickQQMainWindow(electron);
              if (!w) return;
              showMainWindow(w);
              repaintQQWindows(electron, w);
              setTimeout(() => { try { repaintQQWindows(electron, w); } catch (e) {} }, 450);
            } catch (e) {}
          }, delayMs);
        };

        if (nativeClicked) {
          ensureQQWindow(600);
          return;
        }

        const win = pickQQMainWindow(electron);
        if (win) {
          showMainWindow(win);
          repaintQQWindows(electron, win);
          setTimeout(() => { try { repaintQQWindows(electron, win); } catch (e) {} }, 450);
        }
      } catch (eWin) {
        log('handleOpenChat error:', eWin.message);
      }
    }

    // ---------------------------------------------------------------------
    // Tray auto-launch & heartbeat polling
    // ---------------------------------------------------------------------
    function userQuitTraySinceBridgeStart() {
      try {
        if (!fs.existsSync(EXIT_FLAG_FILE)) return false;
        const raw = fs.readFileSync(EXIT_FLAG_FILE, 'utf8').trim();
        const flagMs = parseInt(raw, 10);
        if (!isNaN(flagMs)) {
          return flagMs > BRIDGE_START_MS;
        }
        return fs.statSync(EXIT_FLAG_FILE).mtimeMs > BRIDGE_START_MS;
      } catch (e) {
        return false;
      }
    }

    let consecutivePollFails = 0;
    let lastTrayLaunchAttemptMs = 0;

    function tryAutoLaunchTray() {
      try {
        if (getConfigValue('FollowQQStartup', true, true) === false) {
          return;
        }
        if (userQuitTraySinceBridgeStart()) {
          return;
        }
        const now = Date.now();
        if (now - lastTrayLaunchAttemptMs < 15000) {
          return;
        }
        lastTrayLaunchAttemptMs = now;

        const trayExePath = path.join(__dirname, 'QQToastTray.exe');
        if (fs.existsSync(trayExePath)) {
          log('Tray not responding; auto-launching QQToastTray.exe');
          const child_process = require('child_process');
          const cp = child_process.spawn(trayExePath, [], {
            cwd: __dirname,
            detached: true,
            stdio: 'ignore',
            windowsHide: false
          });
          cp.unref();
        }
      } catch (e) {
        log('tryAutoLaunchTray error:', e.message);
      }
    }

    let pollTicks = 0;
    setInterval(() => {
      try {
        pollTicks++;
        if (!msgServiceInstance) {
          tryAcquireKernelMsgService();
        } else if (dndPeerMap.size === 0 && pollTicks % 5 === 0) {
          inspectDndSettings();
        }
        if (!msgServiceInstance) {
          return;
        }

        const qs =
          `?qqVersion=${encodeURIComponent(RUNNING_QQ_VERSION)}` +
          `&loader=${encodeURIComponent(LOADER_STATUS)}` +
          `&dndCount=${dndPeerMap.size}` +
          `&bridgeVer=${BRIDGE_VERSION}`;

        const req = http.get({
          hostname: '127.0.0.1',
          port: getTrayPort(),
          path: '/poll' + qs,
          headers: buildAuthHeaders(),
          timeout: 1500
        }, (res) => {
          consecutivePollFails = 0;
          lastTrayAliveMs = Date.now();
          let raw = '';
          res.setEncoding('utf8');
          res.on('data', chunk => { raw += chunk; });
          res.on('end', () => {
            try {
              const data = JSON.parse(raw);
              if (data && Array.isArray(data.replies) && data.replies.length > 0) {
                for (const item of data.replies) {
                  if (item.action === 'openChat') {
                    handleOpenChat(item);
                  } else {
                    sendReplyMessage(item.chatType, item.peerUid, item.content);
                  }
                }
              }
            } catch (e) {}
          });
        });
        req.on('error', () => {
          consecutivePollFails++;
          if (consecutivePollFails >= 2) {
            tryAutoLaunchTray();
          }
        });
      } catch (e) {}
    }, 1000);
  } catch (e) {}
})();
