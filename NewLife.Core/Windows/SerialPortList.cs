﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows.Forms;
using NewLife.Log;
using NewLife.Net;
using NewLife.Threading;

namespace NewLife.Windows
{
    /// <summary>串口列表控件</summary>
    [DefaultEvent("ReceivedString")]
    public partial class SerialPortList : UserControl
    {
        #region 属性
        private SerialTransport _Port;
        /// <summary>端口</summary>
        public SerialTransport Port { get { return _Port; } set { _Port = value; } }

        /// <summary>选择的端口</summary>
        public String SelectedPort { get { return cbName.SelectedItem + ""; } set { } }

        private Int32 _BytesOfReceived;
        /// <summary>收到的字节数</summary>
        public Int32 BytesOfReceived { get { return _BytesOfReceived; } set { _BytesOfReceived = value; } }

        private Int32 _BytesOfSent;
        /// <summary>发送的字节数</summary>
        public Int32 BytesOfSent { get { return _BytesOfSent; } set { _BytesOfSent = value; } }
        #endregion

        #region 构造
        /// <summary></summary>
        public SerialPortList()
        {
            InitializeComponent();
        }

        TimerX _timer;
        private void SerialPortList_Load(object sender, EventArgs e)
        {
            LoadInfo();

            // 挂载定时器
            _timer = new TimerX(OnCheck, null, 300, 300);

            var frm = Parent as Form;
            if (frm != null)
            {
                frm.FormClosing += frm_FormClosing;
            }
        }

        void frm_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveInfo();

            _timer.Dispose();
            _timer = null;

            if (Port != null) Port.Close();
        }
        #endregion

        #region 加载保存信息
        /// <summary>加载配置信息</summary>
        public void LoadInfo()
        {
            ShowPorts();

            BindMenu(mi数据位, On数据位Click, new Int32[] { 5, 6, 7, 8 });
            BindMenu(mi停止位, On停止位Click, Enum.GetValues(typeof(StopBits)));
            BindMenu(mi校验, On校验Click, Enum.GetValues(typeof(Parity)));

            //var arr = new Int32[] { 1200, 2400, 4800, 9600, 14400, 19200, 38400, 56000, 57600, 115200, 128000, 194000, 256000, 512000, 1024000, 2048000 };
            var bs = new List<Int32>(new Int32[] { 1200, 2400, 4800, 9600, 14400, 19200, 38400, 56000, 57600, 115200, 128000, 194000, 256000, 512000, 1024000, 2048000 });

            var cfg = SerialPortConfig.Current;
            if (cfg.BaudRate > 0 && !bs.Contains(cfg.BaudRate)) bs.Add(cfg.BaudRate);
            cbBaundrate.DataSource = bs;

            cbName.SelectedItem = cfg.PortName;
            cbBaundrate.SelectedItem = cfg.BaudRate;
            SetMenuItem(mi数据位, cfg.DataBits);
            SetMenuItem(mi停止位, cfg.StopBits);
            SetMenuItem(mi校验, cfg.Parity);

            //cbEncoding.DataSource = new String[] { Encoding.Default.WebName, Encoding.ASCII.WebName, Encoding.UTF8.WebName };
            // 添加编码子菜单
            var encs = new Encoding[] { Encoding.Default, Encoding.ASCII, Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 };
            var list = new List<Encoding>(encs);
            // 暂时不用这么多编码
            //list.AddRange(Encoding.GetEncodings().Select(e => e.GetEncoding()).Where(e => !encs.Contains(e)));
            var k = 0;
            foreach (var item in list)
            {
                if (k++ == encs.Length)
                {
                    var sep = new ToolStripSeparator();
                    mi字符串编码.DropDownItems.Add(sep);
                }
                var ti = mi字符串编码.DropDownItems.Add(item.EncodingName) as ToolStripMenuItem;
                ti.Name = item.WebName;
                ti.Tag = item;
                ti.Checked = item.WebName.EqualIgnoreCase(cfg.WebEncoding);
                ti.Click += On编码Click;
            }

            miHEX编码接收.Checked = cfg.HexShow;
            mi字符串编码.Checked = !cfg.HexShow;
            miHex不换行.Checked = !cfg.HexNewLine;
            miHex自动换行.Checked = cfg.HexNewLine;
            miHEX编码发送.Checked = cfg.HexSend;
        }

