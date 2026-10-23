/**
 * NTQQ <-> QQToastTray Bridge (qq-bridge.js v7)
 * - Respects QQ DND (Do-Not-Disturb) settings via onRecvActiveMsg + @me detection
 * - Resolves friend remark/nickname & avatars via BuddyService / ProfileService / RecentContactService
 * - Opens the REAL QQ main window through Electron APIs (never via Win32 ShowWindow)
 * - Honors a user-initiated tray exit (tray-exit.flag) instead of relaunching it forever
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
    const CONFIG_FILE = path.join(__dirname, 'config.json');
    const EXIT_FLAG_FILE = path.join(__dirname, 'tray-exit.flag');
    const BRIDGE_START_MS = Date.now();

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

    log('=== qq-bridge.js v7 initialized ===');

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
    let lastMsgMeta = { peerUid: '', peerName: '', at: 0 };

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

    // QQ 自己弹出的 Electron 通知被我们拦截不显示，但它的 click 监听器
    // 正是 QQ 原生“点击通知跳转会话”的实现。这里保存对象引用，等用户点击
    // 我们的快捷回复通知时，直接触发它，复用 QQ 自己的跳转逻辑。
    const nativeNotifications = [];
    let nativeDetailLogs = 0;

    function associateNativeNotifications(peerUid, peerName, atMs) {
      if (!peerUid || !atMs) return;
      for (let i = nativeNotifications.length - 1; i >= 0; i--) {
        const rec = nativeNotifications[i];
        if (atMs - rec.ts > 8000) break;
        const delta = Math.abs(rec.ts - atMs);
        if (delta > 5000) continue;
        const prevDelta = rec.assocAt ? Math.abs(rec.ts - rec.assocAt) : Infinity;
        // 以“离通知时间最近的那条消息”为准，被更近的消息覆盖。
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
          hasReply: !!n.hasReply,
          clickListeners: (typeof n.listenerCount === 'function') ? n.listenerCount('click') : -1,
          ref: n
        };
        nativeNotifications.push(record);
        while (nativeNotifications.length > 30) {
          nativeNotifications.shift();
        }
        log('Captured native notification: ' + JSON.stringify({
          title: record.title, body: record.body, tag: record.tag,
          hasReply: record.hasReply, clickListeners: record.clickListeners,
          peerUid: record.peerUid, peerName: record.peerName
        }));

        // 诊断信息：QQ 的通知 title/body 为空，但对象上可能有 toastXml
        // 或自定义属性；click 监听器的源码能直接看出 QQ 原生跳转用了什么参数。
        if (nativeDetailLogs < 10) {
          nativeDetailLogs++;
          let own = [];
          let proto = [];
          let toastXml = '';
          let listenerSource = '';
          try { own = Object.getOwnPropertyNames(n); } catch (e) {}
          try { proto = Object.getOwnPropertyNames(Object.getPrototypeOf(n) || {}); } catch (e) {}
          try {
            if (typeof n.toastXml === 'function') toastXml = String(n.toastXml());
            else if (typeof n.toastXml === 'string') toastXml = n.toastXml;
          } catch (e) {}
          try {
            const listeners = n.listeners('click');
            if (listeners && listeners.length > 0) {
              listenerSource = String(listeners[0]).slice(0, 1500);
            }
          } catch (e) {}
          log('Native notification details: ' + JSON.stringify({
            own: own, proto: proto, peerUid: record.peerUid, peerName: record.peerName,
            toastXml: String(toastXml || '').slice(0, 1500), listenerSource: listenerSource
          }));
        }
      } catch (e) {}
    }

    function emitNativeNotificationClick(item) {
      if (nativeNotifications.length === 0) {
        log('No captured native notification to click.');
        return false;
      }

      const peerUid = String((item && item.peerUid) || '');
      const name = String((item && item.peerName) || '').trim();
      const now = Date.now();
      let pick = null;

      // 1) 优先按“消息 -> 通知”的时间关联匹配 peerUid（QQ 通知的 title/body 是空的）
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

      // 2) 退化匹配：标题/正文/记录中的会话名
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

      if (!pick) {
        log('No matching native notification for peerUid=' + peerUid + ', peerName=' + name + '; skip native click.');
        return false;
      }

      try {
        const count = (typeof pick.ref.listenerCount === 'function') ? pick.ref.listenerCount('click') : 0;
        let source = '';
        try {
          const listeners = pick.ref.listeners('click');
          if (listeners && listeners.length > 0) source = String(listeners[0]).slice(0, 500);
        } catch (e) {}
        log('Emitting native notification click: matchedPeerUid=' + pick.peerUid +
            ', matchedPeerName=' + pick.peerName + ', ageMs=' + (now - pick.ts) +
            ', listeners=' + count + ', source=' + source);
        if (count <= 0) {
          return false;
        }
        pick.ref.emit('click', {});
        return true;
      } catch (e) {
        log('emitNativeNotificationClick error: ' + e.message);
        return false;
      }
    }

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
            // Capture the object before deciding to suppress it: its click
            // listener is QQ's own "open the right conversation" handler.
            captureNativeNotification(this);
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

    // ---------------------------------------------------------------------
    // Open-chat support
    //
    // NTQQ keeps several hidden Electron BrowserWindows alive (the 800x600
    // login/helper window, the 720x640 auxiliary window, the tiny tray host...).
    // The old code simply showed the first window larger than 400x400, which
    // popped up a blank grey helper window instead of the QQ main window.
    // We now pick the real main window by URL (#/main/message), never by size
    // alone. We also never call the native jump API directly any more: on this
    // QQ version it cleared the active conversation and produced a blank chat
    // pane. Instead we replay QQ's own notification click handler when possible.
    // ---------------------------------------------------------------------
    function describeWindow(win) {
      try {
        const bounds = win.getBounds ? win.getBounds() : { width: 0, height: 0 };
        let url = '';
        try { url = win.webContents && win.webContents.getURL ? String(win.webContents.getURL() || '') : ''; } catch (e) {}
        return {
          id: win.id,
          title: (win.getTitle ? win.getTitle() : '') || '',
          width: bounds.width || 0,
          height: bounds.height || 0,
          visible: win.isVisible ? win.isVisible() : false,
          minimized: win.isMinimized ? win.isMinimized() : false,
          resizable: win.isResizable ? win.isResizable() : false,
          minimizable: win.isMinimizable ? win.isMinimizable() : false,
          url: url
        };
      } catch (e) {
        return { id: -1, title: '', width: 0, height: 0, visible: false, minimized: false, resizable: false, minimizable: false, url: '' };
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

      log('openChat candidate windows:', infos.map(x => x.info));

      // 通过 URL 精确识别主窗口，避免把 #/blank 的空白辅助窗口或
      // #/chat 的独立聊天池窗口当主窗口显示出来。
      const isBlankHelper = (url) => /#\/blank/i.test(url) || /chatPoolWin=1/i.test(url);
      const isMainPage = (url) => /#\/main(\/|$)/i.test(url) || /\/main\/message/i.test(url);

      let pool = infos.filter(x => isMainPage(x.info.url) && !isBlankHelper(x.info.url));
      if (pool.length === 0) {
        pool = infos.filter(x => !isBlankHelper(x.info.url));
      }
      if (pool.length === 0) {
        return null; // 宁可触发 QQ 自身的激活逻辑，也不要显示空白辅助窗口
      }

      // Prefer windows that are actually resizable (QQ main window). Hidden
      // helper/login windows are usually fixed-size and much smaller.
      pool.sort((a, b) => {
        if (a.info.resizable !== b.info.resizable) return a.info.resizable ? -1 : 1;
        if (a.info.minimizable !== b.info.minimizable) return a.info.minimizable ? -1 : 1;
        return (b.info.width * b.info.height) - (a.info.width * a.info.height);
      });

      return pool[0].win;
    }

    // QQ 有时在自动登录后并不会创建主窗口（只驻留托盘）。此时由 QQ 原生
    // 通知的 click 回调负责创建/显示主窗口；这里不再主动启动第二个 QQ 进程，
    // 避免出现新的登录窗口。

    function showMainWindow(win) {
      try {
        if (win.isMinimized && win.isMinimized()) win.restore();
        // Always go through Electron's show(): calling Win32 ShowWindow on a
        // BrowserWindow hidden with hide() produces a blank white/grey window.
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

    // 从隐藏/最小化状态恢复后主动刷新一次窗口内容，避免 Chromium 合成层
    // 偶发不重绘（历史版本出现过空白聊天区）。
    function repaintQQWindows(electron, win) {
      try {
        let count = 0;
        try {
          const all = electron.webContents.getAllWebContents();
          for (const wc of all) {
            try {
              if (wc && !wc.isDestroyed() && typeof wc.invalidate === 'function') {
                wc.invalidate();
                count++;
              }
            } catch (e) {}
          }
        } catch (e) {}
        try {
          if (win.webContents && typeof win.webContents.invalidate === 'function') {
            win.webContents.invalidate();
            count++;
          }
        } catch (e) {}
        log('Repaint: invalidated ' + count + ' webContents.');
      } catch (e) {
        log('repaintQQWindows error: ' + e.message);
      }
    }

    function handleOpenChat(item) {
      log('Handling openChat for peerUid=' + item.peerUid + ', chatType=' + item.chatType + ', peerName=' + item.peerName);
      const nativeClicked = emitNativeNotificationClick(item);
      log('Native notification click emitted=' + nativeClicked);
      try {
        let electron = null;
        try { electron = require('electron'); } catch (e) {}

        if (!electron || !electron.BrowserWindow || typeof electron.BrowserWindow.getAllWindows !== 'function') {
          log('Electron BrowserWindow API unavailable.');
          return;
        }

        const ensureQQWindow = (delayMs) => {
          setTimeout(() => {
            try {
              const w = pickQQMainWindow(electron);
              if (!w) {
                log('QQ main window is not available.');
                return;
              }
              const info = describeWindow(w);
              showMainWindow(w);
              repaintQQWindows(electron, w);
              setTimeout(() => { try { repaintQQWindows(electron, w); } catch (e) {} }, 450);
              log('Ensured QQ main window: ' + JSON.stringify(info));
            } catch (e) {
              log('ensureQQWindow error: ' + e.message);
            }
          }, delayMs);
        };

        if (nativeClicked) {
          // 先让 QQ 自己的通知点击逻辑完成打开/跳转，稍后再兜底确保窗口可见。
          log('QQ native notification click emitted; QQ handles window and conversation switch.');
          ensureQQWindow(600);
          return;
        }

        const win = pickQQMainWindow(electron);
        if (win) {
          const info = describeWindow(win);
          showMainWindow(win);
          log('Showed QQ main window: ' + JSON.stringify(info) + ', wasHidden=' + !info.visible);
          repaintQQWindows(electron, win);
          setTimeout(() => { try { repaintQQWindows(electron, win); } catch (e) {} }, 450);
          log('Opened QQ main window without forcing a conversation switch.');
          return;
        }

        log('No main window and no native notification click available; nothing to open.');
      } catch (eWin) {
        log('handleOpenChat error: ' + eWin.message);
      }
    }

    // ---------------------------------------------------------------------
    // Tray auto-launch (follow QQ startup)
    // ---------------------------------------------------------------------
    function readConfigSafely() {
      try {
        if (!fs.existsSync(CONFIG_FILE)) return null;
        // .NET's Encoding.UTF8 used to write a BOM here; JSON.parse() chokes
        // on it and the old code silently fell back to "follow=true".
        let raw = fs.readFileSync(CONFIG_FILE, 'utf8');
        if (raw.charCodeAt(0) === 0xFEFF) raw = raw.slice(1);
        raw = raw.replace(/^\uFEFF/, '').trim();
        if (!raw) return null;
        return JSON.parse(raw);
      } catch (e) {
        return null;
      }
    }

    function getConfigBool(name, defaultValue) {
      // Retry once: the tray may be rewriting config.json at this exact moment.
      for (let attempt = 0; attempt < 2; attempt++) {
        const cfg = readConfigSafely();
        if (cfg && typeof cfg === 'object') {
          const key = Object.keys(cfg).find(k => String(k).toLowerCase() === name.toLowerCase());
          if (key !== undefined) return cfg[key] !== false;
          return defaultValue;
        }
      }
      return defaultValue;
    }

    function userQuitTraySinceBridgeStart() {
      try {
        if (!fs.existsSync(EXIT_FLAG_FILE)) return false;
        const raw = fs.readFileSync(EXIT_FLAG_FILE, 'utf8').trim();
        const flagMs = parseInt(raw, 10);
        if (!isNaN(flagMs)) {
          return flagMs > BRIDGE_START_MS;
        }
        // Fallback for legacy/empty flag files: compare mtime.
        return fs.statSync(EXIT_FLAG_FILE).mtimeMs > BRIDGE_START_MS;
      } catch (e) {
        return false;
      }
    }

    let consecutivePollFails = 0;
    let lastTrayLaunchAttemptMs = 0;

    function tryAutoLaunchTray() {
      try {
        // Respect the tray's "跟随 QQ 启动自动运行" switch. It must be re-read
        // on every attempt so toggling it off takes effect immediately.
        if (!getConfigBool('FollowQQStartup', true)) {
          return;
        }

        // The user explicitly chose "退出程序" during this QQ session:
        // stay quit instead of resurrecting the tray on every poll failure.
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
