import { useEffect, useMemo, useRef, useState } from "react";
import {
  ArrowClockwise20Regular,
  Checkmark20Regular,
  ChevronUp20Regular,
  Clock20Regular,
  Copy20Regular,
  Delete20Regular,
  Dismiss20Regular,
  DocumentText20Regular,
  Eye20Regular,
  EyeOff20Regular,
  Flash20Filled,
  FolderOpen20Regular,
  Home20Regular,
  Info20Regular,
  Keyboard20Regular,
  Mic20Regular,
  MoreVertical20Regular,
  Play20Regular,
  Save20Regular,
  Settings20Regular,
  ShieldCheckmark20Regular,
} from "@fluentui/react-icons";

const seedRecords = [
  {
    id: 1,
    duration: "00:44",
    time: "22:18",
    text: "比方说我们功能方面开发的现在有百分之多少了？",
  },
  {
    id: 2,
    duration: "00:43",
    time: "21:46",
    text: "我们已经开发了四个阶段了，就是我也没有，我也不知道到底每个阶段到底是做的什么东西。就是现在，这个那个页面不是搭好了嘛，对吧？然后的话之前是一个，那看的效果还不错，然后现在主要是做了哪些方面？就是具体内容方面我们已经完成了哪些？一共分了多少个阶段。",
  },
  {
    id: 3,
    duration: "00:36",
    time: "20:31",
    text: "坦白讲，这个投资管家，大家看起来是有点一脸懵逼的感觉。好像这个里面是核心池、卫星池，都是股票交易的吗？不是，一开始说的是大部分是什么核心，核心池不都是什么这个债，指数基金啊，黄金货币这些东西吗？我以前没做过相关东西，有点一脸懵逼的感觉。",
  },
  {
    id: 4,
    duration: "00:26",
    time: "19:58",
    text: "那尚未完成的测试继续完成吧。对，把尚未完成的把它完成掉。",
  },
  {
    id: 5,
    duration: "00:26",
    time: "18:42",
    text: "你的意思是第三、第四阶段另外一个 Codex 的开发的，你全部审计并且修正完了，是吧？之前不是说发现了很多问题，要补充很多问题吗？都已经 OK 了吗？",
  },
  {
    id: 6,
    duration: "00:23",
    time: "17:20",
    text: "现在刚才那个用量没有了，然后你看现在是开发到哪一步了？做到哪一步了？",
  },
];

function IconButton({ label, children, onClick, active = false, danger = false }) {
  return (
    <button
      type="button"
      className={`icon-button${active ? " active" : ""}${danger ? " danger" : ""}`}
      aria-label={label}
      title={label}
      onClick={onClick}
    >
      {children}
    </button>
  );
}

function Metric({ icon, value }) {
  return (
    <div className="metric">
      {icon}
      <span>{value}</span>
    </div>
  );
}

function Sidebar({ page, setPage }) {
  return (
    <aside className="sidebar">
      <div className="brand" aria-label="祖名闪电说">
        <span className="brand-mark"><Flash20Filled /></span>
        <span>祖名闪电说</span>
      </div>
      <nav aria-label="主导航">
        <button className={page === "home" ? "nav-item active" : "nav-item"} onClick={() => setPage("home")}>
          <Home20Regular /><span>首页</span>
        </button>
        <button className={page === "settings" ? "nav-item active" : "nav-item"} onClick={() => setPage("settings")}>
          <Settings20Regular /><span>设置</span>
        </button>
      </nav>
    </aside>
  );
}

function TranscriptCard({ record, newest, onCopy, onDelete, onMenu, menuOpen, onDetails, onToast }) {
  return (
    <article className={`transcript-card${newest ? " newest" : ""}${menuOpen ? " menu-open" : ""}`}>
      <p>{record.text}</p>
      <footer>
        <div className="record-meta"><strong>{record.duration}</strong><span>直接说</span></div>
        <div className="record-actions">
          <IconButton label="复制" onClick={() => onCopy(record)}><Copy20Regular /></IconButton>
          <IconButton label="删除" danger onClick={() => onDelete(record)}><Delete20Regular /></IconButton>
          <div className="menu-anchor">
            <IconButton label="更多" active={menuOpen} onClick={() => onMenu(record.id)}><MoreVertical20Regular /></IconButton>
            {menuOpen && (
              <div className="context-menu" role="menu">
                <button onClick={() => onToast("正在播放这段录音", "info")}><Play20Regular />播放录音</button>
                <button onClick={() => onToast("已提交重新转写", "success")}><ArrowClockwise20Regular />重新转写</button>
                <button onClick={() => onDetails(record)}><Info20Regular />查看详情</button>
              </div>
            )}
          </div>
        </div>
      </footer>
    </article>
  );
}

