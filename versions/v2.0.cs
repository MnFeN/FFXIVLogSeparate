# 脚本用于 Triggernometry 触发器
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
    public const string Version = "2.0";
    public const string Author = "阿洛 MnFeN";

    public static readonly string Title = $"{Name}  v{Version}  by {Author}";
}

public class Encounter
{
    public string ZoneName;
    public int Line01Idx; // 战斗开始前的 01 日志行位置
    public int StartLineIdx;
    public int EndLineIdx;
    public DateTime StartTime; // local time
    public DateTime EndTime;
    public bool Win;

    public int SelfDeathCount = 0;
    public int TotalDeathCount = 0;
    public int SelfDmgDownCount = 0;
    public int TotalDmgDownCount = 0;

    public string GetDuration()
    {
        if (StartTime == DateTime.MinValue || EndTime == DateTime.MinValue)
            return "Error";
        else
        {
            TimeSpan duration = EndTime - StartTime;
            int totalMinutes = (int)duration.TotalMinutes;
            int seconds = duration.Seconds;
            return $"{totalMinutes:D}\'{seconds:D2}\"";
        }
    }
}

public class LogReader
{
    public string LogPath;
    public List<Encounter> Encounters = new List<Encounter>();
    private Encounter _encounter = new Encounter();

    private static Regex rexZone = new Regex(@"^01\|.{34}[^|]*\|(?<zoneName>[^|]*)\|");
    private static Regex rexPlayer = new Regex(@"^02\|.{34}(?<pID>.{8})\|");
    private static Regex rexDeath = new Regex(@"^25\|.{34}(?<pID>1.{7})\|");
    private static Regex rexDmgDown = new Regex(@"^26\|.{34}[^|]*\|(伤害降低|Damage Down|ダメージ低下|Malus de dégâts|Schaden -)\|[^|]*\|[4E].{7}\|[^|]*\|(?<pID>1.{7})\|");
    private static Regex rexDirector = new Regex(@"^33\|.{34}.{8}\|400000(?<type>..)\|");
    private static Regex rexAttackBoss = new Regex(@"^2[12]\|.{34}1.{7}\|[^|]*\|[^|]*\|[^|]*\|4");
    private static Regex rexClearLB = new Regex(@"^41\|.{34}.{8}\|B1C\|");
    private Match m;

    private int _lineIdx;
    private int _line01Idx;
    private string _zoneName;
    private string _selfId;

    private string _line;
    private string _prevLine;

    private bool _separateDoorBoss;

    public LogReader(string path, bool separateDoorBoss)
    {
        LogPath = path;
        _separateDoorBoss = separateDoorBoss;
    }

