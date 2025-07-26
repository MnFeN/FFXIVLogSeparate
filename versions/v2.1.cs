using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using System.Linq;

public struct ProgramInfo
{
    public const string Name = "FFLogs 日志拆分";
    public const string Version = "2.1";
    public const string Author = "阿洛";

    public static readonly string Title = $"{Name}  v{Version}  by {Author}";
}

/// <summary> 记录 </summary>
public class LogChunk
{
    /// <summary> 战斗地图区域名，用于 UI 界面显示。 </summary>
    public string ZoneName;

    /// <summary> 战斗所处区域的 01 日志行位置，即这段日志的上一个 01 行号。 </summary>
    public int Line01Idx;

    /// <summary> 
    /// 战斗开始的行号。<br />
    /// 首场战斗为 01 行，<br />
    /// 其余战斗为团灭后 Director 40000011 的次行（这行代表清空了战斗的实体等资源）。 
    /// </summary>
    public int StartLineIdx;

    /// <summary> 
    /// 战斗结束的行号。<br />
    /// 团灭战斗为团灭后 Director 40000011 行（这行代表清空了战斗的实体等资源），<br />
    /// 胜利战斗为退本切换区域的 01 的前一行（不能使用 4000000[23]，因为此时实体尚未移除）。 
    /// </summary>
    public int EndLineIdx;

    /// <summary> 战斗开始的时间（本地时区）。 </summary>
    public DateTime StartTime => StartTimeOverlay ?? StartTimeACT;
    /// <summary> 战斗开始的时间（本地时区），以 Overlay 的战斗开始日志为准。 </summary>
    public DateTime? StartTimeOverlay;
    /// <summary> 战斗开始的时间（本地时区），以首次对 Boss 使用技能开始计算，以防用户没有安装 OverlayPlugin。 </summary>
    public DateTime StartTimeACT;

    /// <summary> 战斗结束的时间（本地时区），以团灭/胜利时立刻产生的 Director 4000000[235] 计算。 </summary>
    public DateTime EndTime;

    /// <summary> 战斗是否胜利。 </summary>
    public bool Win;

    /// <summary> 是否为战斗记录（记录过战斗开始时间）。 </summary>
    public bool IsEncounter => StartTime != default;

    /// <summary> 是否为有效的战斗记录（战斗时长超过 20 秒）。 </summary>
    public bool IsLegalEncounter => IsEncounter && Duration.TotalSeconds >= 20;

    /// <summary> 对应 DataGridView 中的 checkbox cell，用于判断是否输出此战斗。 </summary>
    public DataGridViewCheckBoxCell CheckBox;

    /// <summary> 判断是否选中输出此战斗。 </summary>
    public bool IsSelected => CheckBox?.Value is bool b && b;

    public int SelfDeathCount { get; set; } = 0;
    public int TotalDeathCount { get; set; } = 0;
    public int SelfDmgDownCount { get; set; } = 0;
    public int TotalDmgDownCount { get; set; } = 0;

    public TimeSpan Duration => EndTime - StartTime;

    /// <summary> 获取战斗持续时间字符串，如 4'33.0'' </summary>
    public string DurationDesc
    {
        get 
        {
            if (StartTime == DateTime.MinValue || EndTime == DateTime.MinValue)
                return "Error";
            else
            {
                TimeSpan duration = Duration;
                int totalMinutes = (int)duration.TotalMinutes;
                double seconds = duration.TotalSeconds - totalMinutes * 60;
                return $"{totalMinutes:D} m {seconds:00.0} s";
            }
        }
    }

    public IEnumerable<string> ReadChunk(StreamReader reader)
    {
        for (int i = 0; i <= EndLineIdx - StartLineIdx; i++)
            yield return reader.ReadLine();
    }

}

public class LogReader
{
    string LogPath;
    bool _separateDoorBoss;
    
    public string ActVersionLine;
    public Dictionary<int, List<LogChunk>> LogChunksDict = new Dictionary<int, List<LogChunk>>();
    public IEnumerable<LogChunk> LogChunks => LogChunksDict.OrderBy(kvp => kvp.Key).SelectMany(kvp => kvp.Value);
    public IEnumerable<LogChunk> LegalEncounters => LogChunks.Where(chunk => chunk.IsLegalEncounter);

