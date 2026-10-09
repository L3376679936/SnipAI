/**
 * 大模型 API 适配器 (兼容 OpenAI / DashScope / DeepSeek 等多模态与流式标准)
 */
const DEFAULT_CONFIG = {
  baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
  apiKey: '',
  visionModel: 'qwen-vl-max',
  translateModel: 'qwen-plus',
  customOcrPrompt: '请仔细识别并提取这张图片中的所有文字。严格保持原有的段落排版和顺序。若含有代码或公式请完整保留。请直接输出提取出的文本内容，严禁输出任何问候语、说明、解释或附加符号。',
  customTranslatePrompt: '你是一位精通多国语言的高级专业翻译官。请将输入的内容准确、地道、优雅地翻译成目标语言，语言通顺，语义精准，保持原有换行格式，直接输出译文，不要包含任何多余解释。'
};

const STORAGE_KEY = 'rubick_ai_ocr_settings_v1';

export const configManager = {
  get: () => {
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
 * 1. 多模态 OCR 文字识别
 */
export async function runVisionOcr({ base64Image, onChunk, onEnd, onError }) {
  const config = configManager.get();
  if (!config.apiKey) {
    onError && onError(new Error('请先在设置中填写 API Key！'));
    return;
  }

  const endpoint = `${config.baseUrl.replace(/\/+$/, '')}/chat/completions`;

  const payload = {
    model: config.visionModel || 'qwen-vl-max',
    stream: true,
    messages: [
      {
        role: 'user',
        content: [
          {
            type: 'text',
            text: config.customOcrPrompt || DEFAULT_CONFIG.customOcrPrompt
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
 * 2. 智能文本翻译
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
    model: config.translateModel || 'qwen-plus',
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
      model: cfg.translateModel || cfg.visionModel || 'qwen-plus',
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
