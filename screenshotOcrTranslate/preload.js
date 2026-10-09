const path = require('path');
const fs = require('fs');
const os = require('os');
const { clipboard } = require('electron');
const { execFile } = require('child_process');

// 统一保存在用户主目录下的持久化配置文件，跨会话/跨重启永不丢失
const CONFIG_FILE_PATH = path.join(os.homedir(), '.rubick_ocr_translate_config.json');

const LOG_FILE = path.join(os.homedir(), '.rubick_snipper_debug.log');
function logDebug(msg) {
  try {
    fs.appendFileSync(LOG_FILE, `[${new Date().toLocaleTimeString()}] ${msg}\n`);
  } catch (e) {}
}

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
   */
  saveHistory: (list) => {
    try {
      const histPath = path.join(os.homedir(), '.rubick_ocr_translate_history.json');
      fs.writeFileSync(histPath, JSON.stringify(list || [], null, 2), 'utf-8');
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
      const histPath = path.join(os.homedir(), '.rubick_ocr_translate_history.json');
      if (fs.existsSync(histPath)) {
        const text = fs.readFileSync(histPath, 'utf-8');
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

      // 3. 执行截图程序
      execFile(snipperExePath, (err, stdout, stderr) => {
        const outStr = (stdout || '').trim();
        logDebug(`snipper.exe 退出: code=${err ? err.code : 0}, stdout=${outStr}`);

        // 用户取消截图 (点 ✕ 或按 Esc)
        if (err && (err.code === 1 || outStr.includes('ACTION:CANCEL'))) {
          logDebug('用户取消截图');
          reject(new Error('截图已取消'));
          return;
        }

        const isOcrAction = outStr.includes('ACTION:OCR') || (err && err.code === 10);
        const isCopyOnly = outStr.includes('ACTION:COPY') || (!isOcrAction && !err);

        if (isCopyOnly) {
          logDebug('用户仅选择确定截图 (✔)，图片已在剪贴板，不触发 OCR');
          resolve({ action: 'copy' });
          return;
        }

        if (isOcrAction) {
          logDebug('用户点击了【识别文字】按钮，恢复主窗口并启动 OCR 翻译');
          window.nativeBridge.showMainWindow();
          try {
            const img = clipboard.readImage();
            if (img && !img.isEmpty()) {
              logDebug('成功从剪贴板读取到图片');
              resolve({ action: 'ocr', dataUrl: img.toDataURL() });
              return;
            }
            reject(new Error('未在剪贴板中检测到截取的图片'));
          } catch (clipErr) {
            logDebug(`读取剪贴板异常: ${clipErr.message}`);
            reject(clipErr);
          }
        }
      });
    });
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