function RecordingCapsule({ state, onCycle }) {
  const config = {
    recording: { label: "直接说", mode: "wave" },
    recognizing: { label: "识别中", mode: "dots" },
    done: { label: "已写入", mode: "check" },
    saved: { label: "已保存", mode: "check" },
    blocked: { label: "未能写入", mode: "copy" },
  }[state];

  if (!config) return null;

  return (
    <button type="button" className={`recording-capsule ${state}`} onClick={onCycle} title="点击切换录音演示状态">
      <span className="capsule-label">{config.label}</span>
      <span className="capsule-divider" />
      {config.mode === "wave" && <span className="wave-bars" aria-label="麦克风音量"><i /><i /><i /><i /><i /></span>}
      {config.mode === "dots" && <span className="pulse-dots"><i /><i /><i /></span>}
      {config.mode === "check" && <Checkmark20Regular />}
      {config.mode === "copy" && <Copy20Regular />}
    </button>
  );
}

function Toast({ toast, onClose, onUndo }) {
  if (!toast) return null;
  return (
    <div className={`toast ${toast.kind || "info"}`} role="status">
      <span className="toast-icon">{toast.kind === "success" ? <Checkmark20Regular /> : <Info20Regular />}</span>
      <span>{toast.message}</span>
      {toast.undo && <button onClick={onUndo}>撤销</button>}
      <IconButton label="关闭提示" onClick={onClose}><Dismiss20Regular /></IconButton>
    </div>
  );
}

function DetailsDrawer({ record, onClose, onCopy, onToast }) {
  if (!record) return null;
  return (
    <>
      <button className="drawer-scrim" aria-label="关闭详情" onClick={onClose} />
      <aside className="details-drawer" aria-label="记录详情">
        <header><div><span className="eyebrow">记录详情</span><h2>{record.time} 的听写</h2></div><IconButton label="关闭" onClick={onClose}><Dismiss20Regular /></IconButton></header>
        <section className="detail-text"><p>{record.text}</p><button className="secondary-button" onClick={() => onCopy(record)}><Copy20Regular />复制文字</button></section>
        <dl className="detail-grid">
          <div><dt>开始时间</dt><dd>2026-07-13 {record.time}</dd></div>
          <div><dt>时长</dt><dd>{record.duration}</dd></div>
          <div><dt>识别引擎</dt><dd>阿里云实时识别</dd></div>
          <div><dt>状态</dt><dd className="success-text">已完成</dd></div>
          <div><dt>重试次数</dt><dd>0</dd></div>
          <div><dt>写入方式</dt><dd>自动选择</dd></div>
        </dl>
        <section className="audio-block"><div className="audio-line"><button aria-label="播放"><Play20Regular /></button><span className="audio-track"><i /></span><time>{record.duration}</time></div></section>
        <button className="primary-button wide" onClick={() => onToast("已提交重新转写", "success")}><ArrowClockwise20Regular />重新转写</button>
      </aside>
    </>
  );
}

