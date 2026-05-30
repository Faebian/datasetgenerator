using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace DataSetGenerator
{
    public partial class Form1 : Form
    {
        private Chart chart;
        private ComboBox xColumnBox;
        private ComboBox yColumnBox;
        private Button openButton;
        private Button exportButton;
       
        private Label statusLabel;

        private Button cleardrawingButton;
        private Button clearmarkersButton;
        private Button clearButton;
        private Button saveButton;
        private Button loadButton;

        private Button inferButton;

        private NumericUpDown xMinBox;
        private NumericUpDown xMaxBox;
        private NumericUpDown yMinBox;
        private NumericUpDown yMaxBox;
        private bool updatingAxisBoxes;

        private DataTable table;
        private readonly List<PointF> drawnPoints = new List<PointF>();
        private bool isDrawing;

        private bool isDraggingXAxisMax;
        private const int AxisDragZonePx = 25;
        private double minXAxisMax = 1.0;

        private ComboBox modeBox;

        private readonly List<Series> markerSeries = new List<Series>();
        private Series draggingMarker;
        private const int MarkerHitTolerancePx = 8;

        public Form1()
        {
            Text = "TinyTCN Dataset Generator";
            WindowState = FormWindowState.Maximized;
            BackColor = Styles.isDarkMode ? Styles.BackColorDark : Styles.BackColorLight;
            ForeColor = Styles.isDarkMode ? Styles.ForeColorDark : Styles.ForeColorLight;
            BuildUi();
        }

        private void BuildUi()
        {
            
            Controls.Clear();

            var topPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 45,
                Padding = new Padding(5),
                FlowDirection = FlowDirection.LeftToRight
            };
            topPanel.DoubleClick += (_, __) => { BuildUi(); };

            openButton = new Button { Text = "Open CSV", Width = 100 };
            exportButton = new Button { Text = "Export Dataset", Width = 120 };
            cleardrawingButton = new Button { Text = "Clear Drawing", Width = 120 };

            xColumnBox = new ComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            yColumnBox = new ComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };

            statusLabel = new Label
            {
                Text = "Open a CSV file",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 8, 0, 0)
            };

            clearButton = new Button { Text = "Clear", Width = 80 };
            clearmarkersButton = new Button { Text = "Clear Markers", Width = 80 };
            saveButton = new Button { Text = "Save", Width = 80 };
            loadButton = new Button { Text = "Load", Width = 80 };
            inferButton = new Button { Text = "Infer", Width = 80 };

            topPanel.Controls.Add(openButton);
            topPanel.Controls.Add(new Label { Text = "X:", AutoSize = true, Padding = new Padding(10, 8, 0, 0) });
            topPanel.Controls.Add(xColumnBox);
            topPanel.Controls.Add(new Label { Text = "Y:", AutoSize = true, Padding = new Padding(10, 8, 0, 0) });
            topPanel.Controls.Add(yColumnBox);
            topPanel.Controls.Add(loadButton);
            topPanel.Controls.Add(saveButton);
            topPanel.Controls.Add(exportButton);
            topPanel.Controls.Add(clearButton);
            topPanel.Controls.Add(clearmarkersButton);
            topPanel.Controls.Add(cleardrawingButton);
            topPanel.Controls.Add(statusLabel);

            topPanel.Controls.Add(inferButton);

            modeBox = new ComboBox
            {
                Width = 100,
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            modeBox.Items.AddRange(new object[] { "Draw", "Mark" });
            modeBox.SelectedIndex = 1;

            topPanel.Controls.Add(new Label { Text = "Mode:", AutoSize = true, Padding = new Padding(10, 8, 0, 0) });
            topPanel.Controls.Add(modeBox);

            var rightPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 250,
                ColumnCount = 2,
                RowCount = 10

            };

            rightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            rightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

            xMinBox = MakeAxisBox();
            xMaxBox = MakeAxisBox();
            yMinBox = MakeAxisBox();
            yMaxBox = MakeAxisBox();

            rightPanel.Controls.Add(new Label { Text = "X min", Dock = DockStyle.Fill }, 0, 0);
            rightPanel.Controls.Add(xMinBox, 1, 0);

            rightPanel.Controls.Add(new Label { Text = "X max", Dock = DockStyle.Fill }, 0, 1);
            rightPanel.Controls.Add(xMaxBox, 1, 1);

            rightPanel.Controls.Add(new Label { Text = "Y min", Dock = DockStyle.Fill }, 0, 2);
            rightPanel.Controls.Add(yMinBox, 1, 2);

            rightPanel.Controls.Add(new Label { Text = "Y max", Dock = DockStyle.Fill }, 0, 3);
            rightPanel.Controls.Add(yMaxBox, 1, 3);

            xMinBox.ValueChanged += AxisBox_ValueChanged;
            xMaxBox.ValueChanged += AxisBox_ValueChanged;
            yMinBox.ValueChanged += AxisBox_ValueChanged;
            yMaxBox.ValueChanged += AxisBox_ValueChanged;

            chart = new Chart
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };
            chart.SuppressExceptions = true;

            var area = new ChartArea("Main");
            area.AxisX.Title = "Time / X";
            area.AxisY.Title = "Value";
            area.AxisX.MajorGrid.LineColor = Color.Gainsboro;
            area.AxisY.MajorGrid.LineColor = Color.Gainsboro;
            area.CursorX.IsUserEnabled = false;
            area.CursorX.IsUserSelectionEnabled = false;
            area.CursorY.IsUserEnabled = false;
            area.CursorY.IsUserSelectionEnabled = false;

            area.AxisX.ScaleView.Zoomable = false;
            area.AxisY.ScaleView.Zoomable = false;

            area.AxisX.Minimum = 0.0;
            area.AxisX.Maximum = 30.0;
            area.AxisY.Minimum = 0.0;
            area.AxisY.Maximum = 1.0;

            chart.ChartAreas.Add(area);

            chart.Series.Add(new Series("CSV")
            {
                ChartType = SeriesChartType.FastLine,
                BorderWidth = 2,
                Color = Color.LimeGreen
            });
            chart.Series[0].Points.AddXY(0.0, 0.0);

            chart.Series.Add(new Series("Drawn")
            {
                ChartType = SeriesChartType.FastLine,
                BorderWidth = 3,
                Color = Color.OrangeRed
            });

            Controls.Add(chart);
            Controls.Add(rightPanel); // allows topPanel to be full width
            Controls.Add(topPanel);


            openButton.Click += OpenButton_Click;
            saveButton.Click += SaveButton_Click;
            loadButton.Click += LoadButton_Click;
            exportButton.Click += ExportButton_Click;
            cleardrawingButton.Click += ClearDrawingButton_Click;
            clearmarkersButton.Click += ClearMarkersButton_Click;
            clearButton.Click += ClearButton_Click;
            inferButton.Click += async (_, __) => await InferButton_Click();

            xColumnBox.SelectedIndexChanged += ColumnBox_SelectedIndexChanged;
            yColumnBox.SelectedIndexChanged += ColumnBox_SelectedIndexChanged;

            chart.MouseDown += Chart_MouseDown;
            chart.MouseMove += Chart_MouseMove;
            chart.MouseUp += Chart_MouseUp;

            ApplyChartTheme(chart);
            UpdateAxisBoxesFromChart();
        }

        private NumericUpDown MakeAxisBox()
        {
            return new NumericUpDown
            {
                Dock = DockStyle.Fill,
                DecimalPlaces = 3,
                Minimum = -1000000,
                Maximum = 1000000,
                Increment = 0.1M
            };
        }

        private void ApplyChartTheme(Chart c)
        {
            bool dark = Styles.isDarkMode;

            Color back = dark ? Styles.BackColorDark : Styles.BackColorLight;
            Color fore = dark ? Styles.ForeColorDark : Styles.ForeColorLight;
            Color grid = dark ? Color.FromArgb(80, 80, 80) : Color.Gainsboro;
            Color plotBack = dark ? Styles.ScriptBackColorDark : Color.White;

            c.BackColor = back;
            c.ForeColor = fore;

            foreach (ChartArea area in c.ChartAreas)
            {
                area.BackColor = plotBack;

                area.AxisX.LineColor = fore;
                area.AxisY.LineColor = fore;

                area.AxisX.LabelStyle.ForeColor = fore;
                area.AxisY.LabelStyle.ForeColor = fore;

                area.AxisX.TitleForeColor = fore;
                area.AxisY.TitleForeColor = fore;

                area.AxisX.MajorGrid.LineColor = grid;
                area.AxisY.MajorGrid.LineColor = grid;

                area.AxisX.MinorGrid.LineColor = grid;
                area.AxisY.MinorGrid.LineColor = grid;

                area.CursorX.LineColor = fore;
                area.CursorY.LineColor = fore;
            }

            foreach (Legend legend in c.Legends)
            {
                legend.BackColor = back;
                legend.ForeColor = fore;
            }
        }

        private void AxisBox_ValueChanged(object sender, EventArgs e)
        {
            if (updatingAxisBoxes)
                return;

            var area = chart.ChartAreas[0];

            double xMin = (double)xMinBox.Value;
            double xMax = (double)xMaxBox.Value;
            double yMin = (double)yMinBox.Value;
            double yMax = (double)yMaxBox.Value;

            if (xMax <= xMin) xMax = xMin + 1;
            if (yMax <= yMin) yMax = yMin + 1;

            area.AxisX.Minimum = xMin;
            area.AxisX.Maximum = xMax;
            area.AxisY.Minimum = yMin;
            area.AxisY.Maximum = yMax;

            chart.Invalidate();
        }

        private void OpenButton_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.InitialDirectory = Path.Combine(Application.StartupPath, "CSV");
                ofd.Filter = "CSV files|*.csv;*.txt|All files|*.*";

                if (ofd.ShowDialog() != DialogResult.OK)
                    return;

                table = LoadCsv(ofd.FileName);

                xColumnBox.Items.Clear();
                yColumnBox.Items.Clear();

                foreach (DataColumn col in table.Columns)
                {
                    xColumnBox.Items.Add(col.ColumnName);
                    yColumnBox.Items.Add(col.ColumnName);
                }

                if (xColumnBox.Items.Count > 0) xColumnBox.SelectedIndex = 0;
                if (yColumnBox.Items.Count > 1) yColumnBox.SelectedIndex = 1;

                statusLabel.Text = Path.GetFileName(ofd.FileName);
                PlotSelectedColumns();
            }
        }

        private DataTable LoadCsv(string path)
        {
            var dt = new DataTable();
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0) return dt;

            char delimiter = DetectDelimiter(lines[0]);

            string[] headers = lines[0].Split(delimiter);
            for (int i = 0; i < headers.Length; i++)
                dt.Columns.Add(string.IsNullOrWhiteSpace(headers[i]) ? "Column" + i : headers[i].Trim());

            for (int r = 1; r < lines.Length; r++)
            {
                if (string.IsNullOrWhiteSpace(lines[r])) continue;

                string[] parts = lines[r].Split(delimiter);
                var row = dt.NewRow();

                for (int c = 0; c < dt.Columns.Count && c < parts.Length; c++)
                    row[c] = parts[c].Trim();

                dt.Rows.Add(row);
            }

            return dt;
        }

        private char DetectDelimiter(string line)
        {
            char[] candidates = { ',', ';', '\t' };
            return candidates
                .OrderByDescending(c => line.Count(ch => ch == c))
                .First();
        }

        private void ColumnBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            PlotSelectedColumns();
        }

        private void PlotSelectedColumns()
        {
            if (table == null || xColumnBox.SelectedItem == null || yColumnBox.SelectedItem == null)
                return;

            var csvSeries = chart.Series["CSV"];
            csvSeries.Points.Clear();

            drawnPoints.Clear();
            chart.Series["Drawn"].Points.Clear();

            string xCol = xColumnBox.SelectedItem.ToString();
            string yCol = yColumnBox.SelectedItem.ToString();

            var points = new List<PointF>();

            foreach (DataRow row in table.Rows)
            {
                double x, y;

                if (double.TryParse(Convert.ToString(row[xCol]), NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                    double.TryParse(Convert.ToString(row[yCol]), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                {
                    points.Add(new PointF((float)x, (float)y));
                }
            }

            foreach (var p in points.OrderBy(p => p.X))
                csvSeries.Points.AddXY(p.X, p.Y);

            AutoScaleAxes();

            statusLabel.Text = "Loaded " + points.Count + " points";
        }

        private void AutoScaleAxes()
        {
            var pts = chart.Series["CSV"].Points;
            if (pts.Count == 0) return;

            double maxX = pts.Max(p => p.XValue);
            double minY = pts.Min(p => p.YValues[0]);
            double maxY = pts.Max(p => p.YValues[0]);

            if (maxX <= 0) maxX = 1;
            if (minY == maxY) maxY = minY + 1;

            minXAxisMax = maxX;

            var area = chart.ChartAreas[0];
            area.AxisX.Minimum = 0;
            area.AxisX.Maximum = maxX;
            area.AxisY.Minimum = 0;
            area.AxisY.Maximum = maxY;

            UpdateAxisBoxesFromChart();
        }

        private void UpdateAxisBoxesFromChart()
        {
            if (chart == null || chart.ChartAreas.Count == 0)
                return;

            updatingAxisBoxes = true;

            var area = chart.ChartAreas[0];

            xMinBox.Value = ClampDecimal((decimal)area.AxisX.Minimum, xMinBox);
            xMaxBox.Value = ClampDecimal((decimal)area.AxisX.Maximum, xMaxBox);
            yMinBox.Value = ClampDecimal((decimal)area.AxisY.Minimum, yMinBox);
            yMaxBox.Value = ClampDecimal((decimal)area.AxisY.Maximum, yMaxBox);

            updatingAxisBoxes = false;
        }

        private decimal ClampDecimal(decimal value, NumericUpDown box)
        {
            return Math.Max(box.Minimum, Math.Min(box.Maximum, value));
        }

        private void Chart_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && IsNearRightPlotEdge(e.X))
            {
                isDraggingXAxisMax = true;
                chart.Cursor = Cursors.SizeWE;
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                RenameMarkerAt(e.X);
                return;
            }

            if (e.Button != MouseButtons.Left)
                return;

            if (modeBox.Text == "Mark")
            {
                draggingMarker = GetMarkerNearMouse(e.X);

                if (draggingMarker == null)
                    draggingMarker = AddMarkerAtPixel(e.X);

                chart.Cursor = Cursors.SizeWE;
                return;
            }

            isDrawing = true;
            drawnPoints.Clear();
            chart.Series["Drawn"].Points.Clear();

            AddDrawnPoint(e.X, e.Y);
        }

        private void Chart_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDraggingXAxisMax)
            {
                DragXAxisMaximum(e.X);
                return;
            }

            if (draggingMarker != null)
            {
                MoveMarkerToPixel(draggingMarker, e.X);
                return;
            }

            if (IsNearRightPlotEdge(e.X))
                chart.Cursor = Cursors.SizeWE;
            else if (!isDrawing)
                chart.Cursor = Cursors.Default;

            if (!isDrawing) return;

            AddDrawnPoint(e.X, e.Y);
        }

        private void Chart_MouseUp(object sender, MouseEventArgs e)
        {
            if (isDraggingXAxisMax)
            {
                isDraggingXAxisMax = false;
                chart.Cursor = Cursors.Default;
                statusLabel.Text = "X max: " + chart.ChartAreas[0].AxisX.Maximum.ToString("0.###");
                UpdateAxisBoxesFromChart();
                return;
            }

            if (draggingMarker != null)
            {
                statusLabel.Text = draggingMarker.Name + ": " + draggingMarker.Points[0].XValue.ToString("0.###");
                draggingMarker = null;
                chart.Cursor = Cursors.Default;
                return;
            }

            isDrawing = false;
            statusLabel.Text = "Drawn points: " + drawnPoints.Count;
        }

        private Series AddMarkerAtPixel(int pixelX)
        {
            var area = chart.ChartAreas[0];

            double x;
            try
            {
                x = area.AxisX.PixelPositionToValue(pixelX);
            }
            catch
            {
                return null;
            }

            if (x < area.AxisX.Minimum || x > area.AxisX.Maximum)
                return null;

            string name = "t" + markerSeries.Count;

            var s = new Series(name)
            {
                ChartType = SeriesChartType.FastLine,
                XValueType = ChartValueType.Double,
                BorderWidth = 2,
                Color = Color.Yellow,
                BorderDashStyle = ChartDashStyle.Dash,
                IsXValueIndexed = false
            };

            chart.Series.Add(s);
            markerSeries.Add(s);

            SetMarkerX(s, x);

            statusLabel.Text = "Added marker " + name + " at " + x.ToString("0.###");

            return s;
        }

        private void SetMarkerX(Series s, double x)
        {
            var area = chart.ChartAreas[0];

            s.Points.Clear();
            s.Points.AddXY(x, area.AxisY.Minimum);
            s.Points.AddXY(x, area.AxisY.Maximum);

            chart.Invalidate();
        }

        private void MoveMarkerToPixel(Series s, int pixelX)
        {
            var area = chart.ChartAreas[0];

            try
            {
                double x = area.AxisX.PixelPositionToValue(pixelX);
                x = Math.Max(area.AxisX.Minimum, Math.Min(area.AxisX.Maximum, x));

                SetMarkerX(s, x);
            }
            catch
            {
            }
        }

        private Series GetMarkerNearMouse(int mouseX)
        {
            var area = chart.ChartAreas[0];

            foreach (var s in markerSeries.ToList())
            {
                if (s == null || !chart.Series.Contains(s) || s.Points.Count == 0)
                    continue;

                double x = s.Points[0].XValue;
                double px = area.AxisX.ValueToPixelPosition(x);

                if (Math.Abs(mouseX - px) <= MarkerHitTolerancePx)
                    return s;
            }

            return null;
        }

        private void RenameMarkerAt(int mouseX)
        {
            var marker = GetMarkerNearMouse(mouseX);
            if (marker == null)
                return;

            string oldName = marker.Name;

            using (var input = new RenameMarkerForm(marker.Name))
            {
                if (input.ShowDialog() != DialogResult.OK)
                    return;

                if (input.DeleteRequested)
                {
                    chart.Series.Remove(marker);
                    markerSeries.Remove(marker);

                    statusLabel.Text = "Deleted marker";
                    return;
                }

                string newName = input.MarkerName;

                if (string.IsNullOrWhiteSpace(newName))
                    return;

                if (newName != marker.Name &&
                    chart.Series.FindByName(newName) != null)
                {
                    MessageBox.Show("A marker with that name already exists.");
                    return;
                }

                marker.Name = newName;

                statusLabel.Text = "Renamed marker to " + newName;
            }
        }

        private bool IsNearRightPlotEdge(int mouseX)
        {
            try
            {
                var area = chart.ChartAreas[0];
                double rightPx = area.AxisX.ValueToPixelPosition(area.AxisX.Maximum);
                return Math.Abs(mouseX - rightPx) <= AxisDragZonePx;
            }
            catch
            {
                return false;
            }
        }

        private void DragXAxisMaximum(int mouseX)
        {
            var area = chart.ChartAreas[0];

            try
            {
                double newMax = area.AxisX.PixelPositionToValue(mouseX);

                if (newMax < minXAxisMax)
                    newMax = minXAxisMax;

                if (newMax < 1)
                    newMax = 1;

                area.AxisX.Minimum = 0;
                area.AxisX.Maximum = newMax;

                chart.Invalidate();
            }
            catch
            {
                // Ignore bad mouse positions outside plot area.
            }
        }

        private void AddDrawnPoint(int pixelX, int pixelY)
        {
            var area = chart.ChartAreas[0];

            try
            {
                double x = area.AxisX.PixelPositionToValue(pixelX);
                double y = area.AxisY.PixelPositionToValue(pixelY);

                if (x < area.AxisX.Minimum || x > area.AxisX.Maximum) return;
                if (y < area.AxisY.Minimum || y > area.AxisY.Maximum) return;

                if (drawnPoints.Count > 0)
                {
                    var last = drawnPoints[drawnPoints.Count - 1];
                    if (Math.Abs(last.X - x) < 1e-9) return;
                }

                drawnPoints.Add(new PointF((float)x, (float)y));

                var s = chart.Series["Drawn"];
                s.Points.Clear();

                foreach (var p in drawnPoints.OrderBy(p => p.X))
                    s.Points.AddXY(p.X, p.Y);
            }
            catch
            {
                // Ignore mouse positions outside the plotting area.
            }
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            string root = Path.Combine(Application.StartupPath, "Saves");
            Directory.CreateDirectory(root);

            string saveName = "save_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outDir = Path.Combine(root, saveName);
            Directory.CreateDirectory(outDir);

            SaveCsvSeries(Path.Combine(outDir, "csv_series.csv"));
            SaveDrawnSeries(Path.Combine(outDir, "drawn_series.csv"));
            SaveMarkers(Path.Combine(outDir, "markers.csv"));
            SaveSessionMeta(Path.Combine(outDir, "session.txt"));

            statusLabel.Text = "Saved annotation: " + outDir;
        }

        private double GetMarkerX(string name)
        {
            var marker = markerSeries.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

            if (marker == null || marker.Points.Count == 0)
                return double.NaN;

            return marker.Points[0].XValue;
        }

        private int[] BuildRegions(double[] times, double t0, double t1, double t2, double t3)
        {
            var regions = new int[times.Length];

            for (int i = 0; i < times.Length; i++)
            {
                double t = times[i];

                if (double.IsNaN(t0) || t < t0)
                    regions[i] = 1;
                else if (double.IsNaN(t1) || t < t1)
                    regions[i] = 2;
                else if (double.IsNaN(t2) || t < t2)
                    regions[i] = 3;
                else if (double.IsNaN(t3) || t < t3)
                    regions[i] = 4;
                else
                    regions[i] = 5;
            }

            return regions;
        }

        #region Save
        private void SaveCsvSeries(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("x,y");

            foreach (var p in chart.Series["CSV"].Points)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F6},{1:F6}",
                    p.XValue,
                    p.YValues[0]));
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private void SaveDrawnSeries(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("x,y");

            foreach (var p in drawnPoints.OrderBy(p => p.X))
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F6},{1:F6}",
                    p.X,
                    p.Y));
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private void SaveMarkers(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("name,x");

            foreach (var marker in markerSeries)
            {
                if (marker.Points.Count == 0)
                    continue;

                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1:F6}",
                    marker.Name,
                    marker.Points[0].XValue));
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private void SaveSessionMeta(string path)
        {
            var area = chart.ChartAreas[0];

            var sb = new StringBuilder();
            sb.AppendLine("Dataset Generator Save");
            sb.AppendLine("Created=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("XMin=" + area.AxisX.Minimum.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("XMax=" + area.AxisX.Maximum.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("YMin=" + area.AxisY.Minimum.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("YMax=" + area.AxisY.Maximum.ToString(CultureInfo.InvariantCulture));

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        #endregion

        #region Load

        private void LoadButton_Click(object sender, EventArgs e)
        {
            string root = Path.Combine(Application.StartupPath, "Saves");

            if (!Directory.Exists(root))
            {
                MessageBox.Show("No saves folder found.");
                return;
            }

            using (var fbd = new FolderBrowserDialog())
            {
                fbd.SelectedPath = root;

                if (fbd.ShowDialog() != DialogResult.OK)
                    return;

                LoadSave(fbd.SelectedPath);
            }
        }
        private void LoadSave(string folder)
        {
            try
            {
                drawnPoints.Clear();

                chart.Series["CSV"].Points.Clear();
                chart.Series["Drawn"].Points.Clear();

                foreach (var marker in markerSeries.ToList())
                    chart.Series.Remove(marker);

                markerSeries.Clear();

                LoadCsvSeries(Path.Combine(folder, "csv_series.csv"));
                LoadDrawnSeries(Path.Combine(folder, "drawn_series.csv"));
                LoadMarkers(Path.Combine(folder, "markers.csv"));
                LoadSessionMeta(Path.Combine(folder, "session.txt"));

                chart.Invalidate();

                statusLabel.Text = "Loaded save: " + Path.GetFileName(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Load Failed");
            }
        }

        private void LoadCsvSeries(string path)
        {
            if (!File.Exists(path))
                return;

            var series = chart.Series["CSV"];
            series.Points.Clear();

            var lines = File.ReadAllLines(path);

            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(',');

                if (parts.Length < 2)
                    continue;

                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                    continue;

                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    continue;

                series.Points.AddXY(x, y);
            }
        }

        private void LoadDrawnSeries(string path)
        {
            if (!File.Exists(path))
                return;

            var series = chart.Series["Drawn"];
            series.Points.Clear();

            var lines = File.ReadAllLines(path);

            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(',');

                if (parts.Length < 2)
                    continue;

                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                    continue;

                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    continue;

                drawnPoints.Add(new PointF((float)x, (float)y));
            }

            foreach (var p in drawnPoints.OrderBy(p => p.X))
                series.Points.AddXY(p.X, p.Y);
        }

        private void LoadMarkers(string path)
        {
            if (!File.Exists(path))
                return;

            var lines = File.ReadAllLines(path);

            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(',');

                if (parts.Length < 2)
                    continue;

                string name = parts[0];

                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                    continue;

                AddMarker(name, x);
            }
        }

        private void AddMarker(string name, double x)
        {
            var s = new Series(name)
            {
                ChartType = SeriesChartType.FastLine,
                XValueType = ChartValueType.Double,
                BorderWidth = 2,
                BorderDashStyle = ChartDashStyle.Dash,
                Color = Color.Yellow,
                IsXValueIndexed = false
            };

            chart.Series.Add(s);
            markerSeries.Add(s);

            SetMarkerX(s, x);
        }

        private void LoadSessionMeta(string path)
        {
            if (!File.Exists(path))
                return;

            var area = chart.ChartAreas[0];

            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("XMin="))
                    area.AxisX.Minimum = ParseDouble(line);

                else if (line.StartsWith("XMax="))
                    area.AxisX.Maximum = ParseDouble(line);

                else if (line.StartsWith("YMin="))
                    area.AxisY.Minimum = ParseDouble(line);

                else if (line.StartsWith("YMax="))
                    area.AxisY.Maximum = ParseDouble(line);
            }

            UpdateAxisBoxesFromChart();
        }

        private double ParseDouble(string line)
        {
            int idx = line.IndexOf('=');

            if (idx < 0)
                return 0;

            double.TryParse(
                line.Substring(idx + 1),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value);

            return value;
        }

        #endregion

        #region Clear

        private void ClearDrawingButton_Click(object sender, EventArgs e)
        {
            ClearDrawing();
        }

        private void ClearMarkersButton_Click(object sender, EventArgs e)
        {
            ClearMarkers();
        }

        private void ClearButton_Click(object sender, EventArgs e)
        {
            ClearAll();
        }

        private void ClearDrawing()
        {
            drawnPoints.Clear();
            chart.Series["Drawn"].Points.Clear();
        }

        private void ClearMarkers()
        {
            foreach (var marker in markerSeries.ToList())
                chart.Series.Remove(marker);

            markerSeries.Clear();
        }

        private void ClearCsv()
        {
            chart.Series["CSV"].Points.Clear();
        }

        private void ClearAll()
        {
            ClearDrawing();
            ClearMarkers();
            ClearCsv();

            var area = chart.ChartAreas[0];

            area.AxisX.Minimum = 0;
            area.AxisX.Maximum = 30;

            area.AxisY.Minimum = 0;
            area.AxisY.Maximum = 1;

            UpdateAxisBoxesFromChart();
        }

        #endregion

        #region Export

        private void ExportButton_Click(object sender, EventArgs e)
        {
            if (chart.Series["CSV"].Points.Count == 0)
            {
                MessageBox.Show("No CSV series to export.");
                return;
            }

            double t0 = GetMarkerX("t0");
            double t1 = GetMarkerX("t1");
            double t2 = GetMarkerX("t2");
            double t3 = GetMarkerX("t3");

            string root = Path.Combine(Application.StartupPath, "DataSets");
            Directory.CreateDirectory(root);

            string datasetName = "dataset_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outDir = Path.Combine(root, datasetName);
            Directory.CreateDirectory(outDir);

            var ordered = chart.Series["CSV"].Points
             .Select(p => new { T = p.XValue, I = p.YValues[0] })
             .OrderBy(p => p.T)
             .ToList();

            double[] times = ordered.Select(p => p.T).ToArray();
            double[] intensity = ordered.Select(p => p.I).ToArray();

            double[] smooth = AnalysisTools.Smooth.SmoothEmaZeroPhaseTime(times, intensity, 1.0);
            double[] slope = RollingSlope(times, smooth, windowSamples: 11);
            slope = MovingAverage(slope, span: 7);

            // signals.csv
            var sb = new StringBuilder();
            sb.Clear();
            sb.AppendLine("t,I_fixed,I_smooth,slope,dx,dy");

            for (int i = 0; i < ordered.Count; i++)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F6},{1:F6},{2:F6},{3:F6},0,0",
                    times[i],
                    intensity[i],
                    smooth[i],
                    slope[i]));
            }

            File.WriteAllText(Path.Combine(outDir, "signals.csv"), sb.ToString(), Encoding.UTF8);

            // heat 
            double[] h0 = BuildHeatmap(times, t0, sigma: 0.75);
            double[] h1 = BuildHeatmap(times, t1, sigma: 0.75);
            double[] h2 = BuildHeatmap(times, t2, sigma: 0.75);
            double[] h3 = BuildHeatmap(times, t3, sigma: 0.75);

            sb.Clear();
            sb.AppendLine("t,heat_t0,heat_t1,heat_t2,heat_t3");

            for (int i = 0; i < times.Length; i++)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F6},{1:F6},{2:F6},{3:F6},{4:F6}",
                    times[i],
                    h0[i],
                    h1[i],
                    h2[i],
                    h3[i]));
            }

            File.WriteAllText(Path.Combine(outDir, "labels_heat.csv"), sb.ToString(), Encoding.UTF8);

            // labels_regions.csv
            var regions = BuildRegions(times, t0, t1, t2, t3);

            sb.Clear();
            sb.AppendLine("t,region");

            for (int i = 0; i < times.Length; i++)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F6},{1}",
                    times[i],
                    regions[i]));
            }

            File.WriteAllText(Path.Combine(outDir, "labels_regions.csv"), sb.ToString(), Encoding.UTF8);

            // meta.json
            string metaJson =
        $@"{{
  ""dataset_id"": ""{datasetName}"",
  ""sample_count"": {ordered.Count},
  ""t0"": {t0.ToString(CultureInfo.InvariantCulture)},
  ""t1"": {t1.ToString(CultureInfo.InvariantCulture)},
  ""t2"": {t2.ToString(CultureInfo.InvariantCulture)},
  ""t3"": {t3.ToString(CultureInfo.InvariantCulture)}
}}";

            File.WriteAllText(Path.Combine(outDir, "meta.json"), metaJson, Encoding.UTF8);

            statusLabel.Text = "Exported dataset: " + outDir;
        }

        private double[] BuildHeatmap(double[] times, double center, double sigma)
        {
            var h = new double[times.Length];

            if (double.IsNaN(center) || sigma <= 0)
                return h;

            double denom = 2.0 * sigma * sigma;

            for (int i = 0; i < times.Length; i++)
            {
                double dt = times[i] - center;
                h[i] = Math.Exp(-(dt * dt) / denom);
            }

            return h;
        }

        private double[] RollingSlope(double[] x, double[] y, int windowSamples)
        {
            var slope = new double[y.Length];
            int half = Math.Max(1, windowSamples / 2);

            for (int i = 0; i < y.Length; i++)
            {
                int a = Math.Max(0, i - half);
                int b = Math.Min(y.Length - 1, i + half);

                int n = b - a + 1;
                if (n < 2)
                {
                    slope[i] = 0;
                    continue;
                }

                double sx = 0, sy = 0, sxx = 0, sxy = 0;

                for (int j = a; j <= b; j++)
                {
                    sx += x[j];
                    sy += y[j];
                    sxx += x[j] * x[j];
                    sxy += x[j] * y[j];
                }

                double denom = n * sxx - sx * sx;
                slope[i] = Math.Abs(denom) < 1e-12 ? 0 : (n * sxy - sx * sy) / denom;
            }

            return slope;
        }

        private double[] MovingAverage(double[] values, int span)
        {
            var output = new double[values.Length];
            int half = Math.Max(1, span / 2);

            for (int i = 0; i < values.Length; i++)
            {
                int a = Math.Max(0, i - half);
                int b = Math.Min(values.Length - 1, i + half);

                double sum = 0;
                int count = 0;

                for (int j = a; j <= b; j++)
                {
                    sum += values[j];
                    count++;
                }

                output[i] = count > 0 ? sum / count : values[i];
            }

            return output;
        }

        #endregion

        #region Inference

        private async Task InferButton_Click()
        {
            if (chart.Series["CSV"].Points.Count == 0)
            {
                MessageBox.Show("No CSV series loaded.");
                return;
            }

            try
            {
                statusLabel.Text = "Running inference...";

                string csv = BuildSignalsCsvFromChart();

                using (var client = new HttpClient())
                using (var content = new MultipartFormDataContent())
                {
                    content.Add(new StringContent("DatasetGenerator"), "name");
                    content.Add(new StringContent(csv), "csv");

                    var response = await client.PostAsync("http://127.0.0.1:11880/infer_csv", content);
                    string json = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        MessageBox.Show(json, "Inference failed");
                        statusLabel.Text = "Inference failed";
                        return;
                    }

                    ApplyInferenceMarkers(json);
                    statusLabel.Text = "Inference complete";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Inference error");
                statusLabel.Text = "Inference error";
            }
        }

        private string BuildSignalsCsvFromChart()
        {
            var ordered = chart.Series["CSV"].Points
                .Select(p => new { T = p.XValue, I = p.YValues[0] })
                .OrderBy(p => p.T)
                .ToList();

            double[] times = ordered.Select(p => p.T).ToArray();
            double[] intensity = ordered.Select(p => p.I).ToArray();

            double[] smooth = AnalysisTools.Smooth.SmoothEmaZeroPhaseTime(times, intensity, 1.0);
            double[] slope = RollingSlope(times, smooth, windowSamples: 11);
            slope = MovingAverage(slope, span: 7);

            var sb = new StringBuilder();
            sb.AppendLine("t,I_fixed,I_smooth,slope,dx,dy");

            for (int i = 0; i < ordered.Count; i++)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F6},{1:F6},{2:F6},{3:F6},0,0",
                    times[i],
                    intensity[i],
                    smooth[i],
                    slope[i]));
            }

            return sb.ToString();
        }

        private void ApplyInferenceMarkers(string json)
        {
            AddOrUpdateMarker("t0", ExtractJsonDouble(json, "t0"));
            AddOrUpdateMarker("t1", ExtractJsonDouble(json, "t1"));
            AddOrUpdateMarker("t2", ExtractJsonDouble(json, "t2"));
            AddOrUpdateMarker("t3", ExtractJsonDouble(json, "t3"));

            chart.Invalidate();
        }

        private void AddOrUpdateMarker(string name, double x)
        {
            if (double.IsNaN(x))
                return;

            var marker = markerSeries.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

            if (marker == null)
            {
                AddMarker(name, x);
            }
            else
            {
                SetMarkerX(marker, x);
            }
        }

        private double ExtractJsonDouble(string json, string key)
        {
            var match = Regex.Match(
                json,
                $"\"{Regex.Escape(key)}\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)");

            if (!match.Success)
                return double.NaN;

            if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return value;

            return double.NaN;
        }

        #endregion

    }
}