        /// <summary>保存配置信息</summary>
        public void SaveInfo()
        {
            try
            {
                var cfg = SerialPortConfig.Current;
                cfg.PortName = cbName.SelectedItem + "";
                if (cbBaundrate.SelectedItem != null)
                    cfg.BaudRate = (Int32)cbBaundrate.SelectedItem;
                else
                    cfg.BaudRate = cbBaundrate.Text.ToInt();
                //cfg.DataBits = (Int32)cbDataBit.SelectedItem;
                //cfg.StopBits = (StopBits)cbStopBit.SelectedItem;
                //cfg.Parity = (Parity)cbParity.SelectedItem;
                //cfg.Encoding = (Encoding)cbEncoding.SelectedItem;
                //cfg.WebEncoding = cbEncoding.SelectedItem + "";

                //cfg.HexSend = chkHEXSend.Checked;
                //cfg.HexShow = chkHEXShow.Checked;

                cfg.Save();
            }
            catch (Exception ex)
            {
                XTrace.WriteException(ex);
            }
        }

        String _ports = null;
        DateTime _nextport = DateTime.MinValue;
        /// <summary>下拉框显示串口</summary>
        public void ShowPorts()
        {
            if (_nextport > DateTime.Now) return;
            _nextport = DateTime.Now.AddSeconds(1);

            var ps = SerialTransport.GetPortNames();
            var str = String.Join(",", ps);
            // 如果端口有所改变，则重新绑定
            if (_ports != str)
            {
                _ports = str;

                this.Invoke(() =>
                {
                    var old = cbName.SelectedItem + "";
                    cbName.DataSource = ps;
                    if (!String.IsNullOrEmpty(old) && Array.IndexOf(ps, old) >= 0) cbName.SelectedItem = old;
                });
            }
        }
        #endregion

        #region 菜单设置
        /// <summary>右键菜单</summary>
        public ContextMenuStrip Menu { get { return contextMenuStrip1; } }

        void On数据位Click(object sender, EventArgs e)
        {
            SelectMenu(sender);

            var mi = sender as ToolStripMenuItem;
            var cfg = SerialPortConfig.Current;
            cfg.DataBits = (Int32)mi.Tag;
        }

        void On停止位Click(object sender, EventArgs e)
        {
            SelectMenu(sender);

            var mi = sender as ToolStripMenuItem;
            var cfg = SerialPortConfig.Current;
            cfg.StopBits = (StopBits)mi.Tag;
        }

        void On校验Click(object sender, EventArgs e)
        {
            SelectMenu(sender);

            var mi = sender as ToolStripMenuItem;
            var cfg = SerialPortConfig.Current;
            cfg.Parity = (Parity)mi.Tag;
        }

        void On编码Click(object sender, EventArgs e)
        {
            // 不要选其它
            var mi = sender as ToolStripMenuItem;
            if (mi == null) return;
            //foreach (ToolStripMenuItem item in (mi.OwnerItem as ToolStripMenuItem).DropDownItems)
            //{
            //    item.Checked = item == mi;
            //}
            SelectMenu(sender);

            // 保存编码
            var cfg = SerialPortConfig.Current;
            cfg.WebEncoding = mi.Name;
        }

        private void mi字符串编码_Click(object sender, EventArgs e)
        {
            var cfg = SerialPortConfig.Current;
            //cfg.HexShow = miHEX编码接收.Checked = !mi字符串编码.Checked;
            cfg.HexShow = SelectMenu(sender, mi字符串编码, miHEX编码接收) == 1;

            // 收起菜单
            contextMenuStrip1.Hide();
        }

        private void miHex自动换行_Click(object sender, EventArgs e)
        {
            var cfg = SerialPortConfig.Current;
            //cfg.HexNewLine = ti.Tag.ToBoolean();
            cfg.HexNewLine = SelectMenu(sender) == 1;
        }

        private void miHEX编码发送_Click(object sender, EventArgs e)
        {
            var cfg = SerialPortConfig.Current;
            miHEX编码发送.Checked = !miHEX编码发送.Checked;
            cfg.HexSend = miHEX编码发送.Checked;
        }

        private void miDTR_Click(object sender, EventArgs e)
        {
            var mi = sender as ToolStripMenuItem;
            mi.Checked = !mi.Checked;
        }
        #endregion

