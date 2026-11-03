using System.Globalization;

namespace CodexStatusbar;

/// <summary>One ordinary, resizable, local-only window. Closing it hides it; polling belongs to the host.</summary>
internal sealed class MonitoringPanelForm : Form
{
    private readonly Label _mode;
    private readonly Label _state;
    private readonly Label _totals;
    private readonly Label _speed;
    private readonly Label _detail;
    private readonly Label _coverage;
    private readonly Label _heartbeat;
    private readonly DataGridView _grid;
    private readonly Button _candidateButton;
    private readonly Dictionary<StripField, CheckBox> _stripChecks = new();
    private bool _presentingFields;
    private string? _candidateId;
    private IReadOnlyList<RecentSessionMetric> _rows = [];
    private TokenSnapshot? _snapshot;
    private bool _allowClose;
    internal Button AutoButton { get; }
    internal Button SelectButton { get; }
    internal Button RefreshButton { get; }
    public event Action<string>? SessionSelected;
    public event Action? AutoRequested;
    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<StripField>? StripFieldsChanged;

    public MonitoringPanelForm()
    {
        Text = "Codex 用量与速度监测 · 1.1.0-local.6";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(780, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10f);
        BackColor = Color.FromArgb(246, 248, 251);
        ForeColor = Color.FromArgb(28, 40, 54);
        Icon = SystemIcons.Information;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 11
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < root.RowCount; i++)
            root.RowStyles.Add(new RowStyle(i == 8 ? SizeType.Percent : SizeType.AutoSize, i == 8 ? 100 : 0));
        Controls.Add(root);
        root.Controls.Add(MakeLabel("Codex  用量与速度", 20, FontStyle.Bold), 0, 0);
        _mode = MakeLabel("自动跟随 · 等待识别", 11, FontStyle.Bold);
        root.Controls.Add(_mode, 0, 1);
        _state = MakeLabel("正在连接本地监测器…", 10);
        root.Controls.Add(_state, 0, 2);
        _totals = MakeLabel("累计输入  --       累计输出  --       缓存输入  --       合计  --", 13, FontStyle.Bold);
        _totals.Margin = new Padding(0, 16, 0, 8);
        root.Controls.Add(_totals, 0, 3);
        _speed = MakeLabel("本轮速度  --       最近一轮速度  --", 12);
        root.Controls.Add(_speed, 0, 4);
        _detail = MakeLabel("本轮输入 / 输出 / 缓存：--\n最后事件：--  ·  最后用量：--  ·  来源：等待本地记录", 10);
        root.Controls.Add(_detail, 0, 5);
        var commands = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 8), WrapContents = true };
        AutoButton = MakeButton("返回自动跟随", () => AutoRequested?.Invoke());
        SelectButton = MakeButton("选择并锁定会话", SelectCurrent);
        RefreshButton = MakeButton("立即检查新记录", () => RefreshRequested?.Invoke());
        _candidateButton = MakeButton("锁定 IPC 候选", () => { if (_candidateId is string id) SessionSelected?.Invoke(id); });
        commands.Controls.AddRange([AutoButton, SelectButton, RefreshButton,
            _candidateButton,
            MakeButton("浮条与高级设置", () => SettingsRequested?.Invoke()),
            MakeButton("退至托盘", Hide),
            MakeButton("退出监测", () => ExitRequested?.Invoke())]);
        root.Controls.Add(commands, 0, 6);
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        fields.Controls.Add(new Label { Text = "浮标内容：", AutoSize = true, Padding = new Padding(0, 3, 0, 0) });
        foreach (var (flag, text) in new[] { (StripField.Speed, "阶段估速"), (StripField.Total, "累计 token"),
            (StripField.CumulativeCache, "缓存命中（累计）"), (StripField.Ttft, "TTFT（上报）"),
            (StripField.RequestContext, "最新请求 / 窗口") })
        {
            var check = new CheckBox { Text = text, AutoSize = true, Checked = StripFieldRules.Default.HasFlag(flag), Margin = new Padding(0, 3, 12, 3) };
            _stripChecks[flag] = check;
            check.CheckedChanged += (_, _) =>
            {
                if (_presentingFields) return;
                var chosen = _stripChecks.Where(pair => pair.Value.Checked).Aggregate(StripField.None, (value, pair) => value | pair.Key);
                if (chosen == StripField.None) { _presentingFields = true; check.Checked = true; _presentingFields = false; return; }
                StripFieldsChanged?.Invoke(chosen);
            };
            fields.Controls.Add(check);
        }
        root.Controls.Add(fields, 0, 7);
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
            AutoGenerateColumns = false, EnableHeadersVisualStyles = false,
            AccessibleName = "近期发现的本地会话"
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 237, 245);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = ForeColor;
        _grid.ColumnHeadersHeight = 40;
        _grid.RowTemplate.Height = 38;
        var columns = new[] { ("会话（短 ID）", 125), ("累计输入", 95), ("累计输出", 85), ("缓存输入", 95),
            ("本轮估速", 95), ("最近一轮", 95), ("状态", 165), ("最后事件", 120) };
        foreach (var (title, width) in columns)
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, FillWeight = width,
                MinimumWidth = Math.Min(width, 85), SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) SelectCurrent(); };
        root.Controls.Add(_grid, 0, 8);
        _coverage = MakeLabel("近期发现：最多 8 条本地会话；启动时有界读取近期记录，之后增量观察。", 9);
        root.Controls.Add(_coverage, 0, 9);
        _heartbeat = MakeLabel("速度为客户端事件推算（低可信，包含推理）；阶段起点未独立验证，不是逐 token 流速或网络实测。", 9);
        root.Controls.Add(_heartbeat, 0, 10);
        FormClosing += (_, e) =>
        {
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && !_allowClose) Hide(); };
    }

    private static Label MakeLabel(string text, float size, FontStyle style = FontStyle.Regular) =>
        new() { Text = text, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4),
            Font = new Font("Microsoft YaHei UI", size, style), UseMnemonic = false };
    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(9, 4, 9, 4),
            Margin = new Padding(0, 0, 8, 4), FlatStyle = FlatStyle.System };
        button.Click += (_, _) => action();
        return button;
    }

    private void SelectCurrent()
    {
        if (_grid.CurrentRow?.Tag is string id) SessionSelected?.Invoke(id);
    }

    public void Present(TokenSnapshot? snapshot, IReadOnlyList<RecentSessionMetric> rows,
        ActiveThreadRouteStatus route, string? selectedId, bool incomplete, DateTime now, string clientVersion,
        DateTime? lastSuccessfulPollUtc = null, StripField stripFields = StripFieldRules.Default)
    {
        if (IsDisposed) return;
        _snapshot = snapshot;
        _rows = rows;
        _presentingFields = true;
        try { foreach (var pair in _stripChecks) pair.Value.Checked = stripFields.HasFlag(pair.Key); }
        finally { _presentingFields = false; }
        _mode.Text = selectedId is null ? "自动跟随 · 前台会话未识别"
            : "手动锁定 · " + MetricFormat.ShortThreadId(selectedId);
        _state.Text = MonitoringStatus.Route(route) + "  |  客户端 " + clientVersion;
        _candidateId = route.IsConnected ? route.ThreadId : null;
        _candidateButton.Enabled = _candidateId is not null && rows.Any(r => r.ThreadId == _candidateId && r.Readable);
        if (_candidateId is not null) _state.Text += "  |  候选 " + MetricFormat.ShortThreadId(_candidateId);
        if (snapshot is null)
        {
            _totals.Text = "累计输入  --       累计输出  --       缓存输入  --       合计  --";
            _speed.Text = "本轮速度  --       最近一轮速度  --";
            _detail.Text = selectedId is null
                ? "客户端未提供可验证的前台窗口 → 会话映射。请从下表选择并锁定本地会话。\nDot / 云端页面没有可读本地日志时不支持；不会沿用其他会话的数字。"
                : "正在读取所选会话的本地记录；若没有可读日志，将显示数据不完整。";
        }
        else
        {
            string Count(long v) => snapshot.UsageBreakdownKnown ? MetricFormat.FullTokens(v) : "--";
            _totals.Text = $"累计输入  {Count(snapshot.InputTokens)}    累计输出  {Count(snapshot.OutputTokens)}    缓存输入  {Count(snapshot.CachedInputTokens)}    合计  {MetricFormat.FullTokens(snapshot.TotalTokens)}";
            _speed.Text = MonitoringStatus.RecordedSpeed(snapshot, now) + "\n" + MonitoringStatus.State(snapshot, now);
            _detail.Text = $"本轮输入 {Optional(snapshot.TurnInputTokens)} / 输出 {(snapshot.TurnId is null ? "--" : Optional(snapshot.TurnOutputTokens))} / 缓存 {Optional(snapshot.TurnCachedInputTokens)}   ·   TTFT {(snapshot.TtftMs is double t ? t.ToString("0.##", CultureInfo.InvariantCulture) + " ms" : "未上报")}\n"
                + $"日志更新 {MonitoringStatus.Timestamp(snapshot.UpdatedAtUtc)}（{MonitoringStatus.Age(snapshot.UpdatedAtUtc, now)}） · 用量更新 {MonitoringStatus.Timestamp(snapshot.LastUsageAtUtc)}（{MonitoringStatus.Age(snapshot.LastUsageAtUtc, now)}） · {MonitoringStatus.ReadProgress(snapshot)} · {(snapshot.UsageBreakdownKnown ? "本地用量日志" : "数据库总量，明细未知")}";
        }
        var preserve = _grid.CurrentRow?.Tag as string;
        _grid.SuspendLayout();
        try
        {
            while (_grid.Rows.Count > rows.Count) _grid.Rows.RemoveAt(_grid.Rows.Count - 1);
            for (var i = 0; i < rows.Count; i++)
            {
                var item = rows[i];
                if (i >= _grid.Rows.Count) _grid.Rows.Add();
                var row = _grid.Rows[i];
                row.Tag = item.ThreadId;
                var known = item.Readable && item.UsageBreakdownKnown;
                var fresh = MonitoringStatus.IsFresh(item.LastUsageAtUtc ?? item.LastWriteUtc, now);
                var state = !item.Readable ? "日志不可读" : !known ? "数据不完整"
                    : fresh && item.Tps is not null ? "阶段计数已更新 · 估速"
                    : !item.Streaming && MetricFormat.IsUsableTps(item.LastTurnTps) ? "上轮阶段记录" : "等待下次用量记录";
                object[] values = [MetricFormat.ShortThreadId(item.ThreadId) + (item.ThreadId == selectedId ? " ✓" : ""),
                    known ? MetricFormat.FullTokens(item.InputTokens) : "--", known ? MetricFormat.FullTokens(item.OutputTokens) : "--",
                    known ? MetricFormat.FullTokens(item.CachedInputTokens) : "--", MonitoringStatus.Speed(fresh ? item.Tps : null),
                    MonitoringStatus.Speed(item.LastTurnTps), state,
                    MonitoringStatus.Timestamp(item.LastWriteUtc)];
                for (var j = 0; j < values.Length; j++) if (!Equals(row.Cells[j].Value, values[j])) row.Cells[j].Value = values[j];
                if (item.ThreadId == preserve && _grid.CurrentRow?.Index != i)
                    _grid.CurrentCell = row.Cells[0];
            }
        }
        finally { _grid.ResumeLayout(); }
        SelectButton.Enabled = rows.Any(row => row.Readable);
        _coverage.Text = $"近期发现 {rows.Count}/8 条 · 本地短 ID · 只读 · 不是全部历史会话"
            + (incomplete ? " · 发现预算 / 队列已满，列表可能不完整" : " · 后台会话轮询最长约 8 秒");
        _heartbeat.Text = $"{MonitoringStatus.PollStatus(lastSuccessfulPollUtc, now)} · 15秒无计数只使估速过期，不判断任务停止。\n阶段估速含推理，非逐 token；缓存命中为累计口径。TTFT 只显示客户端上报值。";
    }

    private static string Optional(long? value) => value is long number ? MetricFormat.FullTokens(number) : "--";
    internal int DisplayedRows => _grid.Rows.Count;
    internal string OverviewText => _totals.Text + _speed.Text + _detail.Text;
    internal string PollText => _heartbeat.Text;
    internal CheckBox StripCheck(StripField field) => _stripChecks[field];
    public void CloseForExit() { _allowClose = true; Close(); }
}