function HomePage({ records, setRecords, setToast, recordingState, cycleRecording }) {
  const [menuFor, setMenuFor] = useState(null);
  const [detail, setDetail] = useState(null);
  const [deleted, setDeleted] = useState(null);
  const scrollRef = useRef(null);
  const [showTop, setShowTop] = useState(false);

  const copyRecord = async (record) => {
    try { await navigator.clipboard.writeText(record.text); } catch { /* preview permissions may block clipboard */ }
    setToast({ message: "已复制到剪贴板", kind: "success" });
    setMenuFor(null);
  };

  const deleteRecord = (record) => {
    setRecords((items) => items.filter((item) => item.id !== record.id));
    setDeleted(record);
    setToast({ message: "已删除这条记录", kind: "info", undo: true });
  };

  const undoDelete = () => {
    if (!deleted) return;
    setRecords((items) => [...items, deleted].sort((a, b) => a.id - b.id));
    setDeleted(null);
    setToast({ message: "记录已恢复", kind: "success" });
  };

  useEffect(() => {
    const onUndo = () => undoDelete();
    window.addEventListener("zumingtalk:undo", onUndo);
    return () => window.removeEventListener("zumingtalk:undo", onUndo);
  }, [deleted]);

  return (
    <div className="page-shell home-page">
      <header className="topbar">
        <div className="date-heading"><h1>今天</h1><span>2026-07-13</span><IconButton label="打开录音文件夹" onClick={() => setToast({ message: "已打开录音文件夹", kind: "success" })}><FolderOpen20Regular /></IconButton></div>
        <div className="metrics">
          <Metric icon={<Clock20Regular className="metric-blue" />} value="12时33分" />
          <span className="metric-separator" />
          <Metric icon={<DocumentText20Regular className="metric-cyan" />} value="157,451字" />
          <span className="metric-separator" />
          <Metric icon={<Flash20Filled className="metric-orange" />} value="209字/分" />
        </div>
      </header>
      <main
        className="transcript-scroll"
        ref={scrollRef}
        onScroll={(event) => setShowTop(event.currentTarget.scrollTop > 300)}
      >
        <div className="transcript-list">
          {records.map((record, index) => (
            <TranscriptCard
              key={record.id}
              record={record}
              newest={index === 0}
              onCopy={copyRecord}
              onDelete={deleteRecord}
              onMenu={(id) => setMenuFor((current) => current === id ? null : id)}
              menuOpen={menuFor === record.id}
              onDetails={(item) => { setDetail(item); setMenuFor(null); }}
              onToast={(message, kind) => { setToast({ message, kind }); setMenuFor(null); }}
            />
          ))}
        </div>
      </main>
      {showTop && <button className="back-to-top" aria-label="回到顶部" onClick={() => scrollRef.current?.scrollTo({ top: 0, behavior: "smooth" })}><ChevronUp20Regular /></button>}
      {recordingState !== "hidden" && <RecordingCapsule state={recordingState} onCycle={cycleRecording} />}
      <DetailsDrawer record={detail} onClose={() => setDetail(null)} onCopy={copyRecord} onToast={(message, kind) => setToast({ message, kind })} />
      <span className="sr-only" data-undo-delete={Boolean(deleted)} />
    </div>
  );
}

function Field({ label, children, hint }) {
  return <label className="field"><span>{label}</span>{children}{hint && <small>{hint}</small>}</label>;
}

function Toggle({ checked, onChange, label }) {
  return <button type="button" role="switch" aria-checked={checked} className={`toggle${checked ? " on" : ""}`} onClick={() => onChange(!checked)}><i /><span>{label}</span></button>;
}