    public void Read()
    {
        using (StreamReader reader = new StreamReader(LogPath))
        {
            while ((_line = reader.ReadLine()) != null)
            {
                ReadLine(_line);
                _lineIdx++;
                _prevLine = _line;
            }
            // 如果最后的战斗没有结束 则将最后一行视为结束
            if (_encounter.StartLineIdx != 0) // 有尚未结束的战斗
                SaveCurrentEncounter(_lineIdx - 1, bWin: false, ParseTime(_prevLine));
        }
        // logs 网站不会记录不足 20 秒的战斗
        Encounters = Encounters.Where(e => (e.EndTime - e.StartTime).TotalSeconds >= 20).ToList();
        if (Encounters.Count == 0)
        {
            MessageBox.Show("日志中不包含任何 20 秒以上的战斗。", "", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public DateTime ParseTime(string line)
    {
        int sepPos = line.IndexOf('|');
        return DateTime.Parse(line.Substring(sepPos + 1, 19));
    }

    private void SetNewEncounter(int startLineIndex)
    {
        _encounter = new Encounter
        {
            ZoneName = _zoneName,
            Line01Idx = _line01Idx,
            StartLineIdx = startLineIndex,
        };
    }

    private void SaveCurrentEncounter(int endLineIndex, bool bWin, DateTime endTime)
    {
        if (_encounter.StartTime != DateTime.MinValue)
        {
            _encounter.Win = bWin;
            _encounter.EndLineIdx = endLineIndex;
            _encounter.EndTime = endTime;
            Encounters.Add(_encounter);
        }

        _encounter = new Encounter();
    }

    private void ReadLine(string line)
    {
        if (line == null || line.Length < 3)
            return;
        string prefix = line.Substring(0, 3);

        if (prefix == "01|") // 副本区域
        {
            m = rexZone.Match(line);
            if (!m.Success)
                return;
            if (_encounter.StartLineIdx != 0) // 有尚未结束的战斗
                SaveCurrentEncounter(_lineIdx - 1, bWin: false, ParseTime(_prevLine));
            _line01Idx = _lineIdx;
            _zoneName = m.Groups["zoneName"].Value;
        }
        else if (prefix == "02|") // 获取当前玩家 ID
        {
            m = rexPlayer.Match(line);
            if (!m.Success)
                return;
            _selfId = m.Groups["pID"].Value;
            SetNewEncounter(startLineIndex: _lineIdx + 1);
        }
        else if (_encounter.StartTime == DateTime.MinValue && (prefix == "20|" || prefix == "21|")) // 首次攻击 Boss
        {
            m = rexAttackBoss.Match(line);
            if (!m.Success)
                return;
            _encounter.StartTime = ParseTime(line);
        }
        else if (prefix == "25|") // 死亡
        {
            m = rexDeath.Match(line);
            if (!m.Success)
                return;

            if (_selfId == m.Groups["pID"].Value)
                _encounter.SelfDeathCount++;
            _encounter.TotalDeathCount++;
        }
        else if (prefix == "26|") // 伤害降低
        {
            m = rexDmgDown.Match(line);
            if (!m.Success)
                return;

            if (_selfId == m.Groups["pID"].Value)
                _encounter.SelfDmgDownCount++;
            _encounter.TotalDmgDownCount++;
        }
        else if (prefix == "33|") // Director
        {
            m = rexDirector.Match(line);
            if (!m.Success)
                return;

            string type = m.Groups["type"].Value;
            switch (type)
            {
                case "01": // 进本
                case "06": // 团灭重开
                    {
                        SetNewEncounter(startLineIndex: _lineIdx + 1);
                        break;
                    }
                case "02": // 多变迷宫胜利
                case "03": // 胜利
                case "11": // 团灭电网消失
                case "12": // 团灭电网消失（6.2 以前）
                    {
                        bool bWin = (type == "03");
                        DateTime endTime = ParseTime(line);
                        SaveCurrentEncounter(_lineIdx, bWin, endTime);
                        break;
                    }
            }
        }
        else if (_separateDoorBoss && prefix == "41") // 清 LB，检测门神转本体
        {
            m = rexClearLB.Match(line);
            if (!m.Success)
                return;
            DateTime endTime = ParseTime(line);
            SaveCurrentEncounter(_lineIdx, bWin: true, endTime);
        }
    }
}

public enum OutputEnum
{
    Unchange,
    Overwrite,
    Skip
}

public class OutputController
{
    public int Start;
    public int End;
    public OutputEnum OutputType;

    public const string PLACEHOLDER = "00|0|";

    public static List<OutputController> ParseEncounters(List<Encounter> encounters)
    {
        List<OutputController> controllers = new List<OutputController>();
        List<int> all01Indices = encounters.Select(e => e.Line01Idx).Distinct().ToList();

        int prevEnd = -1;

        foreach (int line01Index in all01Indices) // 瞎写算法 懒得优化了 反正这步不慢
        {
            // 上一段记录到这次 01 行之前：全部删除
            controllers.Add(new OutputController
            {
                Start = prevEnd + 1,
                End = line01Index - 1,
                OutputType = OutputEnum.Skip
            });

            // 01|... 40|... 02|... 三行：正常输出
            controllers.Add(new OutputController
            {
                Start = line01Index,
                End = line01Index + 2,
                OutputType = OutputEnum.Unchange
            });
            prevEnd = line01Index + 2;

            // 该 01 日志行下的全部所选战斗
            List<Encounter> currentZoneEncounters = encounters.Where(e => e.Line01Idx == line01Index).ToList();
            foreach (Encounter encounter in currentZoneEncounters)
            {
                // 上一段记录和这一场战斗之间的空白：占位覆盖
                controllers.Add(new OutputController
                {
                    Start = prevEnd + 1,
                    End = encounter.StartLineIdx - 1,
                    OutputType = OutputEnum.Overwrite
                });
                // 这一场战斗：正常输出
                controllers.Add(new OutputController
                {
                    Start = encounter.StartLineIdx,
                    End = encounter.EndLineIdx,
                    OutputType = OutputEnum.Unchange
                });
                prevEnd = encounter.EndLineIdx;
            }
        }
        return controllers;
    }

    public void Output(StreamReader reader, StreamWriter writer)
    {
        switch (OutputType)
        {
            case OutputEnum.Unchange:
                for (int i = Start; i <= End; i++)
                {
                    string line = reader.ReadLine();
                    if (/*chk???.Checked &&*/ line.StartsWith("29|")) // 清除实体标点数据
                        line = PLACEHOLDER;
                    writer.WriteLine(line);
                }
                break;

            case OutputEnum.Overwrite:
                for (int i = Start; i <= End; i++)
                {
                    reader.ReadLine();
                    writer.WriteLine(PLACEHOLDER);
                }
                break;

            case OutputEnum.Skip:
                for (int i = Start; i <= End; i++)
                {
                    reader.ReadLine();
                }
                break;
        }
    }

}

public class LogSeparatorForm : Form
{
    public List<Encounter> Encounters;

    private string _originalLogName;

    private OpenFileDialog openFileDialog;
    private SaveFileDialog saveFileDialog;

    TableLayoutPanel table, tableBottom;
    DataGridView dgvEncounters;
    Button btnOpen, btnSave;
    CheckBox chkSeparateDoorBoss, chkHideHeadMarkers, chkHideChat;

    List<int> dgvColumnBaseWidths;

    public LogSeparatorForm()
    {
        Text = ProgramInfo.Title;
        Font = new Font("微软雅黑", 12);
        AutoSize = false;
        StartPosition = FormStartPosition.CenterScreen;
        Width = Screen.PrimaryScreen.Bounds.Width * 3 / 5;
        Height = Screen.PrimaryScreen.Bounds.Height * 3 / 4;
        Resize += Form_Resize;

        openFileDialog = new OpenFileDialog();
        saveFileDialog = new SaveFileDialog();

        SuspendLayout();

        table = new TableLayoutPanel { 
            RowCount = 3, ColumnCount = 2,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
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
            Checked = true,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(20),
        };
        chkHideHeadMarkers = new CheckBox 
        { 
            Text = "隐藏实体标点", 
            Checked = true, 
            AutoSize = true, 
            Anchor = AnchorStyles.None,
            Margin = new Padding(20),
        };
        chkHideChat = new CheckBox 
        { 
            Text = "隐藏聊天记录", 
            Checked = true, 
            AutoSize = true, 
            Anchor = AnchorStyles.None,
            Margin = new Padding(20),
        };
        chkSeparateDoorBoss.CheckedChanged += chkSeparateDoorBoss_CheckedChanged;
        tableBottom = new TableLayoutPanel { 
            RowCount = 1, ColumnCount = 3, 
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill
        };
        for (int i = 0; i < 3; i++)
            tableBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f/3));
        tableBottom.Controls.Add(chkSeparateDoorBoss, 0, 0);
        tableBottom.Controls.Add(chkHideHeadMarkers, 1, 0);
        tableBottom.Controls.Add(chkHideChat, 2, 0);
        table.Controls.Add(tableBottom, 0, 2);
        table.SetColumnSpan(tableBottom, 2);

        ResumeLayout();
        SetDgvColWidth();
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
        openFileDialog.Filter = "日志文件 (*.log)|*.log";

        if (Directory.Exists(DefaultPath))
        {
            openFileDialog.InitialDirectory = DefaultPath;
        }
        if (openFileDialog.ShowDialog() == DialogResult.OK)
        {
            _originalLogName = openFileDialog.FileName;
            LogReader logReader = new LogReader(_originalLogName, chkSeparateDoorBoss.Checked);
            logReader.Read();
            Encounters = logReader.Encounters;
            dgvColumnBaseWidths = null;
            FillDgv();
        }
    }

    void chkSeparateDoorBoss_CheckedChanged(object sender, EventArgs e)
    {
        if (Encounters.Count > 0)
        {
            LogReader logReader = new LogReader(_originalLogName, chkSeparateDoorBoss.Checked);
            logReader.Read();
            Encounters = logReader.Encounters;
            FillDgv();
        }
    }

    void FillDgv()
    {
        dgvEncounters.SuspendLayout();
        try
        {
            dgvEncounters.Rows.Clear();

            int height;
            using (Graphics graphics = CreateGraphics())
            {
                height = (int)(graphics.MeasureString("啊", Font).Height * 1.05);
            }

            foreach (Encounter encounter in Encounters)
            {
                int rowIndex = dgvEncounters.Rows.Add();
                dgvEncounters.Rows[rowIndex].Height = height;
                // 选项框
                dgvEncounters.Rows[rowIndex].Cells[0].Value = false;
                // 序号
                dgvEncounters.Rows[rowIndex].Cells[1].Value = rowIndex + 1;
                // 时间
                dgvEncounters.Rows[rowIndex].Cells[2].Value = encounter.StartTime.ToString("MM/dd HH:mm:ss");
                dgvEncounters.Rows[rowIndex].Cells[3].Value = encounter.EndTime.ToString("HH:mm:ss");
                dgvEncounters.Rows[rowIndex].Cells[4].Value = encounter.GetDuration();
                // 胜利或团灭颜色
                Color color = encounter.Win ? Color.FromArgb(68, 204, 170) : Color.FromArgb(221, 102, 85);
                for (int i = 1; i <= 4; i++)
                    dgvEncounters.Rows[rowIndex].Cells[i].Style.ForeColor = color;
                // 区域
                dgvEncounters.Rows[rowIndex].Cells[5].Value = encounter.ZoneName;
                // 死亡/伤害降低计数
                dgvEncounters.Rows[rowIndex].Cells[6].Value = $"{encounter.SelfDeathCount}/{encounter.TotalDeathCount}";
                dgvEncounters.Rows[rowIndex].Cells[7].Value = $"{encounter.SelfDmgDownCount}/{encounter.TotalDmgDownCount}";
            }
            SetDgvColWidth();
        }
        finally { dgvEncounters.ResumeLayout(); }
    }

    void btnSave_Click(object sender, EventArgs e)
    {
        if ((Encounters?.Count ?? 0) == 0)
            return;

        List<Encounter> filteredEncounters = new List<Encounter>();

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

            SaveLogFile(newLogName, filteredEncounters);
        }
    }

