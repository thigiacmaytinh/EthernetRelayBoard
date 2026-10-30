using System;
using System.Drawing;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RelayControlNetFx
{
    public partial class MainForm : Form
    {
        private readonly RelayTcpTransport _transport = new RelayTcpTransport();
        private Button[] _openButtons;
        private Button[] _closeButtons;
        private Label[] _relayLabels;
        private TransportState _state = TransportState.Disconnected;
        private int _selectedMode;
        private string _clientAddress = "192.168.1.199";
        private string _clientPort = "2000";
        private string _serverAddress = "0.0.0.0";
        private string _serverPort = "8800";
        private bool _closing;

        public MainForm()
        {
            InitializeComponent();
            BindRelayButtons();
            BindEvents();
            cmbMode.SelectedIndex = 0;
            _selectedMode = 0;
            txtAddress.Text = _clientAddress;
            txtPort.Text = _clientPort;
            UpdateModeLabels();
            UpdateConnectionUi(TransportState.Disconnected, "Disconnected");
            AppendLog("Ready. OPEN = ON/energize; CLOSE = OFF/de-energize.");
            AppendLog("Status shows commands SENT, not measured physical relay state.");
        }

        private void BindEvents()
        {
            _transport.StateChanged += TransportStateChanged;
            _transport.MessageLogged += TransportMessageLogged;

            cmbMode.SelectedIndexChanged += CmbModeSelectedIndexChanged;
            btnConnect.Click += BtnConnectClick;
            btnDisconnect.Click += BtnDisconnectClick;
            btnOpenAll.Click += BtnOpenAllClick;
            btnCloseAll.Click += BtnCloseAllClick;
            btnClearLog.Click += BtnClearLogClick;
        }

        private void BindRelayButtons()
        {
            _openButtons = new[]
            {
                btnOpen1, btnOpen2, btnOpen3, btnOpen4,
                btnOpen5, btnOpen6, btnOpen7, btnOpen8
            };

            _closeButtons = new[]
            {
                btnClose1, btnClose2, btnClose3, btnClose4,
                btnClose5, btnClose6, btnClose7, btnClose8
            };

            _relayLabels = new[]
            {
                lblRelay1, lblRelay2, lblRelay3, lblRelay4,
                lblRelay5, lblRelay6, lblRelay7, lblRelay8
            };

            for (int i = 0; i < RelayProtocol.MaximumChannel; i++)
            {
                int channel = i + 1;
                _openButtons[i].Click += async (sender, args) => await SendRelayAsync(channel, true);
                _closeButtons[i].Click += async (sender, args) => await SendRelayAsync(channel, false);
                toolTip.SetToolTip(_openButtons[i], "Send 00 F" + channel + " FF (ON/energize)");
                toolTip.SetToolTip(_closeButtons[i], "Send 00 0" + channel + " FF (OFF/de-energize)");
            }
        }

        private void CmbModeSelectedIndexChanged(object sender, EventArgs e)
        {
            if (cmbMode.SelectedIndex < 0 || cmbMode.SelectedIndex == _selectedMode)
            {
                return;
            }

            if (_selectedMode == 0)
            {
                _clientAddress = txtAddress.Text.Trim();
                _clientPort = txtPort.Text.Trim();
            }
            else
            {
                _serverAddress = txtAddress.Text.Trim();
                _serverPort = txtPort.Text.Trim();
            }

            _selectedMode = cmbMode.SelectedIndex;
            txtAddress.Text = _selectedMode == 0 ? _clientAddress : _serverAddress;
            txtPort.Text = _selectedMode == 0 ? _clientPort : _serverPort;
            UpdateModeLabels();
        }

        private void UpdateModeLabels()
        {
            bool isClient = cmbMode.SelectedIndex == 0;
            lblAddress.Text = isClient ? "Board IP / host" : "Local bind IP";
            btnConnect.Text = isClient ? "CONNECT" : "LISTEN";
            lblModeHelp.Text = isClient
                ? "Use when CH9120 is TCP SERVER. Example: 192.168.1.199 : 2000"
                : "Use when CH9120 is TCP CLIENT. 0.0.0.0 accepts on all local adapters.";
        }

        private async void BtnConnectClick(object sender, EventArgs e)
        {
            int port;

            if (!int.TryParse(txtPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, "Port must be between 1 and 65535.",
                    "Invalid port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string address = txtAddress.Text.Trim();

            if (address.Length == 0)
            {
                MessageBox.Show(this, "Enter an IP address or host name.",
                    "Invalid address", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnConnect.Enabled = false;

            try
            {
                if (cmbMode.SelectedIndex == 0)
                {
                    await _transport.ConnectAsync(address, port);
                }
                else
                {
                    IPAddress bindAddress;

                    if (!IPAddress.TryParse(address, out bindAddress))
                    {
                        throw new ArgumentException("Server bind address must be a valid local IPv4/IPv6 address.");
                    }

                    _transport.Listen(bindAddress, port);
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message, "TCP connection error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateConnectionUi(TransportState.Disconnected, "Disconnected");
            }
        }

        private void BtnDisconnectClick(object sender, EventArgs e)
        {
            _transport.Stop();
            AppendLog("Disconnected by user.");
        }

        private async void BtnOpenAllClick(object sender, EventArgs e)
        {
            DialogResult choice = MessageBox.Show(this,
                "Send OPEN (ON) to ALL eight channels? This may energize connected loads.",
                "Confirm OPEN ALL", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (choice == DialogResult.Yes)
            {
                await SendRelayAsync(RelayProtocol.AllChannels, true);
            }
        }

        private async void BtnCloseAllClick(object sender, EventArgs e)
        {
            await SendRelayAsync(RelayProtocol.AllChannels, false);
        }

        private void BtnClearLogClick(object sender, EventArgs e)
        {
            txtLog.Clear();
        }

        private async Task SendRelayAsync(int channel, bool open)
        {
            if (_state != TransportState.Connected)
            {
                return;
            }

            try
            {
                await _transport.SendAsync(channel, open);
                string status = open ? "ON sent" : "OFF sent";
                Color color = open ? Color.FromArgb(25, 128, 80) : Color.FromArgb(95, 105, 115);

                if (channel == RelayProtocol.AllChannels)
                {
                    for (int i = 0; i < _relayLabels.Length; i++)
                    {
                        _relayLabels[i].Text = status;
                        _relayLabels[i].ForeColor = color;
                    }
                }
                else
                {
                    _relayLabels[channel - 1].Text = status;
                    _relayLabels[channel - 1].ForeColor = color;
                }
            }
            catch (Exception ex)
            {
                AppendLog("SEND ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Relay command failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void TransportStateChanged(TransportState state, string detail)
        {
            PostToUi(() => UpdateConnectionUi(state, detail));
        }

        private void TransportMessageLogged(string message)
        {
            PostToUi(() => AppendLog(message));
        }

        private void PostToUi(Action action)
        {
            if (_closing || IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (!_closing && !IsDisposed)
                    {
                        action();
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // Form handle was destroyed during shutdown.
            }
        }

        private void UpdateConnectionUi(TransportState state, string detail)
        {
            _state = state;
            bool connected = state == TransportState.Connected;
            bool inactive = state == TransportState.Disconnected;

            cmbMode.Enabled = inactive;
            txtAddress.Enabled = inactive;
            txtPort.Enabled = inactive;
            btnConnect.Enabled = inactive;
            btnDisconnect.Enabled = !inactive;
            lblStatus.Text = detail;
            lblStatus.ForeColor = connected ? Color.FromArgb(20, 124, 77) :
                state == TransportState.Listening ? Color.FromArgb(172, 103, 13) :
                Color.FromArgb(80, 88, 98);

            foreach (Button button in _openButtons)
            {
                button.Enabled = connected;
            }

            foreach (Button button in _closeButtons)
            {
                button.Enabled = connected;
            }

            btnOpenAll.Enabled = connected;
            btnCloseAll.Enabled = connected;

            if (!connected)
            {
                foreach (Label label in _relayLabels)
                {
                    label.Text = "No state";
                    label.ForeColor = Color.FromArgb(112, 119, 128);
                }
            }
        }

        private void AppendLog(string message)
        {
            if (txtLog.TextLength > 30000)
            {
                txtLog.Clear();
                txtLog.AppendText("[Older messages cleared to limit memory use]" + Environment.NewLine);
            }

            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " +
                message + Environment.NewLine);
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            _transport.Dispose();
            base.OnFormClosing(e);
        }
    }
}
