/**
 * 屏幕截取管理器
 */
export class ScreenCaptureManager {
  constructor({ onComplete, onCancel }) {
    this.onComplete = onComplete;
    this.onCancel = onCancel;
    this.isCapturing = false;
  }

  /**
   * 启动屏幕截取
   */
  async startCapture() {
    if (this.isCapturing) return;
    this.isCapturing = true;

    try {
      if (window.nativeBridge && window.nativeBridge.captureScreen) {
        const base64Data = await window.nativeBridge.captureScreen();
        this.isCapturing = false;
        if (this.onComplete) {
          this.onComplete(base64Data);
        }
      } else {
        throw new Error('当前环境未提供截图接口');
      }
    } catch (err) {
      this.isCapturing = false;
      if (window.nativeBridge && window.nativeBridge.showMainWindow) {
        window.nativeBridge.showMainWindow();
      }
      console.warn('截图取消或未完成:', err);
      if (this.onCancel) {
        this.onCancel(err.message);
      }
    }
  }
}
