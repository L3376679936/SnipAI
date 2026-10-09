/**
 * 屏幕截取与标注管理器 (集成 js-web-screen-shot)
 */
export class ScreenCaptureManager {
  constructor({ onComplete, onCancel }) {
    this.onComplete = onComplete;
    this.onCancel = onCancel;
    this.isCapturing = false;
  }

  /**
   * 启动屏幕截取流程
   */
  async startCapture() {
    if (this.isCapturing) return;
    this.isCapturing = true;

    try {
      // 1. 隐藏 Rubick 主窗口，避免截屏时把插件自己截进去
      if (window.nativeBridge && window.nativeBridge.hideMainWindow) {
        window.nativeBridge.hideMainWindow();
      }

      // 等待 150ms 确保窗口完全隐藏
      await new Promise((resolve) => setTimeout(resolve, 150));

      let fullScreenDataUrl = null;

      // 2. 调用 Node.js 底层截取全屏底图
      if (window.nativeBridge && window.nativeBridge.captureFullScreen) {
        fullScreenDataUrl = await window.nativeBridge.captureFullScreen();
      }

      // 检查 js-web-screen-shot 是否已就绪
      const ScreenShotConstructor = window.screenShotPlugin || window.ScreenShot;
      if (!ScreenShotConstructor) {
        throw new Error('截图标注库未能正确加载');
      }

      // 3. 实例化截图标注插件（加载全套工具栏：画笔、矩形、箭头、马赛克、文字、撤销、贴图等）
      const config = {
        capture: fullScreenDataUrl
          ? {
              source: 'image',
              imageSrc: fullScreenDataUrl
            }
          : {
              source: 'display-media'
            },
        level: 9999, // 确保遮罩在最顶层
        completeCallback: ({ base64, cutInfo }) => {
          this.isCapturing = false;
          // 恢复显示 Rubick 窗口
          if (window.nativeBridge && window.nativeBridge.showMainWindow) {
            window.nativeBridge.showMainWindow();
          }
          if (this.onComplete) {
            this.onComplete(base64, cutInfo);
          }
        },
        closeCallback: () => {
          this.isCapturing = false;
          if (window.nativeBridge && window.nativeBridge.showMainWindow) {
            window.nativeBridge.showMainWindow();
          }
          if (this.onCancel) {
            this.onCancel();
          }
        },
        cancelCallback: () => {
          this.isCapturing = false;
          if (window.nativeBridge && window.nativeBridge.showMainWindow) {
            window.nativeBridge.showMainWindow();
          }
          if (this.onCancel) {
            this.onCancel();
          }
        }
      };

      new ScreenShotConstructor(config);
    } catch (err) {
      this.isCapturing = false;
      if (window.nativeBridge && window.nativeBridge.showMainWindow) {
        window.nativeBridge.showMainWindow();
      }
      console.error('截屏启动失败:', err);
      alert('截屏失败: ' + (err.message || err));
    }
  }
}