    static Regex reZone = new Regex(@"^01\|.{34}[^|]*\|(?<zoneName>[^|]*)\|", RegexOptions.Compiled);
    static Regex rePlayer = new Regex(@"^02\|.{34}(?<pID>[^|]*)\|", RegexOptions.Compiled);
    static Regex reDeath = new Regex(@"^25\|.{34}(?<pID>.{8})\|", RegexOptions.Compiled);
    // 由于伤害降低对应很多种 debuff，使用名称文本匹配
    static Regex reDmgDown = new Regex(@"^26\|.{34}[^|]*\|(伤害降低|Damage Down|ダメージ低下|Malus de dégâts|Schaden -)\|[^|]*\|[4E].{7}\|[^|]*\|(?<pID>1.{7})\|", RegexOptions.Compiled);
    static Regex reDirector = new Regex(@"^33\|.{34}.{8}\|(?<type>.{8})\|", RegexOptions.Compiled);
    static Regex reAttackBoss = new Regex(@"^2[12]\|.{34}1.{7}\|[^|]*\|[^|]*\|[^|]*\|4", RegexOptions.Compiled);
    static Regex reStartCombat = new Regex(@"^260\|.{34}.\|1\|.\|1", RegexOptions.Compiled);
    static Regex reClearLB = new Regex(@"^41\|.{34}.{8}\|B1C\|", RegexOptions.Compiled);

    public LogReader(string path, bool separateDoorBoss)
    {
        LogPath = path;
        _separateDoorBoss = separateDoorBoss;
    }

