const screenshot = require('screenshot-desktop');
const { clipboard } = require('electron');
const { exec, execSync } = require('child_process');

/**
 * 宿主与底层能力桥接器
 */
window.nativeBridge = {
  /**
   * 唤起系统级手动划选截屏并自动感知选区剪贴板载入
   */
  captureScreen: () => {
    return new Promise((resolve, reject) => {
      // 1. 隐藏 Rubick 窗口，给屏幕让出视野
      window.nativeBridge.hideMainWindow();

      // 2. 记录截图前剪贴板中的图片
      let prevDataUrl = '';
      try {
        const prevImg = clipboard.readImage();
        if (prevImg && !prevImg.isEmpty()) {
          prevDataUrl = prevImg.toDataURL();
        }
      } catch (e) {
        console.warn('获取前置剪贴板失败:', e);
      }

      // 3. 唤起手动划选截屏：优先调用本机已运行的 Snipaste 进程
      let snipastePath = '';
      try {
        const stdout = execSync(
          'powershell -NoProfile -Command "(Get-Process -Name snipaste -ErrorAction SilentlyContinue).Path"',
          { encoding: 'utf-8', timeout: 1500 }
        );
        snipastePath = stdout.trim();
      } catch (e) {
        console.warn('查找 Snipaste 失败:', e);
      }

      if (snipastePath) {
        // 直接调用原生 Snipaste 划选
        exec(`"${snipastePath}" snip`, (err) => {
          if (err) {
            console.warn('执行 Snipaste snip 失败，回退 Windows 原生截图:', err);
            exec('start ms-screenclip:?cliptype=Rectangle');
          }
        });
      } else {
        // 回退到 Windows 原生矩形选区截图
        exec('start ms-screenclip:?cliptype=Rectangle');
      }

      // 4. 轮询剪贴板，等待用户框选完成（双击选区、Enter 或复制）
      const startTime = Date.now();
      const checkTimer = setInterval(() => {
        try {
          const currentImg = clipboard.readImage();
          if (currentImg && !currentImg.isEmpty()) {
            const currentDataUrl = currentImg.toDataURL();
            // 检测到剪贴板出现了新的截图图片
            if (currentDataUrl && currentDataUrl !== prevDataUrl) {
              clearInterval(checkTimer);
              window.nativeBridge.showMainWindow();
              resolve(currentDataUrl);
              return;
            }
          }
        } catch (err) {}

        // 45秒超时保护
        if (Date.now() - startTime > 45000) {
          clearInterval(checkTimer);
          window.nativeBridge.showMainWindow();
          reject(new Error('截图未完成或已取消'));
        }
      }, 300);
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
