/**
 * 大模型 API 适配器 (兼容 OpenAI / DashScope / DeepSeek 等多模态与流式标准)
 */
const DEFAULT_CONFIG = {
  baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
  apiKey: '',
  visionModel: 'qwen-vl-max',
  translateModel: 'qwen-turbo', // 默认使用极速文本模型，首字 200ms，大幅提升重译速度
  customOcrPrompt: '请仔细识别并提取这张图片中的所有文字。严格保持原有的段落排版和顺序。若含有代码或公式请完整保留。请直接输出提取出的文本内容，严禁输出任何问候语、说明、解释或附加符号。',
  customTranslatePrompt: '你是一位精通多国语言的高级专业翻译官。请将输入的内容准确、地道、优雅地翻译成目标语言，语言通顺，语义精准，保持原有换行格式，直接输出译文，不要包含任何多余解释。'
};

const STORAGE_KEY = 'rubick_ai_ocr_settings_v1';

export const configManager = {
  get: () => {
    // 1. 优先从用户主目录本地磁盘文件读取（跨窗口/跨重启永不丢失）
    if (window.nativeBridge && typeof window.nativeBridge.loadConfig === 'function') {
      const fileCfg = window.nativeBridge.loadConfig();
      if (fileCfg && typeof fileCfg === 'object') {
        return { ...DEFAULT_CONFIG, ...fileCfg };
      }
    }

    // 2. 尝试从 localStorage 恢复
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (saved) {
        return { ...DEFAULT_CONFIG, ...JSON.parse(saved) };
      }
    } catch (e) {
      console.warn('读取配置失败:', e);
    }
    return { ...DEFAULT_CONFIG };
  },

  save: (newConfig) => {
    // 1. 优先保存至磁盘物理文件
    if (window.nativeBridge && typeof window.nativeBridge.saveConfig === 'function') {
      window.nativeBridge.saveConfig(newConfig);
    }

    // 2. 同时写入 localStorage 作为镜像
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(newConfig));
      return true;
    } catch (e) {
      console.error('保存配置失败:', e);
      return false;
    }
  }
};

/**
 * 通用 SSE 流式解析器
 */
async function streamFetch(url, options, onChunk, onEnd, onError) {
  try {
    const response = await fetch(url, options);

    if (!response.ok) {
      const errText = await response.text();
      let errorMsg = `HTTP ${response.status}: ${response.statusText}`;
      try {
        const parsed = JSON.parse(errText);
        errorMsg = parsed.error?.message || parsed.message || errorMsg;
      } catch (e) {}
      throw new Error(errorMsg);
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8');
    let buffer = '';

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = lines.pop(); // 保留不完整行

      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed || !trimmed.startsWith('data:')) continue;
        if (trimmed === 'data: [DONE]') {
          onEnd && onEnd();
          return;
        }

        try {
          const json = JSON.parse(trimmed.replace(/^data:\s*/, ''));
          const delta = json.choices?.[0]?.delta?.content || '';
          if (delta) {
            onChunk && onChunk(delta);
          }
        } catch (err) {
          // 忽略格式解析残片
        }
      }
    }

    onEnd && onEnd();
  } catch (err) {
    console.error('流式传输失败:', err);
    onError && onError(err);
  }
}

/**
 * 1. 极速一站式：单次多模态请求同步获取提取原文与目标语言译文（提速 200% 以上）
 */