    public void SeparateLogChunks()
    {
        using (StreamReader reader = new StreamReader(LogPath))
        {
            int lineIdx = 0;
            int line01Idx = 0;

            // 跳过开头的空白行，找到首个非空的有效行，即 ACT 插件版本行。
            // FFLogs 要求首行必须为此日志，否则上传的记录会标红报错版本不正确。
            while ((ActVersionLine = reader.ReadLine()) != null)
            {
                lineIdx++;
                if (!string.IsNullOrWhiteSpace(ActVersionLine)) break;
            }
            if (string.IsNullOrWhiteSpace(ActVersionLine))
                MessageBox.Show("日志文件为空，请检查日志文件是否正确。", ProgramInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (!ActVersionLine.StartsWith("253|"))
                throw new Exception("日志首行不是 ACT 插件的版本记录，疑似日志不完整，无法拆分为有效的日志。");

            // 将首个 01 之前的部分当做一个 chunk
            LogChunk chunk = CreateLogChunk("Init", line01Idx, 0);

            // 读取后续行并拆分为战斗记录
            string line = null;
            string prevLine = null;

            bool bWin = false;
            DateTime endTime = default;

            string zoneName = null;
            string selfId = null;

            Match m;

            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length < 3) 
                    throw new FormatException($"日志行 #{lineIdx + 1} 过短：\n{line}");
                string prefix = line.Substring(0, 3);

                switch (prefix)
                {
                    case "01|": // 切换区域
                        zoneName = MatchedGroup(line, reZone, "zoneName", lineIdx);
                        line01Idx = lineIdx;
                        SaveCurrentChunk(chunk, lineIdx - 1, ref bWin, ref endTime);
                        chunk = CreateLogChunk(zoneName, line01Idx, lineIdx);
                        break;

                    case "02|": // 获取当前玩家 ID
                        var id = MatchedGroup(line, rePlayer, "pID", lineIdx);
                        if (id.StartsWith("10"))
                            selfId = id;
                        break;

                    case "260": // Overlay 开始战斗
                        if (chunk.StartTimeOverlay == default)
                        {
                            m = reStartCombat.Match(line);
                            if (!m.Success) break;
                            chunk.StartTimeOverlay = ParseTime(line);
                        }
                        break;
                    case "20|":
                    case "21|": // 首次对 Boss 使用技能（以防没有 Overlay）
                        if (chunk.StartTimeACT == default)
                        {
                            m = reAttackBoss.Match(line);
                            if (!m.Success) break;
                            chunk.StartTimeACT = ParseTime(line);
                        }
                        break;

                    case "25|": // 死亡
                        var deathId = MatchedGroup(line, reDeath, "pID", lineIdx);
                        if (!deathId.StartsWith("10")) break; // 不是玩家
                        chunk.TotalDeathCount++;
                        if (selfId == deathId) chunk.SelfDeathCount++;
                        break;

                    case "26|": // 伤害降低
                        m = reDmgDown.Match(line);
                        if (!m.Success) break;
                        var dmgDownId = m.Groups["pID"].Value;
                        chunk.TotalDmgDownCount++;
                        if (selfId == dmgDownId) chunk.SelfDmgDownCount++;
                        break;

                    case "33|": // Director
                        string type = MatchedGroup(line, reDirector, "type", lineIdx);
                        switch (type)
                        {
                            case "40000002": // 多变迷宫胜利
                            case "40000003": // 普通副本胜利
                            case "40000005": // 普通副本团灭  记录是否胜利、结束时间
                                bWin = type != "40000005";
                                endTime = ParseTime(line);
                                break;
                            case "40000011": // 团灭重置副本资源  保存并新建 chunk 【可以写一个兼容 6.2 以前的日志的选项（40000012）】
                                SaveCurrentChunk(chunk, lineIdx, ref bWin, ref endTime);
                                chunk = CreateLogChunk(zoneName, line01Idx, lineIdx + 1);
                                break;
                        }
                        break;

                    case "41|": // 清 LB，检测门神转本体
                        if (_separateDoorBoss)
                        {
                            m = reClearLB.Match(line);
                            if (!m.Success) break;
                            endTime = ParseTime(line);
                            bWin = true;
                            chunk.ZoneName += " (P1)";
                            SaveCurrentChunk(chunk, lineIdx, ref bWin, ref endTime);
                            chunk = CreateLogChunk(zoneName, line01Idx, lineIdx + 1);
                        }
                        break;
                }
                lineIdx++;
                prevLine = line;
            }
            // 如果没有在最后一行新建一个 chunk（如最后一行恰好是 40000011），则结束最后的 chunk
            if (chunk.StartLineIdx < lineIdx)
            {
                // 异常结束日志，如日志在战斗结束前截止，将终止时间视为结束时间
                if (endTime == default)
                    endTime = ParseTime(prevLine);
                SaveCurrentChunk(chunk, lineIdx - 1, ref bWin, ref endTime);
            }
        }
    }

    public DateTime ParseTime(string line)
    {
        int sepPos = line.IndexOf('|');
        return DateTime.Parse(line.Substring(sepPos + 1, 23)); // "2025-07-24T21:00:35.905"
    }

    string MatchedGroup(string line, Regex regex, string groupName, int lineIdx)
    {
        if (line == null) throw new ArgumentNullException(nameof(line));
        var m = regex.Match(line);
        if (!m.Success)
            throw new FormatException($"日志行 #{lineIdx + 1} 格式错误：\n{line}");
        var group = m.Groups[groupName];
        if (!group.Success)
            throw new KeyNotFoundException($"日志行 #{lineIdx + 1} 不包含分组 [{groupName}]：\n{line}");
        return group.Value;
    }

    LogChunk CreateLogChunk(string zoneName, int line01Idx, int startLineIndex) => new LogChunk
    {
        ZoneName = zoneName,
        Line01Idx = line01Idx,
        StartLineIdx = startLineIndex,
    };

    void SaveCurrentChunk(LogChunk chunk, int endLineIndex, ref bool bWin, ref DateTime endTime)
    {
        chunk.Win = bWin;
        chunk.EndLineIdx = endLineIndex;
        chunk.EndTime = endTime;
        if (!LogChunksDict.ContainsKey(chunk.Line01Idx))
            LogChunksDict[chunk.Line01Idx] = new List<LogChunk>();
        LogChunksDict[chunk.Line01Idx].Add(chunk);
        bWin = false;
        endTime = default;
    }
}

public class LogSeparatorForm : Form
{
    private List<LogChunk> _encounters = new List<LogChunk>();
    public List<LogChunk> Encounters => _encounters;
    public LogReader LogReader;

