import { configManager, runVisionOcr, runTranslate, testApiConnection } from './llmClient.js';
import { ScreenCaptureManager } from './capture.js';

// DOM 元素引用
const btnCapture = document.getElementById('btnCapture');
const btnPaste = document.getElementById('btnPaste');
const btnSettings = document.getElementById('btnSettings');
const btnCopySource = document.getElementById('btnCopySource');
const btnClearSource = document.getElementById('btnClearSource');
const btnTranslateAction = document.getElementById('btnTranslateAction');
const btnCopyTranslation = document.getElementById('btnCopyTranslation');

const sourceEditor = document.getElementById('sourceEditor');
const sourceCharCount = document.getElementById('sourceCharCount');
const translationView = document.getElementById('translationView');
const selectTargetLang = document.getElementById('selectTargetLang');
const transStatusTip = document.getElementById('transStatusTip');

const thumbWrap = document.getElementById('thumbWrap');
const thumbImg = document.getElementById('thumbImg');
const emptyGuide = document.getElementById('emptyGuide');

// 模态框相关
const settingsModal = document.getElementById('settingsModal');
const btnCloseSettings = document.getElementById('btnCloseSettings');
const btnCancelSettings = document.getElementById('btnCancelSettings');
const btnSaveSettings = document.getElementById('btnSaveSettings');
const btnTestConn = document.getElementById('btnTestConn');
const testConnResult = document.getElementById('testConnResult');

const cfgBaseUrl = document.getElementById('cfgBaseUrl');
const cfgApiKey = document.getElementById('cfgApiKey');
const cfgVisionModel = document.getElementById('cfgVisionModel');
const cfgTranslateModel = document.getElementById('cfgTranslateModel');
const toastBar = document.getElementById('toastBar');

let currentImageBase64 = null;
let isTranslating = false;
let isOcring = false;

// 提示工具
function showToast(msg, duration = 2000) {
  toastBar.textContent = msg;
  toastBar.classList.add('show');
  setTimeout(() => toastBar.classList.remove('show'), duration);
}

// 更新字数统计
function updateCharCount() {
  const len = sourceEditor.value.length;
  sourceCharCount.textContent = `${len} 字`;
}

// 核心：处理新图片（来自截图或剪贴板）
async function handleNewImage(base64Data) {
  if (!base64Data) return;
  currentImageBase64 = base64Data;

  // 1. 界面更新
  emptyGuide.style.display = 'none';
  thumbWrap.style.display = 'flex';
  thumbImg.src = base64Data;
  sourceEditor.value = '';
  translationView.textContent = '';
  updateCharCount();

  const cfg = configManager.get();
  if (!cfg.apiKey) {
    showToast('请先点击右上角设置配置 API Key');
    openSettings();
    return;
  }

  // 2. 触发多模态 OCR
  isOcring = true;
  sourceEditor.placeholder = '大模型正在智能识别提取图片文字...';
  transStatusTip.textContent = '正在识别文字...';

  let extractedText = '';

  await runVisionOcr({
    base64Image: base64Data,
    onChunk: (chunk) => {
      extractedText += chunk;
      sourceEditor.value = extractedText;
      updateCharCount();
    },
    onEnd: () => {
      isOcring = false;
      sourceEditor.placeholder = '你可以直接在此自由修改、校对文字...';
      transStatusTip.textContent = '文字提取完成，准备翻译';
      showToast('文字提取完成！');

      // 3. 提取完成后自动触发翻译
      if (extractedText.trim()) {
        triggerTranslation(extractedText);
      }
    },
    onError: (err) => {
      isOcring = false;
      sourceEditor.placeholder = '文字识别失败，请检查 API 配置';
      transStatusTip.textContent = 'OCR 识别出错';
      showToast('OCR 识别失败: ' + err.message, 3000);
    }
  });
}

// 核心：执行文本翻译
async function triggerTranslation(textToTranslate) {
  const text = textToTranslate || sourceEditor.value;
  if (!text || !text.trim()) {
    showToast('没有可供翻译的文字');
    return;
  }

  const targetLang = selectTargetLang.value;
  isTranslating = true;
  translationView.textContent = '';
  transStatusTip.textContent = `正在翻译为【${targetLang}】...`;

  let result = '';

  await runTranslate({
    text: text,
    targetLang: targetLang,
    onChunk: (chunk) => {
      result += chunk;
      translationView.textContent = result;
    },
    onEnd: () => {
      isTranslating = false;
      transStatusTip.textContent = '翻译完成';
    },
    onError: (err) => {
      isTranslating = false;
      transStatusTip.textContent = '翻译失败';
      showToast('翻译失败: ' + err.message, 3000);
    }
  });
}

// 初始化截图器
const captureManager = new ScreenCaptureManager({
  onComplete: (base64) => {
    handleNewImage(base64);
  },
  onCancel: () => {
    showToast('截图已取消');
  }
});