        #region 窗体
        /// <summary>连接串口</summary>
        public void Connect()
        {
            var name = cbName.SelectedItem + "";
            if (String.IsNullOrEmpty(name))
            {
                MessageBox.Show("请选择串口！", this.Text);
                cbName.Focus();
                return;
            }
            var p = name.IndexOf("(");
            if (p > 0) name = name.Substring(0, p);

            SaveInfo();
            var cfg = SerialPortConfig.Current;

            var pt = Port;
            if (pt != null)
            {
                // 如果上次没有关闭，则关闭
                try
                {
                    pt.Close();
                }
                catch
                {
                    pt = null;
                }
            }
            if (pt == null) pt = new SerialTransport();
            pt.PortName = name;
            pt.BaudRate = cfg.BaudRate;
            pt.Parity = cfg.Parity;
            pt.DataBits = cfg.DataBits;
            pt.StopBits = cfg.StopBits;

            //pt.Open();

            pt.Disconnected += Port_Disconnected;
            pt.Received += OnReceived;
            pt.ReceiveAsync();

            //pt.EnsureCreate();

            Port = pt;

            var sp = pt.Serial;
            // 这几个需要打开端口以后才能设置
            try
            {
                if (sp.DtrEnable != miDTR.Checked) sp.DtrEnable = miDTR.Checked;
                if (sp.RtsEnable != miRTS.Checked) sp.RtsEnable = miRTS.Checked;
                if (sp.BreakState != miBreak.Checked) sp.BreakState = miBreak.Checked;
            }
            catch { }

            this.Enabled = false;
        }

        void Port_Disconnected(object sender, EventArgs e)
        {
            Disconnect();
        }

        /// <summary>断开串口连接</summary>
        public void Disconnect()
        {
            var pt = Port;
            if (pt != null)
            {
                Port = null;
                pt.Disconnected -= Port_Disconnected;
                pt.Received -= OnReceived;
                pt.Close();
                // 异步调用释放，避免死锁卡死界面UI
                //ThreadPoolX.QueueUserWorkItem(() => pt.Close());
            }

            ShowPorts();

            this.Enabled = true;
        }

        private void OnCheck(Object state)
        {
            if (this.Enabled)
            {
                ShowPorts();
            }
            //else
            //{
            //    // 检查串口是否已断开，自动关闭已断开的串口，避免内存暴涨
            //    if (Port != null && Port.Serial != null && !Port.Serial.IsOpen) Disconnect();
            //}
        }
        #endregion

        #region 菜单辅助
        ToolStripItem FindMenu(String name)
        {
            return contextMenuStrip1.Items.Find(name, false)[0];
        }

        void BindMenu(ToolStripMenuItem ti, EventHandler handler, IEnumerable em)
        {
            ti.DropDownItems.Clear();
            foreach (var item in em)
            {
                var tsi = ti.DropDownItems.Add(item + "");
                tsi.Tag = item;
                tsi.Click += handler;
            }
        }

        void SetMenuItem(ToolStripMenuItem ti, Object value)
        {
            foreach (ToolStripMenuItem item in ti.DropDownItems)
            {
                item.Checked = item + "" == value + "";
            }
        }