    void UpdateEncounters(IEnumerable<LogChunk> encounters)
    {
        _encounters = encounters.ToList();
        if (!encounters.Any())
        {
            MessageBox.Show("日志中不包含任何 20 秒以上的战斗。", ProgramInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        // Fill Dgv
        dgvEncounters.SuspendLayout();
        try
        {
            dgvEncounters.Rows.Clear();

            int height;
            using (Graphics graphics = CreateGraphics())
            {
                height = (int)(graphics.MeasureString("啊", Font).Height * 1.05);
            }

            foreach (LogChunk encounter in Encounters)
            {
                int rowIndex = dgvEncounters.Rows.Add();
                dgvEncounters.Rows[rowIndex].Height = height;
                // 选项框
                dgvEncounters.Rows[rowIndex].Cells[0].Value = false;
                encounter.CheckBox = (DataGridViewCheckBoxCell)dgvEncounters.Rows[rowIndex].Cells[0];
                // 序号
                dgvEncounters.Rows[rowIndex].Cells[1].Value = rowIndex + 1;
                // 时间
                dgvEncounters.Rows[rowIndex].Cells[2].Value = encounter.StartTime.ToString("MM/dd HH:mm:ss");
                dgvEncounters.Rows[rowIndex].Cells[3].Value = encounter.EndTime.ToString("HH:mm:ss");
                dgvEncounters.Rows[rowIndex].Cells[4].Value = encounter.DurationDesc;
                // 胜利或团灭颜色
                Color color = encounter.Win
                    ? Color.FromArgb(68, 204, 170)  // 浅绿
                    : Color.FromArgb(221, 102, 85); // 浅红
                for (int i = 1; i <= 4; i++)
                    dgvEncounters.Rows[rowIndex].Cells[i].Style.ForeColor = color;
                // 区域
                dgvEncounters.Rows[rowIndex].Cells[5].Value = encounter.ZoneName;
                // 死亡/伤害降低计数
                dgvEncounters.Rows[rowIndex].Cells[6].Value = $"{encounter.SelfDeathCount}/{encounter.TotalDeathCount}";
                dgvEncounters.Rows[rowIndex].Cells[7].Value = $"{encounter.SelfDmgDownCount}/{encounter.TotalDmgDownCount}";
                // 行号范围
                dgvEncounters.Rows[rowIndex].Cells[8].Value = $"{encounter.StartLineIdx + 1}-{encounter.EndLineIdx + 1}";
            }
            // 设置列宽
            SetDgvColWidth(true);
        }
        finally { dgvEncounters.ResumeLayout(); }
    }

    private string _originalLogName;

    private OpenFileDialog openFileDialog;
    private SaveFileDialog saveFileDialog;

    TableLayoutPanel table, tableBottom;
    DataGridView dgvEncounters;
    Button btnOpen, btnSave;
    CheckBox chkSeparateDoorBoss, chkHideHeadMarkers, chkHideChatLog, chkHideOverlay;

    List<int> dgvColumnBaseWidths;

    public LogSeparatorForm()
    {
        Text = ProgramInfo.Title;
        Font = new Font(SelectFont("微软雅黑", "Microsoft YaHei", "微軟正黑體", "Microsoft JhengHei", "Arial"), 12);
        AutoSize = false;
        StartPosition = FormStartPosition.CenterScreen;
        Width = Screen.PrimaryScreen.Bounds.Width * 3 / 5;
        Height = Screen.PrimaryScreen.Bounds.Height * 3 / 4;
        Resize += Form_Resize;

        openFileDialog = new OpenFileDialog();
        saveFileDialog = new SaveFileDialog();

        SuspendLayout();

        ToolTip tooltip = new ToolTip
        {
            AutoPopDelay = int.MaxValue,
            InitialDelay = 500,
            ReshowDelay = 100,
            ShowAlways = true,
        };

        table = new TableLayoutPanel
        {
            RowCount = 3,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill
        };

        for (int i = 0; i < 2; i++)
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 2));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(table);

        btnOpen = new Button
        {
            Text = "打开日志",
            AutoSize = true,
            Margin = new Padding(20),
            Padding = new Padding(5),
            Anchor = AnchorStyles.None
        };
        btnOpen.Click += btnOpen_Click;

        btnSave = new Button
        {
            Text = "保存日志",
            AutoSize = true,
            Margin = new Padding(20),
            Padding = new Padding(5),
            Anchor = AnchorStyles.None
        };
        btnSave.Click += btnSave_Click;

        table.Controls.Add(btnOpen, 0, 0);
        table.Controls.Add(btnSave, 1, 0);

        dgvEncounters = new DataGridView
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(20, 0, 20, 0),
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToOrderColumns = false,
            ScrollBars = ScrollBars.Vertical,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            // “透明”表单
            BackgroundColor = this.BackColor,
            GridColor = this.BackColor,
            CellBorderStyle = DataGridViewCellBorderStyle.None,
        };
        dgvEncounters.DefaultCellStyle.BackColor = this.BackColor;
        dgvEncounters.ColumnHeadersDefaultCellStyle.BackColor = this.BackColor;

