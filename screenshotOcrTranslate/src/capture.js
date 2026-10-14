/**
 * 纯 JS 原生级全屏截屏引擎与选区悬浮工具栏 (Pure JS Capture Engine)
 * 100% 遵守 uTools 审核规范，零外部二进制依赖，支持选区悬浮工具栏、三图分离与快捷标注
 */

(function () {
  // 核心 DOM 节点
  const stageContainer = document.getElementById('stageContainer');
  const bgCanvas = document.getElementById('bgCanvas');
  const annotCanvas = document.getElementById('annotCanvas');
  const bgCtx = bgCanvas.getContext('2d');
  const annotCtx = annotCanvas.getContext('2d');

  const selectionBox = document.getElementById('selectionBox');
  const sizeBadge = document.getElementById('sizeBadge');
  const magnifier = document.getElementById('magnifier');
  const magCanvas = document.getElementById('magCanvas');
  const magCtx = magCanvas.getContext('2d');
  const magColorSample = document.getElementById('magColorSample');
  const magHexText = document.getElementById('magHexText');
  const magRgbText = document.getElementById('magRgbText');

  const toolbarWrap = document.getElementById('toolbarWrap');
  const floatingToolbar = document.getElementById('floatingToolbar');
  const subPalette = document.getElementById('subPalette');
  const inlineTextInput = document.getElementById('inlineTextInput');

  // 工具栏按钮
  const toolBtns = document.querySelectorAll('.tool-btn[data-tool]');
  const btnUndo = document.getElementById('btnUndo');
  const actAiEdit = document.getElementById('actAiEdit');
  const actOcr = document.getElementById('actOcr');
  const actPin = document.getElementById('actPin');
  const actSave = document.getElementById('actSave');
  const actCancel = document.getElementById('actCancel');
  const actConfirm = document.getElementById('actConfirm');

  // 状态机与配置
  let fullSnapshotImg = new Image();
  let imgLoaded = false;
  let screenWidth = window.innerWidth;
  let screenHeight = window.innerHeight;
  let dpr = window.devicePixelRatio || 1;

  // 选区几何信息 (相对于物理像素/CSS像素)
  let selRect = { x: 0, y: 0, w: 0, h: 0 };
  let isSelecting = false;
  let isSelected = false;
  let isResizing = false;
  let isMoving = false;
  let activeHandle = null;
  let dragStart = { x: 0, y: 0 };
  let origRect = { x: 0, y: 0, w: 0, h: 0 };

  // 当前标注配置
  let activeTool = null; // 'rect' | 'ellipse' | 'arrow' | 'brush' | 'mosaic' | 'text' | null
  let activeColor = '#ef4444';
  let activeSize = 4;
  let isDrawing = false;
  let drawStart = { x: 0, y: 0 };
  let brushPoints = [];

  // 标注历史栈与三图分离框记录
  let annotHistory = [];
  let recordedBoxes = []; // 供 AI 改图继承的标注矩形框坐标 [{x, y, w, h}]

  /**
   * 1. 初始化全屏尺寸与底图
   */
  function initScreen() {
    screenWidth = window.innerWidth;
    screenHeight = window.innerHeight;
    dpr = window.devicePixelRatio || 1;

    bgCanvas.width = screenWidth * dpr;
    bgCanvas.height = screenHeight * dpr;
    bgCanvas.style.width = screenWidth + 'px';
    bgCanvas.style.height = screenHeight + 'px';
    bgCtx.scale(dpr, dpr);

    annotCanvas.width = screenWidth * dpr;
    annotCanvas.height = screenHeight * dpr;
    annotCanvas.style.width = screenWidth + 'px';
    annotCanvas.style.height = screenHeight + 'px';
    annotCtx.scale(dpr, dpr);
  }

  window.addEventListener('resize', initScreen);
  initScreen();

  let isRegionMode = false;

  /**
   * 载入全屏快照底图并初次绘制
   */
  window.loadScreenSnapshot = function (dataUrl, options = {}) {
    if (!dataUrl) return;
    fullSnapshotImg.onload = () => {
      imgLoaded = true;
      const nw = fullSnapshotImg.naturalWidth;
      const nh = fullSnapshotImg.naturalHeight;

      if (options.isRegion || (nw < screenWidth * 0.95 || nh < screenHeight * 0.95)) {
        isRegionMode = true;
        // 选区模式：直接居中呈现选区与右下角悬浮工具栏
        const rw = Math.min(nw, screenWidth - 40);
        const rh = Math.min(nh, screenHeight - 80);
        const rx = Math.max(10, Math.floor((screenWidth - rw) / 2));
        const ry = Math.max(10, Math.floor((screenHeight - rh) / 2));

        selRect = { x: rx, y: ry, w: rw, h: rh };
        isSelected = true;
        magnifier.style.display = 'none';

        renderBackground();
        updateSelectionUI();
      } else {
        isRegionMode = false;
        renderBackground();
        magnifier.style.display = 'flex';
      }
    };
    fullSnapshotImg.src = dataUrl;
  };

  // 尝试从 sessionStorage 或 URL 参数预读取快照
  try {
    const cachedSnap = sessionStorage.getItem('rubick_screen_snapshot');
    if (cachedSnap) {
      window.loadScreenSnapshot(cachedSnap);
      sessionStorage.removeItem('rubick_screen_snapshot');
    }
  } catch (e) {}

  /**
   * 2. 绘制全屏暗色遮罩与掏空选区
   */
  function renderBackground() {
    if (!imgLoaded) return;
    bgCtx.clearRect(0, 0, screenWidth, screenHeight);

    if (isRegionMode) {
      // 选区模式：全屏半透明暗底，选区位置直接居中绘制原始清晰图
      bgCtx.fillStyle = 'rgba(0, 0, 0, 0.42)';
      bgCtx.fillRect(0, 0, screenWidth, screenHeight);

      if (selRect.w > 0 && selRect.h > 0) {
        bgCtx.drawImage(fullSnapshotImg, selRect.x, selRect.y, selRect.w, selRect.h);
      }
    } else {
      // 全屏模式：铺满底图，盖暗色层，掏空选区高亮
      bgCtx.drawImage(fullSnapshotImg, 0, 0, screenWidth, screenHeight);
      bgCtx.fillStyle = 'rgba(0, 0, 0, 0.42)';
      bgCtx.fillRect(0, 0, screenWidth, screenHeight);

      if (selRect.w > 0 && selRect.h > 0) {
        bgCtx.save();
        bgCtx.beginPath();
        bgCtx.rect(selRect.x, selRect.y, selRect.w, selRect.h);
        bgCtx.clip();
        bgCtx.drawImage(fullSnapshotImg, 0, 0, screenWidth, screenHeight);
        bgCtx.restore();
      }
    }
  }

  /**
   * 3. 像素放大镜与取色器渲染
   */
  function updateMagnifier(x, y) {
    if (isSelected || !imgLoaded) {
      magnifier.style.display = 'none';
      return;
    }

    magnifier.style.display = 'flex';
    // 偏移鼠标一定距离，避免遮挡光标中心
    let magX = x + 18;
    let magY = y + 18;
    if (magX + 140 > screenWidth) magX = x - 148;
    if (magY + 160 > screenHeight) magY = y - 168;

    magnifier.style.left = magX + 'px';
    magnifier.style.top = magY + 'px';

    // 采样鼠标周围 15x15 像素并放大到 130x130
    const sampleSize = 15;
    const magW = magCanvas.width;
    const magH = magCanvas.height;

    magCtx.imageSmoothingEnabled = false;
    magCtx.clearRect(0, 0, magW, magH);

    // 绘制局部放大
    magCtx.drawImage(
      fullSnapshotImg,
      (x - Math.floor(sampleSize / 2)) * (fullSnapshotImg.naturalWidth / screenWidth),
      (y - Math.floor(sampleSize / 2)) * (fullSnapshotImg.naturalHeight / screenHeight),
      sampleSize * (fullSnapshotImg.naturalWidth / screenWidth),
      sampleSize * (fullSnapshotImg.naturalHeight / screenHeight),
      0, 0, magW, magH
    );

    // 绘制放大镜准星网格中心红十字
    magCtx.strokeStyle = 'rgba(239, 68, 68, 0.85)';
    magCtx.lineWidth = 1.5;
    magCtx.beginPath();
    magCtx.moveTo(magW / 2, 0); magCtx.lineTo(magW / 2, magH);
    magCtx.moveTo(0, magH / 2); magCtx.lineTo(magW, magH / 2);
    magCtx.stroke();

    // 采样光标像素点颜色
    try {
      const p = magCtx.getImageData(Math.floor(magW / 2), Math.floor(magH / 2), 1, 1).data;
      const hex = rgbToHex(p[0], p[1], p[2]);
      magColorSample.style.backgroundColor = hex;
      magHexText.textContent = hex;
      magRgbText.textContent = `RGB(${p[0]}, ${p[1]}, ${p[2]})`;
      magnifier.dataset.currentColor = hex;
    } catch (e) {}
  }

  function rgbToHex(r, g, b) {
    return '#' + [r, g, b].map(x => {
      const h = x.toString(16);
      return h.length === 1 ? '0' + h : h;
    }).join('').toUpperCase();
  }

  /**
   * 4. 选区 DOM 与悬浮工具栏智能定位
   */
  function updateSelectionUI() {
    if (selRect.w <= 0 || selRect.h <= 0) {
      selectionBox.style.display = 'none';
      toolbarWrap.style.display = 'none';
      return;
    }

    selectionBox.style.display = 'block';
    selectionBox.style.left = selRect.x + 'px';
    selectionBox.style.top = selRect.y + 'px';
    selectionBox.style.width = selRect.w + 'px';
    selectionBox.style.height = selRect.h + 'px';

    sizeBadge.textContent = `${Math.round(selRect.w)} × ${Math.round(selRect.h)}`;

    // 定位悬浮工具栏
    updateToolbarPosition();
  }

  function updateToolbarPosition() {
    if (!isSelected) {
      toolbarWrap.style.display = 'none';
      return;
    }

    toolbarWrap.style.display = 'flex';
    const tbWidth = 430;
    const tbHeight = subPalette.style.display === 'flex' ? 74 : 38;

    // 水平位置：贴右侧对齐，若越过左边界则贴左侧
    let left = selRect.x + selRect.w - tbWidth;
    if (left + tbWidth > screenWidth - 8) left = screenWidth - tbWidth - 8;
    if (left < 8) left = 8;

    // 垂直位置：优先放选区正下方；空间不足放选区正上方；上下均不足沉浸在选区靠底边缘
    let top = selRect.y + selRect.h + 8;
    if (top + tbHeight > screenHeight - 8) {
      if (selRect.y - tbHeight - 8 >= 8) {
        top = selRect.y - tbHeight - 8;
      } else {
        top = selRect.y + selRect.h - tbHeight - 8;
      }
    }

    toolbarWrap.style.left = left + 'px';
    toolbarWrap.style.top = top + 'px';
  }

  /**
   * 5. 鼠标交互状态机处理
   */
  window.addEventListener('mousedown', (e) => {
    // 点击悬浮工具栏内部不触发选区变更
    if (toolbarWrap.contains(e.target) || inlineTextInput.contains(e.target)) return;

    const x = e.clientX;
    const y = e.clientY;

    // A. 若正在涂鸦标注模式
    if (isSelected && activeTool) {
      if (isInSelection(x, y)) {
        isDrawing = true;
        drawStart = { x, y };
        if (activeTool === 'brush') {
          brushPoints = [{ x, y }];
        }
        if (activeTool === 'text') {
          showInlineTextInput(x, y);
          isDrawing = false;
        }
      }
      return;
    }

    // B. 若点击了 8 个 resize 控制手柄
    if (isSelected && e.target.classList.contains('handle')) {
      isResizing = true;
      activeHandle = e.target.dataset.handle;
      dragStart = { x, y };
      origRect = { ...selRect };
      return;
    }

    // C. 若点击了选区内部：进入移动选区模式
    if (isSelected && isInSelection(x, y)) {
      isMoving = true;
      dragStart = { x, y };
      origRect = { ...selRect };
      return;
    }

    // D. 全新开始划定选区
    isSelecting = true;
    isSelected = false;
    dragStart = { x, y };
    selRect = { x, y, w: 0, h: 0 };
    clearAnnotations();
    renderBackground();
    updateSelectionUI();
  });

  window.addEventListener('mousemove', (e) => {
    const x = e.clientX;
    const y = e.clientY;

    // 放大镜跟随 (未定选区时)
    if (!isSelected && !isSelecting) {
      updateMagnifier(x, y);
    }

    // A. 正在划选区
    if (isSelecting) {
      const curX = Math.max(0, Math.min(screenWidth, x));
      const curY = Math.max(0, Math.min(screenHeight, y));
      selRect = {
        x: Math.min(dragStart.x, curX),
        y: Math.min(dragStart.y, curY),
        w: Math.abs(curX - dragStart.x),
        h: Math.abs(curY - dragStart.y)
      };
      renderBackground();
      updateSelectionUI();
      return;
    }

    // B. 正在拉伸选区 (8 控制点)
    if (isResizing && activeHandle) {
      resizeSelection(x, y);
      renderBackground();
      updateSelectionUI();
      return;
    }

    // C. 正在平移选区
    if (isMoving) {
      const dx = x - dragStart.x;
      const dy = y - dragStart.y;
      selRect.x = Math.max(0, Math.min(screenWidth - origRect.w, origRect.x + dx));
      selRect.y = Math.max(0, Math.min(screenHeight - origRect.h, origRect.y + dy));
      renderBackground();
      updateSelectionUI();
      redrawAnnotations();
      return;
    }

    // D. 正在涂鸦绘制标注
    if (isDrawing && activeTool) {
      renderActiveDrawing(x, y);
    }
  });

  window.addEventListener('mouseup', (e) => {
    // 结束选区划定
    if (isSelecting) {
      isSelecting = false;
      if (selRect.w > 8 && selRect.h > 8) {
        isSelected = true;
        magnifier.style.display = 'none';
        updateSelectionUI();
      } else {
        // 区域过小，视为无效点击，清空选区
        selRect = { x: 0, y: 0, w: 0, h: 0 };
        isSelected = false;
        renderBackground();
        updateSelectionUI();
      }
      return;
    }

    // 结束拉伸或平移
    if (isResizing || isMoving) {
      isResizing = false;
      isMoving = false;
      activeHandle = null;
      updateToolbarPosition();
      return;
    }

    // 结束涂鸦绘制
    if (isDrawing) {
      isDrawing = false;
      commitActiveDrawing(e.clientX, e.clientY);
    }
  });

  function isInSelection(x, y) {
    return x >= selRect.x && x <= selRect.x + selRect.w &&
           y >= selRect.y && y <= selRect.y + selRect.h;
  }

  function resizeSelection(curX, curY) {
    let { x, y, w, h } = origRect;
    const dx = curX - dragStart.x;
    const dy = curY - dragStart.y;

    if (activeHandle.includes('e')) w = Math.max(10, origRect.w + dx);
    if (activeHandle.includes('s')) h = Math.max(10, origRect.h + dy);
    if (activeHandle.includes('w')) {
      const newW = Math.max(10, origRect.w - dx);
      x = origRect.x + (origRect.w - newW);
      w = newW;
    }
    if (activeHandle.includes('n')) {
      const newH = Math.max(10, origRect.h - dy);
      y = origRect.y + (origRect.h - newH);
      h = newH;
    }

    selRect = { x, y, w, h };
  }

  /**
   * 6. 截图标注绘制引擎
   */
  function renderActiveDrawing(curX, curY) {
    redrawAnnotations();

    annotCtx.save();
    // 限制只能在选区内部绘制
    annotCtx.beginPath();
    annotCtx.rect(selRect.x, selRect.y, selRect.w, selRect.h);
    annotCtx.clip();

    annotCtx.strokeStyle = activeColor;
    annotCtx.lineWidth = activeSize;
    annotCtx.lineCap = 'round';
    annotCtx.lineJoin = 'round';

    const minX = Math.min(drawStart.x, curX);
    const minY = Math.min(drawStart.y, curY);
    const w = Math.abs(curX - drawStart.x);
    const h = Math.abs(curY - drawStart.y);

    if (activeTool === 'rect') {
      annotCtx.strokeRect(minX, minY, w, h);
    } else if (activeTool === 'ellipse') {
      annotCtx.beginPath();
      annotCtx.ellipse(minX + w / 2, minY + h / 2, w / 2, h / 2, 0, 0, Math.PI * 2);
      annotCtx.stroke();
    } else if (activeTool === 'arrow') {
      drawArrow(annotCtx, drawStart.x, drawStart.y, curX, curY, activeSize);
    } else if (activeTool === 'brush') {
      brushPoints.push({ x: curX, y: curY });
      drawBrushPath(annotCtx, brushPoints);
    } else if (activeTool === 'mosaic') {
      drawMosaic(annotCtx, minX, minY, w, h);
    }

    annotCtx.restore();
  }

  function commitActiveDrawing(curX, curY) {
    const minX = Math.min(drawStart.x, curX);
    const minY = Math.min(drawStart.y, curY);
    const w = Math.abs(curX - drawStart.x);
    const h = Math.abs(curY - drawStart.y);

    if (activeTool === 'rect' && w > 4 && h > 4) {
      annotHistory.push({ type: 'rect', color: activeColor, size: activeSize, x: minX, y: minY, w, h });
      // 记录供 AI 改图继承的红框几何相对坐标
      recordedBoxes.push({
        x: Math.round(minX - selRect.x),
        y: Math.round(minY - selRect.y),
        w: Math.round(w),
        h: Math.round(h)
      });
    } else if (activeTool === 'ellipse' && w > 4 && h > 4) {
      annotHistory.push({ type: 'ellipse', color: activeColor, size: activeSize, x: minX, y: minY, w, h });
    } else if (activeTool === 'arrow' && (Math.abs(curX - drawStart.x) > 4 || Math.abs(curY - drawStart.y) > 4)) {
      annotHistory.push({ type: 'arrow', color: activeColor, size: activeSize, x1: drawStart.x, y1: drawStart.y, x2: curX, y2: curY });
    } else if (activeTool === 'brush' && brushPoints.length > 1) {
      annotHistory.push({ type: 'brush', color: activeColor, size: activeSize, points: [...brushPoints] });
    } else if (activeTool === 'mosaic' && w > 4 && h > 4) {
      annotHistory.push({ type: 'mosaic', x: minX, y: minY, w, h });
    }

    brushPoints = [];
    redrawAnnotations();
  }

  function redrawAnnotations() {
    annotCtx.clearRect(0, 0, screenWidth, screenHeight);

    annotCtx.save();
    annotCtx.beginPath();
    annotCtx.rect(selRect.x, selRect.y, selRect.w, selRect.h);
    annotCtx.clip();

    for (const item of annotHistory) {
      annotCtx.strokeStyle = item.color;
      annotCtx.lineWidth = item.size;
      annotCtx.lineCap = 'round';
      annotCtx.lineJoin = 'round';

      if (item.type === 'rect') {
        annotCtx.strokeRect(item.x, item.y, item.w, item.h);
      } else if (item.type === 'ellipse') {
        annotCtx.beginPath();
        annotCtx.ellipse(item.x + item.w / 2, item.y + item.h / 2, item.w / 2, item.h / 2, 0, 0, Math.PI * 2);
        annotCtx.stroke();
      } else if (item.type === 'arrow') {
        drawArrow(annotCtx, item.x1, item.y1, item.x2, item.y2, item.size);
      } else if (item.type === 'brush') {
        drawBrushPath(annotCtx, item.points);
      } else if (item.type === 'mosaic') {
        drawMosaic(annotCtx, item.x, item.y, item.w, item.h);
      } else if (item.type === 'text') {
        annotCtx.fillStyle = item.color;
        annotCtx.font = `bold ${item.fontSize || 16}px sans-serif`;
        annotCtx.fillText(item.text, item.x, item.y);
      }
    }

    annotCtx.restore();
  }

  function clearAnnotations() {
    annotHistory = [];
    recordedBoxes = [];
    annotCtx.clearRect(0, 0, screenWidth, screenHeight);
  }

  function drawArrow(ctx, x1, y1, x2, y2, size) {
    const headLen = Math.max(12, size * 3.5);
    const angle = Math.atan2(y2 - y1, x2 - x1);

    ctx.beginPath();
    ctx.moveTo(x1, y1);
    ctx.lineTo(x2, y2);
    ctx.stroke();

    ctx.save();
    ctx.fillStyle = ctx.strokeStyle;
    ctx.beginPath();
    ctx.moveTo(x2, y2);
    ctx.lineTo(x2 - headLen * Math.cos(angle - Math.PI / 7), y2 - headLen * Math.sin(angle - Math.PI / 7));
    ctx.lineTo(x2 - (headLen * 0.7) * Math.cos(angle), y2 - (headLen * 0.7) * Math.sin(angle));
    ctx.lineTo(x2 - headLen * Math.cos(angle + Math.PI / 7), y2 - headLen * Math.sin(angle + Math.PI / 7));
    ctx.closePath();
    ctx.fill();
    ctx.restore();
  }

  function drawBrushPath(ctx, points) {
    if (points.length < 2) return;
    ctx.beginPath();
    ctx.moveTo(points[0].x, points[0].y);
    for (let i = 1; i < points.length; i++) {
      ctx.lineTo(points[i].x, points[i].y);
    }
    ctx.stroke();
  }

  function drawMosaic(ctx, mx, my, mw, mh) {
    const blockSize = 8;
    // 从原始全屏底图读取该区域像素
    const tempCanvas = document.createElement('canvas');
    tempCanvas.width = mw;
    tempCanvas.height = mh;
    const tCtx = tempCanvas.getContext('2d');
    tCtx.drawImage(fullSnapshotImg,
      mx * (fullSnapshotImg.naturalWidth / screenWidth),
      my * (fullSnapshotImg.naturalHeight / screenHeight),
      mw * (fullSnapshotImg.naturalWidth / screenWidth),
      mh * (fullSnapshotImg.naturalHeight / screenHeight),
      0, 0, mw, mh
    );

    const imgData = tCtx.getImageData(0, 0, mw, mh);
    const data = imgData.data;

    for (let y = 0; y < mh; y += blockSize) {
      for (let x = 0; x < mw; x += blockSize) {
        const pIndex = (y * mw + x) * 4;
        const r = data[pIndex];
        const g = data[pIndex + 1];
        const b = data[pIndex + 2];
        ctx.fillStyle = `rgb(${r},${g},${b})`;
        ctx.fillRect(mx + x, my + y, blockSize, blockSize);
      }
    }
  }

  function showInlineTextInput(x, y) {
    inlineTextInput.style.display = 'block';
    inlineTextInput.style.left = x + 'px';
    inlineTextInput.style.top = y + 'px';
    inlineTextInput.style.color = activeColor;
    inlineTextInput.value = '';
    inlineTextInput.focus();

    const commitText = () => {
      const val = inlineTextInput.value.trim();
      if (val) {
        annotHistory.push({
          type: 'text',
          text: val,
          x: x,
          y: y + 16,
          color: activeColor,
          fontSize: 16
        });
        redrawAnnotations();
      }
      inlineTextInput.style.display = 'none';
      inlineTextInput.removeEventListener('blur', commitText);
    };

    inlineTextInput.addEventListener('blur', commitText);
    inlineTextInput.addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter' && !ev.shiftKey) {
        ev.preventDefault();
        inlineTextInput.blur();
      }
    });
  }

  /**
   * 7. 悬浮工具栏与二级调色板事件
   */
  toolBtns.forEach(btn => {
    btn.addEventListener('click', () => {
      const tool = btn.dataset.tool;
      if (activeTool === tool) {
        // 再次点击取消激活
        activeTool = null;
        btn.classList.remove('active');
        subPalette.style.display = 'none';
      } else {
        toolBtns.forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        activeTool = tool;
        subPalette.style.display = 'flex';
      }
      updateToolbarPosition();
    });
  });

  // 调色板颜色圆点
  document.querySelectorAll('.color-dot').forEach(dot => {
    dot.addEventListener('click', () => {
      document.querySelectorAll('.color-dot').forEach(d => d.classList.remove('active'));
      dot.classList.add('active');
      activeColor = dot.dataset.color;
    });
  });

  // 线宽圆点
  document.querySelectorAll('.size-dot').forEach(dot => {
    dot.addEventListener('click', () => {
      document.querySelectorAll('.size-dot').forEach(d => d.classList.remove('active'));
      dot.classList.add('active');
      activeSize = parseInt(dot.dataset.size, 10) || 4;
    });
  });

  // 撤销按钮
  btnUndo.addEventListener('click', () => {
    if (annotHistory.length > 0) {
      const popped = annotHistory.pop();
      if (popped.type === 'rect') {
        recordedBoxes.pop();
      }
      redrawAnnotations();
    }
  });

  /**
   * 8. 三图分离导出与动作派发
   */
  function exportCaptureData() {
    if (selRect.w <= 0 || selRect.h <= 0) return null;

    let realW = selRect.w;
    let realH = selRect.h;
    const cleanCanvas = document.createElement('canvas');
    const cCtx = cleanCanvas.getContext('2d');

    if (isRegionMode) {
      realW = fullSnapshotImg.naturalWidth;
      realH = fullSnapshotImg.naturalHeight;
      cleanCanvas.width = realW;
      cleanCanvas.height = realH;
      cCtx.drawImage(fullSnapshotImg, 0, 0, realW, realH);
    } else {
      const scaleX = fullSnapshotImg.naturalWidth / screenWidth;
      const scaleY = fullSnapshotImg.naturalHeight / screenHeight;
      const realX = Math.round(selRect.x * scaleX);
      const realY = Math.round(selRect.y * scaleY);
      realW = Math.round(selRect.w * scaleX);
      realH = Math.round(selRect.h * scaleY);
      cleanCanvas.width = realW;
      cleanCanvas.height = realH;
      cCtx.drawImage(fullSnapshotImg, realX, realY, realW, realH, 0, 0, realW, realH);
    }
    const cleanDataUrl = cleanCanvas.toDataURL('image/png');

    // 2. 带涂鸦的完整图 (Annotated Image)
    const annotCanvasMerged = document.createElement('canvas');
    annotCanvasMerged.width = realW;
    annotCanvasMerged.height = realH;
    const aCtx = annotCanvasMerged.getContext('2d');
    // 底图
    aCtx.drawImage(cleanCanvas, 0, 0);
    // 叠加标注涂鸦
    aCtx.drawImage(annotCanvas,
      selRect.x * dpr, selRect.y * dpr, selRect.w * dpr, selRect.h * dpr,
      0, 0, realW, realH
    );
    const annotatedDataUrl = annotCanvasMerged.toDataURL('image/png');

    // 3. 构建三图分离包
    const bundle = {
      cleanDataUrl,
      annotatedDataUrl,
      boxes: recordedBoxes,
      hasMaskRegion: recordedBoxes.length > 0,
      timestamp: Date.now()
    };

    return { cleanDataUrl, annotatedDataUrl, bundle };
  }

  function finishWithAction(actionType) {
    const data = exportCaptureData();
    if (!data && actionType !== 'cancel') return;

    // 若动作是复制，直接将带标注截图写入系统剪贴板
    if (actionType === 'copy' && data && (data.annotatedDataUrl || data.cleanDataUrl)) {
      const copyUrl = data.annotatedDataUrl || data.cleanDataUrl;
      try {
        if (window.utools && typeof window.utools.copyImage === 'function') {
          window.utools.copyImage(copyUrl);
        }
      } catch (e) {}
    }

    // 写入 IPC 临时共享文件供主窗口 Preload 读取
    try {
      const fs = require('fs');
      const os = require('os');
      const path = require('path');
      const CAPTURE_IPC_FILE = path.join(os.tmpdir(), 'rubick_capture_ipc_result.json');
      fs.writeFileSync(CAPTURE_IPC_FILE, JSON.stringify({
        action: actionType,
        data: data,
        timestamp: Date.now()
      }), 'utf-8');
    } catch (e) {}

    try {
      localStorage.setItem('rubick_last_capture_result', JSON.stringify({
        action: actionType,
        data: data,
        timestamp: Date.now()
      }));
    } catch (e) {}

    if (window.onCaptureFinished) {
      window.onCaptureFinished(actionType, data);
    } else if (window.opener && window.opener.onSubWindowCaptureFinished) {
      window.opener.onSubWindowCaptureFinished(actionType, data);
      window.close();
    } else {
      window.close();
    }
  }

  // 动作按钮绑定
  actAiEdit.addEventListener('click', () => finishWithAction('ai_edit'));
  actOcr.addEventListener('click', () => finishWithAction('ocr'));
  actPin.addEventListener('click', () => finishWithAction('pin'));
  actSave.addEventListener('click', () => {
    const data = exportCaptureData();
    if (!data) return;
    const a = document.createElement('a');
    a.href = data.annotatedDataUrl || data.cleanDataUrl;
    a.download = `Capture_${Date.now()}.png`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    finishWithAction('copy');
  });
  actCancel.addEventListener('click', () => finishWithAction('cancel'));
  actConfirm.addEventListener('click', () => finishWithAction('copy'));

  // 双击选区直接复制完成
  selectionBox.addEventListener('dblclick', () => finishWithAction('copy'));

  // 键盘快捷键监听
  window.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      finishWithAction('cancel');
    } else if (e.key === 'Enter') {
      finishWithAction('copy');
    } else if (e.key === 'c' || e.key === 'C') {
      // 取色快捷键复制
      const hex = magnifier.dataset.currentColor;
      if (hex && !isSelected) {
        navigator.clipboard.writeText(hex);
        magHexText.textContent = '已复制 ' + hex;
      }
    } else if ((e.ctrlKey || e.metaKey) && e.key === 'z') {
      btnUndo.click();
    }
  });

})();