        /// <summary>在一个菜单列表中点击某一个菜单，改变菜单选择并返回选中的菜单索引</summary>
        /// <param name="sender"></param>
        /// <param name="ms"></param>
        Int32 SelectMenu(Object sender, params ToolStripMenuItem[] ms)
        {
            var mi = sender as ToolStripMenuItem;
            if (mi == null) return -1;

            var idx = -1;
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i] == mi)
                {
                    ms[i].Checked = true;
                    idx = i;
                }
                else
                    ms[i].Checked = false;
            }
            return idx;
        }

        /// <summary>在同级菜单中选择</summary>
        /// <param name="sender"></param>
        /// <param name="tsi"></param>
        /// <returns></returns>
        Int32 SelectMenu(Object sender, ToolStripItem tsi = null)
        {
            if (tsi == null) tsi = (sender as ToolStripMenuItem).OwnerItem;

            var ms = (tsi as ToolStripMenuItem).DropDownItems;
            if (ms == null) return -1;

            var arr = new ToolStripMenuItem[ms.Count];
            ms.CopyTo(arr, 0);

            return SelectMenu(sender, arr);
        }
        #endregion

        #region 收发数据
        /// <summary>发送字节数组</summary>
        /// <param name="data"></param>
        public void Send(Byte[] data)
        {
            if (data == null || data.Length <= 0) return;

            BytesOfSent += data.Length;

            Port.Send(data);
        }

        /// <summary>发送字符串。根据配置进行十六进制编码</summary>
        /// <param name="str"></param>
        /// <returns>发送字节数</returns>
        public Int32 Send(String str)
        {
            var cfg = SerialPortConfig.Current;
            // 16进制发送
            Byte[] data = null;
            if (cfg.HexSend)
                data = str.ToHex();
            else
                data = cfg.Encoding.GetBytes(str);

            Send(data);

            return data.Length;
        }

        //public event SerialReceived Received;
        /// <summary>收到数据时触发。第一个参数是数据，第二个参数返回是否继续往下传递数据</summary>
        [Browsable(true)]
        [EditorBrowsable(EditorBrowsableState.Always)]
        public event EventHandler<BufferEventArgs> Received;

        /// <summary>收到数据时转为字符串后触发该事件。注意字符串编码和十六进制编码。</summary>
        /// <remarks>如果需要收到的数据，可直接在<seealso cref="Port"/>上挂载事件</remarks>
        [Browsable(true)]
        [EditorBrowsable(EditorBrowsableState.Always)]
        public event EventHandler<StringEventArgs> ReceivedString;

        MemoryStream _stream;
        StreamReader _reader;
        void OnReceived(Object sender, ReceivedEventArgs e)
        {
            var data = e.Data;
            if (data == null || data.Length < 1) return;

            BytesOfReceived += data.Length;

            // 处理数据委托
            if (Received != null)
            {
                var e2 = new BufferEventArgs { Value = data };
                Received(this, e2);
                if (!e2.Cancel) return;
                // 外部可能修改了数据
                data = e2.Value;
                //if (!BufferEventArgs.Invoke(Received, data)) return null;
            }

            // 处理字符串委托
            if (ReceivedString == null) return;

            var cfg = SerialPortConfig.Current;

            var line = "";
            if (cfg.HexShow)
            {
                if (data.Length > 32)
                    line = "[{0}]=\r\n{1}".F(data.Length, data.ToHex("-", 32));
                else
                    line = "[{0}]={1}".F(data.Length, data.ToHex("-", 32));
                if (cfg.HexNewLine) line += Environment.NewLine;
            }
            else
            {
                line = cfg.Encoding.GetString(data);
                if (_stream == null)
                    _stream = new MemoryStream();
                else if (_stream.Length > 10 * 1024 && _stream.Position == _stream.Length) // 达到最大大小时，从头开始使用
                    _stream = new MemoryStream();
                _stream.Write(data);
                _stream.Seek(-1 * data.Length, SeekOrigin.Current);

                if (_reader == null ||
                    _reader.BaseStream != _stream ||
                    _reader.CurrentEncoding != cfg.Encoding)
                    _reader = new StreamReader(_stream, cfg.Encoding);
                line = _reader.ReadToEnd();
            }

            if (ReceivedString != null) ReceivedString(this, new StringEventArgs { Value = line });
        }
        #endregion

        #region 收发统计
        /// <summary>清空发送</summary>
        public void ClearSend()
        {
            BytesOfSent = 0;

            var sp = Port;
            if (sp != null)
            {
                try
                {
                    if (sp.Serial != null) sp.Serial.DiscardOutBuffer();
                }
                catch { }
            }
        }

        /// <summary>清空接收</summary>
        public void ClearReceive()
        {
            BytesOfReceived = 0;
            if (_stream != null) _stream.SetLength(0);

            var sp = Port;
            if (sp != null)
            {
                try
                {
                    if (sp.Serial != null) sp.Serial.DiscardInBuffer();
                }
                catch { }
            }
        }
        #endregion
    }

    /// <summary>字符串事件参数</summary>
    public class StringEventArgs : EventArgs
    {
        private String _Value;
        /// <summary>字符串值</summary>
        public String Value { get { return _Value; } set { _Value = value; } }
    }

    /// <summary>缓冲区事件参数</summary>
    public class BufferEventArgs : CancelEventArgs
    {
        private Byte[] _Value;
        /// <summary>缓冲区数据</summary>
        public Byte[] Value { get { return _Value; } set { _Value = value; } }

        //public static Boolean Invoke(EventHandler<BufferEventArgs> handler, Object sender, Byte[] buf)
        //{
        //    var e = new BufferEventArgs { Value = buf, Cancel = false };
        //    handler(sender, e);
        //    return e.Cancel;
        //}
    }
}