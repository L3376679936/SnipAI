const path = require('path');
const fs = require('fs');
const os = require('os');
const { clipboard, nativeImage } = require('electron');
const { execFile } = require('child_process');

// 统一保存在用户主目录下的持久化配置文件，跨会话/跨重启永不丢失
const CONFIG_FILE_PATH = path.join(os.homedir(), '.rubick_ocr_translate_config.json');
const HISTORY_FILE_PATH = path.join(os.homedir(), '.rubick_ocr_translate_history.json');

const LOG_FILE = path.join(os.homedir(), '.rubick_snipper_debug.log');
function logDebug(msg) {
  try {
    fs.appendFileSync(LOG_FILE, `[${new Date().toLocaleTimeString()}] ${msg}\n`);
  } catch (e) {}
}

// 历史记录的异步落盘状态（防抖窗口内只保留最后一份待写数据）
let pendingHistory = null;
let historySaveTimer = null;
function flushHistoryToDisk() {
  if (historySaveTimer) {
    clearTimeout(historySaveTimer);
    historySaveTimer = null;
  }
  if (pendingHistory === null) return;
  const snapshot = pendingHistory;
  pendingHistory = null;
  try {
    fs.writeFile(HISTORY_FILE_PATH, JSON.stringify(snapshot, null, 2), 'utf-8', (err) => {
      if (err) logDebug(`写入历史记录失败: ${err.message}`);
    });
  } catch (err) {
    logDebug(`写入历史记录失败: ${err.message}`);
  }
}
// 渲染进程退出前同步刷盘：防抖窗口内若正好退出，异步写来不及完成会丢数据
process.on('exit', () => {
  if (historySaveTimer && pendingHistory !== null) {
    try {
      fs.writeFileSync(HISTORY_FILE_PATH, JSON.stringify(pendingHistory, null, 2), 'utf-8');
    } catch (e) {}
  }
});

logDebug(`Preload 初始化: __dirname=${__dirname}`);

/**
 * 宿主与底层能力桥接器 (纯内置自包含实现，无任何外部软件依赖)
 */