        dgvEncounters.SelectionChanged += (obj, e) => dgvEncounters.ClearSelection();

        // 选中行时着色
        Color SelectedColor = MixColor(BackColor, Color.DodgerBlue, 0.1);
        dgvEncounters.CellFormatting += (s, e) =>
        {
            if (e.RowIndex >= 0 && dgvEncounters.Rows[e.RowIndex].Cells[0] is DataGridViewCheckBoxCell cell)
            {
                bool isSelected = cell.Value is bool b && b;
                DataGridViewRow row = dgvEncounters.Rows[e.RowIndex];
                row.DefaultCellStyle.BackColor = isSelected ? SelectedColor : this.BackColor;
            }
        };
        // 立刻提交 checkbox 的修改，使该行颜色立刻刷新
        dgvEncounters.CurrentCellDirtyStateChanged += (s, e) => 
        {
            if (dgvEncounters.CurrentCell is DataGridViewCheckBoxCell)
            {
                dgvEncounters.CommitEdit(DataGridViewDataErrorContexts.Commit);
                dgvEncounters.InvalidateRow(dgvEncounters.CurrentCell.RowIndex);
            }
        };
        
        dgvEncounters.CellMouseClick += (s, e) =>
        {
            // 表头的“全选”
            if (e.RowIndex == -1 && e.ColumnIndex == 0)
            {
                bool allSelected = dgvEncounters.Rows.Cast<DataGridViewRow>()
                    .All(row => row.Cells[0] is DataGridViewCheckBoxCell cell && cell.Value is bool b && b);

                foreach (DataGridViewRow row in dgvEncounters.Rows)
                {
                    if (row.Cells[0] is DataGridViewCheckBoxCell cell)
                    {
                        cell.Value = !allSelected; // 已经全选时全清，否则全选
                    }
                }
                dgvEncounters.RefreshEdit();
            }
            // 第一列选项框的有效区域扩大到整格
            if (e.RowIndex >= 0 && (e.ColumnIndex == 0 || e.ColumnIndex == 1))
            {
                var cell = dgvEncounters.Rows[e.RowIndex].Cells[0] as DataGridViewCheckBoxCell;
                bool current = cell?.Value is bool b && b;
                cell.Value = !current;
                dgvEncounters.CommitEdit(DataGridViewDataErrorContexts.Commit);
                dgvEncounters.RefreshEdit();
            }
        };

        using (Graphics graphics = CreateGraphics())
        {
            dgvEncounters.ColumnHeadersHeight = (int)(graphics.MeasureString("啊", Font).Height * 1.2);
        }

