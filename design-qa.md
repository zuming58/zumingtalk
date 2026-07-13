# Design QA：祖名闪电说 UI Demo

- Source visual truth: `design/mockups/home-recording.png`
- Focused capsule reference: `codex-clipboard-046f52bd-ee1f-4dd5-9ae0-e244d9e1f63e.png`
- Implementation: `prototype/`
- Implementation screenshot: `design/prototype-captures/home-1440x1024.png`
- Settings screenshot: `design/prototype-captures/settings-1440x1024.png`
- Focused capsule screenshot: `design/prototype-captures/capsule-focus.png`
- Viewport: 1440 × 1024
- State: 首页 / 录音中
- Runtime behavior: 页面默认空闲且不显示胶囊；QA 截图通过按右 Alt 进入录音中状态后采集。

## Full-view comparison evidence

源稿与实现已在同一轮视觉输入中对照。最终实现保持了 240 px 左侧栏、品牌区、首页与设置导航、顶部日期与累计指标、独立转录卡片、卡片操作区、窗口控制以及底部居中的玻璃录音胶囊。六条记录在 1440 × 1024 画布内完整呈现，信息密度与源稿一致。

## Focused region comparison evidence

对录音胶囊进行了独立聚焦比较。实现尺寸为 204 × 58 px，符合确认规范；保留了乳白玻璃表面、蓝紫“直接说”、细分隔线和青紫声波。实现比图片稿略克制，避免在真实 WPF 中产生过重光晕。

## Required fidelity surfaces

- **Fonts and typography**: 使用 Microsoft YaHei UI / Segoe UI Variable；层级、字号、行高和中文换行与源稿一致。通过。
- **Spacing and layout rhythm**: 侧栏、内容边距、卡片间距和卡片高度已在第二轮压缩；六条记录和胶囊完整可见。通过。
- **Colors and visual tokens**: 珍珠白、冰蓝、电光蓝、能量青和柔紫已统一到 CSS 变量，并与 `design/tokens.json` 对齐。通过。
- **Image and asset fidelity**: 可见操作图标全部来自 Fluent UI 图标库；未使用 emoji、占位图或手绘 SVG。页面没有额外摄影或插画资产。通过。
- **Copy and content**: 产品名、导航、统计、中文转录、胶囊和兼容性文案与 PRD、设计规范一致。通过。

## Comparison history

### Iteration 1

- [P1] “更多”菜单被后续卡片遮挡，点击“查看详情”无法打开抽屉。
  - Fix: 菜单打开时提升当前卡片层级，并移除列表容器抢占点击的逻辑。
  - Post-fix evidence: 详情抽屉成功打开，显示文本、元数据、播放器和重新转写按钮。
- [P2] 卡片纵向密度偏松，最后一条记录在 1440 × 1024 下被裁切。
  - Fix: 收紧卡片上下内边距和元数据间距。
  - Post-fix evidence: 六张卡片均完整显示，最后一张底部为 974 px，胶囊为 936–994 px。
- [P2] 左侧栏出现源稿没有的“服务正常”状态。
  - Fix: 删除额外状态，保持源稿信息架构。
  - Post-fix evidence: 最终首页截图左侧栏仅保留品牌、首页和设置。

## Primary interactions tested

- 首页与设置页切换。
- 阿里云连接与麦克风模拟测试，成功反馈可见。
- 口语顺滑、备用热键和写入模式切换。
- 历史记录更多菜单、详情抽屉、复制和重新转写反馈。
- 删除记录后数量 6 → 5，撤销后 5 → 6。
- 玻璃胶囊“直接说 → 识别中 → 已写入 → 直接说”状态循环。
- 1280 × 800 下无页面级水平或垂直溢出。
- 浏览器控制台错误：0。

## Follow-up polish

- [P3] 原生 WPF 实现时可根据实际桌面合成效果微调胶囊背景模糊和边缘折射，保持尺寸与布局不变。

final result: passed