window.nativeBridge = {
  log: logDebug,

  /**
   * 持久化保存大模型配置到本地磁盘文件
   */
  saveConfig: (cfg) => {
    try {
      fs.writeFileSync(CONFIG_FILE_PATH, JSON.stringify(cfg, null, 2), 'utf-8');
      return true;
    } catch (err) {
      logDebug(`写入配置失败: ${err.message}`);
      return false;
    }
  },

  /**
   * 从本地磁盘文件读取大模型配置
   */
  loadConfig: () => {
    try {
      if (fs.existsSync(CONFIG_FILE_PATH)) {
        const text = fs.readFileSync(CONFIG_FILE_PATH, 'utf-8');
        return JSON.parse(text);
      }
    } catch (err) {
      logDebug(`读取磁盘配置失败: ${err.message}`);
    }
    return null;
  },

  /**
   * 持久化保存历史识别记录到本地磁盘 (最多保存 30 条)
   *
   * 历史里带 base64 截图，JSON 体积可达数 MB：原先的 writeFileSync 会在渲染进程
   * 主线程上同步落盘，每次删除/新增都卡一次 UI。这里改为异步写 + 200ms 防抖，
   * 连续操作只落一次盘；渲染进程退出时再同步兜底刷一次，避免丢最后一条。
   */
  saveHistory: (list) => {
    try {
      pendingHistory = list || [];
      if (historySaveTimer) clearTimeout(historySaveTimer);
      historySaveTimer = setTimeout(flushHistoryToDisk, 200);
      return true;
    } catch (err) {
      logDebug(`写入历史记录失败: ${err.message}`);
      return false;
    }
  },

  /**
   * 从本地磁盘读取历史识别记录
   */
  loadHistory: () => {
    try {
      if (fs.existsSync(HISTORY_FILE_PATH)) {
        const text = fs.readFileSync(HISTORY_FILE_PATH, 'utf-8');
        return JSON.parse(text);
      }
    } catch (err) {
      logDebug(`读取历史记录失败: ${err.message}`);
    }
    return [];
  },

  /**
   * 唤起项目内置编译的轻量全屏选区截屏器 (bin/snipper.exe)
   */
  captureScreen: () => {
    return new Promise((resolve, reject) => {
      logDebug('captureScreen 被调用');
      // 1. 隐藏 Rubick 主窗口，露出屏幕全部视野
      window.nativeBridge.hideMainWindow();

      // 2. 寻址项目自包含的选区截图程序
      let snipperExePath = path.join(__dirname, 'bin', 'snipper.exe');
      if (!fs.existsSync(snipperExePath)) {
        // 兜底尝试项目相对路径
        const fallback = path.resolve(__dirname, '..', 'screenshotOcrTranslate', 'bin', 'snipper.exe');
        if (fs.existsSync(fallback)) {
          snipperExePath = fallback;
        }
      }
      logDebug(`snipper.exe 路径: ${snipperExePath}, 存在: ${fs.existsSync(snipperExePath)}`);

      // 两条路径都找不到时必须立即失败：否则下面会去执行一个不存在的文件，
      // 只拿到一个 ENOENT 错误码，而主窗口已经隐藏、用户看不到任何反馈。
      if (!fs.existsSync(snipperExePath)) {
        logDebug('未找到 snipper.exe，放弃截图');
        window.nativeBridge.showMainWindow();
        reject(new Error('未找到截图程序 snipper.exe，请重新编译插件'));
        return;
      }

      // 3. 执行截图程序
      //    注意：这里刻意不设 timeout。用户选择「贴图」时 snipper.exe 会驻留运行
      //    显示置顶窗口，直到用户主动关闭才退出；若加超时，超时一到就会把这个
      //    窗口连同用户的贴图一起杀掉。子进程卡死的兜底交给下面 close 事件的异常分支。
      execFile(snipperExePath, { windowsHide: true }, (err, stdout, stderr) => {
        const outStr = (stdout || '').trim();
        logDebug(`snipper.exe 退出: code=${err ? err.code : 0}, stdout=${outStr}`);

        // 用户取消截图 (点 ✕ 或按 Esc)
        if (err && (err.code === 1 || outStr.includes('ACTION:CANCEL'))) {
          logDebug('用户取消截图');
          reject(new Error('截图已取消'));
          return;
        }

        if (outStr.includes('ACTION:COPY_COLOR')) {
          const parts = outStr.split('ACTION:COPY_COLOR:');
          const colorVal = parts[1] ? parts[1].trim() : '';
          logDebug(`用户使用取色器复制颜色值: ${colorVal}`);
          resolve({ action: 'copy_color', color: colorVal });
          return;
        }

        if (outStr.includes('ACTION:PIN')) {
          logDebug('用户选择将截图钉在桌面上 (贴图)，已置顶展示并平稳退出');
          resolve({ action: 'pin' });
          return;
        }

        const readBundleFromFile = (filePath, deleteAfter = false) => {
          try {
            if (filePath && fs.existsSync(filePath)) {
              const rawJson = fs.readFileSync(filePath, 'utf-8').replace(/^\uFEFF/, '').trim();
              const parsed = JSON.parse(rawJson);
              if (deleteAfter) {
                try { fs.unlinkSync(filePath); } catch (e) {}
              }
              return parsed;
            }
          } catch (e) {
            logDebug(`读取改图 JSON (${filePath}) 失败: ${e.message}`);
          }
          return null;
        };

        const latestBundlePath = path.join(os.tmpdir(), 'rubick_latest_capture_bundle.json');

        const isAiEditAction = outStr.includes('ACTION:AI_EDIT:') || (err && err.code === 11);
        if (isAiEditAction) {
          logDebug('用户点击了【✨ 改图】按钮，恢复主窗口并进入 AI 智能改图模式');
          window.nativeBridge.showMainWindow();
          window.nativeBridge.setHeight(600);

          let bundle = null;
          if (outStr.includes('ACTION:AI_EDIT:')) {
            const bundlePath = outStr.split('ACTION:AI_EDIT:')[1].split(/\r?\n/)[0].trim();
            bundle = readBundleFromFile(bundlePath, true);
          }
          if (!bundle) {
            bundle = readBundleFromFile(latestBundlePath, false);
          }

          if (bundle && bundle.cleanDataUrl) {
            logDebug(`成功载入 AI 改图三图分离包: width=${bundle.width}, height=${bundle.height}, boxes=${(bundle.boxes || []).length}`);
            resolve({
              action: 'ai_edit',
              dataUrl: bundle.annotatedDataUrl || bundle.cleanDataUrl,
              bundle
            });
            return;
          }

          try {
            const img = clipboard.readImage();
            if (img && !img.isEmpty()) {
              const fallbackUrl = img.toDataURL();
              logDebug('通过剪贴板兜底载入待改图图片');
              resolve({
                action: 'ai_edit',
                dataUrl: fallbackUrl,
                bundle: {
                  cleanDataUrl: fallbackUrl,
                  annotatedDataUrl: fallbackUrl,
                  maskDataUrl: null,
                  hasMaskRegion: false,
                  textNotes: [],
                  boxes: []
                }
              });
              return;
            }
          } catch (clipErr) {
            logDebug(`读取剪贴板兜底异常: ${clipErr.message}`);
          }

          reject(new Error('未能读取到待修改的截图数据'));
          return;
        }

        const isOcrAction = outStr.includes('ACTION:OCR') || (err && err.code === 10);
        const isCopyOnly = outStr.includes('ACTION:COPY') || (!isOcrAction && !err);

        if (isCopyOnly) {
          logDebug('用户仅选择确定截图 (✔)，图片已在剪贴板');
          const latestBundle = readBundleFromFile(latestBundlePath, false);
          resolve({ action: 'copy', bundle: latestBundle });
          return;
        }

        if (isOcrAction) {
          logDebug('用户点击了【识别文字】按钮，恢复主窗口并启动 OCR 翻译');
          window.nativeBridge.showMainWindow();
          window.nativeBridge.setHeight(600);
          const latestBundle = readBundleFromFile(latestBundlePath, false);
          try {
            const img = clipboard.readImage();
            if (img && !img.isEmpty()) {
              logDebug('成功从剪贴板读取到图片');
              resolve({ action: 'ocr', dataUrl: img.toDataURL(), bundle: latestBundle });
              return;
            }
            if (latestBundle && latestBundle.annotatedDataUrl) {
              resolve({ action: 'ocr', dataUrl: latestBundle.annotatedDataUrl, bundle: latestBundle });
              return;
            }
            reject(new Error('未在剪贴板中检测到截取的图片'));
          } catch (clipErr) {
            logDebug(`读取剪贴板异常: ${clipErr.message}`);
            reject(clipErr);
          }
          return;
        }

        // 兜底收口：走到这里说明既不是取消、取色、贴图、复制，也不符合 OCR 判定
        // （典型是 ENOENT / 进程被外部杀死 / maxBuffer 溢出等异常退出）。必须显式 reject，
        // 否则 Promise 永远 pending，前端会一直停在"截图中…"状态无法恢复。
        logDebug(`未识别的退出状态: code=${err ? err.code : 0}, signal=${err ? err.signal : ''}, stderr=${(stderr || '').slice(0, 200)}`);
        window.nativeBridge.showMainWindow();
        reject(new Error('截图程序异常退出，请重试'));
      });
    });
  },

  /**
   * 获取最近一次 snipper.exe 生成的三图分离数据包（供「送去改图」或「剪贴板载入」自动恢复干净原图与红框坐标）
   */
  getLatestCaptureBundle: () => {
    try {
      const latestBundlePath = path.join(os.tmpdir(), 'rubick_latest_capture_bundle.json');
      if (fs.existsSync(latestBundlePath)) {
        const rawJson = fs.readFileSync(latestBundlePath, 'utf-8').replace(/^\uFEFF/, '').trim();
        const parsed = JSON.parse(rawJson);
        // 仅在 15 分钟内有效
        if (parsed && parsed.cleanDataUrl && (!parsed.timestamp || Date.now() - parsed.timestamp < 15 * 60 * 1000)) {
          return parsed;
        }
      }
    } catch (e) {
      logDebug(`getLatestCaptureBundle 失败: ${e.message}`);
    }
    return null;
  },

  /**
   * 直接从剪贴板读取最新图片 DataURL
   */
  readClipboardImage: () => {
    try {
      const img = clipboard.readImage();
      if (img && !img.isEmpty()) {
        return img.toDataURL();
      }
    } catch (e) {
      console.warn('剪贴板读取图片失败:', e);
    }
    return null;
  },

  /**
   * 将 DataURL 图片直接写入系统剪贴板
   */
  copyImageDataUrl: (dataUrl) => {
    try {
      if (window.utools && typeof window.utools.copyImage === 'function') {
        window.utools.copyImage(dataUrl);
        return true;
      }
      if (nativeImage && clipboard && typeof clipboard.writeImage === 'function') {
        const img = nativeImage.createFromDataURL(dataUrl);
        clipboard.writeImage(img);
        return true;
      }
    } catch (e) {
      logDebug(`写入图片到剪贴板失败: ${e.message}`);
    }
    return false;
  },

  /**
   * 将修改后的 DataURL 图片钉在桌面上变成置顶贴图
   */
  pinImageDataUrl: (dataUrl) => {
    try {
      if (!dataUrl || !dataUrl.includes('base64,')) return false;
      const base64Data = dataUrl.split('base64,')[1];
      const tempPath = path.join(os.tmpdir(), `rubick_pin_${Date.now()}.png`);
      fs.writeFileSync(tempPath, Buffer.from(base64Data, 'base64'));

      let snipperExePath = path.join(__dirname, 'bin', 'snipper.exe');
      if (!fs.existsSync(snipperExePath)) {
        const fallback = path.resolve(__dirname, '..', 'screenshotOcrTranslate', 'bin', 'snipper.exe');
        if (fs.existsSync(fallback)) snipperExePath = fallback;
      }
      if (!fs.existsSync(snipperExePath)) return false;

      execFile(snipperExePath, ['--pin-image', tempPath], { windowsHide: true }, () => {});
      return true;
    } catch (e) {
      logDebug(`钉图到桌面失败: ${e.message}`);
      return false;
    }
  },

  /**
   * 隐藏 Rubick 窗口
   */
  hideMainWindow: () => {
    if (window.rubick && typeof window.rubick.hideMainWindow === 'function') {
      window.rubick.hideMainWindow();
    } else if (window.utools && typeof window.utools.hideMainWindow === 'function') {
      window.utools.hideMainWindow();
    }
  },

  /**
   * 显示/恢复 Rubick 窗口
   */
  showMainWindow: () => {
    if (window.rubick && typeof window.rubick.showMainWindow === 'function') {
      window.rubick.showMainWindow();
    } else if (window.utools && typeof window.utools.showMainWindow === 'function') {
      window.utools.showMainWindow();
    }
  },

  /**
   * 调整插件窗口高度
   */
  setHeight: (height) => {
    if (window.rubick && typeof window.rubick.setExpendHeight === 'function') {
      window.rubick.setExpendHeight(height);
    } else if (window.utools && typeof window.utools.setExpendHeight === 'function') {
      window.utools.setExpendHeight(height);
    }
  },

  /**
   * 写入剪贴板文本
   */
  copyText: (text) => {
    if (clipboard && clipboard.writeText) {
      clipboard.writeText(text);
    } else {
      navigator.clipboard.writeText(text);
    }
  },

  /**
   * 监听插件激活（包括由剪贴板图片直接唤起）
   * 采用提前捕获 + 缓存重放机制，杜绝因 ES Module 异步加载慢而错过 Rubick 首屏 onPluginEnter 事件
   */
  onPluginEnter: (callback) => {
    logDebug('前端注册 onPluginEnter, 已有缓存=' + !!_cachedEnterAction);
    _activeEnterCallback = callback;
    if (_cachedEnterAction && callback) {
      try {
        logDebug('立即重放缓存的 action 给前端');
        callback(_cachedEnterAction);
      } catch (e) {
        logDebug('执行缓存的 onPluginEnter 失败: ' + e.message);
      }
    }
  }
};

// 在 preload 加载的第一时间就挂载宿主监听
let _cachedEnterAction = null;
let _activeEnterCallback = null;

const globalEnterDispatcher = (action) => {
  logDebug('宿主底层派发 onPluginEnter: ' + JSON.stringify(action));
  _cachedEnterAction = action;
  if (_activeEnterCallback) {
    _activeEnterCallback(action);
  }
};

if (window.rubick && typeof window.rubick.onPluginEnter === 'function') {
  logDebug('挂载 window.rubick.onPluginEnter 成功');
  window.rubick.onPluginEnter(globalEnterDispatcher);
} else if (window.utools && typeof window.utools.onPluginEnter === 'function') {
  logDebug('挂载 window.utools.onPluginEnter 成功');
  window.utools.onPluginEnter(globalEnterDispatcher);
} else {
  logDebug('警告: 未找到 window.rubick 或 window.utools 的 onPluginEnter');
}


