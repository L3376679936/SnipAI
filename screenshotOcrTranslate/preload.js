const path = require('path');
const fs = require('fs');
const os = require('os');
const { clipboard } = require('electron');
const { execFile } = require('child_process');

// 统一保存在用户主目录下的持久化配置文件，跨会话/跨重启永不丢失
const CONFIG_FILE_PATH = path.join(os.homedir(), '.rubick_ocr_translate_config.json');

/**
 * 宿主与底层能力桥接器 (纯内置自包含实现，无任何外部软件依赖)
 */
window.nativeBridge = {
  /**
   * 持久化保存大模型配置到本地磁盘文件
   */
  saveConfig: (cfg) => {
    try {
      fs.writeFileSync(CONFIG_FILE_PATH, JSON.stringify(cfg, null, 2), 'utf-8');
      return true;
    } catch (err) {
      console.error('写入磁盘配置失败:', err);
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
      console.warn('读取磁盘配置失败:', err);
    }
    return null;
  },

  /**
   * 唤起项目内置编译的轻量全屏选区截屏器 (bin/snipper.exe)
   */
  captureScreen: () => {
    return new Promise((resolve, reject) => {
      // 1. 隐藏 Rubick 主窗口，露出屏幕全部视野
      window.nativeBridge.hideMainWindow();

      // 2. 寻址项目自包含的选区截图程序
      const snipperExePath = path.join(__dirname, 'bin', 'snipper.exe');

      // 3. 执行截图程序
      execFile(snipperExePath, (err, stdout, stderr) => {
        // 无论成功与否，恢复主窗口显示
        window.nativeBridge.showMainWindow();

        if (err) {
          // 退出码为 1 表示用户主动按 Esc 或右键取消了截图
          console.warn('截图取消或未完成:', err.message);
          reject(new Error('截图已取消'));
          return;
        }

        // 退出码为 0，说明用户框选完成，图片已精准放入剪贴板
        try {
          const img = clipboard.readImage();
          if (img && !img.isEmpty()) {
            resolve(img.toDataURL());
            return;
          }
          reject(new Error('未在剪贴板中检测到截取的图片'));
        } catch (clipErr) {
          reject(clipErr);
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
   */
  onPluginEnter: (callback) => {
    const handler = (action) => {
      callback && callback(action);
    };

    if (window.rubick && typeof window.rubick.onPluginEnter === 'function') {
      window.rubick.onPluginEnter(handler);
    } else if (window.utools && typeof window.utools.onPluginEnter === 'function') {
      window.utools.onPluginEnter(handler);
    }
  }
};