        dgvEncounters.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "全选" });
        dgvEncounters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#" });
        dgvEncounters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "开始" });
        dgvEncounters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "结束" });
        dgvEncounters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "时长" });
        dgvEncounters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "副本名" });
        dgvEncounters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "死亡" });
        dgvEncounters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "伤降" });
        dgvEncounters.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "行号" });

        for (int i = 0; i < dgvEncounters.Columns.Count; i++)
        {
            dgvEncounters.Columns[i].ReadOnly = (i != 0);
            dgvEncounters.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            dgvEncounters.Columns[i].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }

        table.Controls.Add(dgvEncounters, 0, 1);
        table.SetColumnSpan(dgvEncounters, 2);

        chkSeparateDoorBoss = new CheckBox
        {
            Text = "以 LB 清空日志拆分门神",
            Checked = false,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(20),
            Cursor = Cursors.Help,
        };
        chkHideHeadMarkers = new CheckBox
        {
            Text = "隐藏实体标点",
            Checked = true,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(20),
            Cursor = Cursors.Help,
        };
        chkHideChatLog = new CheckBox
        {
            Text = "隐藏聊天记录",
            Checked = true,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(20),
            Cursor = Cursors.Help,
        };
        chkHideOverlay = new CheckBox
        {
            Text = "隐藏 Overlay 日志",
            Checked = false,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(20),
            Cursor = Cursors.Help,
        };
        tooltip.SetToolTip(chkSeparateDoorBoss, "测试功能：\n如果想在有存档点但中途没跳的战斗中拆分两阶段，可尝试开启此选项。\n否则请保持关闭。");
        tooltip.SetToolTip(chkHideHeadMarkers, "隐藏实体标点的相关日志，如攻击、锁链、禁止等。");
        tooltip.SetToolTip(chkHideChatLog, "隐藏聊天栏中的大多数系统日志（含聊天记录）。\nFFLogs 不需要这些日志，隐藏后可减小文件体积。");
        tooltip.SetToolTip(chkHideOverlay, "隐藏 OverlayPlugin 产生的大多数日志（除战斗状态、战斗倒计时）。\nFFLogs 不需要这些日志，隐藏后可减小文件体积。\n但这里包含很多有用的信息，如果你要分享日志，请勿开启此选项。");

        chkSeparateDoorBoss.CheckedChanged += chkSeparateDoorBoss_CheckedChanged;
        var chkCount = 4;
        tableBottom = new TableLayoutPanel
        {
            RowCount = 1,
            ColumnCount = chkCount,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill
        };
        for (int i = 0; i < chkCount; i++)
            tableBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / chkCount));
        tableBottom.Controls.Add(chkSeparateDoorBoss, 0, 0);
        tableBottom.Controls.Add(chkHideHeadMarkers, 1, 0);
        tableBottom.Controls.Add(chkHideChatLog, 2, 0);
        tableBottom.Controls.Add(chkHideOverlay, 3, 0);
        table.Controls.Add(tableBottom, 0, 2);
        table.SetColumnSpan(tableBottom, 2);

        ResumeLayout();
        SetDgvColWidth(true);
    }

    string SelectFont(params string[] fontNames)
    {
        foreach (string name in fontNames)
        {
            try
            {
                using (var testFont = new Font(name, 12)) // 避免 GDI fallback 字体
                {
                    if (testFont.Name == name) return name;
                }
            }
            catch { }
        }
        return SystemFonts.DefaultFont.Name;
    }

    Color MixColor(Color baseColor, Color targetColor, double weight)
    {
        int r = (int)(baseColor.R * (1 - weight) + targetColor.R * weight);
        int g = (int)(baseColor.G * (1 - weight) + targetColor.G * weight);
        int b = (int)(baseColor.B * (1 - weight) + targetColor.B * weight);
        return Color.FromArgb(r, g, b);
    }

    private string _defaultPath;
    public string DefaultPath
    {
        get
        {
            if (_defaultPath == null)
            {
                _defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFXIVLogs");
            }
            return _defaultPath;
        }
    }

    void btnOpen_Click(object sender, EventArgs e)
    {
        openFileDialog.Filter = "Log File (*.log)|*.log";

        if (Directory.Exists(DefaultPath))
        {
            openFileDialog.InitialDirectory = DefaultPath;
        }
        if (openFileDialog.ShowDialog() == DialogResult.OK)
        {
            _originalLogName = openFileDialog.FileName;
            LogReader = new LogReader(_originalLogName, chkSeparateDoorBoss.Checked);
            LogReader.SeparateLogChunks();
            UpdateEncounters(LogReader.LegalEncounters);
        }
    }

    void chkSeparateDoorBoss_CheckedChanged(object sender, EventArgs e)
    {
        if (Encounters.Count > 0)
        {
            LogReader = new LogReader(_originalLogName, chkSeparateDoorBoss.Checked);
            LogReader.SeparateLogChunks();
            UpdateEncounters(LogReader.LegalEncounters);
        }
    }

    void btnSave_Click(object sender, EventArgs e)
    {
        if ((Encounters?.Count ?? 0) == 0) return;

        List<LogChunk> filteredEncounters = new List<LogChunk>();

        for (int i = 0; i < Encounters.Count; i++)
        {
            var checkBoxCell = dgvEncounters.Rows[i].Cells[0] as DataGridViewCheckBoxCell;
            if (checkBoxCell != null && Convert.ToBoolean(checkBoxCell.Value) == true)
                filteredEncounters.Add(Encounters[i]);
        }

        if (filteredEncounters.Count == 0)
        {
            MessageBox.Show("未选中任何战斗。", "", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        saveFileDialog.Filter = "日志文件 (*.log)|*.log";

        if (Directory.Exists(DefaultPath))
        {
            saveFileDialog.InitialDirectory = DefaultPath;
        }

        if (saveFileDialog.ShowDialog() == DialogResult.OK)
        {
            string newLogName = saveFileDialog.FileName;

            SaveLogFile(newLogName);
        }
    }

    static Regex reZoneInitEssentialLines = new Regex(@"^01\||^40\||^02\||^03\|.{34}10", RegexOptions.Compiled);
    const string EmptyLinePlaceholder = "00|0|";
    void SaveLogFile(string newLogName)
    {
        try
        {
            // 确保所有段从头开始覆盖日志文件
            ValidateChunkContinuity();
            using (StreamReader streamReader = new StreamReader(_originalLogName))
            using (StreamWriter streamWriter = new StreamWriter(newLogName))
            {
                // 写入 ACT 插件版本行
                streamWriter.WriteLine(LogReader.ActVersionLine);

                var line01Indices = LogReader.LogChunksDict.Keys.OrderBy(idx => idx);
                foreach (int line01Idx in line01Indices)
                {
                    var chunks = new List<LogChunk>(LogReader.LogChunksDict[line01Idx]);
                    
                    var lastSelectedIdx = chunks.FindLastIndex(c => c.IsSelected);
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        // 根据这场战斗是否需要导出等条件，确认每一个 chunk 的行如何导出
                        Action<string, StreamWriter> writeAction;
                        var chunk = chunks[i];

                        // 最后一个选中的战斗之后的所有日志均可忽略（未找到时为 -1，即全部可忽略）
                        if (i > lastSelectedIdx)
                            writeAction = (line, writer) => { };
                        // 首场战斗不需要时，仅保留首场的 01 40 02 03:10XXXXXX 行
                        else if (i == 0 && !chunk.IsSelected)
                            writeAction = (line, writer) =>
                            {
                                if (reZoneInitEssentialLines.IsMatch(line))
                                    writer.WriteLine(line);
                                else
                                    writer.WriteLine(EmptyLinePlaceholder);
                            };
                        // 其他场次战斗或非战斗段落不需要时，全部占位行
                        else if (!chunk.IsSelected)
                            writeAction = (line, writer) => writer.WriteLine(EmptyLinePlaceholder);
                        // 其他情况（选中的战斗）
                        else
                            writeAction = (line, writer) =>
                            {
                                bool keep = true;
                                bool hideChatLog = chkHideChatLog.Checked;
                                bool hideHeadMarkers = chkHideHeadMarkers.Checked;
                                bool hideOverlay = chkHideOverlay.Checked;
                                switch (line.Substring(0, 3))
                                {
                                    case "00|": // ChatLog
                                        if (hideChatLog)
                                        {
                                            var type = line.Substring(37, 4);
                                            if (type != "0044" && type != "0039") // Boss 台词 / 某些重要系统消息
                                                keep = false;
                                        }
                                        break;
                                    case "28|": // 场地标点
                                        // 还没加这个
                                        break;
                                    case "29|": // 实体标点
                                        if (hideHeadMarkers) 
                                            keep = false;
                                        break;
                                    case "251": // 报错日志，如 Oodle 压缩错误
                                        keep = false;
                                        break;
                                    case "256": case "257": case "258": case "259": case "261": case "262": case "263": case "264":
                                    case "265": case "266": case "267": case "270": case "271": case "272": case "273": case "274":
                                    case "275": case "276": case "277": case "278": // 除了 战斗状态、战斗倒计时 以外的 Overlay 日志行
                                        if (hideOverlay) 
                                            keep = false;
                                        break;
                                }
                                if (keep) // 先写个最简单的，全部导出
                                    writer.WriteLine(line);
                                else
                                    writer.WriteLine(EmptyLinePlaceholder);
                            };

                        // 读取 chunk 的行，并根据 writeAction 写入到新日志文件
                        foreach (var line in chunk.ReadChunk(streamReader))
                        {
                            writeAction(line, streamWriter);
                        }
                    }
                }
            }

            Clipboard.SetText(newLogName);
            MessageBox.Show(
                "日志文件保存成功！\n\n" +
                "已将文件目录复制到剪贴板，\n" +
                "可在 FFLogs 上传器中选择文件界面中直接粘贴到 “文件名”。",
                "", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "保存日志文件时出错: \n\n" + ex.ToString(),
                "", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ValidateChunkContinuity()
    {
        var prevEnd = -1;
        foreach (var chunk in LogReader.LogChunks)
        {
            if (prevEnd + 1 != chunk.StartLineIdx)
            {
                throw new Exception(
                    $"检测到不连续的日志段：\n" +
                    $"  前一段结束行号：{prevEnd + 1}\n" +
                    $"  当前段开始行号：{chunk.StartLineIdx + 1}"
                );
            }
            prevEnd = chunk.EndLineIdx;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        e.Handled = true;
    }

    void Form_Resize(object sender, EventArgs e)
    {
        SetDgvColWidth(false);
    }

    void SetDgvColWidth(bool resetBaseWidth)
    {
        dgvEncounters.Width = Width - 20;
        string[] dgvMeasureStrings = new string[] {
            "-全选-",
            "-99-",
            "03/29 00:00:00",
            "00:00:59",
            "04 m 33.0 s",
            "阿卡迪亚零式登天斗技场 (轻量级1)",
            "1/99",
            "1/99",
            "000000 - 000000"
        };

        if (dgvColumnBaseWidths == null || resetBaseWidth)
        {
            using (Graphics graphics = CreateGraphics())
            {
                dgvColumnBaseWidths = dgvMeasureStrings.Select(
                    str => (int)Math.Ceiling(graphics.MeasureString(str, Font).Width)
                    ).ToList();
                foreach (DataGridViewRow row in dgvEncounters.Rows)
                {
                    string zoneName = row.Cells[5].Value?.ToString() ?? "";
                    int zoneNameWidth = (int)Math.Ceiling(graphics.MeasureString(zoneName, Font).Width);
                    dgvColumnBaseWidths[5] = Math.Max(dgvColumnBaseWidths[5], zoneNameWidth);
                }
            }
        }

        bool isVerticalScrollBarVisible = dgvEncounters.DisplayedRowCount(false) < dgvEncounters.RowCount;
        int totalWidth = dgvEncounters.Width - (isVerticalScrollBarVisible ? SystemInformation.VerticalScrollBarWidth : 0);
        int totalResidueWidth = totalWidth - dgvColumnBaseWidths.Sum();
        int residueWidthOthers = (int)(Math.Round((double)totalResidueWidth) / (dgvEncounters.ColumnCount - 2));
        int residueWidth5 = totalResidueWidth - (dgvEncounters.ColumnCount - 3) * residueWidthOthers;

        dgvEncounters.Columns[0].Width = dgvColumnBaseWidths[0];
        dgvEncounters.Columns[1].Width = dgvColumnBaseWidths[1];
        for (int i = 2; i < dgvEncounters.ColumnCount; i++)
            dgvEncounters.Columns[i].Width = dgvColumnBaseWidths[i] + (i == 5 ? residueWidth5 : residueWidthOthers);
    }

    [STAThread]
    public static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Thread newThread = new Thread(() =>
        {
            LogSeparatorForm form = new LogSeparatorForm();
            Application.Run(form);
        });

        newThread.SetApartmentState(ApartmentState.STA);
        newThread.Start();
    }

}

LogSeparatorForm.Main();