// 从剪贴板读取图片
async function pasteImageFromClipboard() {
  try {
    const clipboardItems = await navigator.clipboard.read();
    for (const item of clipboardItems) {
      for (const type of item.types) {
        if (type.startsWith('image/')) {
          const blob = await item.getType(type);
          const reader = new FileReader();
          reader.onload = (e) => {
            handleNewImage(e.target.result);
          };
          reader.readAsDataURL(blob);
          return;
        }
      }
    }
    showToast('剪贴板中未检测到图片，请先截图复制');
  } catch (err) {
    console.warn('读取剪贴板失败:', err);
    showToast('无法直接读取剪贴板，请尝试直接截图');
  }
}

// 设置抽屉控制
function openSettings() {
  const cfg = configManager.get();
  cfgBaseUrl.value = cfg.baseUrl || '';
  cfgApiKey.value = cfg.apiKey || '';
  cfgVisionModel.value = cfg.visionModel || '';
  cfgTranslateModel.value = cfg.translateModel || '';
  testConnResult.textContent = '';
  settingsModal.classList.add('active');
}

function closeSettings() {
  settingsModal.classList.remove('active');
}

function saveSettings() {
  const ok = configManager.save({
    baseUrl: cfgBaseUrl.value.trim(),
    apiKey: cfgApiKey.value.trim(),
    visionModel: cfgVisionModel.value.trim(),
    translateModel: cfgTranslateModel.value.trim()
  });

  if (ok) {
    showToast('配置已保存！');
    closeSettings();
  } else {
    showToast('保存配置失败');
  }
}

// 预设快速填充
const PRESETS = {
  dashscope: {
    baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
    vision: 'qwen-vl-max',
    translate: 'qwen-plus'
  },
  openai: {
    baseUrl: 'https://api.openai.com/v1',
    vision: 'gpt-4o-mini',
    translate: 'gpt-4o-mini'
  },
  deepseek: {
    baseUrl: 'https://api.deepseek.com',
    vision: 'deepseek-chat',
    translate: 'deepseek-chat'
  },
  ollama: {
    baseUrl: 'http://localhost:11434/v1',
    vision: 'minicpm-v',
    translate: 'qwen2.5:7b'
  }
};

document.querySelectorAll('.tag-btn').forEach((btn) => {
  btn.addEventListener('click', () => {
    const p = PRESETS[btn.dataset.preset];
    if (p) {
      cfgBaseUrl.value = p.baseUrl;
      cfgVisionModel.value = p.vision;
      cfgTranslateModel.value = p.translate;
      showToast(`已应用【${btn.textContent}】预设模板`);
    }
  });
});

// 事件绑定
btnCapture.addEventListener('click', () => captureManager.startCapture());
btnPaste.addEventListener('click', () => pasteImageFromClipboard());
btnSettings.addEventListener('click', openSettings);
btnCloseSettings.addEventListener('click', closeSettings);
btnCancelSettings.addEventListener('click', closeSettings);
btnSaveSettings.addEventListener('click', saveSettings);

// 连通性测试
btnTestConn.addEventListener('click', async () => {
  testConnResult.style.color = '#3b82f6';
  testConnResult.textContent = '测试中...';

  try {
    await testApiConnection({
      baseUrl: cfgBaseUrl.value.trim(),
      apiKey: cfgApiKey.value.trim(),
      visionModel: cfgVisionModel.value.trim(),
      translateModel: cfgTranslateModel.value.trim()
    });
    testConnResult.style.color = '#10b981';
    testConnResult.textContent = '✓ 连接成功！API 正常可用';
  } catch (err) {
    testConnResult.style.color = '#ef4444';
    testConnResult.textContent = `✗ ${err.message}`;
  }
});

// 复制与清空
btnCopySource.addEventListener('click', () => {
  const val = sourceEditor.value;
  if (!val) return;
  if (window.nativeBridge && window.nativeBridge.copyText) {
    window.nativeBridge.copyText(val);
  } else {
    navigator.clipboard.writeText(val);
  }
  showToast('已复制原文到剪贴板');
});

btnCopyTranslation.addEventListener('click', () => {
  const val = translationView.textContent;
  if (!val) return;
  if (window.nativeBridge && window.nativeBridge.copyText) {
    window.nativeBridge.copyText(val);
  } else {
    navigator.clipboard.writeText(val);
  }
  showToast('已复制译文到剪贴板');
});

btnClearSource.addEventListener('click', () => {
  sourceEditor.value = '';
  translationView.textContent = '';
  updateCharCount();
  showToast('已清空');
});

// 原文输入监听与快捷键
sourceEditor.addEventListener('input', updateCharCount);

sourceEditor.addEventListener('keydown', (e) => {
  // Ctrl + Enter 触发翻译
  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
    e.preventDefault();
    triggerTranslation();
  }
});

btnTranslateAction.addEventListener('click', () => triggerTranslation());

selectTargetLang.addEventListener('change', () => {
  if (sourceEditor.value.trim()) {
    triggerTranslation();
  }
});

// 监听 Rubick 宿主激活
if (window.nativeBridge && window.nativeBridge.onPluginEnter) {
  window.nativeBridge.onPluginEnter((action) => {
    // 设置适宜的高度
    window.nativeBridge.setHeight(560);

    // 如果是通过复制图片触发插件的
    if (action.type === 'img' && action.payload) {
      handleNewImage(action.payload);
    }
  });
}