function SettingsPage({ setToast }) {
  const [showSecret, setShowSecret] = useState(false);
  const [smooth, setSmooth] = useState(true);
  const [fallback, setFallback] = useState(true);
  const [insertMode, setInsertMode] = useState("auto");
  const [testing, setTesting] = useState(false);

  const testConnection = () => {
    setTesting(true);
    setTimeout(() => { setTesting(false); setToast({ message: "连接成功，麦克风工作正常", kind: "success" }); }, 900);
  };

  return (
    <div className="page-shell settings-page">
      <header className="settings-header"><div><span className="eyebrow">偏好设置</span><h1>设置</h1><p>配置识别服务、麦克风和自动写入兼容性。</p></div><button className="primary-button" onClick={() => setToast({ message: "设置已保存", kind: "success" })}><Save20Regular />保存</button></header>
      <main className="settings-scroll">
        <section className="settings-section">
          <div className="section-title"><span className="section-icon"><Flash20Filled /></span><div><h2>语音识别服务</h2><p>凭证只保存在当前 Windows 用户下。</p></div><span className="connection-badge"><i />已连接</span></div>
          <div className="form-grid">
            <Field label="服务商"><select defaultValue="aliyun"><option value="aliyun">阿里云智能语音交互</option></select></Field>
            <Field label="AppKey"><input defaultValue="a9f2••••••••7c1" /></Field>
            <Field label="AccessKey ID"><input defaultValue="LTAI5t••••••••••••" /></Field>
            <Field label="AccessKey Secret"><div className="secret-input"><input type={showSecret ? "text" : "password"} defaultValue="encrypted-preview-secret" /><IconButton label={showSecret ? "隐藏" : "显示"} onClick={() => setShowSecret(!showSecret)}>{showSecret ? <EyeOff20Regular /> : <Eye20Regular />}</IconButton></div></Field>
          </div>
          <button className="secondary-button" onClick={testConnection} disabled={testing}>{testing ? "正在测试…" : "测试连接与麦克风"}</button>
        </section>

        <section className="settings-section split-section">
          <div className="section-title"><span className="section-icon cyan"><Mic20Regular /></span><div><h2>麦克风与听写</h2><p>声波会实时反映当前输入音量。</p></div></div>
          <Field label="输入设备"><select defaultValue="default"><option value="default">系统默认麦克风</option><option>麦克风阵列 (Realtek Audio)</option></select></Field>
          <div className="level-preview"><span>输入音量</span><div className="level-track"><i /></div><strong>正常</strong></div>
          <Toggle checked={smooth} onChange={setSmooth} label="口语顺滑（过滤语气词和连续重复）" />
        </section>

        <section className="settings-section compatibility-section">
          <div className="section-title"><span className="section-icon violet"><ShieldCheckmark20Regular /></span><div><h2>自动写入兼容性</h2><p>安全软件阻止输入时，自动降级为复制文字。</p></div><span className="safe-badge">兼容模式已启用</span></div>
          <div className="compat-grid">
            <div className="compat-stat"><span>主快捷键</span><strong><Keyboard20Regular />右 Alt</strong><small className="success-text">可用</small></div>
            <div className="compat-stat"><span>最近目标</span><strong>Codex.exe</strong><small>输入模拟</small></div>
            <div className="compat-stat"><span>最近写入</span><strong>已成功</strong><small>22:18</small></div>
          </div>
          <div className="settings-row"><div><strong>固定备用热键</strong><span>右 Alt 被 360 等安全软件拦截时使用 Ctrl + Win + Space。</span></div><Toggle checked={fallback} onChange={setFallback} label={fallback ? "已启用" : "已关闭"} /></div>
          <div className="settings-row"><div><strong>写入模式</strong><span>自动选择原生插入、粘贴消息或输入模拟。</span></div><div className="segmented"><button className={insertMode === "auto" ? "active" : ""} onClick={() => setInsertMode("auto")}>自动选择</button><button className={insertMode === "copy" ? "active" : ""} onClick={() => setInsertMode("copy")}>仅复制</button></div></div>
          <button className="secondary-button" onClick={() => setToast({ message: "测试成功：当前输入框可自动写入", kind: "success" })}>测试自动写入</button>
        </section>
      </main>
    </div>
  );
}

export function App() {
  const [page, setPage] = useState("home");
  const [records, setRecords] = useState(seedRecords);
  const [toast, setToast] = useState(null);
  const [recordingState, setRecordingState] = useState("hidden");
  const toastTimer = useRef(null);

  const showToast = (value) => {
    clearTimeout(toastTimer.current);
    setToast(value);
    toastTimer.current = setTimeout(() => setToast(null), value?.undo ? 5000 : 2800);
  };

  const cycleRecording = () => {
    if (recordingState === "hidden") {
      setRecordingState("recording");
    } else if (recordingState === "recording") {
      setRecordingState("recognizing");
      setTimeout(() => setRecordingState("done"), 900);
      setTimeout(() => { setRecordingState("hidden"); showToast({ message: "听写已写入并保存", kind: "success" }); }, 1800);
    } else if (recordingState === "recognizing") {
      return;
    } else {
      setRecordingState("hidden");
    }
  };

  useEffect(() => {
    const onKeyDown = (event) => {
      if (event.code === "AltRight") { event.preventDefault(); cycleRecording(); }
      if (event.code === "Escape" && recordingState === "recording") {
        setRecordingState("hidden");
        showToast({ message: "本次听写已取消", kind: "info" });
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [recordingState]);

  const toastValue = useMemo(() => toast, [toast]);

  return (
    <div className="app-frame">
      <Sidebar page={page} setPage={setPage} />
      {page === "home" ? (
        <HomePage records={records} setRecords={setRecords} setToast={showToast} recordingState={recordingState} cycleRecording={cycleRecording} />
      ) : (
        <SettingsPage setToast={showToast} />
      )}
      <Toast
        toast={toastValue}
        onClose={() => setToast(null)}
        onUndo={() => { window.dispatchEvent(new Event("zumingtalk:undo")); setToast(null); }}
      />
      <div className="window-controls" aria-hidden="true"><span>—</span><span>□</span><span>×</span></div>
    </div>
  );
}