export async function runFastVisionOcrAndTranslate({
  base64Image,
  targetLang,
  onOriginalChunk,
  onTranslateChunk,
  onEnd,
  onError
}) {
  const config = configManager.get();
  if (!config.apiKey) {
    onError && onError(new Error('请先在设置中填写 API Key！'));
    return;
  }

  const endpoint = `${config.baseUrl.replace(/\/+$/, '')}/chat/completions`;

  const fastPrompt = `请仔细识别图片中的文字，并将其精准翻译为【${targetLang}】。
严格遵守以下标记格式输出，严禁输出任何多余的开场白或解释：
<<<ORIGINAL>>>
(在此输出识别提取出的原文，保持原有换行格式)
<<<TRANSLATED>>>
(在此输出翻译后的${targetLang}译文)`;

  const payload = {
    model: config.visionModel || 'qwen-vl-max',
    stream: true,
    messages: [
      {
        role: 'user',
        content: [
          {
            type: 'text',
            text: fastPrompt
          },
          {
            type: 'image_url',
            image_url: {
              url: base64Image
            }
          }
        ]
      }
    ]
  };

  let mode = 'pre'; // 'pre' -> 'original' -> 'translated'
  let rawBuffer = '';

  await streamFetch(
    endpoint,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${config.apiKey}`
      },
      body: JSON.stringify(payload)
    },
    (delta) => {
      rawBuffer += delta;

      if (mode === 'pre') {
        const origIdx = rawBuffer.indexOf('<<<ORIGINAL>>>');
        if (origIdx !== -1) {
          mode = 'original';
          rawBuffer = rawBuffer.substring(origIdx + '<<<ORIGINAL>>>'.length).replace(/^\s+/, '');
          if (rawBuffer) onOriginalChunk(rawBuffer);
        }
      } else if (mode === 'original') {
        const transIdx = rawBuffer.indexOf('<<<TRANSLATED>>>');
        if (transIdx !== -1) {
          mode = 'translated';
          rawBuffer = rawBuffer.substring(transIdx + '<<<TRANSLATED>>>'.length).replace(/^\s+/, '');
          if (rawBuffer) onTranslateChunk(rawBuffer);
        } else {
          onOriginalChunk(delta);
        }
      } else if (mode === 'translated') {
        onTranslateChunk(delta);
      }
    },
    onEnd,
    onError
  );
}

/**
 * 2. 独立纯文本流式重译 (当用户修改了左侧原文后触发，调用极速文本模型)
 */
export async function runTranslate({ text, targetLang, onChunk, onEnd, onError }) {
  const config = configManager.get();
  if (!config.apiKey) {
    onError && onError(new Error('请先在设置中填写 API Key！'));
    return;
  }
  if (!text.trim()) {
    onError && onError(new Error('翻译内容不能为空'));
    return;
  }

  const endpoint = `${config.baseUrl.replace(/\/+$/, '')}/chat/completions`;

  const payload = {
    model: config.translateModel || 'qwen-turbo',
    stream: true,
    messages: [
      {
        role: 'system',
        content: config.customTranslatePrompt || DEFAULT_CONFIG.customTranslatePrompt
      },
      {
        role: 'user',
        content: `请将以下内容翻译为【${targetLang}】：\n\n${text}`
      }
    ]
  };

  await streamFetch(
    endpoint,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${config.apiKey}`
      },
      body: JSON.stringify(payload)
    },
    onChunk,
    onEnd,
    onError
  );
}

/**
 * 3. 测试 API 连通性
 */
export async function testApiConnection(customCfg) {
  const cfg = customCfg || configManager.get();
  if (!cfg.apiKey) {
    throw new Error('API Key 不能为空');
  }

  const endpoint = `${cfg.baseUrl.replace(/\/+$/, '')}/chat/completions`;
  const response = await fetch(endpoint, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${cfg.apiKey}`
    },
    body: JSON.stringify({
      model: cfg.translateModel || cfg.visionModel || 'qwen-turbo',
      messages: [{ role: 'user', content: 'Hi, please reply with OK.' }],
      max_tokens: 10
    })
  });

  if (!response.ok) {
    const errorText = await response.text();
    let msg = `连接失败 (HTTP ${response.status})`;
    try {
      const errJson = JSON.parse(errorText);
      msg = errJson.error?.message || errJson.message || msg;
    } catch (e) {}
    throw new Error(msg);
  }

  return true;
}