    void SaveLogFile(string newLogName, List<Encounter> encounters)
    {
        var controllers = OutputController.ParseEncounters(encounters);
        try
        {
            using (StreamReader reader = new StreamReader(_originalLogName))
            using (StreamWriter writer = new StreamWriter(newLogName))
            {
                foreach (OutputController controller in controllers)
                {
                    controller.Output(reader, writer);
                }
            }

            // 新建线程调用剪贴板
            Thread staThread = new Thread(() => Clipboard.SetText(newLogName));
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();

            MessageBox.Show(
                "日志文件保存成功！\n\n" +
                "已将文件目录复制到剪贴板，\n" +
                "可在 FFLogs 上传器中选择文件界面中直接粘贴到 “文件名”",
                "", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "保存日志文件时出错: \n\n" + ex.ToString(),
                "", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        e.Handled = true;
    }

    void Form_Resize(object sender, EventArgs e)
    {
        SetDgvColWidth();
    }

    void SetDgvColWidth()
    {
        dgvEncounters.Width = Width - 20;
        string[] dgvMeasureStrings = new string[] {
            "-全选-",
            "-99-",
            "08/17 00:00:59",
            "00:00:59",
            "00'59\"",
            "塔塔露大锅歼灭战",
            "99/99",
            "99/99"
        };

        if (dgvColumnBaseWidths == null)
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
