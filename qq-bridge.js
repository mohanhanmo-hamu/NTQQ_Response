/**
 * NTQQ <-> QQToastTray Bridge (qq-bridge.js v6)
 * - Respects QQ DND (Do-Not-Disturb) settings via onRecvActiveMsg + @me detection
 * - Resolves friend remark/nickname & avatars via BuddyService / ProfileService / RecentContactService
 */

(function () {
  try {
    const argvStr = (process.argv || []).join(' ');
    if (argvStr.includes('--type=')) {
      return;
    }

    const http = require('http');
    const fs = require('fs');
    const path = require('path');
    const Module = require('module');

    const TRAY_PORT = 39876;
    const LOG_FILE = path.join(__dirname, 'bridge-debug.log');

    function log(...args) {
      try {
        const line = `[${new Date().toISOString()}] [PID:${process.pid}] ` + args.map(a => {
          if (typeof a === 'object') {
            try { return JSON.stringify(a); } catch { return String(a); }
          }
          return String(a);
        }).join(' ') + '\n';
        fs.appendFileSync(LOG_FILE, line, 'utf8');
      } catch (e) {}
    }

    log('=== qq-bridge.js v6 initialized ===');

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
    const contactNameCache = new Map(); // uid/peerUid -> name
    const avatarPathCache = new Map();  // uid/peerUid -> local file path
    const dndPeerMap = new Map();       // peerUid -> boolean (true if DND/muted)

    let loggedSamplePicElement = false;

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

    function postToTray(endpoint, payload) {
      try {
        const data = Buffer.from(JSON.stringify(payload), 'utf8');
        const req = http.request({
          hostname: '127.0.0.1',
          port: TRAY_PORT,
          path: endpoint,
          method: 'POST',
          headers: {
            'Content-Type': 'application/json; charset=utf-8',
            'Content-Length': data.length
          },
          timeout: 2000
        }, (res) => {
          res.resume();
        });
        req.on('error', () => {});
        req.write(data);
        req.end();
      } catch (e) {}
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
          // Check if @me (atType: 1 = @All, 2 = @Specific user)
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
      // 1. Check direct fields on msg
      let sName = msg.sendRemarkName || msg.sendMemberName || msg.sendNickName || contactNameCache.get(senderUid) || '';
      let pName = msg.peerName || contactNameCache.get(peerUid) || '';

      // 2. Try BuddyService synchronous/async methods for C2C friend name
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

      if (sName && senderUid) contactNameCache.set(senderUid, sName);
      if (pName && peerUid) contactNameCache.set(peerUid, pName);

      if (chatType === 1) {
        const finalName = sName || pName || contactNameCache.get(peerUid) || (msg.senderUin ? String(msg.senderUin) : 'QQ好友');
        if (finalName !== 'QQ好友') contactNameCache.set(peerUid, finalName);
        return { senderName: finalName, peerName: finalName };
      } else {
        return {
          senderName: sName || (msg.senderUin ? String(msg.senderUin) : '群成员'),
          peerName: pName || peerUid
        };
      }
    }

    let loggedSampleMsg = false;
    async function handleIncomingMsgList(msgList, sourceTag, isActiveEvent) {
      if (!Array.isArray(msgList)) return;
      const nowSec = Math.floor(Date.now() / 1000);

      for (const msg of msgList) {
        try {
          if (!msg || !msg.msgId) continue;
          if (!loggedSampleMsg) {
            loggedSampleMsg = true;
            const copy = Object.assign({}, msg);
            delete copy.elements;
            log('Sample raw msg fields (' + sourceTag + '):', copy);
          }
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

          // Cache names if present on msg
          if (msg.peerName && peerUid) contactNameCache.set(peerUid, msg.peerName);
          if ((msg.sendRemarkName || msg.sendNickName) && msg.senderUid) {
            contactNameCache.set(msg.senderUid, msg.sendRemarkName || msg.sendNickName);
          }

          // DND filtering logic:
          // For group chats (chatType === 2):
          // NTQQ only fires `onRecvActiveMsg` (isActiveEvent === true) for NON-muted groups!
          // Muted (免打扰) groups only fire `onRecvMsg` (isActiveEvent === false).
          // So if chatType === 2 and !isActiveEvent and !isAtMe, it is a muted/DND group message -> skip!
          if (chatType === 2 && !isActiveEvent && !isAtMe) {
            continue;
          }
          // Also check explicit dndPeerMap from RecentContact list
          if (dndPeerMap.get(peerUid) === true && !isAtMe) {
            continue;
          }

          // Deduplicate across channels once it passes the active/atMe filter
          if (seenMsgIds.has(msgIdStr)) continue;
          seenMsgIds.add(msgIdStr);
          if (seenMsgIds.size > 500) {
            const first = seenMsgIds.values().next().value;
            seenMsgIds.delete(first);
          }

          const { senderName, peerName } = await resolveContactName(chatType, peerUid, String(msg.senderUid || ''), msg);

          let avatarPath = avatarPathCache.get(peerUid) || '';
          try {
            if (!avatarPath && avatarServiceInstance) {
              if (chatType === 2 && typeof avatarServiceInstance.getGroupAvatarPath === 'function') {
                avatarPath = avatarServiceInstance.getGroupAvatarPath(peerUid, 0) || '';
              } else if (typeof avatarServiceInstance.getAvatarPath === 'function') {
                const targetUid = chatType === 1 ? (msg.senderUid || peerUid) : peerUid;
                avatarPath = avatarServiceInstance.getAvatarPath(targetUid, 0, false) || '';
              }
            }
          } catch (e) {}

          let imagePath = '';
          let pendingPicEl = null;
          if (Array.isArray(msg.elements)) {
            for (const el of msg.elements) {
              if (el && el.picElement) {
                if (!loggedSamplePicElement) {
                  loggedSamplePicElement = true;
                  try {
                    const picCopy = {};
                    for (const k of Object.keys(el.picElement)) {
                      const v = el.picElement[k];
                      if (Buffer.isBuffer(v)) picCopy[k] = `<Buffer ${v.length}>`;
                      else if (v instanceof Map) picCopy[k] = `Map(${v.size})`;
                      else picCopy[k] = v;
                    }
                    log('Sample picElement structure:', picCopy);
                  } catch (e) {}
                }
                imagePath = await extractImagePath(el.picElement, msg, el);
                if (imagePath) {
                  break;
                } else {
                  pendingPicEl = el;
                }
              }
            }
          }

          log(`Notify msg (${sourceTag}) [chatType=${chatType}, atMe=${isAtMe}] from ${senderName} (peer=${peerName}, uid=${peerUid}, img=${imagePath ? 'yes' : 'no'}): ${content}`);

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

          // Background async retry for real QQ image downloading (if > 600ms)
          if (!imagePath && pendingPicEl && pendingPicEl.picElement) {
            const targetPicEl = pendingPicEl.picElement;
            let checkCount = 0;
            const maxChecks = 6;
            const timer = setInterval(() => {
              checkCount++;
              try {
                const asyncPath = findValidLocalPath(targetPicEl);
                if (asyncPath) {
                  clearInterval(timer);
                  log(`Async image downloaded for ${msgIdStr}: ${asyncPath}`);
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
              if (checkCount >= maxChecks) {
                clearInterval(timer);
              }
            }, 400);
          }
        } catch (err) {
          log('Error handling msg item:', err.message);
        }
      }
    }

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
        log('Registered KernelMsgListener! listenerId =', kernelListenerId);
      } catch (e) {
        log('attachCustomMsgListener error:', e.message);
      }
    }

    let loggedRecentSample = false;
    let loggedGroupSample = false;

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
        if (name) contactNameCache.set(uid, String(name));
        if (c.avatarPath) avatarPathCache.set(uid, String(c.avatarPath));

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

    function processGroupList(groupList, source) {
      if (!Array.isArray(groupList) || groupList.length === 0) return;
      if (!loggedGroupSample && groupList.length > 2) {
        loggedGroupSample = true;
        const summary = groupList.map(g => `${g.groupCode}(${g.groupName}):mask=${g.cmdUinMsgMask}`);
        log(`GroupList full summary (${source}, total=${groupList.length}):`, summary);
      }
      for (const g of groupList) {
        if (!g || !g.groupCode) continue;
        const gCode = String(g.groupCode);
        if (g.groupName) contactNameCache.set(gCode, String(g.groupName));
        if (g.remarkName) contactNameCache.set(gCode, String(g.remarkName));
        // In NTQQ GroupSimpleInfo: cmdUinMsgMask: 1 = normal notify, 2/3/4 = assistant / shield / mute (receive without alert)
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
            processGroupList(groupList, 'KernelGroupListener.onGroupListUpdate');
          }
        };
        keptGroupListenerRef = new Proxy(target, {
          get(t, prop) {
            if (prop in t) return t[prop];
            if (typeof prop === 'string' && prop.startsWith('on')) {
              return function (...args) {
                if (prop === 'onGroupListUpdate') {
                  processGroupList(args[1] || args[0], 'KernelGroupProxy.onGroupListUpdate');
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
        log('Registered KernelGroupListener! id =', groupListenerId);
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
                if (typeof sess.getGroupService === 'function') {
                  groupServiceInstance = sess.getGroupService();
                  if (groupServiceInstance) {
                    const gProto = Object.getOwnPropertyNames(Object.getPrototypeOf(groupServiceInstance) || {});
                    log('GroupService methods:', gProto.filter(k => k.toLowerCase().includes('mask') || k.toLowerCase().includes('group') || k.toLowerCase().includes('list')));
                  }
                }
                if (typeof sess.getRecentContactService === 'function') {
                  recentContactServiceInstance = sess.getRecentContactService();
                  if (recentContactServiceInstance) {
                    const rProto = Object.getOwnPropertyNames(Object.getPrototypeOf(recentContactServiceInstance) || {});
                    log('RecentContactService methods:', rProto);
                  }
                }
              } catch (e) {}
              log(`Acquired active session & services via getNTWrapperSession(${JSON.stringify(key)})!`);
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
      log(`Attempting sendReplyMessage -> chatType=${chatType}, peerUid=${peerUid}, text=${textContent}`);
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
          const res = await svc.sendMsg('0', peer, elements, new Map());
          log('sendMsg (4 args) result:', res);
          return true;
        } catch (e1) {
          try {
            const res2 = await svc.sendMsg('0', peer, elements);
            log('sendMsg (3 args) result:', res2);
            return true;
          } catch (e2) {
            log('sendMsg failed:', e2.message);
          }
        }
      }
      return false;
    }

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

    let lastTrayAliveMs = 0;

    function inspectAndCacheIpcPayload(item) {
      if (!item || typeof item !== 'object') return;
      const cmd = String(item.cmdName || item.callbackId || '');
      const payload = item.payload;

      // 1. Recent contact list updates contain peerName, remark, and notDisturb/msgMask!
      if (cmd.includes('onRecentContactListChanged') || cmd.includes('onRecentContact')) {
        const list = (payload && payload[0] && payload[0].changedList) || (payload && payload.changedList);
        if (Array.isArray(list)) {
          processContactList(list, 'IPC:' + cmd);
        }
      }

      // 1b. Group list updates contain groupCode, groupName, cmdUinMsgMask
      if (cmd.includes('onGroupListUpdate') || cmd.includes('GroupList')) {
        const gList = (payload && payload[0] && payload[0].groupList) || (payload && payload.groupList) || (payload && payload[1]);
        if (Array.isArray(gList)) {
          processGroupList(gList, 'IPC:' + cmd);
        }
      }

      // 2. Active / incoming messages
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

      // Suppress duplicate native Electron Notification when QQToastTray is active
      try {
        if (electron.Notification && electron.Notification.prototype) {
          const origShow = electron.Notification.prototype.show;
          electron.Notification.prototype.show = function (...args) {
            if (Date.now() - lastTrayAliveMs < 5000) {
              log('Suppressed duplicate native Electron Notification');
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

    function handleOpenChat(item) {
      log('Handling openChat for peerUid=' + item.peerUid + ', chatType=' + item.chatType);
      try {
        let electron = null;
        try { electron = require('electron'); } catch (e) {}
        if (electron && electron.BrowserWindow && typeof electron.BrowserWindow.getAllWindows === 'function') {
          const windows = electron.BrowserWindow.getAllWindows();
          for (const win of windows) {
            if (!win || win.isDestroyed()) continue;
            const bounds = win.getBounds ? win.getBounds() : { width: 0, height: 0 };
            const title = (win.getTitle ? win.getTitle() : '') || '';
            if (title === 'QQ' || (bounds.width > 400 && bounds.height > 400)) {
              if (win.isMinimized && win.isMinimized()) win.restore();
              if (win.show) win.show();
              if (win.setAlwaysOnTop) {
                win.setAlwaysOnTop(true);
                setTimeout(() => { try { if (!win.isDestroyed()) win.setAlwaysOnTop(false); } catch (e) {} }, 300);
              }
              if (win.focus) win.focus();
              break;
            }
          }
        }
      } catch (eWin) {
        log('handleOpenChat wake window error:', eWin.message);
      }

      try {
        if (recentContactServiceInstance) {
          const jumpParam = {
            chatType: Number(item.chatType || 1),
            peerUid: String(item.peerUid || ''),
            guildId: ''
          };
          if (typeof recentContactServiceInstance.jumpToSpecifyRecentContact === 'function') {
            recentContactServiceInstance.jumpToSpecifyRecentContact(jumpParam);
            log('Called jumpToSpecifyRecentContact successfully');
          } else if (typeof recentContactServiceInstance.jumpToSpecifyRecentContactVer2 === 'function') {
            recentContactServiceInstance.jumpToSpecifyRecentContactVer2(jumpParam);
            log('Called jumpToSpecifyRecentContactVer2 successfully');
          }
        }
      } catch (eJump) {
        log('handleOpenChat jump error:', eJump.message);
      }
    }

    let consecutivePollFails = 0;
    let lastTrayLaunchAttemptMs = 0;

    function tryAutoLaunchTray() {
      try {
        const now = Date.now();
        if (now - lastTrayLaunchAttemptMs < 15000) {
          return;
        }
        lastTrayLaunchAttemptMs = now;

        let follow = true;
        const configPath = path.join(__dirname, 'config.json');
        if (fs.existsSync(configPath)) {
          try {
            const raw = JSON.parse(fs.readFileSync(configPath, 'utf8'));
            if (raw && raw.FollowQQStartup === false) {
              follow = false;
            }
          } catch (e) {}
        }
        if (!follow) return;

        const trayExePath = path.join(__dirname, 'QQToastTray.exe');
        if (fs.existsSync(trayExePath)) {
          log('QQToastTray not responding, auto-launching QQToastTray.exe...');
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
        // CRITICAL: Only the main NTQQ process that actually holds msgServiceInstance
        // is allowed to poll /poll! Otherwise a child/helper process without wrapper.node
        // will steal and discard pending replies from the queue!
        if (!msgServiceInstance) {
          return;
        }
        const req = http.get({
          hostname: '127.0.0.1',
          port: TRAY_PORT,
          path: '/poll',
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
